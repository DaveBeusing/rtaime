// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class HdrReferenceColorPipelineTests
{
    private static readonly FrameTiming Timing =
        new(17, 17, new Timebase(1, 50));

    private static (HdrNativeFrameLayout Layout, byte[] Buffer) TestPattern(
        ushort first = 64, ushort second = 940, ushort cb = 512, ushort cr = 512)
    {
        var layout = HdrNativeFrameLayout.TightlyPacked(4, 2,
            HdrNativePixelLayout.PlanarYcbcr422P10Le);
        var frame = new byte[checked((int)layout.RequiredBytes)];
        for (uint y = 0; y < 2; y++)
        for (uint x = 0; x < 4; x++)
        {
            Write(frame, layout.Planes[0], x, y, x % 2 == 0 ? first : second);
            Write(frame, layout.Planes[1], x / 2, y, cb);
            Write(frame, layout.Planes[2], x / 2, y, cr);
        }
        return (layout, frame);
    }

    private static void Write(byte[] buffer, HdrPlaneLayout plane,
        uint x, uint y, ushort code) =>
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(
            checked((int)(plane.OffsetBytes + (long)y * plane.RowPitchBytes + x * 2L)), 2),
            checked((ushort)(code << 6)));

    private static HdrSignalMetadata Hlg(string origin = "test-pattern") =>
        HdrSignalMetadata.AuthoritativeHlg422(origin);

    [Fact]
    public void HLG_black_white_ramp_preserves_native_source_10bit_codes_and_48k_timing()
    {
        var (layout, bytes) = TestPattern();
        var original = bytes.ToArray();
        var linear = HdrReferenceColorPipeline.Decode422(layout, bytes, Hlg(), Timing);
        Assert.Equal(Timing, linear.Timing);
        Assert.Equal(original, bytes);
        Assert.Equal(4u, linear.Width);
        Assert.Equal(2u, linear.Height);
        Assert.InRange(linear.LinearRgba.Span[0], -0.00001f, 0.00001f);
        Assert.InRange(linear.LinearRgba.Span[4], 0.99f, 1.001f);
        Assert.Equal(1f, linear.LinearRgba.Span[3]);

        var half = HdrReferenceColorPipeline.EncodeLinearHalf(linear);
        Assert.Equal(HdrNativePixelLayout.LinearRgbaHalfLe, half.Layout.PixelLayout);
        Assert.Equal(64L, half.Layout.RequiredBytes);
        Assert.Equal(Timing, half.Timing);
        Assert.False(half.IsGpuResident);
        Assert.Equal(BitConverter.HalfToUInt16Bits((Half)linear.LinearRgba.Span[4]),
            BinaryPrimitives.ReadUInt16LittleEndian(half.Buffer.Span.Slice(8, 2)));

        var preview = HdrReferenceColorPipeline.CreateSdrPreview(
            linear, new HdrSdrPreviewPolicy(0));
        Assert.Equal(Timing, preview.Timing);
        Assert.Equal(ColorDescription.SrgbFullRgba8, preview.DisplayColor);
        Assert.Equal("reference.hlg-bt2020-to-srgb.reinhard-scene.v1",
            preview.Policy.TransformId);
        Assert.False(preview.IsProgramOrRecordingOutput);
        Assert.Equal(0, preview.Pixels.Span[0]);
        Assert.InRange(preview.Pixels.Span[4], (byte)160, (byte)210);
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Different_native_10bit_levels_remain_distinct_before_8bit_preview()
    {
        var (layout, lowerBytes) = TestPattern(first: 512, second: 513);
        var lower = HdrReferenceColorPipeline.Decode422(layout, lowerBytes, Hlg(), Timing);
        Assert.True(lower.LinearRgba.Span[0] < lower.LinearRgba.Span[4]);
        var half = HdrReferenceColorPipeline.EncodeLinearHalf(lower);
        Assert.NotEqual(
            BinaryPrimitives.ReadUInt16LittleEndian(half.Buffer.Span.Slice(0, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(half.Buffer.Span.Slice(8, 2)));
        // An accidental 10->8 downshift would make both codes 128.
        Assert.Equal(512 >> 2, 513 >> 2);
    }

    [Fact]
    public void Unauthoritative_PQ_P010_and_invalid_padding_must_fail_before_color_transform()
    {
        var (layout, bytes) = TestPattern();
        var pq = new HdrSignalMetadata(9, 16, 9, true, 10,
            HdrChromaSampleLocation.LeftCosited, HdrPrecisionAlphaMode.Opaque,
            ColorMetadataAuthority.Authoritative, "pq-source");
        Assert.Throws<NotSupportedException>(() =>
            HdrReferenceColorPipeline.Decode422(layout, bytes, pq, Timing));
        Assert.Throws<NotSupportedException>(() =>
            HdrReferenceColorPipeline.Decode422(
                HdrNativeFrameLayout.TightlyPacked(4, 2,
                    HdrNativePixelLayout.P010Ycbcr420Le),
                new byte[24], Hlg(), Timing));
        bytes[0] |= 0x01;
        Assert.Throws<FormatException>(() =>
            HdrReferenceColorPipeline.Decode422(layout, bytes, Hlg(), Timing));
        Assert.Throws<ArgumentException>(() =>
            HdrReferenceColorPipeline.Decode422(layout, bytes.AsSpan(0, bytes.Length - 1),
                Hlg(), Timing));
    }

    [Fact]
    public void Linear_alpha_composite_is_premultiplied_and_rejects_stale_timing_or_color()
    {
        var lower = new HdrReferenceLinearFrame(2, 1, Timing, Hlg(),
            new float[] { 0.1f, 0.2f, 0.3f, 1f, 0, 0, 0, 1 });
        var upper = new HdrReferenceLinearFrame(2, 1, Timing, Hlg(),
            new float[] { 0.25f, 0, 0, 0.5f, 0, 0.25f, 0, 0.5f });
        var mixed = HdrReferenceColorPipeline.CompositePremultiplied(lower, upper);
        Assert.Equal(Timing, mixed.Timing);
        Assert.InRange(mixed.LinearRgba.Span[0], 0.2999f, 0.3001f);
        Assert.Equal(1f, mixed.LinearRgba.Span[3]);
        Assert.InRange(mixed.LinearRgba.Span[5], 0.2499f, 0.2501f);

        var stale = new HdrReferenceLinearFrame(2, 1,
            new FrameTiming(18, 18, new Timebase(1, 50)), Hlg(), upper.LinearRgba.Span);
        Assert.Throws<NotSupportedException>(() =>
            HdrReferenceColorPipeline.CompositePremultiplied(lower, stale));
        var wrongColor = new HdrReferenceLinearFrame(2, 1, Timing,
            Hlg("different-source"), upper.LinearRgba.Span);
        Assert.Throws<NotSupportedException>(() =>
            HdrReferenceColorPipeline.CompositePremultiplied(lower, wrongColor));
    }

    [Fact]
    public void Explicit_SDR_preview_policy_is_bounded_and_does_not_modify_HDR_frame()
    {
        var (layout, bytes) = TestPattern(first: 512, second: 940);
        var linear = HdrReferenceColorPipeline.Decode422(layout, bytes, Hlg(), Timing);
        var original = linear.LinearRgba.ToArray();
        var dim = HdrReferenceColorPipeline.CreateSdrPreview(
            linear, new HdrSdrPreviewPolicy(-2));
        var bright = HdrReferenceColorPipeline.CreateSdrPreview(
            linear, new HdrSdrPreviewPolicy(2));
        Assert.True(dim.Pixels.Span[4] < bright.Pixels.Span[4]);
        Assert.Equal(original, linear.LinearRgba.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HdrSdrPreviewPolicy(100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HdrSdrPreviewPolicy(double.NaN));
    }

    [Fact]
    public void R103_10bit_range_is_measured_in_native_codes_not_in_eight_bit_preview()
    {
        Assert.True(VideoRangeLimits.TryGet(10, out var codeRange));
        Assert.Equal(20, codeRange.PreferredMin);
        Assert.Equal(984, codeRange.PreferredMax);
        var (layout, buffer) = TestPattern(first: 20, second: 984);
        var linear = HdrReferenceColorPipeline.Decode422(layout, buffer, Hlg(), Timing);
        Assert.Equal(Timing, linear.Timing);
        Assert.True(float.IsFinite(linear.LinearRgba.Span[0]));
        Assert.True(float.IsFinite(linear.LinearRgba.Span[4]));
        Assert.False(HdrReferenceColorPipeline.CreateSdrPreview(
            linear, new HdrSdrPreviewPolicy(0)).IsProgramOrRecordingOutput);
    }
}
