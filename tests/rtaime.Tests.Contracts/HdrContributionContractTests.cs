// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class HdrContributionContractTests
{
    [Theory]
    [InlineData(50, 1)]
    [InlineData(60000, 1001)]
    public void Exact_R153_2160p_HLG_422_requires_source_bound_CICP_and_exact_cadence(
        long numerator, long denominator)
    {
        var frameRate = new FrameRate(numerator, denominator);
        var profile = new HdrContributionVideoProfile(3840, 2160, frameRate,
            HdrNativePixelLayout.PlanarYcbcr422P10Le,
            HdrSignalMetadata.AuthoritativeHlg422("native-vendor-metadata"));
        Assert.True(profile.IsExactR153LiveHlg);
        Assert.Equal(9, profile.Metadata.PrimariesCicp);
        Assert.Equal(18, profile.Metadata.TransferCicp);
        Assert.Equal(9, profile.Metadata.MatrixCicp);
        Assert.True(profile.Metadata.LimitedRange);
        Assert.Equal(10, profile.Metadata.BitDepth);
    }

    [Fact]
    public void Unknown_SDR_PQ_420_and_file_only_25p_must_not_become_R153_live()
    {
        var correct = HdrContributionProfiles.Live2160p50("trusted-source");
        Assert.True(correct.IsExactR153LiveHlg);
        Assert.False(new HdrContributionVideoProfile(3840, 2160,
            new FrameRate(25, 1), correct.Layout, correct.Metadata).IsExactR153LiveHlg);
        Assert.False(new HdrContributionVideoProfile(3840, 2160,
            correct.FrameRate, HdrNativePixelLayout.P010Ycbcr420Le,
            correct.Metadata).IsExactR153LiveHlg);

        foreach (var invalid in new[]
        {
            new HdrSignalMetadata(9, 16, 9, true, 10, HdrChromaSampleLocation.LeftCosited,
                HdrPrecisionAlphaMode.Opaque, ColorMetadataAuthority.Authoritative, "pq"),
            new HdrSignalMetadata(9, 18, 9, true, 8, HdrChromaSampleLocation.LeftCosited,
                HdrPrecisionAlphaMode.Opaque, ColorMetadataAuthority.Authoritative, "lost-bit-depth"),
            new HdrSignalMetadata(9, 18, 9, true, 10, HdrChromaSampleLocation.LeftCosited,
                HdrPrecisionAlphaMode.Opaque, ColorMetadataAuthority.Inferred, "inferred"),
            new HdrSignalMetadata(9, 18, 9, false, 10, HdrChromaSampleLocation.LeftCosited,
                HdrPrecisionAlphaMode.Opaque, ColorMetadataAuthority.Authoritative, "full-range"),
            new HdrSignalMetadata(9, 18, 9, true, 10, HdrChromaSampleLocation.Centered,
                HdrPrecisionAlphaMode.Opaque, ColorMetadataAuthority.Authoritative, "wrong-chroma"),
            HdrSignalMetadata.AuthoritativeHlg422("")
        })
        {
            Assert.False(new HdrContributionVideoProfile(3840, 2160,
                FrameRate.Fps50, HdrNativePixelLayout.PlanarYcbcr422P10Le, invalid)
                .IsExactR153LiveHlg);
        }
        Assert.Equal(ColorDescription.UnknownRgba8, VideoFormat.Hd1080p50Rgba8.Color);
        Assert.Equal(1, MediaContractVersion.Current.Major);
    }

    [Fact]
    public void Native_422_P010_and_linear_half_have_distinct_checked_plane_footprints()
    {
        var yuv = HdrNativeFrameLayout.TightlyPacked(3840, 2160,
            HdrNativePixelLayout.PlanarYcbcr422P10Le);
        var p010 = HdrNativeFrameLayout.TightlyPacked(3840, 2160,
            HdrNativePixelLayout.P010Ycbcr420Le);
        var rgba = HdrNativeFrameLayout.TightlyPacked(3840, 2160,
            HdrNativePixelLayout.LinearRgbaHalfLe);

        Assert.Equal(33_177_600, yuv.RequiredBytes);
        Assert.Equal(3, yuv.Planes.Count);
        Assert.Equal(24_883_200, p010.RequiredBytes);
        Assert.Equal(2, p010.Planes.Count);
        Assert.Equal(66_355_200, rgba.RequiredBytes);
        Assert.Single(rgba.Planes);
        Assert.Equal(7680, yuv.Planes[0].RowPitchBytes);
        Assert.Equal(3840, yuv.Planes[1].RowPitchBytes);
        Assert.False(yuv.ExactlyMatches(p010));
        Assert.True(yuv.ExactlyMatches(HdrNativeFrameLayout.TightlyPacked(3840, 2160,
            HdrNativePixelLayout.PlanarYcbcr422P10Le)));
    }

    [Fact]
    public void Native_layout_rejects_odd_subsampling_overlap_pitch_overflow_and_external_mutation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HdrNativeFrameLayout.TightlyPacked(3, 2, HdrNativePixelLayout.PlanarYcbcr422P10Le));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HdrNativeFrameLayout.TightlyPacked(4, 3, HdrNativePixelLayout.P010Ycbcr420Le));
        Assert.Throws<ArgumentException>(() => new HdrNativeFrameLayout(4, 2,
            HdrNativePixelLayout.PlanarYcbcr422P10Le,
            [
                new(4, 2, 2, 8, 0),
                new(2, 2, 2, 4, 10),
                new(2, 2, 2, 4, 24)
            ]));
        Assert.Throws<ArgumentException>(() => new HdrNativeFrameLayout(4, 2,
            HdrNativePixelLayout.P010Ycbcr420Le,
            [new(4, 2, 2, 4, 0), new(2, 1, 4, 8, 8)]));
        Assert.Throws<ArgumentException>(() => new HdrNativeFrameLayout(4, 2,
            HdrNativePixelLayout.LinearRgbaHalfLe,
            [new(4, 2, 8, long.MaxValue - 1, 0)]));

        var mutable = new[]
        {
            new HdrPlaneLayout(4, 2, 2, 8, 0),
            new HdrPlaneLayout(2, 2, 2, 4, 16),
            new HdrPlaneLayout(2, 2, 2, 4, 24)
        };
        var layout = new HdrNativeFrameLayout(4, 2,
            HdrNativePixelLayout.PlanarYcbcr422P10Le, mutable);
        mutable[0] = default;
        Assert.Equal(8, layout.Planes[0].RowPitchBytes);
        Assert.Equal(32, layout.RequiredBytes);
    }

    [Fact]
    public void A_new_native_HLG_descriptor_never_promotes_legacy_RGBA8()
    {
        var legacy = VideoFormat.Hd1080p50Rgba8;
        Assert.Equal(1920u, legacy.Width);
        Assert.Equal(ColorMetadataAuthority.Missing, legacy.Color.Authority);
        Assert.Equal(PixelFormat.Rgba8, legacy.PixelFormat);
        Assert.Equal(HdrNativePixelLayout.PlanarYcbcr422P10Le,
            HdrContributionProfiles.Live2160p50("test-only").Layout);
        Assert.False(legacy.Color.IsComplete);
    }
}
