// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Control;

/// <summary>Pure authored geometry. No allocation or device admission is implied.</summary>
public static class OutputCanvasGeometry
{
    public static bool IsValid(OutputCanvasQuad q)
    {
        OutputCanvasPoint[] p = [q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft];
        if (p.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y) ||
            Math.Abs(v.X) > OutputCanvasContract.MaximumDimension || Math.Abs(v.Y) > OutputCanvasContract.MaximumDimension))
            return false;
        for (var i = 0; i < 4; i++)
        {
            var a = p[i]; var b = p[(i + 1) % 4]; var c = p[(i + 2) % 4];
            // Screen coordinates (Y down): TL,TR,BR,BL must be strictly clockwise/convex.
            if ((b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) <= 1e-8)
                return false;
        }
        return true;
    }

    // Maps unit-square EDGE coordinates through a projective homography, not bilinear interpolation.
    public static OutputCanvasPoint Map(OutputCanvasQuad q, double u, double v)
    {
        if (!IsValid(q) || !double.IsFinite(u) || !double.IsFinite(v) || u < 0 || u > 1 || v < 0 || v > 1)
            throw new ArgumentException("A convex clockwise quad and unit-square coordinates are required.");
        var a = q.TopLeft; var b = q.TopRight; var c = q.BottomRight; var d = q.BottomLeft;
        var dx1 = b.X - c.X; var dx2 = d.X - c.X; var dx3 = a.X - b.X + c.X - d.X;
        var dy1 = b.Y - c.Y; var dy2 = d.Y - c.Y; var dy3 = a.Y - b.Y + c.Y - d.Y;
        double g = 0, h = 0;
        if (Math.Abs(dx3) + Math.Abs(dy3) > 1e-12)
        {
            var determinant = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(determinant) <= 1e-12) throw new ArgumentException("Singular quad.");
            g = (dx3 * dy2 - dx2 * dy3) / determinant;
            h = (dx1 * dy3 - dx3 * dy1) / determinant;
        }
        var w = g * u + h * v + 1;
        if (Math.Abs(w) <= 1e-12) throw new ArgumentException("Singular homography.");
        return new(((b.X - a.X + g * b.X) * u + (d.X - a.X + h * d.X) * v + a.X) / w,
            ((b.Y - a.Y + g * b.Y) * u + (d.Y - a.Y + h * d.Y) * v + a.Y) / w);
    }
}
