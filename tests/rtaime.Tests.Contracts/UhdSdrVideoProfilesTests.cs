// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Contracts;

public sealed class UhdSdrVideoProfilesTests
{
    [Theory]
    [InlineData(25, 1, 829_440_000L, 400_000L, 1_920L)]
    [InlineData(50, 1, 1_658_880_000L, 200_000L, 960L)]
    [InlineData(60_000, 1_001, 1_988_667_333L, 166_834L, 800L)]
    public void Exact_SDR_RGB_candidates_preserve_bytes_deadlines_and_48k_audio(
        long numerator, long denominator, long bytesPerSecond, long deadlineTicks, long firstSamples)
    {
        var format = new VideoFormat(3840, 2160, new FrameRate(numerator, denominator),
            PixelFormat.Rgba8, ScanMode.Progressive, ColorDescription.Rec709FullRgba8);

        Assert.True(UhdSdrVideoProfiles.IsExactSdrRgbCandidate(format));
        Assert.Equal(33_177_600L, UhdSdrVideoProfiles.RequiredFrameBytes(format));
        Assert.Equal(bytesPerSecond, UhdSdrVideoProfiles.RequiredDirectionalBytesPerSecond(format));
        Assert.Equal(deadlineTicks, UhdSdrVideoProfiles.FrameDeadlineTicks(format));
        Assert.Equal(firstSamples, UhdSdrVideoProfiles.AudioSamplesBeforeFrame(format, 1));
        Assert.Equal(0, UhdSdrVideoProfiles.AudioSamplesBeforeFrame(format, 0));
        Assert.Equal(ColorMetadataAuthority.Authoritative, format.Color.Authority);
        Assert.Equal(ColorMatrix.IdentityRgb, format.Color.Matrix);
        Assert.Equal(ColorTransfer.Rec709, format.Color.Transfer);
    }

    [Fact]
    public void Fractional_cadence_uses_absolute_sample_boundaries()
    {
        var format = UhdSdrVideoProfiles.Rgb2160p59_94;
        Assert.Equal(new long[] { 0, 800, 1601, 2402, 3203, 4004 },
            Enumerable.Range(0, 6)
                .Select(frame => UhdSdrVideoProfiles.AudioSamplesBeforeFrame(format, (ulong)frame)));
    }

    [Fact]
    public void Unknown_HD_or_changed_SDR_color_depth_range_alpha_and_cadence_cannot_be_promoted()
    {
        Assert.Equal(3, UhdSdrVideoProfiles.Candidates.Count);
        Assert.False(UhdSdrVideoProfiles.IsExactSdrRgbCandidate(VideoFormat.Hd1080p50Rgba8));
        Assert.False(UhdSdrVideoProfiles.IsExactSdrRgbCandidate(
            new VideoFormat(3840, 2160, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive)));
        Assert.True(UhdSdrVideoProfiles.Has2160pGeometry(
            new VideoFormat(3840, 2160, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive)));

        var reference = ColorDescription.Rec709FullRgba8;
        foreach (var altered in new[]
        {
            new ColorDescription(reference.Primaries, reference.Transfer, reference.Matrix,
                reference.Range, 10, reference.Alpha, reference.Authority),
            new ColorDescription(reference.Primaries, reference.Transfer, reference.Matrix,
                NominalRange.Limited, 8, reference.Alpha, reference.Authority),
            new ColorDescription(reference.Primaries, reference.Transfer, ColorMatrix.Rec709,
                reference.Range, 8, reference.Alpha, reference.Authority),
            new ColorDescription(reference.Primaries, ColorTransfer.Srgb, reference.Matrix,
                reference.Range, 8, reference.Alpha, reference.Authority),
            new ColorDescription(reference.Primaries, reference.Transfer, reference.Matrix,
                reference.Range, 8, AlphaMode.Opaque, reference.Authority),
            new ColorDescription(reference.Primaries, reference.Transfer, reference.Matrix,
                reference.Range, 8, reference.Alpha, ColorMetadataAuthority.Inferred)
        })
        {
            var alteredFormat = new VideoFormat(3840, 2160, FrameRate.Fps50,
                PixelFormat.Rgba8, ScanMode.Progressive, altered);
            Assert.False(UhdSdrVideoProfiles.IsExactSdrRgbCandidate(alteredFormat));
            Assert.Throws<ArgumentException>(() => UhdSdrVideoProfiles.RequiredFrameBytes(alteredFormat));
        }

        var otherRate = new VideoFormat(3840, 2160, new FrameRate(24, 1),
            PixelFormat.Rgba8, ScanMode.Progressive, reference);
        Assert.False(UhdSdrVideoProfiles.IsExactSdrRgbCandidate(otherRate));
        Assert.Throws<ArgumentException>(() => UhdSdrVideoProfiles.AudioSamplesBeforeFrame(otherRate, 1));
        Assert.Throws<OverflowException>(() =>
            UhdSdrVideoProfiles.AudioSamplesBeforeFrame(UhdSdrVideoProfiles.Rgb2160p50, ulong.MaxValue));
    }
}
