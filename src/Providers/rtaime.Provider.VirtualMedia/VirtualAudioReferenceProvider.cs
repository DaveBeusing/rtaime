using System.Collections.ObjectModel;
using System.Numerics;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Provider.VirtualMedia;

public sealed record VirtualAudioPacket(AudioBufferDescriptor Descriptor, double PeakLevel);

public sealed class VirtualSyntheticAudioSource
{
    private readonly FrameRate _videoFrameRate;
    private readonly double _peakLevel;

    internal VirtualSyntheticAudioSource(
        AudioStreamDescriptor descriptor,
        FrameRate videoFrameRate,
        double peakLevel)
    {
        if (!double.IsFinite(peakLevel) || peakLevel < 0 || peakLevel > 1)
            throw new ArgumentOutOfRangeException(nameof(peakLevel));

        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _videoFrameRate = videoFrameRate;
        _peakLevel = peakLevel;
    }

    public AudioStreamDescriptor Descriptor { get; }

    public VirtualAudioPacket GeneratePacket(ulong videoFrameSequence)
    {
        var timing = GetTiming(videoFrameSequence);
        var handleIdentity = VirtualMediaIdentity.Create(
            "virtual-audio-buffer",
            Descriptor.StreamId.ToString(),
            videoFrameSequence.ToString());

        var buffer = new AudioBufferDescriptor(
            MediaContractVersion.Current,
            Descriptor.StreamId,
            Descriptor.Format,
            Descriptor.TimingDomainId,
            timing,
            new OpaqueAudioHandle("virtual.synthetic.audio", handleIdentity.ToString()));

        return new VirtualAudioPacket(buffer, _peakLevel);
    }

    private AudioBufferTiming GetTiming(ulong videoFrameSequence)
    {
        var denominator = new BigInteger(_videoFrameRate.Numerator);
        var rateFactor = new BigInteger(Descriptor.Format.SampleRate) * _videoFrameRate.Denominator;
        var start = new BigInteger(videoFrameSequence) * rateFactor / denominator;
        var end = (new BigInteger(videoFrameSequence) + BigInteger.One) * rateFactor / denominator;
        var count = end - start;

        if (start < BigInteger.Zero || start > long.MaxValue || start > ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(videoFrameSequence));
        if (count <= BigInteger.Zero || count > uint.MaxValue)
            throw new InvalidOperationException("Virtual audio sample window exceeds supported range.");

        return new AudioBufferTiming(
            (ulong)start,
            (uint)count,
            (long)start,
            new Timebase(1, Descriptor.Format.SampleRate));
    }
}

public sealed class VirtualEmbeddedAudioReferenceProvider
{
    private readonly ReadOnlyCollection<VirtualSyntheticAudioSource> _sources;
    private readonly Dictionary<MediaSourceId, VirtualSyntheticAudioSource> _sourceByVideoSource;

    public VirtualEmbeddedAudioReferenceProvider(
        MediaSourceId sourceAId,
        MediaSourceId sourceBId,
        VideoFormat videoFormat,
        AudioFormat? audioFormat = null)
    {
        if (sourceAId == sourceBId)
            throw new ArgumentException("Virtual audio source identities must be distinct.", nameof(sourceBId));
        if (!VirtualMediaReferenceProvider.IsSupportedFormat(videoFormat))
            throw new ArgumentException("Virtual embedded audio supports only V1 development video formats.", nameof(videoFormat));

        VideoFormat = videoFormat;
        AudioFormat = audioFormat ?? AudioFormat.Stereo48kFloat32;
        TimingDomainId = VirtualMediaIdentity.Create(
            "virtual-av-timing-domain",
            videoFormat.FrameRate.ToString(),
            AudioFormat.SampleRate.ToString());

        SourceA = CreateSource(sourceAId, "A", 0.25);
        SourceB = CreateSource(sourceBId, "B", 0.75);
        _sources = Array.AsReadOnly(new[] { SourceA, SourceB });
        _sourceByVideoSource = _sources.ToDictionary(source => source.Descriptor.FollowedVideoSourceId);
    }

    public VideoFormat VideoFormat { get; }
    public AudioFormat AudioFormat { get; }
    public Identity TimingDomainId { get; }
    public VirtualSyntheticAudioSource SourceA { get; }
    public VirtualSyntheticAudioSource SourceB { get; }
    public IReadOnlyList<VirtualSyntheticAudioSource> Sources => _sources;
    public IReadOnlyList<AudioStreamDescriptor> Streams => _sources.Select(source => source.Descriptor).ToArray();

    public VirtualSyntheticAudioSource GetSource(MediaSourceId videoSourceId) =>
        _sourceByVideoSource.TryGetValue(videoSourceId, out var source)
            ? source
            : throw new KeyNotFoundException($"Unknown virtual audio video source '{videoSourceId}'.");

    private VirtualSyntheticAudioSource CreateSource(
        MediaSourceId videoSourceId,
        string suffix,
        double peakLevel)
    {
        var streamId = new AudioStreamId(VirtualMediaIdentity.Create(
            "virtual-audio-stream",
            videoSourceId.ToString(),
            suffix));
        var descriptor = new AudioStreamDescriptor(
            MediaContractVersion.Current,
            streamId,
            videoSourceId,
            AudioFormat,
            TimingDomainId);

        return new VirtualSyntheticAudioSource(descriptor, VideoFormat.FrameRate, peakLevel);
    }
}
