// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public sealed class RtaimeMediaCompareView : Control
{
	public static readonly DependencyProperty FrameAProperty = DependencyProperty.Register(
		nameof(FrameA), typeof(ImageSource), typeof(RtaimeMediaCompareView),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty FrameBProperty = DependencyProperty.Register(
		nameof(FrameB), typeof(ImageSource), typeof(RtaimeMediaCompareView),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty DifferenceFrameProperty = DependencyProperty.Register(
		nameof(DifferenceFrame), typeof(ImageSource), typeof(RtaimeMediaCompareView),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty CompareModeProperty = DependencyProperty.Register(
		nameof(CompareMode), typeof(MediaCompareMode), typeof(RtaimeMediaCompareView),
		new FrameworkPropertyMetadata(MediaCompareMode.Off, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty WipePositionProperty = DependencyProperty.Register(
		nameof(WipePosition), typeof(double), typeof(RtaimeMediaCompareView),
		new FrameworkPropertyMetadata(0.5d, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceWipe));

	public ImageSource? FrameA { get => (ImageSource?)GetValue(FrameAProperty); set => SetValue(FrameAProperty, value); }
	public ImageSource? FrameB { get => (ImageSource?)GetValue(FrameBProperty); set => SetValue(FrameBProperty, value); }
	public ImageSource? DifferenceFrame { get => (ImageSource?)GetValue(DifferenceFrameProperty); set => SetValue(DifferenceFrameProperty, value); }
	public MediaCompareMode CompareMode { get => (MediaCompareMode)GetValue(CompareModeProperty); set => SetValue(CompareModeProperty, value); }
	public double WipePosition { get => (double)GetValue(WipePositionProperty); set => SetValue(WipePositionProperty, value); }

	protected override void OnRender(DrawingContext dc)
	{
		base.OnRender(dc);
		var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
		if (bounds.IsEmpty) return;

		if (CompareMode == MediaCompareMode.Difference)
		{
			if (DifferenceFrame is not null) DrawUniform(dc, DifferenceFrame, bounds, null);
			return;
		}

		if (FrameA is not null) DrawUniform(dc, FrameA, bounds, null);
		if (CompareMode == MediaCompareMode.Off || FrameB is null) return;

		var vertical = CompareMode is MediaCompareMode.SplitVertical or MediaCompareMode.WipeVertical;
		var position = CompareMode is MediaCompareMode.SplitVertical or MediaCompareMode.SplitHorizontal ? 0.5 : WipePosition;
		var clip = vertical
			? new Rect(bounds.Width * position, 0, bounds.Width * (1 - position), bounds.Height)
			: new Rect(0, bounds.Height * position, bounds.Width, bounds.Height * (1 - position));
		DrawUniform(dc, FrameB, bounds, clip);
	}

	private static void DrawUniform(DrawingContext dc, ImageSource image, Rect bounds, Rect? viewportClip)
	{
		var sourceWidth = image.Width;
		var sourceHeight = image.Height;
		if (sourceWidth <= 0 || sourceHeight <= 0) return;
		var scale = Math.Min(bounds.Width / sourceWidth, bounds.Height / sourceHeight);
		var target = new Rect(
			(bounds.Width - sourceWidth * scale) / 2,
			(bounds.Height - sourceHeight * scale) / 2,
			sourceWidth * scale,
			sourceHeight * scale);
		if (viewportClip is { } clip)
		{
			dc.PushClip(new RectangleGeometry(clip));
			dc.DrawImage(image, target);
			dc.Pop();
		}
		else
		{
			dc.DrawImage(image, target);
		}
	}

	private static object CoerceWipe(DependencyObject _, object value) =>
		Math.Clamp((double)value, 0d, 1d);
}
