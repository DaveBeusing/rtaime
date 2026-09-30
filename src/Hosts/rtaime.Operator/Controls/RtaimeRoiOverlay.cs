// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public sealed class RtaimeRoiOverlay : FrameworkElement
{
	public static readonly DependencyProperty RoiProperty = DependencyProperty.Register(
		nameof(Roi), typeof(MediaInspectionRoi?), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty PresentationWidthProperty = DependencyProperty.Register(
		nameof(PresentationWidth), typeof(double), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty PresentationHeightProperty = DependencyProperty.Register(
		nameof(PresentationHeight), typeof(double), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty OffsetXProperty = DependencyProperty.Register(
		nameof(OffsetX), typeof(double), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty OffsetYProperty = DependencyProperty.Register(
		nameof(OffsetY), typeof(double), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty SourceWidthProperty = DependencyProperty.Register(
		nameof(SourceWidth), typeof(int), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty SourceHeightProperty = DependencyProperty.Register(
		nameof(SourceHeight), typeof(int), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
		nameof(Stroke), typeof(Brush), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
		nameof(Fill), typeof(Brush), typeof(RtaimeRoiOverlay),
		new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

	public MediaInspectionRoi? Roi
	{
		get => (MediaInspectionRoi?)GetValue(RoiProperty);
		set => SetValue(RoiProperty, value);
	}

	public double PresentationWidth
	{
		get => (double)GetValue(PresentationWidthProperty);
		set => SetValue(PresentationWidthProperty, value);
	}

	public double PresentationHeight
	{
		get => (double)GetValue(PresentationHeightProperty);
		set => SetValue(PresentationHeightProperty, value);
	}

	public double OffsetX
	{
		get => (double)GetValue(OffsetXProperty);
		set => SetValue(OffsetXProperty, value);
	}

	public double OffsetY
	{
		get => (double)GetValue(OffsetYProperty);
		set => SetValue(OffsetYProperty, value);
	}

	public int SourceWidth
	{
		get => (int)GetValue(SourceWidthProperty);
		set => SetValue(SourceWidthProperty, value);
	}

	public int SourceHeight
	{
		get => (int)GetValue(SourceHeightProperty);
		set => SetValue(SourceHeightProperty, value);
	}

	public Brush Stroke
	{
		get => (Brush)GetValue(StrokeProperty);
		set => SetValue(StrokeProperty, value);
	}

	public Brush Fill
	{
		get => (Brush)GetValue(FillProperty);
		set => SetValue(FillProperty, value);
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		if (Roi is not { IsEmpty: false } roi ||
			SourceWidth <= 0 || SourceHeight <= 0 ||
			PresentationWidth <= 0 || PresentationHeight <= 0)
			return;

		var presentationLeft = (ActualWidth - PresentationWidth) / 2.0 + OffsetX;
		var presentationTop = (ActualHeight - PresentationHeight) / 2.0 + OffsetY;
		var left = presentationLeft + roi.X * PresentationWidth / SourceWidth;
		var top = presentationTop + roi.Y * PresentationHeight / SourceHeight;
		var width = roi.Width * PresentationWidth / SourceWidth;
		var height = roi.Height * PresentationHeight / SourceHeight;
		var rectangle = new Rect(left, top, Math.Max(1.0, width), Math.Max(1.0, height));
		var pen = new Pen(Stroke, 1.0);
		pen.Freeze();

		drawingContext.DrawRectangle(Fill, pen, rectangle);
		const double handle = 6.0;
		DrawHandle(drawingContext, rectangle.TopLeft, handle);
		DrawHandle(drawingContext, rectangle.TopRight, handle);
		DrawHandle(drawingContext, rectangle.BottomLeft, handle);
		DrawHandle(drawingContext, rectangle.BottomRight, handle);
	}

	private void DrawHandle(DrawingContext drawingContext, Point point, double size)
	{
		var half = size / 2.0;
		drawingContext.DrawRectangle(Stroke, null, new Rect(point.X - half, point.Y - half, size, size));
	}
}
