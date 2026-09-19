// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;

namespace rtaime.Media;

internal readonly record struct Mp4LocalMediaMetadata(
	uint Width,
	uint Height,
	long FrameRateNumerator,
	long FrameRateDenominator,
	uint AudioChannels,
	uint AudioSampleRate,
	TimeSpan Duration);

internal static class Mp4LocalMediaMetadataReader
{
	private const long MaximumMoovSize = 32L * 1024 * 1024;
	private const uint Moov = 0x6D6F6F76;
	private const uint Mvhd = 0x6D766864;
	private const uint Trak = 0x7472616B;
	private const uint Mdia = 0x6D646961;
	private const uint Mdhd = 0x6D646864;
	private const uint Hdlr = 0x68646C72;
	private const uint Minf = 0x6D696E66;
	private const uint Stbl = 0x7374626C;
	private const uint Stsd = 0x73747364;
	private const uint Stts = 0x73747473;
	private const uint Vide = 0x76696465;
	private const uint Soun = 0x736F756E;
	private const uint Avc1 = 0x61766331;
	private const uint Avc3 = 0x61766333;
	private const uint Mp4a = 0x6D703461;

	public static Mp4LocalMediaMetadata Read(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		var moov = ReadMoovPayload(stream);
		var movieHeader = RequireChild(moov, 0, moov.Length, Mvhd, "mvhd");
		var duration = ReadDuration(moov, movieHeader, "mvhd");

		uint? width = null;
		uint? height = null;
		long? frameRateNumerator = null;
		long? frameRateDenominator = null;
		uint? channels = null;
		uint? sampleRate = null;

		foreach (var track in Children(moov, 0, moov.Length).Where(box => box.Type == Trak))
		{
			var media = RequireChild(moov, track.PayloadOffset, track.PayloadLength, Mdia, "mdia");
			var handler = RequireChild(moov, media.PayloadOffset, media.PayloadLength, Hdlr, "hdlr");
			var handlerType = ReadHandlerType(moov, handler);
			var mediaHeader = RequireChild(moov, media.PayloadOffset, media.PayloadLength, Mdhd, "mdhd");
			var mediaTimescale = ReadTimescale(moov, mediaHeader, "mdhd");
			var mediaInformation = RequireChild(moov, media.PayloadOffset, media.PayloadLength, Minf, "minf");
			var sampleTable = RequireChild(moov, mediaInformation.PayloadOffset, mediaInformation.PayloadLength, Stbl, "stbl");
			var sampleDescription = RequireChild(moov, sampleTable.PayloadOffset, sampleTable.PayloadLength, Stsd, "stsd");

			if (handlerType == Vide)
			{
				var videoEntry = ReadFirstSampleEntry(moov, sampleDescription, new[] { Avc1, Avc3 }, "avc1/avc3");
				(width, height) = ReadVisualSampleEntryDimensions(moov, videoEntry);
				var timing = RequireChild(moov, sampleTable.PayloadOffset, sampleTable.PayloadLength, Stts, "stts");
				(frameRateNumerator, frameRateDenominator) = ReadFrameRate(moov, timing, mediaTimescale);
			}
			else if (handlerType == Soun)
			{
				var audioEntry = ReadFirstSampleEntry(moov, sampleDescription, Mp4a, "mp4a");
				(channels, sampleRate) = ReadAudioSampleEntry(moov, audioEntry);
			}
		}

		if (width is null || height is null || frameRateNumerator is null || frameRateDenominator is null)
			throw new InvalidDataException("MP4 video metadata is incomplete.");
		if (channels is null || sampleRate is null)
			throw new InvalidDataException("MP4 audio metadata is incomplete.");
		if (duration <= TimeSpan.Zero)
			throw new InvalidDataException("MP4 duration must be greater than zero.");

		return new Mp4LocalMediaMetadata(
			width.Value,
			height.Value,
			frameRateNumerator.Value,
			frameRateDenominator.Value,
			channels.Value,
			sampleRate.Value,
			duration);
	}

	private static byte[] ReadMoovPayload(FileStream stream)
	{
		var end = stream.Length;
		while (stream.Position < end)
		{
			var header = ReadStreamBoxHeader(stream, end);
			if (header.Type == Moov)
			{
				if (header.PayloadLength <= 0 || header.PayloadLength > MaximumMoovSize || header.PayloadLength > int.MaxValue)
					throw new InvalidDataException("MP4 moov box size is outside the supported V1 probe limit.");

				var payload = new byte[checked((int)header.PayloadLength)];
				stream.ReadExactly(payload);
				return payload;
			}

			stream.Position = checked(stream.Position + header.PayloadLength);
		}

		throw new InvalidDataException("MP4 moov box was not found.");
	}

	private static StreamBoxHeader ReadStreamBoxHeader(FileStream stream, long containerEnd)
	{
		Span<byte> header = stackalloc byte[16];
		if (containerEnd - stream.Position < 8)
			throw new InvalidDataException("MP4 box header is truncated.");
		stream.ReadExactly(header[..8]);

		var size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
		var type = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
		long size;
		var headerSize = 8;
		if (size32 == 1)
		{
			if (containerEnd - stream.Position < 8)
				throw new InvalidDataException("Extended MP4 box header is truncated.");
			stream.ReadExactly(header.Slice(8, 8));
			var size64 = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
			if (size64 > long.MaxValue)
				throw new InvalidDataException("MP4 box size exceeds the supported range.");
			size = checked((long)size64);
			headerSize = 16;
		}
		else if (size32 == 0)
		{
			size = checked(containerEnd - (stream.Position - 8));
		}
		else
		{
			size = size32;
		}

		if (size < headerSize)
			throw new InvalidDataException("MP4 box size is smaller than its header.");
		var payloadLength = size - headerSize;
		if (payloadLength > containerEnd - stream.Position)
			throw new InvalidDataException("MP4 box extends beyond its containing file.");
		return new StreamBoxHeader(type, payloadLength);
	}

	private static IEnumerable<BufferBox> Children(byte[] buffer, int start, int length)
	{
		var end = checked(start + length);
		var offset = start;
		while (offset < end)
		{
			var box = ReadBufferBox(buffer, offset, end);
			yield return box;
			offset = checked(box.Offset + box.Length);
		}
	}

	private static BufferBox RequireChild(byte[] buffer, int start, int length, uint type, string name) =>
		Children(buffer, start, length).FirstOrDefault(box => box.Type == type) is var box && box.Length > 0
			? box
			: throw new InvalidDataException($"Required MP4 '{name}' box was not found.");

	private static BufferBox ReadBufferBox(byte[] buffer, int offset, int end)
	{
		if (offset < 0 || end > buffer.Length || end - offset < 8)
			throw new InvalidDataException("MP4 box header is truncated.");

		var span = buffer.AsSpan(offset, end - offset);
		var size32 = BinaryPrimitives.ReadUInt32BigEndian(span[..4]);
		var type = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
		long size;
		var headerSize = 8;
		if (size32 == 1)
		{
			if (span.Length < 16)
				throw new InvalidDataException("Extended MP4 box header is truncated.");
			var size64 = BinaryPrimitives.ReadUInt64BigEndian(span.Slice(8, 8));
			if (size64 > int.MaxValue)
				throw new InvalidDataException("MP4 box size exceeds the supported probe range.");
			size = checked((long)size64);
			headerSize = 16;
		}
		else if (size32 == 0)
		{
			size = end - offset;
		}
		else
		{
			size = size32;
		}

		if (size < headerSize || size > end - offset || size > int.MaxValue)
			throw new InvalidDataException("MP4 box size is invalid for its container.");
		return new BufferBox(offset, checked((int)size), headerSize, type);
	}

	private static uint ReadHandlerType(byte[] buffer, BufferBox handler)
	{
		EnsurePayloadLength(handler, 12, "hdlr");
		return BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(handler.PayloadOffset + 8, 4));
	}

	private static uint ReadTimescale(byte[] buffer, BufferBox header, string name)
	{
		EnsurePayloadLength(header, 20, name);
		var version = buffer[header.PayloadOffset];
		var offset = version switch
		{
			0 => header.PayloadOffset + 12,
			1 => header.PayloadOffset + 20,
			_ => throw new InvalidDataException($"Unsupported MP4 '{name}' version '{version}'.")
		};
		if (offset + 4 > header.Offset + header.Length)
			throw new InvalidDataException($"MP4 '{name}' timescale is truncated.");
		var timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
		if (timescale == 0)
			throw new InvalidDataException($"MP4 '{name}' timescale must be greater than zero.");
		return timescale;
	}

	private static TimeSpan ReadDuration(byte[] buffer, BufferBox header, string name)
	{
		var timescale = ReadTimescale(buffer, header, name);
		var version = buffer[header.PayloadOffset];
		ulong duration;
		if (version == 0)
		{
			EnsurePayloadLength(header, 20, name);
			duration = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(header.PayloadOffset + 16, 4));
		}
		else
		{
			EnsurePayloadLength(header, 32, name);
			duration = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(header.PayloadOffset + 24, 8));
		}

		var ticks = checked((long)Math.Round(duration * (double)TimeSpan.TicksPerSecond / timescale, MidpointRounding.AwayFromZero));
		return TimeSpan.FromTicks(ticks);
	}

	private static BufferBox ReadFirstSampleEntry(byte[] buffer, BufferBox stsd, uint expectedType, string expectedName) =>
		ReadFirstSampleEntry(buffer, stsd, new[] { expectedType }, expectedName);

	private static BufferBox ReadFirstSampleEntry(
		byte[] buffer,
		BufferBox stsd,
		IReadOnlyCollection<uint> expectedTypes,
		string expectedName)
	{
		EnsurePayloadLength(stsd, 16, "stsd");
		var entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(stsd.PayloadOffset + 4, 4));
		if (entryCount == 0)
			throw new InvalidDataException("MP4 stsd contains no sample descriptions.");
		var entryOffset = stsd.PayloadOffset + 8;
		var entry = ReadBufferBox(buffer, entryOffset, stsd.Offset + stsd.Length);
		if (!expectedTypes.Contains(entry.Type))
			throw new InvalidDataException($"MP4 sample description is not a supported '{expectedName}' type.");
		return entry;
	}

	private static (uint Width, uint Height) ReadVisualSampleEntryDimensions(byte[] buffer, BufferBox entry)
	{
		if (entry.Length < 36)
			throw new InvalidDataException("MP4 visual sample entry is truncated.");
		var width = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(entry.Offset + 32, 2));
		var height = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(entry.Offset + 34, 2));
		if (width == 0 || height == 0)
			throw new InvalidDataException("MP4 visual sample dimensions must be greater than zero.");
		return (width, height);
	}

	private static (uint Channels, uint SampleRate) ReadAudioSampleEntry(byte[] buffer, BufferBox entry)
	{
		if (entry.Length < 36)
			throw new InvalidDataException("MP4 audio sample entry is truncated.");
		var channels = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(entry.Offset + 24, 2));
		var packedSampleRate = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(entry.Offset + 32, 4));
		var sampleRate = packedSampleRate >> 16;
		if (channels == 0 || sampleRate == 0)
			throw new InvalidDataException("MP4 audio channel count and sample rate must be greater than zero.");
		return (channels, sampleRate);
	}

	private static (long Numerator, long Denominator) ReadFrameRate(byte[] buffer, BufferBox stts, uint timescale)
	{
		EnsurePayloadLength(stts, 16, "stts");
		var entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(stts.PayloadOffset + 4, 4));
		if (entryCount == 0)
			throw new InvalidDataException("MP4 stts contains no timing entries.");
		if (entryCount > (uint)((stts.PayloadLength - 8) / 8))
			throw new InvalidDataException("MP4 stts timing entries are truncated.");

		ulong totalSamples = 0;
		ulong totalMediaTicks = 0;
		for (var index = 0u; index < entryCount; index++)
		{
			var offset = checked(stts.PayloadOffset + 8 + (int)index * 8);
			var sampleCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
			var sampleDelta = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + 4, 4));
			if (sampleCount == 0 || sampleDelta == 0)
				throw new InvalidDataException("MP4 stts contains a zero sample count or sample delta.");
			totalSamples = checked(totalSamples + sampleCount);
			totalMediaTicks = checked(totalMediaTicks + (ulong)sampleCount * sampleDelta);
		}

		var numerator = checked((ulong)timescale * totalSamples);
		var denominator = totalMediaTicks;
		var gcd = GreatestCommonDivisor(numerator, denominator);
		numerator /= gcd;
		denominator /= gcd;
		if (numerator > long.MaxValue || denominator > long.MaxValue)
			throw new InvalidDataException("MP4 frame rate exceeds the supported rational range.");
		return ((long)numerator, (long)denominator);
	}

	private static ulong GreatestCommonDivisor(ulong left, ulong right)
	{
		while (right != 0)
		{
			var remainder = left % right;
			left = right;
			right = remainder;
		}
		return left;
	}

	private static void EnsurePayloadLength(BufferBox box, int minimum, string name)
	{
		if (box.PayloadLength < minimum)
			throw new InvalidDataException($"MP4 '{name}' box is truncated.");
	}

	private readonly record struct StreamBoxHeader(uint Type, long PayloadLength);

	private readonly record struct BufferBox(int Offset, int Length, int HeaderSize, uint Type)
	{
		public int PayloadOffset => checked(Offset + HeaderSize);
		public int PayloadLength => checked(Length - HeaderSize);
	}
}
