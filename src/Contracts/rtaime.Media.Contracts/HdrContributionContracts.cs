// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using rtaime.Core;

namespace rtaime.Media.Contracts;

/// <summary>
/// Independent native-precision descriptors. Existing VideoFormat, PixelFormat,
/// MediaContractVersion and MonitoringFrameWire remain unchanged and RGBA8-only.
/// None of these values grants a live GPU, decoder, recorder or output capability.
/// </summary>
public enum HdrNativePixelLayout
{
    PlanarYcbcr422P10Le = 1,
    P010Ycbcr420Le = 2,
    LinearRgbaHalfLe = 3
}

public enum HdrChromaSampleLocation
{
    LeftCosited = 1,
    Centered = 2
}

public enum HdrPrecisionAlphaMode
{
    Opaque = 1,
    Straight = 2,
    PremultipliedLinear = 3
}

/// <summary>
/// CICP BT.2020 primaries=9, HLG transfer=18, BT.2020 nonconstant matrix=9.
/// HLG does not infer mastering metadata, calibrated display luminance or SDR maps.
/// </summary>
public sealed record HdrSignalMetadata
{
    public const byte Bt2020Primaries = 9;
    public const byte Bt2100HlgTransfer = 18;
    public const byte Bt2020NonConstantMatrix = 9;

    public HdrSignalMetadata(byte primariesCicp, byte transferCicp, byte matrixCicp,
        bool limitedRange, byte bitDepth, HdrChromaSampleLocation chromaLocation,
        HdrPrecisionAlphaMode alpha, ColorMetadataAuthority authority,
        string provenance)
    {
        if (!Enum.IsDefined(chromaLocation) || !Enum.IsDefined(alpha) ||
            !Enum.IsDefined(authority))
            throw new ArgumentOutOfRangeException(nameof(authority));
        if (provenance is null || provenance.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(provenance));
        PrimariesCicp = primariesCicp;
        TransferCicp = transferCicp;
        MatrixCicp = matrixCicp;
        LimitedRange = limitedRange;
        BitDepth = bitDepth;
        ChromaLocation = chromaLocation;
        Alpha = alpha;
        Authority = authority;
        Provenance = provenance;
    }

    public byte PrimariesCicp { get; }
    public byte TransferCicp { get; }
    public byte MatrixCicp { get; }
    public bool LimitedRange { get; }
    public byte BitDepth { get; }
    public HdrChromaSampleLocation ChromaLocation { get; }
    public HdrPrecisionAlphaMode Alpha { get; }
    public ColorMetadataAuthority Authority { get; }
    public string Provenance { get; }

    public bool IsAuthoritativeHlg422 =>
        PrimariesCicp == Bt2020Primaries &&
        TransferCicp == Bt2100HlgTransfer &&
        MatrixCicp == Bt2020NonConstantMatrix &&
        LimitedRange &&
        BitDepth == 10 &&
        ChromaLocation == HdrChromaSampleLocation.LeftCosited &&
        Alpha == HdrPrecisionAlphaMode.Opaque &&
        Authority == ColorMetadataAuthority.Authoritative &&
        !string.IsNullOrWhiteSpace(Provenance);

    public static HdrSignalMetadata AuthoritativeHlg422(string provenance) =>
        new(Bt2020Primaries, Bt2100HlgTransfer, Bt2020NonConstantMatrix,
            true, 10, HdrChromaSampleLocation.LeftCosited,
            HdrPrecisionAlphaMode.Opaque, ColorMetadataAuthority.Authoritative,
            provenance);
}

public sealed record HdrContributionVideoProfile
{
    public HdrContributionVideoProfile(uint width, uint height, FrameRate frameRate,
        HdrNativePixelLayout layout, HdrSignalMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (width == 0 || height == 0 || !Enum.IsDefined(layout))
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        FrameRate = frameRate;
        Layout = layout;
        Metadata = metadata;
    }

    public uint Width { get; }
    public uint Height { get; }
    public FrameRate FrameRate { get; }
    public HdrNativePixelLayout Layout { get; }
    public HdrSignalMetadata Metadata { get; }

    public bool IsExactR153LiveHlg =>
        Width == 3840 && Height == 2160 &&
        (FrameRate == FrameRate.Fps50 || FrameRate == FrameRate.Fps59_94) &&
        Layout == HdrNativePixelLayout.PlanarYcbcr422P10Le &&
        Metadata.IsAuthoritativeHlg422;
}

/// <summary>Only exact R 153 live raster/cadence candidates; not provider support.</summary>
public static class HdrContributionProfiles
{
    public static HdrContributionVideoProfile Live2160p50(string provenance) =>
        new(3840, 2160, FrameRate.Fps50, HdrNativePixelLayout.PlanarYcbcr422P10Le,
            HdrSignalMetadata.AuthoritativeHlg422(provenance));

    public static HdrContributionVideoProfile Live2160p59_94(string provenance) =>
        new(3840, 2160, FrameRate.Fps59_94,
            HdrNativePixelLayout.PlanarYcbcr422P10Le,
            HdrSignalMetadata.AuthoritativeHlg422(provenance));
}

/// <summary>
/// Planar samples use little-endian 16-bit words, high 10 significant bits,
/// low six bits zero. R 153 contribution samples are 4:2:2 Y/Cb/Cr.
/// P010 (4:2:0) is a separate ingest/intermediate candidate, not R 153 4:2:2.
/// Linear RGBA Half is an internal high-precision target, not a contribution wire format.
/// </summary>
public readonly record struct HdrPlaneLayout(
    uint Width, uint Height, int BytesPerSampleGroup,
    long RowPitchBytes, long OffsetBytes)
{
    public long ActiveRowBytes => checked((long)Width * BytesPerSampleGroup);
    public long ReservedBytes => checked(RowPitchBytes * Height);
}

public sealed class HdrNativeFrameLayout
{
    private readonly ReadOnlyCollection<HdrPlaneLayout> _planes;
    public const long MaximumReferenceFrameBytes = 512L * 1024 * 1024;

    public HdrNativeFrameLayout(uint width, uint height, HdrNativePixelLayout pixelLayout,
        IReadOnlyList<HdrPlaneLayout> planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        if (!Enum.IsDefined(pixelLayout) ||
            width == 0 || height == 0 || width > 8192 || height > 4320 ||
            (width & 1) != 0 ||
            (pixelLayout == HdrNativePixelLayout.P010Ycbcr420Le && (height & 1) != 0))
            throw new ArgumentOutOfRangeException(nameof(width));

        var spec = RequiredPlanes(width, height, pixelLayout);
        if (planes.Count != spec.Length)
            throw new ArgumentException("Native precision plane count does not match the layout.", nameof(planes));

        long previousEnd = 0;
        for (var i = 0; i < planes.Count; i++)
        {
            var plane = planes[i];
            if (plane.Width != spec[i].Width || plane.Height != spec[i].Height ||
                plane.BytesPerSampleGroup != spec[i].BytesPerSampleGroup ||
                plane.RowPitchBytes < plane.ActiveRowBytes ||
                (plane.RowPitchBytes & 1) != 0 ||
                plane.OffsetBytes < previousEnd || (plane.OffsetBytes & 1) != 0)
                throw new ArgumentException("Invalid, overlapping or undersized native precision plane.", nameof(planes));
            try
            {
                previousEnd = checked(plane.OffsetBytes + plane.ReservedBytes);
            }
            catch (OverflowException e)
            {
                throw new ArgumentException("Native plane storage size overflow.", nameof(planes), e);
            }
            if (previousEnd > MaximumReferenceFrameBytes)
                throw new ArgumentOutOfRangeException(nameof(planes), "Native reference frame exceeds the bounded layout budget.");
        }
        Width = width;
        Height = height;
        PixelLayout = pixelLayout;
        _planes = Array.AsReadOnly(planes.ToArray());
        RequiredBytes = previousEnd;
    }

    public uint Width { get; }
    public uint Height { get; }
    public HdrNativePixelLayout PixelLayout { get; }
    public IReadOnlyList<HdrPlaneLayout> Planes => _planes;
    public long RequiredBytes { get; }

    public bool ExactlyMatches(HdrNativeFrameLayout other) =>
        other is not null && Width == other.Width && Height == other.Height &&
        PixelLayout == other.PixelLayout && _planes.SequenceEqual(other._planes);

    public static HdrNativeFrameLayout TightlyPacked(uint width, uint height,
        HdrNativePixelLayout layout)
    {
        var spec = RequiredPlanes(width, height, layout);
        var planes = new HdrPlaneLayout[spec.Length];
        long offset = 0;
        for (var i = 0; i < spec.Length; i++)
        {
            var row = checked((long)spec[i].Width * spec[i].BytesPerSampleGroup);
            planes[i] = new(spec[i].Width, spec[i].Height,
                spec[i].BytesPerSampleGroup, row, offset);
            offset = checked(offset + row * spec[i].Height);
        }
        return new HdrNativeFrameLayout(width, height, layout, planes);
    }

    private static HdrPlaneLayout[] RequiredPlanes(uint width, uint height,
        HdrNativePixelLayout layout)
    {
        if (width == 0 || height == 0 || (width & 1) != 0 ||
            (layout == HdrNativePixelLayout.P010Ycbcr420Le && (height & 1) != 0))
            throw new ArgumentOutOfRangeException(nameof(width));
        return layout switch
        {
            HdrNativePixelLayout.PlanarYcbcr422P10Le =>
            [
                new(width, height, 2, 0, 0),
                new(width / 2, height, 2, 0, 0),
                new(width / 2, height, 2, 0, 0)
            ],
            HdrNativePixelLayout.P010Ycbcr420Le =>
            [
                new(width, height, 2, 0, 0),
                new(width / 2, height / 2, 4, 0, 0)
            ],
            HdrNativePixelLayout.LinearRgbaHalfLe =>
            [
                new(width, height, 8, 0, 0)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
    }
}
