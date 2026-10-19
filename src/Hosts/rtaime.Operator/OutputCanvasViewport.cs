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

    public static Point Align(Point point, double pixelsPerDip) =>
        pixelsPerDip > 0 && double.IsFinite(pixelsPerDip)
            ? new(Math.Round(point.X * pixelsPerDip) / pixelsPerDip,
                  Math.Round(point.Y * pixelsPerDip) / pixelsPerDip)
            : point;
}
