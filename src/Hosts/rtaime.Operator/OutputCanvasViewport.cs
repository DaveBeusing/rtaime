// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

/// <summary>Logical canvas edges to WPF DIPs, independent of monitor DPI.</summary>
public readonly record struct OutputCanvasViewport(
    double Width, double Height, int CanvasWidth, int CanvasHeight,
    double Zoom, double PanX, double PanY)
{
    public double Scale => CanvasWidth > 0 && CanvasHeight > 0 && Width > 32 && Height > 32 &&
        double.IsFinite(Width) && double.IsFinite(Height) &&
        double.IsFinite(Zoom) && Zoom > 0
        ? Math.Min((Width - 32) / CanvasWidth, (Height - 32) / CanvasHeight) * Zoom
        : 0;

    public Point ToView(OutputCanvasPoint point) =>
        new(Width / 2 + PanX + (point.X - CanvasWidth / 2d) * Scale,
            Height / 2 + PanY + (point.Y - CanvasHeight / 2d) * Scale);

    public OutputCanvasPoint ToCanvas(Point point) =>
        Scale <= 0 ? default :
        new((point.X - Width / 2 - PanX) / Scale + CanvasWidth / 2d,
            (point.Y - Height / 2 - PanY) / Scale + CanvasHeight / 2d);

    public static OutputCanvasPoint Snap(OutputCanvasPoint point, bool enabled, bool fine = false, double step = 10) =>
        !enabled || fine || step <= 0 || !double.IsFinite(step) ? point :
        new(Math.Round(point.X / step) * step, Math.Round(point.Y / step) * step);

    /// <summary>Normalize a logical destination point without clamping intentional off-canvas warp.</summary>
    public OutputCanvasPoint Normalize(OutputCanvasPoint point) =>
        CanvasWidth <= 0 || CanvasHeight <= 0 ? default :
        new(point.X / CanvasWidth, point.Y / CanvasHeight);

    /// <summary>Optional 5%/10% logical design overlays, not provider calibration or output masking.</summary>
    public Rect SafeAreaBounds(double insetFraction)
    {
        if (Scale <= 0 || !double.IsFinite(insetFraction) ||
            insetFraction < 0 || insetFraction >= .5)
            return Rect.Empty;
        return new Rect(
            ToView(new(CanvasWidth * insetFraction, CanvasHeight * insetFraction)),
            ToView(new(CanvasWidth * (1 - insetFraction), CanvasHeight * (1 - insetFraction))));
    }

    /// <summary>Finite visible logical-pixel ruler marks sharing the precise pointer transform.</summary>
    public IReadOnlyList<OutputCanvasRulerMark> Ruler(bool horizontal, double minimumSpacingDip = 64)
    {
        if (Scale <= 0 || !double.IsFinite(minimumSpacingDip) || minimumSpacingDip <= 0)
            return [];
        var extent = horizontal ? CanvasWidth : CanvasHeight;
        var first = ToCanvas(new Point(0, 0));
        var last = ToCanvas(new Point(Width, Height));
        var lower = Math.Max(0, horizontal ? first.X : first.Y);
        var upper = Math.Min(extent, horizontal ? last.X : last.Y);
        if (lower > upper) return [];

        var ideal = minimumSpacingDip / Scale;
        var decade = Math.Pow(10, Math.Floor(Math.Log10(ideal)));
        var step = new[] { decade, 2 * decade, 5 * decade, 10 * decade }
            .First(candidate => candidate >= ideal);
        var marks = new List<OutputCanvasRulerMark>(32);
        for (var value = Math.Ceiling(lower / step) * step;
             value <= upper && marks.Count < 64; value += step)
        {
            var coordinate = (int)Math.Round(value);
            var view = horizontal ? ToView(new(coordinate, 0)).X :
                ToView(new(0, coordinate)).Y;
            marks.Add(new(coordinate, view));
        }
        return marks;
    }

    /// <summary>Rigid grid-snap for body movement: snap one anchor, never each warped corner.</summary>
    public static OutputCanvasQuad TranslateRigid(OutputCanvasQuad quad,
        double deltaX, double deltaY, bool snapEnabled, bool fine = false)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        var moved = Snap(new(quad.TopLeft.X + deltaX, quad.TopLeft.Y + deltaY),
            snapEnabled, fine);
        var dx = moved.X - quad.TopLeft.X;
        var dy = moved.Y - quad.TopLeft.Y;
        OutputCanvasPoint Translate(OutputCanvasPoint point) => new(point.X + dx, point.Y + dy);
        return new(Translate(quad.TopLeft), Translate(quad.TopRight),
            Translate(quad.BottomRight), Translate(quad.BottomLeft));
    }

    public static Point Align(Point point, double pixelsPerDip) =>
        pixelsPerDip > 0 && double.IsFinite(pixelsPerDip)
            ? new(Math.Round(point.X * pixelsPerDip) / pixelsPerDip,
                  Math.Round(point.Y * pixelsPerDip) / pixelsPerDip)
            : point;
}

/// <summary>A bounded ruler mark projected into WPF DIPs from a logical canvas pixel edge.</summary>
public readonly record struct OutputCanvasRulerMark(int LogicalPixel, double ViewPosition);
