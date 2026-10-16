// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using rtaime.Media.Contracts;

namespace rtaime.Media;

internal static class IsoBaseMediaMetadataReader
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

	public static LocalMediaStreamMetadata Read(string path, MediaContainerFormat container)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		var moov = ReadMoovPayload(stream, container);
		var movieHeader = RequireChild(moov, 0, moov.Length, Mvhd, "mvhd");
		var duration = ReadDuration(moov, movieHeader, "mvhd");

		LocalMediaStreamMetadata? result = null;
        var trackCount = 0;
        string audioEntry = "";
        uint? width = null;
		uint? height = null;
		long? frameRateNumerator = null;
		long? frameRateDenominator = null;
		foreach (var track in Children(moov, 0, moov.Length).Where(box => box.Type == Trak))
		{
			if (++trackCount > 16) throw new InvalidDataException("Too many media tracks.");
            var media = RequireChild(moov, track.PayloadOffset, track.PayloadLength, Mdia, "mdia");
			var handler = RequireChild(moov, media.PayloadOffset, media.PayloadLength, Hdlr, "hdlr");
			var handlerType = ReadHandlerType(moov, handler);
			var mediaHeader = RequireChild(moov, media.PayloadOffset, media.PayloadLength, Mdhd, "mdhd");
			var mediaTimescale = ReadTimescale(moov, mediaHeader, "mdhd");
			var mediaInformation = RequireChild(moov, media.PayloadOffset, media.PayloadLength, Minf, "minf");
			ValidateDataReference(moov, mediaInformation);
            var sampleTable = RequireChild(moov, mediaInformation.PayloadOffset, mediaInformation.PayloadLength, Stbl, "stbl");
			ValidateEditList(moov, track, ReadTimescale(moov, movieHeader, "mvhd"), mediaTimescale,
                duration, ReadPresentationEnd(moov, sampleTable, mediaTimescale, ReadDuration(moov, mediaHeader, "mdhd")));
            var sampleDescription = RequireChild(moov, sampleTable.PayloadOffset, sampleTable.PayloadLength, Stsd, "stsd");

			if (handlerType == Vide)
			{
				if (result is not null) throw new InvalidDataException("Multiple video tracks are not supported.");
                ValidateTrackMatrix(moov, track);
                var videoEntry = ReadFirstSampleEntry(moov, sampleDescription);
				(width, height) = ReadVisualSampleEntryDimensions(moov, videoEntry);
				var timing = RequireChild(moov, sampleTable.PayloadOffset, sampleTable.PayloadLength, Stts, "stts");
				(frameRateNumerator, frameRateDenominator) = ReadFrameRate(moov, timing, mediaTimescale);
                result = ReadVideoDetails(moov, videoEntry, new LocalMediaStreamMetadata(container,
                    width.Value, height.Value, frameRateNumerator.Value, frameRateDenominator.Value, duration)
                    { VariableFrameRate = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(timing.PayloadOffset + 4, 4)) > 1 });
            }
            else if (handlerType == 0x736F756E)
            {
                if (audioEntry.Length != 0) throw new InvalidDataException("Multiple audio tracks are not supported.");
                var entry = ReadFirstSampleEntry(moov, sampleDescription);
                audioEntry = FourCc(entry.Type);
                if (container == MediaContainerFormat.QuickTimeMov &&
                    audioEntry is not ("mp4a" or "sowt"))
                    throw new LocalMediaProbeException("media.file.audio_codec_unsupported",
                        $"QuickTime audio sample entry '{audioEntry}' is not admitted.");
                if (container == MediaContainerFormat.QuickTimeMov)
                {
                    EnsurePayloadLength(entry, 28, "audio sample entry");
                    var version = BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(entry.PayloadOffset + 8, 2));
                    if (audioEntry == "sowt" &&
                        (BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(entry.PayloadOffset + 16, 2)) != 2 ||
                         BinaryPrimitives.ReadUInt16BigEndian(moov.AsSpan(entry.PayloadOffset + 18, 2)) != 16 ||
                         BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry.PayloadOffset + 24, 4)) != 48000u << 16))
                        throw new LocalMediaProbeException("media.file.audio_layout_unsupported", "QuickTime PCM requires 16-bit stereo 48 kHz.");
                    if (version == 1 && audioEntry == "mp4a")
                    {
                        EnsurePayloadLength(entry, 44, "QuickTime AAC packet description");
                        var samplesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry.PayloadOffset + 28, 4));
                        var bytesPerPacket = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry.PayloadOffset + 32, 4));
                        var bytesPerFrame = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry.PayloadOffset + 36, 4));
                        var bytesPerSample = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(entry.PayloadOffset + 40, 4));
                        if (samplesPerPacket != 1024 || bytesPerPacket != 0 || bytesPerFrame != 0 || bytesPerSample != 2)
                            throw new LocalMediaProbeException("media.file.audio_layout_unsupported", "QuickTime AAC requires the qualified 1024-sample VBR packet description.");
                    }
                    else if (version != 0) throw new LocalMediaProbeException("media.file.audio_layout_unsupported",
                        "QuickTime supports version-0 PCM/AAC or version-1 AAC with qualified packet metadata.");
                }
			}
		}

		if (width is null || height is null || frameRateNumerator is null || frameRateDenominator is null)
			throw new InvalidDataException("MP4 video metadata is incomplete.");
		if (duration <= TimeSpan.Zero)
			throw new InvalidDataException("MP4 duration must be greater than zero.");

		return result! with { AudioSampleEntry = audioEntry };
	}

	private static byte[] ReadMoovPayload(FileStream stream, MediaContainerFormat container)
	{
		var end = stream.Length;
        var boxes = 0;
        var brandValidated = false;
        while (stream.Position < end)
		{
			if (++boxes > 4096) throw new InvalidDataException("Container box count exceeds the probe limit.");
            var header = ReadStreamBoxHeader(stream, end);
            if (header.Type == 0x66747970)
            {
                if (brandValidated || header.PayloadLength < 8 || header.PayloadLength > 1024 ||
                    header.PayloadLength % 4 != 0)
                    throw new InvalidDataException("Container brands are invalid.");
                var brands = new byte[(int)header.PayloadLength];
                stream.ReadExactly(brands);
                var quickTime = false;
                var mp4 = false;
                for (var offset = 0; offset < brands.Length; offset += 4)
                {
                    if (offset == 4) continue; // minor version is not a compatible brand
                    var brand = BinaryPrimitives.ReadUInt32BigEndian(brands.AsSpan(offset, 4));
                    quickTime |= brand == 0x71742020;
                    mp4 |= brand is 0x69736F6D or >= 0x69736F32 and <= 0x69736F39 or
                        0x6D703431 or 0x6D703432 or 0x61766331 or 0x4D345620 or 0x4D534E56;
                }
                if (!quickTime && !mp4) throw new LocalMediaProbeException("media.file.container_unqualified", "No admitted MP4/QuickTime brand was found.");
                if (quickTime != (container == MediaContainerFormat.QuickTimeMov))
                    throw new LocalMediaProbeException("media.file.container_mismatch",
                        "The declared file extension and container brand do not agree.");
                brandValidated = true;
                continue;
            }
			if (header.Type == Moov)
            {
                if (!brandValidated) throw new LocalMediaProbeException("media.file.container_unqualified",
                    "A qualified ftyp brand must precede moov; legacy QuickTime without ftyp is not admitted.");
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
        var count = 0;
		while (offset < end)
		{
			if (++count > 4096) throw new InvalidDataException("Container child box count exceeds the probe limit.");
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

	private static BufferBox ReadFirstSampleEntry(byte[] buffer, BufferBox stsd)
	{
		EnsurePayloadLength(stsd, 16, "stsd");
		var entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(stsd.PayloadOffset + 4, 4));
		if (entryCount != 1)
            throw new InvalidDataException("Exactly one sample description is required.");
        var entry = ReadBufferBox(buffer, stsd.PayloadOffset + 8, stsd.Offset + stsd.Length);
        EnsurePayloadLength(entry, 8, "sample entry data reference");
        if (BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(entry.PayloadOffset + 6, 2)) != 1)
            throw new LocalMediaProbeException("media.file.external_reference_unsupported",
                "A sample entry must reference the self-contained media data.");
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

	private static (long Numerator, long Denominator) ReadFrameRate(byte[] buffer, BufferBox stts, uint timescale)
	{
		EnsurePayloadLength(stts, 16, "stts");
		var entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(stts.PayloadOffset + 4, 4));
		if (entryCount == 0)
			throw new InvalidDataException("MP4 stts contains no timing entries.");
		if (entryCount > 65536 || entryCount > (uint)((stts.PayloadLength - 8) / 8))
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


    private static void ValidateDataReference(byte[] buffer, BufferBox information)
    {
        var dinf = RequireChild(buffer, information.PayloadOffset, information.PayloadLength, 0x64696E66, "dinf");
        var dref = RequireChild(buffer, dinf.PayloadOffset, dinf.PayloadLength, 0x64726566, "dref");
        EnsurePayloadLength(dref, 20, "dref");
        var count = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(dref.PayloadOffset + 4, 4));
        var entries = Children(buffer, dref.PayloadOffset + 8, dref.PayloadLength - 8).ToArray();
        if (count != 1 || entries.Length != 1 || entries[0].Type != 0x75726C20 || entries[0].PayloadLength != 4 ||
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(entries[0].PayloadOffset, 4)) != 1)
            throw new LocalMediaProbeException("media.file.external_reference_unsupported",
                "Only a self-contained local media data reference is admitted.");
    }

    private static void SkipAvcParameters(byte[] buffer, ref int offset, int end, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (end - offset < 2) throw new InvalidDataException("AVC parameter length is truncated.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            offset += 2;
            if (length == 0 || length > end - offset) throw new InvalidDataException("AVC parameter is truncated.");
            offset += length;
        }
    }

    private static string FourCc(uint type) =>
        new string([(char)(type >> 24), (char)((type >> 16) & 255), (char)((type >> 8) & 255), (char)(type & 255)]);

    private static void ValidateTrackMatrix(byte[] buffer, BufferBox track)
    {
        var header = RequireChild(buffer, track.PayloadOffset, track.PayloadLength, 0x746B6864, "tkhd");
        EnsurePayloadLength(header, 4, "tkhd");
        var version = buffer[header.PayloadOffset];
        var matrixOffset = version switch { 0 => 40, 1 => 52,
            _ => throw new InvalidDataException("Unsupported tkhd version.") };
        EnsurePayloadLength(header, matrixOffset + 36, "tkhd matrix");
        for (var index = 0; index < 9; index++)
        {
            var expected = index is 0 or 4 ? 0x00010000u : index == 8 ? 0x40000000u : 0u;
            var actual = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(header.PayloadOffset + matrixOffset + index * 4, 4));
            if (actual != expected) throw new LocalMediaProbeException("media.file.transform_unsupported",
                "Rotated, translated or nonidentity track transforms are not qualified.");
        }
    }


    private static decimal ReadPresentationEnd(byte[] buffer, BufferBox table, uint timescale, TimeSpan decodeDuration)
    {
        if (Children(buffer, table.PayloadOffset, table.PayloadLength).Any(box => box.Type == 0x63736C67))
            throw new LocalMediaProbeException("media.file.edit_list_unsupported", "Composition shift programs are not qualified.");
        var composition = Children(buffer, table.PayloadOffset, table.PayloadLength).FirstOrDefault(box => box.Type == 0x63747473);
        if (composition.Length == 0) return (decimal)decodeDuration.Ticks / TimeSpan.TicksPerSecond;
        var timing = RequireChild(buffer, table.PayloadOffset, table.PayloadLength, Stts, "stts");
        var decodeCount = TimingEntryCount(buffer, timing);
        var compositionCount = TimingEntryCount(buffer, composition);
        var version = buffer[composition.PayloadOffset];
        if (version > 1) throw new InvalidDataException("Unsupported composition offset version.");
        uint decodeIndex = 0, compositionIndex = 0, decodeRemaining = 0, compositionRemaining = 0, delta = 0;
        long compositionOffset = 0;
        decimal decodeTime = 0, presentationEnd = decimal.MinValue;
        // Merge the bounded run tables; never expand to per-frame storage or loops.
        while (decodeIndex < decodeCount || decodeRemaining != 0)
        {
            if (decodeRemaining == 0)
            {
                var at = checked(timing.PayloadOffset + 8 + (int)decodeIndex++ * 8);
                decodeRemaining = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(at, 4));
                delta = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(at + 4, 4));
                if (decodeRemaining == 0 || delta == 0) throw new InvalidDataException("Empty decode timing run.");
            }
            if (compositionRemaining == 0)
            {
                if (compositionIndex >= compositionCount) throw new InvalidDataException("Composition/decode sample counts disagree.");
                var at = checked(composition.PayloadOffset + 8 + (int)compositionIndex++ * 8);
                compositionRemaining = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(at, 4));
                compositionOffset = version == 0
                    ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(at + 4, 4))
                    : BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(at + 4, 4));
                if (compositionRemaining == 0) throw new InvalidDataException("Empty composition timing run.");
            }
            var count = Math.Min(decodeRemaining, compositionRemaining);
            decodeTime += (decimal)count * delta;
            presentationEnd = Math.Max(presentationEnd, decodeTime + compositionOffset);
            decodeRemaining -= count; compositionRemaining -= count;
        }
        if (compositionRemaining != 0 || compositionIndex != compositionCount)
            throw new InvalidDataException("Composition/decode sample counts disagree.");
        return presentationEnd / timescale;
    }

    private static uint TimingEntryCount(byte[] buffer, BufferBox table)
    {
        EnsurePayloadLength(table, 16, "timing table");
        var count = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(table.PayloadOffset + 4, 4));
        if (count == 0 || count > 65536 || count > (uint)((table.PayloadLength - 8) / 8))
            throw new InvalidDataException("Timing table exceeds its bounded extent.");
        return count;
    }

    private static void ValidateEditList(byte[] buffer, BufferBox track, uint movieTimescale, uint mediaTimescale,
        TimeSpan movieDuration, decimal presentationEndSeconds)
    {
        var edits = Children(buffer, track.PayloadOffset, track.PayloadLength).FirstOrDefault(box => box.Type == 0x65647473);
        if (edits.Length == 0) return;
        var list = RequireChild(buffer, edits.PayloadOffset, edits.PayloadLength, 0x656C7374, "elst");
        EnsurePayloadLength(list, 8, "elst");
        var version = buffer[list.PayloadOffset];
        var count = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(list.PayloadOffset + 4, 4));
        if (count != 1 || version > 1) throw new LocalMediaProbeException("media.file.edit_list_unsupported",
            "Only one nonempty, unit-rate timeline edit is admitted.");
        var entryBytes = version == 0 ? 12 : 20;
        EnsurePayloadLength(list, 8 + entryBytes, "elst entry");
        var offset = list.PayloadOffset + 8;
        var duration = version == 0 ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4)) :
            BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(offset, 8));
        var mediaTime = version == 0 ? BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset + 4, 4)) :
            BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(offset + 8, 8));
        var rate = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset + entryBytes - 4, 4));
        if (duration == 0 || mediaTime < 0 || rate != 0x00010000 ||
            (decimal)duration / movieTimescale > (decimal)movieDuration.Ticks / TimeSpan.TicksPerSecond + 0.001m ||
            (decimal)mediaTime / mediaTimescale + (decimal)duration / movieTimescale >
                presentationEndSeconds + 0.001m)
            throw new LocalMediaProbeException("media.file.edit_list_unsupported",
                $"Timeline edit is outside the unit-rate presentation extent (start={mediaTime}/{mediaTimescale}, duration={duration}/{movieTimescale}, presentationEnd={presentationEndSeconds}s).");
    }

    private static LocalMediaStreamMetadata ReadVideoDetails(byte[] buffer, BufferBox entry, LocalMediaStreamMetadata metadata)
    {
        var name = FourCc(entry.Type);
        var codec = name switch {
            "avc1" or "avc3" => MediaVideoCodec.H264,
            "hvc1" or "hev1" => MediaVideoCodec.Hevc,
            "av01" => MediaVideoCodec.Av1, "vp09" => MediaVideoCodec.Vp9,
            "mp4v" => MediaVideoCodec.Mpeg4Part2, "vc-1" => MediaVideoCodec.Vc1,
            "jpeg" => MediaVideoCodec.Mjpeg,
            _ => throw new LocalMediaProbeException("media.file.video_codec_unsupported", $"Video sample entry '{name}' is not admitted.") };
        if (metadata.Container == MediaContainerFormat.QuickTimeMov && codec != MediaVideoCodec.H264)
            throw new LocalMediaProbeException("media.file.video_codec_unsupported", "QuickTime input is restricted to H.264.");
        EnsurePayloadLength(entry, 78, "visual sample entry");
        var children = Children(buffer, entry.PayloadOffset + 78, entry.PayloadLength - 78).ToArray();
        int? profile = null, depth = null, chroma = null;
        if (codec is MediaVideoCodec.H264 or MediaVideoCodec.Hevc)
        {
            var configType = codec == MediaVideoCodec.H264 ? 0x61766343u : 0x68766343u;
            var config = children.FirstOrDefault(box => box.Type == configType);
            if (config.Length == 0) throw new InvalidDataException("Decoder configuration is missing.");
            EnsurePayloadLength(config, codec == MediaVideoCodec.H264 ? 7 : 23, "decoder configuration");
            if (buffer[config.PayloadOffset] != 1) throw new InvalidDataException("Unsupported decoder configuration version.");
            profile = buffer[config.PayloadOffset + 1] & (codec == MediaVideoCodec.H264 ? 255 : 31);
            if (codec == MediaVideoCodec.Hevc)
            {
                chroma = buffer[config.PayloadOffset + 16] & 3;
                depth = 8 + (buffer[config.PayloadOffset + 17] & 7);
                var chromaDepth = 8 + (buffer[config.PayloadOffset + 18] & 7);
                if (profile is not (1 or 2) || chroma != 1 || depth is not (8 or 10) || depth != chromaDepth ||
                    (profile == 1 && depth != 8))
                    throw new LocalMediaProbeException("media.file.profile_unsupported", "Only HEVC Main/Main10 4:2:0 SDR is admitted.");
            }
            else
            {
                // High-10/4:2:2/4:4:4 profiles require separate conversion qualification.
                if (profile is not (66 or 77 or 88 or 100))
                    throw new LocalMediaProbeException("media.file.profile_unsupported", "Only 8-bit 4:2:0 H.264 Baseline/Main/Extended/High is admitted.");
                depth = 8; chroma = 1;
                var offset = config.PayloadOffset + 6;
                var configEnd = config.Offset + config.Length;
                var spsCount = buffer[config.PayloadOffset + 5] & 31;
                if (spsCount == 0) throw new InvalidDataException("AVC sequence parameters are missing.");
                SkipAvcParameters(buffer, ref offset, configEnd, spsCount);
                if (offset >= configEnd) throw new InvalidDataException("AVC picture parameters are missing.");
                var ppsCount = buffer[offset++];
                if (ppsCount == 0) throw new InvalidDataException("AVC picture parameters are missing.");
                SkipAvcParameters(buffer, ref offset, configEnd, ppsCount);
                if (profile == 100 && offset < configEnd)
                {
                    if (configEnd - offset < 4) throw new InvalidDataException("AVC high-profile extension is truncated.");
                    chroma = buffer[offset] & 3;
                    depth = 8 + (buffer[offset + 1] & 7);
                    if (chroma != 1 || depth != 8 || (buffer[offset + 2] & 7) != 0)
                        throw new LocalMediaProbeException("media.file.profile_unsupported", "AVC High input requires 8-bit 4:2:0.");
                }
            }
        }
        var color = "unspecified";
        foreach (var child in children)
        {
            if (child.Type == 0x70617370)
            {
                EnsurePayloadLength(child, 8, "pasp");
                var horizontal = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(child.PayloadOffset, 4));
                var vertical = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(child.PayloadOffset + 4, 4));
                if (horizontal == 0 || horizontal != vertical)
                    throw new LocalMediaProbeException("media.file.pixel_aspect_unsupported", "Non-square input pixels are not qualified.");
            }
            if (child.Type == 0x636C6170)
                throw new LocalMediaProbeException("media.file.clean_aperture_unsupported", "Clean-aperture cropping is not qualified.");
            if (child.Type == 0x636F6C72)
            {
                EnsurePayloadLength(child, 10, "colr");
                var type = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(child.PayloadOffset, 4));
                if (type is not (0x6E636C78 or 0x6E636C63))
                    throw new LocalMediaProbeException("media.file.color_unsupported", "ICC/unknown color conversion is not qualified.");
                var primaries = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(child.PayloadOffset + 4, 2));
                var transfer = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(child.PayloadOffset + 6, 2));
                var matrix = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(child.PayloadOffset + 8, 2));
                if (primaries is not (1 or 2 or 5 or 6) || transfer is not (1 or 2 or 6) || matrix is not (1 or 2 or 5 or 6))
                    throw new LocalMediaProbeException("media.file.color_unsupported", "HDR/wide-gamut color conversion is not qualified.");
                if (type == 0x6E636C78) EnsurePayloadLength(child, 11, "nclx");
                color = $"{FourCc(type)}:{primaries}/{transfer}/{matrix}";
            }
        }
        return metadata with { SampleEntry = name, VideoCodec = codec, Profile = profile,
            BitDepth = depth, ChromaFormat = chroma, ColorDescription = color };
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
