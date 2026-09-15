using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.VirtualMedia;

public static class VirtualMediaCapabilityKinds
{
    public const string MediaRoute = "media.route";
}

public sealed class VirtualTimingProvider
{
    public VirtualTimingProvider(VideoFormat format)
    {
        if (!VirtualMediaReferenceProvider.IsSupportedFormat(format))
            throw new ArgumentException("Virtual timing supports only the V1 development formats 1080p50 and 1080p59.94.", nameof(format));

        Format = format;
        FrameTimebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
    }

    public VideoFormat Format { get; }
    public Timebase FrameTimebase { get; }

    public FrameTiming GetFrameTiming(ulong sequenceNumber)
    {
        if (sequenceNumber > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Virtual frame timestamp exceeds Int64 range.");

        return new FrameTiming(
            sequenceNumber,
            checked((long)sequenceNumber),
            FrameTimebase);
    }
}

public sealed class VirtualSyntheticVideoSource
{
    private readonly VirtualTimingProvider _timing;

    internal VirtualSyntheticVideoSource(
        MediaSourceId sourceId,
        string name,
        VideoFormat format,
        VirtualTimingProvider timing)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Virtual source name is required.", nameof(name));

        SourceId = sourceId;
        Name = name.Trim();
        Format = format;
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
    }

    public MediaSourceId SourceId { get; }
    public string Name { get; }
    public VideoFormat Format { get; }

    public FrameDescriptor GenerateFrame(ulong sequenceNumber)
    {
        var surfaceId = new SurfaceId(VirtualMediaIdentity.Create(
            "virtual-surface",
            SourceId.ToString(),
            Format.Width.ToString(),
            Format.Height.ToString(),
            Format.FrameRate.ToString(),
            sequenceNumber.ToString()));

        var surface = new SurfaceDescriptor(
            surfaceId,
            Format,
            SurfaceStorageDomain.Host,
            SurfaceOwnership.ProducerOwned,
            new SurfaceLifetimeDescriptor(new Generation(sequenceNumber), null),
            new OpaqueSurfaceHandle("virtual.synthetic.frame", surfaceId.ToString()));

        return new FrameDescriptor(
            MediaContractVersion.Current,
            SourceId,
            surface,
            _timing.GetFrameTiming(sequenceNumber));
    }
}

public sealed record VirtualOutputFrame(MediaSinkId SinkId, FrameDescriptor Frame);

public sealed class VirtualVideoOutput
{
    private readonly object _gate = new();
    private readonly List<VirtualOutputFrame> _frames = new();

    internal VirtualVideoOutput(MediaSinkId sinkId, VideoFormat format)
    {
        SinkId = sinkId;
        Format = format;
    }

    public MediaSinkId SinkId { get; }
    public VideoFormat Format { get; }

    public IReadOnlyList<VirtualOutputFrame> Frames
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<VirtualOutputFrame>(_frames.ToArray());
        }
    }

    public VirtualOutputFrame? LastFrame
    {
        get
        {
            lock (_gate)
                return _frames.Count == 0 ? null : _frames[^1];
        }
    }

    public void WriteFrame(FrameDescriptor frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            if (frame.Surface.Format != Format)
                throw new InvalidOperationException("Virtual output frame format does not match the configured output format.");

            if (_frames.Count > 0)
            {
                var previous = _frames[^1].Frame.Timing.SequenceNumber;
                if (previous == ulong.MaxValue || frame.Timing.SequenceNumber != previous + 1)
                {
                    throw new InvalidOperationException(
                        $"Virtual output requires continuous frame sequence numbers. Previous '{previous}', incoming '{frame.Timing.SequenceNumber}'.");
                }
            }

            _frames.Add(new VirtualOutputFrame(SinkId, frame));
        }
    }
}

public sealed class VirtualMediaReferenceProvider
{
    private static readonly VideoFormat[] SupportedFormats =
    {
        VideoFormat.Hd1080p50Rgba8,
        VideoFormat.Hd1080p59_94Rgba8
    };

    private readonly ReadOnlyCollection<VirtualSyntheticVideoSource> _sources;

    public VirtualMediaReferenceProvider(
        MediaSourceId sourceAId,
        MediaSourceId sourceBId,
        VideoFormat format)
    {
        if (sourceAId == sourceBId)
            throw new ArgumentException("Virtual source identities must be distinct.", nameof(sourceBId));
        if (!IsSupportedFormat(format))
            throw new ArgumentException("Virtual media supports only the V1 development formats 1080p50 and 1080p59.94.", nameof(format));

        Format = format;
        Timing = new VirtualTimingProvider(format);
        SourceA = new VirtualSyntheticVideoSource(sourceAId, "Source A", format, Timing);
        SourceB = new VirtualSyntheticVideoSource(sourceBId, "Source B", format, Timing);
        _sources = Array.AsReadOnly(new[] { SourceA, SourceB });
        Descriptor = CreateDescriptor();
    }

    public VideoFormat Format { get; }
    public VirtualTimingProvider Timing { get; }
    public VirtualSyntheticVideoSource SourceA { get; }
    public VirtualSyntheticVideoSource SourceB { get; }
    public IReadOnlyList<VirtualSyntheticVideoSource> Sources => _sources;
    public ProviderDescriptor Descriptor { get; }

    public VirtualVideoOutput CreateOutput(MediaSinkId sinkId) => new(sinkId, Format);

    public static bool IsSupportedFormat(VideoFormat format) => SupportedFormats.Contains(format);

    private static ProviderDescriptor CreateDescriptor()
    {
        var providerId = new ProviderId(VirtualMediaIdentity.Create("virtual-provider", "reference-media"));
        var capabilityId = new CapabilityId(VirtualMediaIdentity.Create("virtual-capability", VirtualMediaCapabilityKinds.MediaRoute));

        var capability = new ProviderCapabilityDescriptor(
            capabilityId,
            VirtualMediaCapabilityKinds.MediaRoute,
            SupportedFormats);

        var resources = new[]
        {
            new ProviderResourceDescriptor(
                new ProviderResourceId(VirtualMediaIdentity.Create("virtual-resource", "media-route", "0")),
                providerId,
                VirtualMediaCapabilityKinds.MediaRoute,
                1,
                true),
            new ProviderResourceDescriptor(
                new ProviderResourceId(VirtualMediaIdentity.Create("virtual-resource", "media-route", "1")),
                providerId,
                VirtualMediaCapabilityKinds.MediaRoute,
                1,
                true)
        };

        return new ProviderDescriptor(
            ProviderContractVersion.Current,
            providerId,
            "rtaime Virtual Media Reference Provider",
            new ProviderAvailability(ProviderAvailabilityState.Available),
            new[] { capability },
            resources);
    }
}

internal static class VirtualMediaIdentity
{
    public static Identity Create(string scope, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Virtual media identity scope is required.", nameof(scope));
        ArgumentNullException.ThrowIfNull(parts);

        var canonical = string.Join('\u001f', new[] { scope }.Concat(parts));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var guidText = Convert.ToHexString(hash.AsSpan(0, 16));
        return new Identity(Guid.ParseExact(guidText, "N"));
    }
}
