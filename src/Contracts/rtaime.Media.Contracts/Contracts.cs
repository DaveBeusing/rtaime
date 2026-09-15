using rtaime.Core;

namespace rtaime.Media.Contracts;

public static class MediaContractVersion
{
    public static CompatibilityVersion Current { get; } = new(1, 0);

    public static bool IsSupported(CompatibilityVersion version) => version == Current;

    public static void EnsureSupported(CompatibilityVersion version)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"Unsupported Media contract version '{version}'. Supported version is '{Current}'.");
    }
}

public readonly record struct MediaSourceId
{
    public MediaSourceId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Media source identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static MediaSourceId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct MediaSinkId
{
    public MediaSinkId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Media sink identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static MediaSinkId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct SurfaceId
{
    public SurfaceId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Surface identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static SurfaceId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public enum PixelFormat
{
    Rgba8 = 1
}

public enum ScanMode
{
    Progressive = 1
}

public readonly record struct VideoFormat
{
    public VideoFormat(uint width, uint height, FrameRate frameRate, PixelFormat pixelFormat, ScanMode scanMode)
    {
        if (width == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Video width must be greater than zero.");
        if (height == 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Video height must be greater than zero.");
        if (!Enum.IsDefined(typeof(PixelFormat), pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat), "Pixel format must be a defined contract value.");
        if (!Enum.IsDefined(typeof(ScanMode), scanMode))
            throw new ArgumentOutOfRangeException(nameof(scanMode), "Scan mode must be a defined contract value.");

        Width = width;
        Height = height;
        FrameRate = frameRate;
        PixelFormat = pixelFormat;
        ScanMode = scanMode;
    }

    public uint Width { get; }
    public uint Height { get; }
    public FrameRate FrameRate { get; }
    public PixelFormat PixelFormat { get; }
    public ScanMode ScanMode { get; }

    public static VideoFormat Hd1080p50Rgba8 => new(1920, 1080, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
    public static VideoFormat Hd1080p59_94Rgba8 => new(1920, 1080, FrameRate.Fps59_94, PixelFormat.Rgba8, ScanMode.Progressive);
}

public readonly record struct FrameTiming
{
    public FrameTiming(ulong sequenceNumber, long presentationTimestamp, Timebase timebase)
    {
        SequenceNumber = sequenceNumber;
        PresentationTimestamp = presentationTimestamp;
        Timebase = timebase;
    }

    public ulong SequenceNumber { get; }
    public long PresentationTimestamp { get; }
    public Timebase Timebase { get; }
}

public enum SurfaceStorageDomain
{
    Host = 1,
    Device = 2,
    Shared = 3
}

public enum SurfaceOwnership
{
    ProducerOwned = 1,
    ConsumerOwned = 2,
    SharedLease = 3
}

public readonly record struct SurfaceLifetimeDescriptor
{
    public SurfaceLifetimeDescriptor(Generation generation, Identity? leaseId)
    {
        Generation = generation;
        LeaseId = leaseId;
    }

    public Generation Generation { get; }
    public Identity? LeaseId { get; }
}

public sealed record OpaqueSurfaceHandle
{
    public OpaqueSurfaceHandle(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Surface handle kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Surface handle value is required.", nameof(value));

        Kind = kind.Trim();
        Value = value.Trim();
    }

    public string Kind { get; }
    public string Value { get; }
}

public sealed record SurfaceDescriptor
{
    public SurfaceDescriptor(
        SurfaceId surfaceId,
        VideoFormat format,
        SurfaceStorageDomain storageDomain,
        SurfaceOwnership ownership,
        SurfaceLifetimeDescriptor lifetime,
        OpaqueSurfaceHandle? handle)
    {
        if (!Enum.IsDefined(typeof(SurfaceStorageDomain), storageDomain))
            throw new ArgumentOutOfRangeException(nameof(storageDomain), "Surface storage domain must be a defined contract value.");
        if (!Enum.IsDefined(typeof(SurfaceOwnership), ownership))
            throw new ArgumentOutOfRangeException(nameof(ownership), "Surface ownership must be a defined contract value.");

        SurfaceId = surfaceId;
        Format = format;
        StorageDomain = storageDomain;
        Ownership = ownership;
        Lifetime = lifetime;
        Handle = handle;
    }

    public SurfaceId SurfaceId { get; }
    public VideoFormat Format { get; }
    public SurfaceStorageDomain StorageDomain { get; }
    public SurfaceOwnership Ownership { get; }
    public SurfaceLifetimeDescriptor Lifetime { get; }
    public OpaqueSurfaceHandle? Handle { get; }
}

public sealed record FrameDescriptor
{
    public FrameDescriptor(
        CompatibilityVersion version,
        MediaSourceId sourceId,
        SurfaceDescriptor surface,
        FrameTiming timing)
    {
        MediaContractVersion.EnsureSupported(version);
        Version = version;
        SourceId = sourceId;
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Timing = timing;
    }

    public CompatibilityVersion Version { get; }
    public MediaSourceId SourceId { get; }
    public SurfaceDescriptor Surface { get; }
    public FrameTiming Timing { get; }
}
