// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Media;

internal enum LocalMediaSampleDisposition { Gap, Frame, Ended }

// One operation budget includes empty callbacks and key-frame seek scanning.
// It bounds managed progress, not time spent inside an unresponsive COM call.
internal sealed class LocalMediaSampleProgress
{
    internal const int MaximumReads = 4096;
    internal const int MaximumEmptyReads = 128;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private int _reads;
    private int _empty;
    private long? _lastSkippedTimestamp;

    internal LocalMediaSampleProgress(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
    }

    internal TimeSpan Remaining
    {
        get
        {
            var remaining = TimeSpan.FromSeconds(30) - _clock.GetElapsedTime(_started);
            if (remaining <= TimeSpan.Zero)
                throw new LocalMediaDecodeException("media.file.read_timeout", "Local media operation exceeded its progress deadline.");
            return remaining;
        }
    }

    internal TimeSpan BeforeRead(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++_reads > MaximumReads)
            throw new LocalMediaDecodeException("media.file.no_progress", "Local media seek/read exceeded its sample budget.");
        return Remaining;
    }

    internal LocalMediaSampleDisposition Observe(uint flags, long timestamp, bool hasSample, long? minimumTimestamp)
    {
        // MF_SOURCE_READER_FLAG: ERROR, NEWSTREAM, NATIVEMEDIATYPECHANGED,
        // CURRENTMEDIATYPECHANGED, STREAMTICK, ALLEFFECTSREMOVED.
        if ((flags & 0x1) != 0)
            throw new LocalMediaDecodeException("media.file.decoder_lost", "Media Foundation reported a terminal reader error.");
        if ((flags & (0x4 | 0x10 | 0x20 | 0x200)) != 0)
            throw new LocalMediaDecodeException("media.file.decoded_type_changed", "Decoded stream layout changed; close and reopen the asset.");
        if ((flags & ~(0x1u | 0x2u | 0x4u | 0x10u | 0x20u | 0x100u | 0x200u)) != 0)
            throw new LocalMediaDecodeException("media.file.stream_flags_invalid", "Decoder returned unknown stream flags.");
        if (!hasSample)
        {
            if ((flags & 0x2) != 0) return LocalMediaSampleDisposition.Ended;
            if (++_empty >= MaximumEmptyReads)
                throw new LocalMediaDecodeException("media.file.no_progress", "Decoder repeatedly returned no sample without end of stream.");
            return LocalMediaSampleDisposition.Gap;
        }
        _empty = 0;
        if (minimumTimestamp is { } minimum && timestamp < minimum)
        {
            if (_lastSkippedTimestamp is { } previous && timestamp <= previous)
                throw new LocalMediaDecodeException("media.file.timestamp_discontinuity", "Seek scanning did not advance its sample timestamp.");
            _lastSkippedTimestamp = timestamp;
            return LocalMediaSampleDisposition.Gap;
        }
        return LocalMediaSampleDisposition.Frame;
    }

    internal static string NativeFailureCode(int status) => unchecked((uint)status) switch
    {
        // Win32 file/path/access, device-not-ready, sharing, disk-full and I/O failures.
        0x80070002 or 0x80070003 or 0x80070005 or 0x80070015 or
        0x80070017 or 0x80070020 or 0x80070021 or 0x80070070 or
        0x8007045D or 0x8007048F or 0x80070570 => "media.file.storage_failure",
        _ => "media.file.decoder_lost"
    };

    internal static int ValidatePcmLength(uint length)
    {
        // 48-kHz stereo PCM16: at most one second in a retained native packet.
        if (length == 0 || length > 192_000 || length % 4 != 0)
            throw new LocalMediaDecodeException("media.file.audio_payload_invalid", "Decoded PCM packet exceeds its bounded aligned extent.");
        return checked((int)length);
    }
}

internal sealed class LocalMediaDecodeException : IOException
{
    internal LocalMediaDecodeException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
    internal string Code { get; }
}
