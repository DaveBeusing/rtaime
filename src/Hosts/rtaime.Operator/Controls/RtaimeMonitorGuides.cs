// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public sealed class RtaimeMonitorGuides : FrameworkElement
{
	private static DependencyProperty Property(string name, Type type, object value) =>
		DependencyProperty.Register(name, type, typeof(RtaimeMonitorGuides), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
	public static readonly DependencyProperty PresentationWidthProperty = Property(nameof(PresentationWidth), typeof(double), 0.0);
	public static readonly DependencyProperty PresentationHeightProperty = Property(nameof(PresentationHeight), typeof(double), 0.0);
	public static readonly DependencyProperty OffsetXProperty = Property(nameof(OffsetX), typeof(double), 0.0);
	public static readonly DependencyProperty OffsetYProperty = Property(nameof(OffsetY), typeof(double), 0.0);
	public static readonly DependencyProperty SafeProperty = Property(nameof(Safe), typeof(bool), false);
	public static readonly DependencyProperty GridProperty = Property(nameof(Grid), typeof(bool), false);
	public static readonly DependencyProperty CenterProperty = Property(nameof(Center), typeof(bool), false);
	public static readonly DependencyProperty StrokeProperty = Property(nameof(Stroke), typeof(Brush), Brushes.Transparent);
	public double PresentationWidth { get => (double)GetValue(PresentationWidthProperty); set => SetValue(PresentationWidthProperty, value); }
	public double PresentationHeight { get => (double)GetValue(PresentationHeightProperty); set => SetValue(PresentationHeightProperty, value); }
	public double OffsetX { get => (double)GetValue(OffsetXProperty); set => SetValue(OffsetXProperty, value); }
	public double OffsetY { get => (double)GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
	public bool Safe { get => (bool)GetValue(SafeProperty); set => SetValue(SafeProperty, value); }
	public bool Grid { get => (bool)GetValue(GridProperty); set => SetValue(GridProperty, value); }
	public bool Center { get => (bool)GetValue(CenterProperty); set => SetValue(CenterProperty, value); }
	public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
	protected override void OnRender(DrawingContext context)
	{
		base.OnRender(context);
		if (!(PresentationWidth > 0 && PresentationHeight > 0) || !double.IsFinite(PresentationWidth + PresentationHeight + OffsetX + OffsetY)) return;
		var rectangle = new Rect((ActualWidth - PresentationWidth) / 2 + OffsetX,
			(ActualHeight - PresentationHeight) / 2 + OffsetY, PresentationWidth, PresentationHeight);
		var pen = new Pen(Stroke, 1);
		if (Safe) foreach (var inset in new[] { 0.05, 0.1 })
			context.DrawRectangle(null, pen, new Rect(rectangle.X + rectangle.Width * inset, rectangle.Y + rectangle.Height * inset,
				rectangle.Width * (1 - inset * 2), rectangle.Height * (1 - inset * 2)));
		if (Grid) for (var index = 1; index <= 2; index++)
		{
			var x = rectangle.X + rectangle.Width * index / 3;
			var y = rectangle.Y + rectangle.Height * index / 3;
			context.DrawLine(pen, new Point(x, rectangle.Top), new Point(x, rectangle.Bottom));
			context.DrawLine(pen, new Point(rectangle.Left, y), new Point(rectangle.Right, y));
		}
		if (Center)
		{
			var center = new Point(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
			context.DrawLine(pen, new Point(center.X - 14, center.Y), new Point(center.X + 14, center.Y));
			context.DrawLine(pen, new Point(center.X, center.Y - 14), new Point(center.X, center.Y + 14));
		}
	}
}
