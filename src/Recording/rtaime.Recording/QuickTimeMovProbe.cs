// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Core;

namespace rtaime.Recording;

public sealed record QuickTimeMovProbeResult(
	string Container,
	string VideoSampleEntry,
	string AudioSampleEntry,
	uint Width,
	uint Height,
	FrameRate FrameRate,
	uint AudioSampleRate,
	ushort AudioChannels,
	ushort AudioBitsPerSample,
	uint VideoSampleCount,
	ulong AudioSampleFrameCount,
	TimeSpan VideoDuration,
	TimeSpan AudioDuration,
	TimeSpan VideoStartOffset,
	TimeSpan AudioStartOffset);

/// <summary>
/// Independent bounded parser for the concrete MOV profile emitted by the managed QuickTime writer.
/// It does not reuse writer state and rejects malformed sample tables or chunks outside the media-data atom.
/// </summary>
public static class QuickTimeMovProbe
{
	public static QuickTimeMovProbeResult Probe(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("MOV path is required.", nameof(path));
		if (!File.Exists(path))
			throw new FileNotFoundException("MOV file does not exist.", path);

		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		var topLevel = ReadChildren(stream, 0, stream.Length);
		var ftyp = Single(topLevel, "ftyp");
		var mdat = Single(topLevel, "mdat");
		var moov = Single(topLevel, "moov");
		ValidateFileType(stream, ftyp);

		var movieChildren = ReadChildren(stream, moov.DataOffset, moov.End);
		var (movieTimescale, movieDuration) = ReadMovieHeader(stream, Single(movieChildren, "mvhd"));
		var tracks = movieChildren
			.Where(atom => atom.Type == "trak")
			.Select(track => ReadTrack(stream, track, mdat, movieTimescale))
			.ToArray();
		var video = tracks.SingleOrDefault(track => track.HandlerType == "vide")
			?? throw new InvalidDataException("MOV contains no video track.");
		var audio = tracks.SingleOrDefault(track => track.HandlerType == "soun")
			?? throw new InvalidDataException("MOV contains no audio track.");
		if (tracks.Count(track => track.HandlerType == "vide") != 1 ||
			tracks.Count(track => track.HandlerType == "soun") != 1)
			throw new InvalidDataException("MOV profile requires exactly one video track and one audio track.");

		if (video.SampleEntry != "2vuy")
			throw new InvalidDataException($"MOV video sample entry '{video.SampleEntry}' is not the required 2vuy essence.");
		if (audio.SampleEntry != "sowt")
			throw new InvalidDataException($"MOV audio sample entry '{audio.SampleEntry}' is not the required sowt PCM essence.");
		if (audio.Timescale != 48_000 || audio.AudioChannels != 2 || audio.AudioBitsPerSample != 16)
			throw new InvalidDataException("MOV audio track is not stereo 48 kHz PCM16.");
		if (video.SampleCount == 0 || audio.SampleCount == 0)
			throw new InvalidDataException("MOV tracks must contain media samples.");
		if (video.DurationUnits == 0 || audio.DurationUnits == 0)
			throw new InvalidDataException("MOV tracks must have positive duration.");

		var frameRate = new FrameRate(
			checked((long)video.SampleCount * video.Timescale),
			checked((long)video.DurationUnits));
		var videoSeconds = (double)video.DurationUnits / video.Timescale;
		var audioSeconds = (double)audio.DurationUnits / audio.Timescale;
		var videoStartSeconds = (double)video.PresentationDelayMovieUnits / movieTimescale;
		var audioStartSeconds = (double)audio.PresentationDelayMovieUnits / movieTimescale;
		var frameTolerance = 1.0 / frameRate.FramesPerSecond;
		if (Math.Abs(videoStartSeconds - audioStartSeconds) > frameTolerance)
			throw new InvalidDataException("MOV video/audio start offsets exceed one video-frame alignment tolerance.");
		if (Math.Abs((videoStartSeconds + videoSeconds) - (audioStartSeconds + audioSeconds)) > frameTolerance)
			throw new InvalidDataException("MOV video/audio presentation ends exceed one video-frame alignment tolerance.");

		var expectedMovieDuration = Math.Max(
			checked(video.PresentationDelayMovieUnits + ScaleDuration(video.DurationUnits, video.Timescale, movieTimescale)),
			checked(audio.PresentationDelayMovieUnits + ScaleDuration(audio.DurationUnits, audio.Timescale, movieTimescale)));
		if (movieDuration != expectedMovieDuration)
			throw new InvalidDataException("MOV movie duration does not match the presented track durations.");

		return new QuickTimeMovProbeResult(
			"QuickTime Movie (MOV)",
			video.SampleEntry,
			audio.SampleEntry,
			video.Width,
			video.Height,
			frameRate,
			audio.Timescale,
			audio.AudioChannels,
			audio.AudioBitsPerSample,
			video.SampleCount,
			audio.SampleCount,
			TimeSpan.FromSeconds(videoSeconds),
			TimeSpan.FromSeconds(audioSeconds),
			TimeSpan.FromSeconds(videoStartSeconds),
			TimeSpan.FromSeconds(audioStartSeconds));
	}

	private static TrackProbe ReadTrack(Stream stream, AtomInfo track, AtomInfo mdat, uint movieTimescale)
	{
		var trackChildren = ReadChildren(stream, track.DataOffset, track.End);
		var trackHeaderDuration = ReadTrackHeaderDuration(stream, Single(trackChildren, "tkhd"));
		var presentationEdit = ReadPresentationEdit(stream, trackChildren);
		var mdia = Single(trackChildren, "mdia");
		var mediaChildren = ReadChildren(stream, mdia.DataOffset, mdia.End);
		var handler = Single(mediaChildren, "hdlr");
		var handlerType = ReadHandler(stream, handler);
		var mdhd = Single(mediaChildren, "mdhd");
		var (timescale, duration) = ReadMediaHeader(stream, mdhd);
		var minf = Single(mediaChildren, "minf");
		var minfChildren = ReadChildren(stream, minf.DataOffset, minf.End);
		var stbl = Single(minfChildren, "stbl");
		var sampleAtoms = ReadChildren(stream, stbl.DataOffset, stbl.End);
		var stsd = Single(sampleAtoms, "stsd");
		var stts = Single(sampleAtoms, "stts");
		var stsc = Single(sampleAtoms, "stsc");
		var stsz = Single(sampleAtoms, "stsz");
		var chunkOffsets = sampleAtoms.SingleOrDefault(atom => atom.Type == "co64");
		var uses64BitOffsets = chunkOffsets.Type == "co64";
		if (!uses64BitOffsets)
			chunkOffsets = Single(sampleAtoms, "stco");

		var description = ReadSampleDescription(stream, stsd, handlerType);
		var timing = ReadTimeToSample(stream, stts);
		var sizes = ReadSampleSizes(stream, stsz);
		if (timing.SampleCount != sizes.SampleCount)
			throw new InvalidDataException("MOV timing and sample-size tables disagree on sample count.");
		if (timing.Duration != duration)
			throw new InvalidDataException("MOV media duration does not equal its time-to-sample table duration.");
		if (handlerType == "vide" && sizes.SampleSize != checked(description.Width * description.Height * 2))
			throw new InvalidDataException("MOV 2vuy sample size does not match 16 bits per video pixel.");
		if (handlerType == "soun" && sizes.SampleSize != 4)
			throw new InvalidDataException("MOV sowt sample size does not match stereo PCM16.");

		var offsets = ReadChunkOffsets(stream, chunkOffsets, uses64BitOffsets);
		var mappings = ReadSampleToChunk(stream, stsc);
		ValidateChunks(offsets, mappings, sizes.SampleSize, sizes.SampleCount, mdat);

		var scaledMediaDuration = ScaleDuration(duration, timescale, movieTimescale);
		if (presentationEdit.MediaDurationMovieUnits is { } editMediaDuration && editMediaDuration != scaledMediaDuration)
			throw new InvalidDataException("MOV media edit duration does not match the media-header duration.");
		var presentedDuration = checked(presentationEdit.DelayMovieUnits + scaledMediaDuration);
		if (trackHeaderDuration != presentedDuration)
			throw new InvalidDataException("MOV track-header duration does not match its edit/media duration.");

		return new TrackProbe(
			handlerType,
			description.SampleEntry,
			description.Width,
			description.Height,
			description.AudioChannels,
			description.AudioBitsPerSample,
			timescale,
			duration,
			sizes.SampleCount,
			presentationEdit.DelayMovieUnits);
	}

	private static void ValidateFileType(Stream stream, AtomInfo atom)
	{
		if (atom.DataSize < 12)
			throw new InvalidDataException("MOV file-type atom is incomplete.");
		stream.Position = atom.DataOffset;
		var major = ReadFourCc(stream);
		_ = ReadUInt32(stream);
		var brands = new List<string> { major };
		while (stream.Position + 4 <= atom.End)
			brands.Add(ReadFourCc(stream));
		if (!brands.Contains("qt  ", StringComparer.Ordinal))
			throw new InvalidDataException("MOV file does not declare the QuickTime brand.");
	}

	private static (uint Timescale, ulong Duration) ReadMovieHeader(Stream stream, AtomInfo atom)
	{
		RequirePayload(atom, 32);
		stream.Position = atom.DataOffset;
		var version = checked((byte)stream.ReadByte());
		stream.Position += 3;
		if (version == 1)
		{
			stream.Position += 16;
			var timescale = ReadUInt32(stream);
			var duration = ReadUInt64(stream);
			if (timescale == 0)
				throw new InvalidDataException("MOV movie timescale must be positive.");
			return (timescale, duration);
		}
		if (version == 0)
		{
			stream.Position += 8;
			var timescale = ReadUInt32(stream);
			var duration = ReadUInt32(stream);
			if (timescale == 0)
				throw new InvalidDataException("MOV movie timescale must be positive.");
			return (timescale, duration);
		}
		throw new InvalidDataException($"Unsupported MOV movie-header version '{version}'.");
	}

	private static ulong ReadTrackHeaderDuration(Stream stream, AtomInfo atom)
	{
		RequirePayload(atom, 36);
		stream.Position = atom.DataOffset;
		var version = checked((byte)stream.ReadByte());
		stream.Position += 3;
		if (version == 1)
		{
			stream.Position += 16;
			_ = ReadUInt32(stream);
			stream.Position += 4;
			return ReadUInt64(stream);
		}
		if (version == 0)
		{
			stream.Position += 8;
			_ = ReadUInt32(stream);
			stream.Position += 4;
			return ReadUInt32(stream);
		}
		throw new InvalidDataException($"Unsupported MOV track-header version '{version}'.");
	}

	private static PresentationEdit ReadPresentationEdit(Stream stream, IReadOnlyList<AtomInfo> trackChildren)
	{
		var editContainers = trackChildren.Where(atom => atom.Type == "edts").ToArray();
		if (editContainers.Length == 0)
			return new PresentationEdit(0, null);
		if (editContainers.Length != 1)
			throw new InvalidDataException("MOV track contains multiple edit containers.");

		var elst = Single(ReadChildren(stream, editContainers[0].DataOffset, editContainers[0].End), "elst");
		RequirePayload(elst, 8);
		stream.Position = elst.DataOffset;
		var version = checked((byte)stream.ReadByte());
		stream.Position += 3;
		if (version != 1)
			throw new InvalidDataException("Qualified MOV edit lists must use 64-bit version 1 entries.");
		var entryCount = ReadUInt32(stream);
		if (entryCount != 2)
			throw new InvalidDataException("Qualified MOV delay edit list must contain exactly two entries.");

		RequireRemaining(stream, elst.End, 40);
		var delay = ReadUInt64(stream);
		var emptyMediaTime = ReadInt64(stream);
		var emptyRateInteger = ReadInt16(stream);
		var emptyRateFraction = ReadInt16(stream);
		var mediaDuration = ReadUInt64(stream);
		var mediaTime = ReadInt64(stream);
		var mediaRateInteger = ReadInt16(stream);
		var mediaRateFraction = ReadInt16(stream);
		if (stream.Position != elst.End)
			throw new InvalidDataException("MOV edit-list payload contains unexpected trailing data.");
		if (delay == 0 || mediaDuration == 0 || emptyMediaTime != -1 || emptyRateInteger != 1 || emptyRateFraction != 0 ||
			mediaTime != 0 || mediaRateInteger != 1 || mediaRateFraction != 0)
		{
			throw new InvalidDataException("MOV edit list does not match the bounded A/V-delay pattern.");
		}
		return new PresentationEdit(delay, mediaDuration);
	}

	private static string ReadHandler(Stream stream, AtomInfo atom)
	{
		RequirePayload(atom, 12);
		stream.Position = atom.DataOffset + 8;
		return ReadFourCc(stream);
	}

	private static (uint Timescale, ulong Duration) ReadMediaHeader(Stream stream, AtomInfo atom)
	{
		RequirePayload(atom, 20);
		stream.Position = atom.DataOffset;
		var version = checked((byte)stream.ReadByte());
		stream.Position += 3;
		if (version == 1)
		{
			RequirePayload(atom, 32);
			stream.Position += 16;
			var timescale = ReadUInt32(stream);
			var duration = ReadUInt64(stream);
			if (timescale == 0)
				throw new InvalidDataException("MOV media timescale must be positive.");
			return (timescale, duration);
		}
		if (version == 0)
		{
			stream.Position += 8;
			var timescale = ReadUInt32(stream);
			var duration = ReadUInt32(stream);
			if (timescale == 0)
				throw new InvalidDataException("MOV media timescale must be positive.");
			return (timescale, duration);
		}
		throw new InvalidDataException($"Unsupported MOV media-header version '{version}'.");
	}

	private static SampleDescription ReadSampleDescription(Stream stream, AtomInfo atom, string handlerType)
	{
		RequirePayload(atom, 16);
		stream.Position = atom.DataOffset + 4;
		if (ReadUInt32(stream) != 1)
			throw new InvalidDataException("MOV profile requires exactly one sample description.");

		var entryStart = stream.Position;
		var entrySize = ReadUInt32(stream);
		var entryType = ReadFourCc(stream);
		if (entrySize < 16 || entryStart + entrySize > atom.End)
			throw new InvalidDataException("MOV sample-description entry has an invalid size.");
		var dataOffset = entryStart + 8;
		var entryEnd = checked(entryStart + entrySize);

		stream.Position = dataOffset + 6;
		if (ReadUInt16(stream) != 1)
			throw new InvalidDataException("MOV sample entry must reference the self-contained media data reference.");

		if (handlerType == "vide")
		{
			if (entrySize < 86)
				throw new InvalidDataException("MOV visual sample entry is incomplete.");
			stream.Position = dataOffset + 24;
			var width = ReadUInt16(stream);
			var height = ReadUInt16(stream);
			if (width == 0 || height == 0)
				throw new InvalidDataException("MOV video dimensions must be positive.");
			stream.Position = dataOffset + 74;
			var depth = ReadUInt16(stream);
			var colorTableId = ReadInt16(stream);
			if (depth != 24 || colorTableId != -1)
				throw new InvalidDataException("MOV 2vuy visual sample entry must use the qualified 24-bit/no-color-table declaration.");
			ValidateVideoExtensions(stream, checked(dataOffset + 78), entryEnd);
			return new SampleDescription(entryType, width, height, 0, 0);
		}

		if (handlerType == "soun")
		{
			if (entrySize != 36)
				throw new InvalidDataException("MOV qualified sowt audio sample entry must use the bounded version-0 layout.");
			stream.Position = dataOffset + 16;
			var channels = ReadUInt16(stream);
			var bits = ReadUInt16(stream);
			var compressionId = ReadUInt16(stream);
			var packetSize = ReadUInt16(stream);
			var fixedRate = ReadUInt32(stream);
			var sampleRate = fixedRate >> 16;
			if (channels != 2 || bits != 16 || compressionId != 0 || packetSize != 0 || sampleRate != 48_000)
				throw new InvalidDataException("MOV sowt sample entry is not stereo 48 kHz uncompressed PCM16.");
			return new SampleDescription(entryType, 0, 0, channels, bits);
		}

		throw new InvalidDataException($"Unsupported MOV handler type '{handlerType}'.");
	}

	private static void ValidateVideoExtensions(Stream stream, long start, long end)
	{
		if (start >= end)
			throw new InvalidDataException("MOV 2vuy sample entry is missing required field/color extensions.");
		var extensions = ReadChildren(stream, start, end);
		var field = Single(extensions, "fiel");
		var color = Single(extensions, "colr");
		if (extensions.Count != 2)
			throw new InvalidDataException("MOV 2vuy sample entry contains unsupported visual extensions.");

		RequirePayload(field, 2);
		stream.Position = field.DataOffset;
		var fieldCount = stream.ReadByte();
		var fieldDetail = stream.ReadByte();
		if (fieldCount != 1 || fieldDetail != 0 || stream.Position != field.End)
			throw new InvalidDataException("MOV video field-order metadata is not progressive.");

		RequirePayload(color, 10);
		stream.Position = color.DataOffset;
		if (ReadFourCc(stream) != "nclc" || ReadUInt16(stream) != 1 || ReadUInt16(stream) != 1 || ReadUInt16(stream) != 1 || stream.Position != color.End)
			throw new InvalidDataException("MOV 2vuy color metadata is not the qualified BT.709 nclc 1/1/1 declaration.");
	}

	private static TimingTable ReadTimeToSample(Stream stream, AtomInfo atom)
	{
		RequirePayload(atom, 8);
		stream.Position = atom.DataOffset + 4;
		var entryCount = ReadUInt32(stream);
		ulong samples = 0;
		ulong duration = 0;
		for (var index = 0U; index < entryCount; index++)
		{
			RequireRemaining(stream, atom.End, 8);
			var count = ReadUInt32(stream);
			var delta = ReadUInt32(stream);
			if (count == 0 || delta == 0)
				throw new InvalidDataException("MOV time-to-sample entries must be positive.");
			samples = checked(samples + count);
			duration = checked(duration + checked((ulong)count * delta));
		}
		if (samples > uint.MaxValue)
			throw new InvalidDataException("MOV sample count exceeds the supported qualification range.");
		return new TimingTable(checked((uint)samples), duration);
	}

	private static SampleSizeTable ReadSampleSizes(Stream stream, AtomInfo atom)
	{
		RequirePayload(atom, 12);
		stream.Position = atom.DataOffset + 4;
		var sampleSize = ReadUInt32(stream);
		var sampleCount = ReadUInt32(stream);
		if (sampleSize == 0)
			throw new InvalidDataException("The qualified MOV profile requires a constant sample size per track.");
		return new SampleSizeTable(sampleSize, sampleCount);
	}

	private static IReadOnlyList<SampleToChunkEntry> ReadSampleToChunk(Stream stream, AtomInfo atom)
	{
		RequirePayload(atom, 8);
		stream.Position = atom.DataOffset + 4;
		var entryCount = ReadUInt32(stream);
		if (entryCount == 0)
			throw new InvalidDataException("MOV sample-to-chunk table is empty.");
		var entries = new List<SampleToChunkEntry>(checked((int)entryCount));
		uint previousFirstChunk = 0;
		for (var index = 0U; index < entryCount; index++)
		{
			RequireRemaining(stream, atom.End, 12);
			var firstChunk = ReadUInt32(stream);
			var samplesPerChunk = ReadUInt32(stream);
			var descriptionIndex = ReadUInt32(stream);
			if (firstChunk == 0 || firstChunk <= previousFirstChunk || samplesPerChunk == 0 || descriptionIndex != 1)
				throw new InvalidDataException("MOV sample-to-chunk table is invalid.");
			entries.Add(new SampleToChunkEntry(firstChunk, samplesPerChunk));
			previousFirstChunk = firstChunk;
		}
		if (entries[0].FirstChunk != 1)
			throw new InvalidDataException("MOV sample-to-chunk table must begin at chunk one.");
		return entries;
	}

	private static IReadOnlyList<ulong> ReadChunkOffsets(Stream stream, AtomInfo atom, bool is64Bit)
	{
		RequirePayload(atom, 8);
		stream.Position = atom.DataOffset + 4;
		var entryCount = ReadUInt32(stream);
		if (entryCount == 0)
			throw new InvalidDataException("MOV chunk-offset table is empty.");
		var offsets = new ulong[entryCount];
		ulong previous = 0;
		for (var index = 0; index < offsets.Length; index++)
		{
			RequireRemaining(stream, atom.End, is64Bit ? 8 : 4);
			var offset = is64Bit ? ReadUInt64(stream) : ReadUInt32(stream);
			if (offset == 0 || (index != 0 && offset <= previous))
				throw new InvalidDataException("MOV chunk offsets must be strictly increasing.");
			offsets[index] = offset;
			previous = offset;
		}
		return offsets;
	}

	private static void ValidateChunks(
		IReadOnlyList<ulong> offsets,
		IReadOnlyList<SampleToChunkEntry> mappings,
		uint sampleSize,
		uint sampleCount,
		AtomInfo mdat)
	{
		ulong mappedSamples = 0;
		for (var chunkIndex = 0; chunkIndex < offsets.Count; chunkIndex++)
		{
			var chunkNumber = checked((uint)chunkIndex + 1);
			var mapping = mappings[0];
			for (var index = 1; index < mappings.Count && mappings[index].FirstChunk <= chunkNumber; index++)
				mapping = mappings[index];

			var chunkBytes = checked((ulong)mapping.SamplesPerChunk * sampleSize);
			var offset = offsets[chunkIndex];
			var end = checked(offset + chunkBytes);
			if (offset < checked((ulong)mdat.DataOffset) || end > checked((ulong)mdat.End))
				throw new InvalidDataException("MOV media chunk lies outside the media-data atom.");
			mappedSamples = checked(mappedSamples + mapping.SamplesPerChunk);
		}

		if (mappedSamples != sampleCount)
			throw new InvalidDataException("MOV chunk mapping does not cover the declared sample count exactly.");
	}

	private static ulong ScaleDuration(ulong duration, uint sourceTimescale, uint targetTimescale)
	{
		if (sourceTimescale == 0 || targetTimescale == 0)
			throw new InvalidDataException("MOV duration scaling requires positive timescales.");
		var numerator = checked((UInt128)duration * targetTimescale);
		var scaled = (numerator + (sourceTimescale / 2U)) / sourceTimescale;
		if (scaled > ulong.MaxValue)
			throw new InvalidDataException("MOV scaled duration exceeds UInt64 range.");
		return (ulong)scaled;
	}

	private static IReadOnlyList<AtomInfo> ReadChildren(Stream stream, long start, long end)
	{
		if (start < 0 || end < start || end > stream.Length)
			throw new InvalidDataException("MOV atom container bounds are invalid.");
		var result = new List<AtomInfo>();
		stream.Position = start;
		while (stream.Position < end)
		{
			var atom = ReadAtom(stream, end);
			result.Add(atom);
			stream.Position = atom.End;
		}
		if (stream.Position != end)
			throw new InvalidDataException("MOV atom container does not end on an atom boundary.");
		return result;
	}

	private static AtomInfo ReadAtom(Stream stream, long parentEnd)
	{
		var start = stream.Position;
		if (parentEnd - start < 8)
			throw new InvalidDataException("MOV atom header is truncated.");
		var size32 = ReadUInt32(stream);
		var type = ReadFourCc(stream);
		long headerSize = 8;
		ulong size;
		if (size32 == 1)
		{
			if (parentEnd - stream.Position < 8)
				throw new InvalidDataException("MOV extended atom header is truncated.");
			size = ReadUInt64(stream);
			headerSize = 16;
		}
		else if (size32 == 0)
		{
			size = checked((ulong)(parentEnd - start));
		}
		else
		{
			size = size32;
		}

		if (size < checked((ulong)headerSize) || size > long.MaxValue)
			throw new InvalidDataException($"MOV atom '{type}' has an invalid size.");
		var end = checked(start + (long)size);
		if (end > parentEnd)
			throw new InvalidDataException($"MOV atom '{type}' exceeds its parent bounds.");
		return new AtomInfo(type, start, checked(start + headerSize), end);
	}

	private static AtomInfo Single(IReadOnlyList<AtomInfo> atoms, string type)
	{
		var matches = atoms.Where(atom => atom.Type == type).ToArray();
		if (matches.Length != 1)
			throw new InvalidDataException($"MOV requires exactly one '{type}' atom in this container.");
		return matches[0];
	}

	private static void RequirePayload(AtomInfo atom, long bytes)
	{
		if (atom.DataSize < bytes)
			throw new InvalidDataException($"MOV atom '{atom.Type}' payload is truncated.");
	}

	private static void RequireRemaining(Stream stream, long end, long bytes)
	{
		if (bytes < 0 || stream.Position > end - bytes)
			throw new InvalidDataException("MOV table payload is truncated.");
	}

	private static string ReadFourCc(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[4];
		ReadExactly(stream, buffer);
		return string.Create(4, buffer.ToArray(), static (characters, bytes) =>
		{
			for (var index = 0; index < 4; index++)
			{
				if (bytes[index] > 0x7f)
					throw new InvalidDataException("MOV FourCC contains non-ASCII data.");
				characters[index] = (char)bytes[index];
			}
		});
	}

	private static ushort ReadUInt16(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[2];
		ReadExactly(stream, buffer);
		return BinaryPrimitives.ReadUInt16BigEndian(buffer);
	}

	private static short ReadInt16(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[2];
		ReadExactly(stream, buffer);
		return BinaryPrimitives.ReadInt16BigEndian(buffer);
	}

	private static long ReadInt64(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[8];
		ReadExactly(stream, buffer);
		return BinaryPrimitives.ReadInt64BigEndian(buffer);
	}

	private static uint ReadUInt32(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[4];
		ReadExactly(stream, buffer);
		return BinaryPrimitives.ReadUInt32BigEndian(buffer);
	}

	private static ulong ReadUInt64(Stream stream)
	{
		Span<byte> buffer = stackalloc byte[8];
		ReadExactly(stream, buffer);
		return BinaryPrimitives.ReadUInt64BigEndian(buffer);
	}

	private static void ReadExactly(Stream stream, Span<byte> buffer)
	{
		var read = 0;
		while (read < buffer.Length)
		{
			var current = stream.Read(buffer[read..]);
			if (current == 0)
				throw new EndOfStreamException("MOV payload ended unexpectedly.");
			read += current;
		}
	}

	private readonly record struct AtomInfo(string Type, long Offset, long DataOffset, long End)
	{
		public long DataSize => End - DataOffset;
	}

	private readonly record struct SampleDescription(
		string SampleEntry,
		uint Width,
		uint Height,
		ushort AudioChannels,
		ushort AudioBitsPerSample);

	private readonly record struct TimingTable(uint SampleCount, ulong Duration);
	private readonly record struct SampleSizeTable(uint SampleSize, uint SampleCount);
	private readonly record struct SampleToChunkEntry(uint FirstChunk, uint SamplesPerChunk);
	private readonly record struct PresentationEdit(ulong DelayMovieUnits, ulong? MediaDurationMovieUnits);

	private sealed record TrackProbe(
		string HandlerType,
		string SampleEntry,
		uint Width,
		uint Height,
		ushort AudioChannels,
		ushort AudioBitsPerSample,
		uint Timescale,
		ulong DurationUnits,
		uint SampleCount,
		ulong PresentationDelayMovieUnits);
}
