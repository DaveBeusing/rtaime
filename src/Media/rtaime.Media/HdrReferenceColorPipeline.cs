// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Media.Contracts;

namespace rtaime.Media;

/// <summary>
/// Offline native-code reference and test-vector path, never a Program renderer,
/// GPU backend, calibrated HLG OOTF, hardware decode, or approved SDR conversion.
/// The original 10-bit source remains untouched; only a distinct preview is mapped.
/// </summary>
public static class HdrReferenceColorPipeline
{
    private const double HlgA = 0.17883277;
    private const double HlgB = 1.0 - 4.0 * HlgA;
    private static readonly double HlgC = 0.5 - HlgA * Math.Log(4.0 * HlgA);

    public static HdrReferenceLinearFrame Decode422(
        HdrNativeFrameLayout layout, ReadOnlySpan<byte> source,
        HdrSignalMetadata metadata, FrameTiming timing)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(metadata);
        if (layout.PixelLayout != HdrNativePixelLayout.PlanarYcbcr422P10Le ||
            !metadata.IsAuthoritativeHlg422)
            throw new NotSupportedException(
                "Reference decode requires authoritative BT.2100 HLG, limited 10-bit 4:2:2; P010, PQ and inferred signal are not admitted.");
        if (source.Length != layout.RequiredBytes)
            throw new ArgumentException("Native HDR payload must have the exact bounded plane extent.", nameof(source));

        var pixels = new float[checked((int)((long)layout.Width * layout.Height * 4L))];
        for (uint y = 0; y < layout.Height; y++)
        for (uint x = 0; x < layout.Width; x++)
        {
            var yCode = ReadSample(source, layout.Planes[0], x, y);
            var cbCode = ReadSample(source, layout.Planes[1], x / 2, y);
            var crCode = ReadSample(source, layout.Planes[2], x / 2, y);
            var luma = (yCode - 64.0) / 876.0;
            var cb = (cbCode - 512.0) / 896.0;
            var cr = (crCode - 512.0) / 896.0;

            // BT.2020 NCL reference coefficients. Gamut excursions are retained
            // through float before any deliberate monitoring-only gamut clamp.
            var nonlinearRed = luma + 1.4746 * cr;
            var nonlinearGreen = luma - 0.1645531268 * cb - 0.5713531268 * cr;
            var nonlinearBlue = luma + 1.8814 * cb;

            var offset = checked((int)(((long)y * layout.Width + x) * 4));
            pixels[offset] = (float)InverseHlgOetf(nonlinearRed);
            pixels[offset + 1] = (float)InverseHlgOetf(nonlinearGreen);
            pixels[offset + 2] = (float)InverseHlgOetf(nonlinearBlue);
            pixels[offset + 3] = 1f;
        }
        return new HdrReferenceLinearFrame(layout.Width, layout.Height,
            timing, metadata, pixels);
    }

    /// <summary>
    /// Encode a linear RGBA half-float intermediate for device upload tests.
    /// No GPU allocation, synchronization fence, kernel or output activation occurs.
    /// </summary>
    public static HdrReferenceHalfFrame EncodeLinearHalf(HdrReferenceLinearFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var layout = HdrNativeFrameLayout.TightlyPacked(frame.Width, frame.Height,
            HdrNativePixelLayout.LinearRgbaHalfLe);
        var output = new byte[checked((int)layout.RequiredBytes)];
        var values = frame.LinearRgba.Span;
        for (var i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]) || Math.Abs(values[i]) > 65504.0f)
                throw new ArgumentOutOfRangeException(nameof(frame),
                    "Linear precision exceeds the representable finite half-float range.");
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(i * 2, 2),
                BitConverter.HalfToUInt16Bits((Half)values[i]));
        }
        return new HdrReferenceHalfFrame(layout, frame.Timing, frame.SourceMetadata, output);
    }

    public static HdrReferenceLinearFrame CompositePremultiplied(
        HdrReferenceLinearFrame background, HdrReferenceLinearFrame foreground)
    {
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(foreground);
        if (background.Width != foreground.Width || background.Height != foreground.Height ||
            background.Timing != foreground.Timing ||
            background.SourceMetadata != foreground.SourceMetadata)
            throw new NotSupportedException(
                "Compositing requires identical authoritative HLG color, geometry and PTS; mixed SDR/PQ/gamut/cadence is not implicit.");
        var lower = background.LinearRgba.Span;
        var upper = foreground.LinearRgba.Span;
        var output = new float[lower.Length];
        for (var i = 0; i < lower.Length; i += 4)
        {
            var foregroundAlpha = upper[i + 3];
            var backgroundAlpha = lower[i + 3];
            if (foregroundAlpha is < 0 or > 1 || backgroundAlpha is < 0 or > 1)
                throw new ArgumentException("Premultiplied linear alpha must be within [0,1].");
            // Inputs are already premultiplied in linear scene space; no
            // gamma-domain blend, auto keying or clipped RGB reinterpretation.
            var complement = 1f - foregroundAlpha;
            output[i] = upper[i] + lower[i] * complement;
            output[i + 1] = upper[i + 1] + lower[i + 1] * complement;
            output[i + 2] = upper[i + 2] + lower[i + 2] * complement;
            output[i + 3] = foregroundAlpha + backgroundAlpha * complement;
        }
        return new HdrReferenceLinearFrame(background.Width, background.Height,
            background.Timing, background.SourceMetadata, output);
    }

    /// <summary>
    /// A deliberately named SDR preview transform: approximate BT.2020-to-709
    /// scene-linear matrix then explicit Reinhard highlight compression and sRGB.
    /// This is neither EBU TR 070/BT.2408 approved down-map nor calibrated HDR.
    /// It never modifies source/Program bytes or source timestamps.
    /// </summary>
    public static HdrReferenceSdrPreview CreateSdrPreview(
        HdrReferenceLinearFrame frame, HdrSdrPreviewPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(policy);
        if (!frame.SourceMetadata.IsAuthoritativeHlg422)
            throw new NotSupportedException("An authoritative HLG source is required for an explicit SDR preview.");
        var output = new byte[checked((int)((long)frame.Width * frame.Height * 4))];
        var input = frame.LinearRgba.Span;
        var exposure = Math.Pow(2.0, policy.ExposureStops);
        for (var i = 0; i < input.Length; i += 4)
        {
            var r = input[i];
            var g = input[i + 1];
            var b = input[i + 2];
            // Approximate linear BT.2020 to BT.709/sRGB conversion (D65).
            var r709 = 1.660491 * r - 0.587641 * g - 0.072850 * b;
            var g709 = -0.124550 * r + 1.132900 * g - 0.008350 * b;
            var b709 = -0.018151 * r - 0.100579 * g + 1.118730 * b;
            output[i] = EncodeSrgb(CompressForPreview(r709, exposure));
            output[i + 1] = EncodeSrgb(CompressForPreview(g709, exposure));
            output[i + 2] = EncodeSrgb(CompressForPreview(b709, exposure));
            output[i + 3] = 255;
        }
        return new HdrReferenceSdrPreview(frame.Width, frame.Height, frame.Timing,
            ColorDescription.SrgbFullRgba8, policy, output);
    }

    private static ushort ReadSample(ReadOnlySpan<byte> source,
        HdrPlaneLayout plane, uint x, uint y)
    {
        var offset = checked((int)(plane.OffsetBytes + (long)y * plane.RowPitchBytes + x * 2L));
        var word = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2));
        if ((word & 0x3F) != 0)
            throw new FormatException("10-bit native container low padding bits must be zero.");
        return (ushort)(word >> 6);
    }

    private static double InverseHlgOetf(double signal)
    {
        // Preserve the source unchanged; scene-linear reference clamps only
        // values outside the display transform's normalized active domain.
        var value = Math.Clamp(signal, 0, 1);
        return value <= 0.5
            ? value * value / 3.0
            : (Math.Exp((value - HlgC) / HlgA) + HlgB) / 12.0;
    }

    private static double CompressForPreview(double linear, double exposure)
    {
        var value = Math.Max(0, linear * exposure);
        return value / (1.0 + value);
    }

    private static byte EncodeSrgb(double linear)
    {
        var value = linear <= 0.0031308
            ? 12.92 * linear
            : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);
    }
}

public sealed class HdrReferenceLinearFrame
{
    private readonly float[] _rgba;

    public HdrReferenceLinearFrame(uint width, uint height, FrameTiming timing,
        HdrSignalMetadata sourceMetadata, ReadOnlySpan<float> premultipliedLinearRgba)
    {
        ArgumentNullException.ThrowIfNull(sourceMetadata);
        if (!sourceMetadata.IsAuthoritativeHlg422)
            throw new NotSupportedException("Offline high precision frames require authoritative HLG source provenance.");
        if (width == 0 || height == 0 || width > 3840 || height > 2160 ||
            premultipliedLinearRgba.Length != checked((int)((long)width * height * 4)))
            throw new ArgumentException("Linear frame geometry/payload must be finite and exact.");
        for (var i = 0; i < premultipliedLinearRgba.Length; i++)
            if (!float.IsFinite(premultipliedLinearRgba[i]))
                throw new ArgumentException("Linear reference samples must be finite.");
        Width = width;
        Height = height;
        Timing = timing;
        SourceMetadata = sourceMetadata;
        _rgba = premultipliedLinearRgba.ToArray();
    }

    public uint Width { get; }
    public uint Height { get; }
    public FrameTiming Timing { get; }
    public HdrSignalMetadata SourceMetadata { get; }
    public ReadOnlyMemory<float> LinearRgba => _rgba;
}

public sealed class HdrReferenceHalfFrame
{
    private readonly byte[] _buffer;
    public HdrReferenceHalfFrame(HdrNativeFrameLayout layout,
        FrameTiming timing, HdrSignalMetadata sourceMetadata, ReadOnlySpan<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sourceMetadata);
        if (layout.PixelLayout != HdrNativePixelLayout.LinearRgbaHalfLe ||
            buffer.Length != layout.RequiredBytes || !sourceMetadata.IsAuthoritativeHlg422)
            throw new ArgumentException("Half-float intermediate requires exact layout and source-bound HLG metadata.");
        Layout = layout;
        Timing = timing;
        SourceMetadata = sourceMetadata;
        _buffer = buffer.ToArray();
    }

    public HdrNativeFrameLayout Layout { get; }
    public FrameTiming Timing { get; }
    public HdrSignalMetadata SourceMetadata { get; }
    public ReadOnlyMemory<byte> Buffer => _buffer;
    public bool IsGpuResident => false;
}

public sealed record HdrSdrPreviewPolicy
{
    public HdrSdrPreviewPolicy(double exposureStops)
    {
        if (!double.IsFinite(exposureStops) || exposureStops is < -8 or > 8)
            throw new ArgumentOutOfRangeException(nameof(exposureStops));
        ExposureStops = exposureStops;
    }

    public double ExposureStops { get; }
    public string TransformId => "reference.hlg-bt2020-to-srgb.reinhard-scene.v1";
}

public sealed class HdrReferenceSdrPreview
{
    private readonly byte[] _pixels;
    public HdrReferenceSdrPreview(uint width, uint height, FrameTiming timing,
        ColorDescription displayColor, HdrSdrPreviewPolicy policy, ReadOnlySpan<byte> pixels)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (displayColor != ColorDescription.SrgbFullRgba8 ||
            pixels.Length != checked((int)((long)width * height * 4)))
            throw new ArgumentException("SDR preview must be an explicit full-range sRGB RGBA8 presentation.");
        Width = width;
        Height = height;
        Timing = timing;
        DisplayColor = displayColor;
        Policy = policy;
        _pixels = pixels.ToArray();
    }

    public uint Width { get; }
    public uint Height { get; }
    public FrameTiming Timing { get; }
    public ColorDescription DisplayColor { get; }
    public HdrSdrPreviewPolicy Policy { get; }
    public ReadOnlyMemory<byte> Pixels => _pixels;
    public bool IsProgramOrRecordingOutput => false;
}
