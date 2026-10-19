// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media.Contracts;

namespace rtaime.Provider.Gpu;

// Output mapping is an optional provider capability. No hardware acceleration is
// inferred from the presence of the ordinary compositing capability.
public interface IGpuOutputMappingBackend
{
    void RenderOutputMapping(
        SurfaceId outputSurfaceId,
        VideoFormat format,
        IReadOnlyList<GpuOutputMappingSurface> surfaces);
}

public sealed record GpuOutputMappingInput(GpuFrame Frame, GpuOutputMappingSurface Surface);

public enum GpuOutputBlendCurve
{
    Linear = 1,
    SmoothStep = 2
}

public readonly record struct GpuOutputPoint(double X, double Y);

public sealed record GpuOutputEdgeBlend(
    double Left, double Top, double Right, double Bottom,
    GpuOutputBlendCurve Curve = GpuOutputBlendCurve.SmoothStep)
{
    public static GpuOutputEdgeBlend None { get; } = new(0, 0, 0, 0);
}

public sealed record GpuOutputMappingSurface(
    SurfaceId SourceId,
    VideoFormat SourceFormat,
    double CropX, double CropY, double CropWidth, double CropHeight,
    GpuOutputPoint TopLeft, GpuOutputPoint TopRight,
    GpuOutputPoint BottomRight, GpuOutputPoint BottomLeft,
    GpuOutputEdgeBlend Blend,
    int ZOrder,
    Guid SurfaceIdentity = default)
{
    public void Validate()
    {
        if (SourceFormat.PixelFormat != PixelFormat.Rgba8)
            throw new NotSupportedException("Output mapping supports RGBA8 sources only.");
        if (!double.IsFinite(CropX) || !double.IsFinite(CropY) ||
            !double.IsFinite(CropWidth) || !double.IsFinite(CropHeight) ||
            CropX < 0 || CropY < 0 || CropWidth <= 0 || CropHeight <= 0 ||
            CropX + CropWidth > 1 || CropY + CropHeight > 1)
            throw new ArgumentException("Invalid normalized source crop.");
        var points = new[] { TopLeft, TopRight, BottomRight, BottomLeft };
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            var c = points[(i + 2) % 4];
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) ||
                Math.Abs(a.X) > 16384 || Math.Abs(a.Y) > 16384 ||
                (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) <= 1e-8)
                throw new ArgumentException("Output mapping quad must be finite, strictly convex and clockwise.");
        }
        if (Blend is null ||
            !Enum.IsDefined(Blend.Curve) ||
            !ValidWidth(Blend.Left) || !ValidWidth(Blend.Right) ||
            !ValidWidth(Blend.Top) || !ValidWidth(Blend.Bottom))
            throw new ArgumentException("Invalid output edge blend.");
    }

    private static bool ValidWidth(double width) =>
        double.IsFinite(width) && width >= 0 && width <= 0.5;
}

/// <summary>
/// Deterministic RGBA8 software reference executed by the existing managed GPU
/// backend. Samples source once per mapped destination pixel using one inverse
/// homography and bilinear filtering. Over uses straight alpha in the encoded
/// RGBA8 transfer space, not HDR or linear-light projector compensation.
/// </summary>
public static class GpuOutputMappingRaster
{
    public static void Render(
        VideoFormat format,
        Span<byte> destination,
        IReadOnlyList<GpuOutputMappingSurface> surfaces,
        Func<SurfaceId, ReadOnlyMemory<byte>> resolveSource)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(resolveSource);
        if (format.PixelFormat != PixelFormat.Rgba8)
            throw new NotSupportedException("Output mapping supports RGBA8 only.");
        if (destination.Length != RgbaFrameBuffer.RequiredByteLength(format))
            throw new ArgumentException("Output mapping target length is incorrect.", nameof(destination));
        if (surfaces.Count > 8)
            throw new ArgumentException("Output mapping supports at most eight visible surfaces.", nameof(surfaces));
        // Validate all geometry and source allocations before touching the sink.
        var sorted = surfaces.OrderBy(s => s.ZOrder).ThenBy(s => s.SurfaceIdentity.ToString("N"), StringComparer.Ordinal).ToArray();
        var sources = new ReadOnlyMemory<byte>[sorted.Length];
        var inverses = new InverseHomography[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            sorted[i].Validate();
            sources[i] = resolveSource(sorted[i].SourceId);
            if (sources[i].Length != RgbaFrameBuffer.RequiredByteLength(sorted[i].SourceFormat))
                throw new ArgumentException("Output mapping source allocation does not match its format.");
            inverses[i] = InverseHomography.Create(sorted[i]);
        }

        destination.Clear(); // uncovered pixels have transparent-black semantics
        var targetWidth = checked((int)format.Width);
        var targetHeight = checked((int)format.Height);
        for (var layer = 0; layer < sorted.Length; layer++)
        {
            var surface = sorted[layer];
            var inverse = inverses[layer];
            var source = sources[layer].Span;
            var sourceWidth = checked((int)surface.SourceFormat.Width);
            var sourceHeight = checked((int)surface.SourceFormat.Height);
            var minX = Math.Clamp((int)Math.Floor(Math.Min(Math.Min(surface.TopLeft.X, surface.TopRight.X),
                Math.Min(surface.BottomRight.X, surface.BottomLeft.X))), 0, targetWidth);
            var maxX = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Max(surface.TopLeft.X, surface.TopRight.X),
                Math.Max(surface.BottomRight.X, surface.BottomLeft.X))), 0, targetWidth);
            var minY = Math.Clamp((int)Math.Floor(Math.Min(Math.Min(surface.TopLeft.Y, surface.TopRight.Y),
                Math.Min(surface.BottomRight.Y, surface.BottomLeft.Y))), 0, targetHeight);
            var maxY = Math.Clamp((int)Math.Ceiling(Math.Max(Math.Max(surface.TopLeft.Y, surface.TopRight.Y),
                Math.Max(surface.BottomRight.Y, surface.BottomLeft.Y))), 0, targetHeight);
            for (var y = minY; y < maxY; y++)
            for (var x = minX; x < maxX; x++)
            {
                if (!inverse.TryMap(x + 0.5, y + 0.5, out var u, out var v) ||
                    u < 0 || v < 0 || u > 1 || v > 1)
                    continue;
                var coverage = EdgeCoverage(surface.Blend, u, v);
                if (coverage <= 0) continue;

                // Crop is specified in source edge coordinates; sample positions are
                // pixel centres. Clamp bilinear taps inside the cropped region.
                var srcX = (surface.CropX + u * surface.CropWidth) * sourceWidth - 0.5;
                var srcY = (surface.CropY + v * surface.CropHeight) * sourceHeight - 0.5;
                var cropMinX = Math.Clamp((int)Math.Ceiling(surface.CropX * sourceWidth - 0.5), 0, sourceWidth - 1);
                var cropMaxX = Math.Clamp((int)Math.Floor((surface.CropX + surface.CropWidth) * sourceWidth - 0.5), cropMinX, sourceWidth - 1);
                var cropMinY = Math.Clamp((int)Math.Ceiling(surface.CropY * sourceHeight - 0.5), 0, sourceHeight - 1);
                var cropMaxY = Math.Clamp((int)Math.Floor((surface.CropY + surface.CropHeight) * sourceHeight - 0.5), cropMinY, sourceHeight - 1);
                srcX = Math.Clamp(srcX, cropMinX, cropMaxX);
                srcY = Math.Clamp(srcY, cropMinY, cropMaxY);
                var x0 = (int)Math.Floor(srcX);
                var y0 = (int)Math.Floor(srcY);
                var x1 = Math.Min(cropMaxX, x0 + 1);
                var y1 = Math.Min(cropMaxY, y0 + 1);
                var fx = srcX - x0;
                var fy = srcY - y0;
                var top = checked((y0 * sourceWidth + x0) * 4);
                var topRight = checked((y0 * sourceWidth + x1) * 4);
                var bottom = checked((y1 * sourceWidth + x0) * 4);
                var bottomRight = checked((y1 * sourceWidth + x1) * 4);
                var alpha = Math.Clamp(Sample(source, top, topRight, bottom, bottomRight, fx, fy, 3) * coverage, 0, 1);
                if (alpha <= 0) continue;
                var offset = checked((y * targetWidth + x) * 4);
                var previousAlpha = destination[offset + 3] / 255.0;
                var newAlpha = alpha + previousAlpha * (1 - alpha);
                for (var channel = 0; channel < 3; channel++)
                {
                    var prev = destination[offset + channel] / 255.0;
                    var blended = (Sample(source, top, topRight, bottom, bottomRight, fx, fy, channel) * alpha + prev * previousAlpha * (1 - alpha)) / newAlpha;
                    destination[offset + channel] = (byte)Math.Clamp((int)Math.Round(blended * 255.0), 0, 255);
                }
                destination[offset + 3] = (byte)Math.Clamp((int)Math.Round(newAlpha * 255.0), 0, 255);
            }
        }
    }

    private static double Sample(ReadOnlySpan<byte> source, int top, int topRight,
        int bottom, int bottomRight, double fx, double fy, int channel)
    {
        var a = source[top + channel] * (1 - fx) + source[topRight + channel] * fx;
        var b = source[bottom + channel] * (1 - fx) + source[bottomRight + channel] * fx;
        return (a * (1 - fy) + b * fy) / 255.0;
    }

    private static double EdgeCoverage(GpuOutputEdgeBlend blend, double u, double v)
    {
        static double Factor(double distance, double width, GpuOutputBlendCurve curve)
        {
            if (width <= 0) return 1;
            var t = Math.Clamp(distance / width, 0, 1);
            return curve == GpuOutputBlendCurve.SmoothStep ? t * t * (3 - 2 * t) : t;
        }
        return Factor(u, blend.Left, blend.Curve) *
            Factor(v, blend.Top, blend.Curve) *
            Factor(1 - u, blend.Right, blend.Curve) *
            Factor(1 - v, blend.Bottom, blend.Curve);
    }

    private readonly record struct InverseHomography(
        double A, double B, double C, double D, double E, double F,
        double G, double H, double I)
    {
        public static InverseHomography Create(GpuOutputMappingSurface s)
        {
            var a = s.TopLeft; var b = s.TopRight;
            var c = s.BottomRight; var d = s.BottomLeft;
            var dx1 = b.X - c.X; var dx2 = d.X - c.X;
            var dy1 = b.Y - c.Y; var dy2 = d.Y - c.Y;
            var dx3 = a.X - b.X + c.X - d.X;
            var dy3 = a.Y - b.Y + c.Y - d.Y;
            double g = 0, h = 0;
            if (Math.Abs(dx3) + Math.Abs(dy3) > 1e-12)
            {
                var determinant = dx1 * dy2 - dx2 * dy1;
                if (Math.Abs(determinant) <= 1e-12)
                    throw new ArgumentException("Singular output mapping quad.");
                g = (dx3 * dy2 - dx2 * dy3) / determinant;
                h = (dx1 * dy3 - dx3 * dy1) / determinant;
            }
            var aa = b.X - a.X + g * b.X;
            var bb = d.X - a.X + h * d.X;
            var cc = a.X;
            var dd = b.Y - a.Y + g * b.Y;
            var ee = d.Y - a.Y + h * d.Y;
            var ff = a.Y;
            var det = aa * (ee - ff * h) - bb * (dd - ff * g) + cc * (dd * h - ee * g);
            if (!double.IsFinite(det) || Math.Abs(det) <= 1e-15)
                throw new ArgumentException("Singular output mapping homography.");
            var scale = 1.0 / det;
            return new(
                (ee - ff * h) * scale, (cc * h - bb) * scale, (bb * ff - cc * ee) * scale,
                (ff * g - dd) * scale, (aa - cc * g) * scale, (cc * dd - aa * ff) * scale,
                (dd * h - ee * g) * scale, (bb * g - aa * h) * scale, (aa * ee - bb * dd) * scale);
        }

        public bool TryMap(double x, double y, out double u, out double v)
        {
            var w = G * x + H * y + I;
            if (!double.IsFinite(w) || Math.Abs(w) < 1e-12)
            {
                u = v = 0;
                return false;
            }
            u = (A * x + B * y + C) / w;
            v = (D * x + E * y + F) / w;
            return double.IsFinite(u) && double.IsFinite(v);
        }
    }
}
