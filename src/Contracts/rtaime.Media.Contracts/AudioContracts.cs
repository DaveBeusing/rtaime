using rtaime.Core;

namespace rtaime.Media.Contracts;

public readonly record struct AudioStreamId
{
    public AudioStreamId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Audio stream identity must not be empty.", nameof(value));

        Value = value;
    }

    public Identity Value { get; }
    public static AudioStreamId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public enum AudioChannelLayout
{
    Mono = 1,
    Stereo = 2
}

public enum AudioSampleFormat
{
    PcmS16 = 1,
    Float32 = 2
}

public readonly record struct AudioFormat
{
    public AudioFormat(
        uint sampleRate,
        AudioChannelLayout channelLayout,
        AudioSampleFormat sampleFormat,
        uint channelCount)
    {
        if (sampleRate == 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Audio sample rate must be greater than zero.");
        if (!Enum.IsDefined(typeof(AudioChannelLayout), channelLayout))
            throw new ArgumentOutOfRangeException(nameof(channelLayout), "Audio channel layout must be a defined contract value.");
        if (!Enum.IsDefined(typeof(AudioSampleFormat), sampleFormat))
            throw new ArgumentOutOfRangeException(nameof(sampleFormat), "Audio sample format must be a defined contract value.");
        if (channelCount == 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Audio channel count must be greater than zero.");
        if (channelLayout == AudioChannelLayout.Mono && channelCount != 1)
            throw new ArgumentException("Mono layout requires exactly one channel.", nameof(channelCount));
        if (channelLayout == AudioChannelLayout.Stereo && channelCount != 2)
            throw new ArgumentException("Stereo layout requires exactly two channels.", nameof(channelCount));

        SampleRate = sampleRate;
        ChannelLayout = channelLayout;
        SampleFormat = sampleFormat;
        ChannelCount = channelCount;
    }

    public uint SampleRate { get; }
    public AudioChannelLayout ChannelLayout { get; }
    public AudioSampleFormat SampleFormat { get; }
    public uint ChannelCount { get; }

    public static AudioFormat Stereo48kFloat32 =>
        new(48_000, AudioChannelLayout.Stereo, AudioSampleFormat.Float32, 2);
}

public sealed record OpaqueAudioHandle
{
    public OpaqueAudioHandle(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Audio handle kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio handle value is required.", nameof(value));

        Kind = kind.Trim();
        Value = value.Trim();
    }

    public string Kind { get; }
    public string Value { get; }
}

public sealed record AudioStreamDescriptor
{
    public AudioStreamDescriptor(
        CompatibilityVersion version,
        AudioStreamId streamId,
        MediaSourceId followedVideoSourceId,
        AudioFormat format,
        Identity timingDomainId)
    {
        MediaContractVersion.EnsureSupported(version);
        if (timingDomainId.IsEmpty)
            throw new ArgumentException("Audio timing domain identity must not be empty.", nameof(timingDomainId));

        Version = version;
        StreamId = streamId;
        FollowedVideoSourceId = followedVideoSourceId;
        Format = format;
        TimingDomainId = timingDomainId;
    }

    public CompatibilityVersion Version { get; }
    public AudioStreamId StreamId { get; }
    public MediaSourceId FollowedVideoSourceId { get; }
    public AudioFormat Format { get; }
    public Identity TimingDomainId { get; }
}

public readonly record struct AudioBufferTiming
{
    public AudioBufferTiming(
        ulong samplePosition,
        uint sampleCount,
        long presentationTimestamp,
        Timebase timebase)
    {
        if (sampleCount == 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "Audio buffer sample count must be greater than zero.");

        SamplePosition = samplePosition;
        SampleCount = sampleCount;
        PresentationTimestamp = presentationTimestamp;
        Timebase = timebase;
    }

    public ulong SamplePosition { get; }
    public uint SampleCount { get; }
    public long PresentationTimestamp { get; }
    public Timebase Timebase { get; }
}

public sealed record AudioBufferDescriptor
{
    public AudioBufferDescriptor(
        CompatibilityVersion version,
        AudioStreamId streamId,
        AudioFormat format,
        Identity timingDomainId,
        AudioBufferTiming timing,
        OpaqueAudioHandle? handle)
    {
        MediaContractVersion.EnsureSupported(version);
        if (timingDomainId.IsEmpty)
            throw new ArgumentException("Audio timing domain identity must not be empty.", nameof(timingDomainId));

        Version = version;
        StreamId = streamId;
        Format = format;
        TimingDomainId = timingDomainId;
        Timing = timing;
        Handle = handle;
    }

    public CompatibilityVersion Version { get; }
    public AudioStreamId StreamId { get; }
    public AudioFormat Format { get; }
    public Identity TimingDomainId { get; }
    public AudioBufferTiming Timing { get; }
    public OpaqueAudioHandle? Handle { get; }
}
