// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Media;
using rtaime.Operator;

namespace rtaime.Operator.Controls;

public sealed class RtaimePixelGridOverlay : FrameworkElement
{
	public static readonly DependencyProperty PresentationWidthProperty = DependencyProperty.Register(
		nameof(PresentationWidth), typeof(double), typeof(RtaimePixelGridOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty PresentationHeightProperty = DependencyProperty.Register(
		nameof(PresentationHeight), typeof(double), typeof(RtaimePixelGridOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
		nameof(OffsetX), typeof(double), typeof(RtaimePixelGridOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
		nameof(OffsetY), typeof(double), typeof(RtaimePixelGridOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty SourceWidthProperty = DependencyProperty.Register(
		nameof(SourceWidth), typeof(int), typeof(RtaimePixelGridOverlay),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty SourceHeightProperty = DependencyProperty.Register(
		nameof(SourceHeight), typeof(int), typeof(RtaimePixelGridOverlay),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
		nameof(LineBrush), typeof(Brush), typeof(RtaimePixelGridOverlay),
		new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

	public double PresentationWidth { get => (double)GetValue(PresentationWidthProperty); set => SetValue(PresentationWidthProperty, value); }
	public double PresentationHeight { get => (double)GetValue(PresentationHeightProperty); set => SetValue(PresentationHeightProperty, value); }
	public double OffsetX { get => (double)GetValue(OffsetXProperty); set => SetValue(OffsetXProperty, value); }
	public double OffsetY { get => (double)GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
	public int SourceWidth { get => (int)GetValue(SourceWidthProperty); set => SetValue(SourceWidthProperty, value); }
	public int SourceHeight { get => (int)GetValue(SourceHeightProperty); set => SetValue(SourceHeightProperty, value); }
	public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		if (SourceWidth <= 0 || SourceHeight <= 0 || PresentationWidth <= 0 || PresentationHeight <= 0)
			return;

		var stepX = PresentationWidth / SourceWidth;
		var stepY = PresentationHeight / SourceHeight;
		if (stepX < MediaPixelInspection.PixelGridMinimumScale || stepY < MediaPixelInspection.PixelGridMinimumScale)
			return;

		var left = (ActualWidth - PresentationWidth) / 2.0 + OffsetX;
		var top = (ActualHeight - PresentationHeight) / 2.0 + OffsetY;
		var right = left + PresentationWidth;
		var bottom = top + PresentationHeight;
		var pen = new Pen(LineBrush, 1.0);
		pen.Freeze();

		var firstColumn = Math.Max(0, (int)Math.Floor(-left / stepX));
		var lastColumn = Math.Min(SourceWidth, (int)Math.Ceiling((ActualWidth - left) / stepX));
		for (var x = firstColumn; x <= lastColumn; x++)
		{
			var px = left + x * stepX;
			drawingContext.DrawLine(pen, new Point(px, Math.Max(0, top)), new Point(px, Math.Min(ActualHeight, bottom)));
		}

		var firstRow = Math.Max(0, (int)Math.Floor(-top / stepY));
		var lastRow = Math.Min(SourceHeight, (int)Math.Ceiling((ActualHeight - top) / stepY));
		for (var y = firstRow; y <= lastRow; y++)
		{
			var py = top + y * stepY;
			drawingContext.DrawLine(pen, new Point(Math.Max(0, left), py), new Point(Math.Min(ActualWidth, right), py));
		}
	}
}
