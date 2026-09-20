// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public class RtaimeOutputRow : System.Windows.Controls.Control
{
	public static readonly DependencyProperty ThumbnailProperty = DependencyProperty.Register(nameof(Thumbnail), typeof(ImageSource), typeof(RtaimeOutputRow));
	public static readonly DependencyProperty OutputNameProperty = DependencyProperty.Register(nameof(OutputName), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty AssignedSourceProperty = DependencyProperty.Register(nameof(AssignedSource), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty ResolutionProperty = DependencyProperty.Register(nameof(Resolution), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty FrameRateProperty = DependencyProperty.Register(nameof(FrameRate), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty ColorSpaceProperty = DependencyProperty.Register(nameof(ColorSpace), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty RouteStateProperty = DependencyProperty.Register(nameof(RouteState), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty EvidenceStateProperty = DependencyProperty.Register(nameof(EvidenceState), typeof(string), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata("UNVERIFIED"));
	public static readonly DependencyProperty ShowOverflowProperty = DependencyProperty.Register(nameof(ShowOverflow), typeof(bool), typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(false));
	public static readonly DependencyProperty OverflowCommandProperty = DependencyProperty.Register(nameof(OverflowCommand), typeof(ICommand), typeof(RtaimeOutputRow));

	static RtaimeOutputRow() =>
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeOutputRow), new FrameworkPropertyMetadata(typeof(RtaimeOutputRow)));

	public ImageSource? Thumbnail { get => (ImageSource?)GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
	public string OutputName { get => (string)GetValue(OutputNameProperty); set => SetValue(OutputNameProperty, value); }
	public string AssignedSource { get => (string)GetValue(AssignedSourceProperty); set => SetValue(AssignedSourceProperty, value); }
	public string Target { get => (string)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
	public string Resolution { get => (string)GetValue(ResolutionProperty); set => SetValue(ResolutionProperty, value); }
	public string FrameRate { get => (string)GetValue(FrameRateProperty); set => SetValue(FrameRateProperty, value); }
	public string ColorSpace { get => (string)GetValue(ColorSpaceProperty); set => SetValue(ColorSpaceProperty, value); }
	public string RouteState { get => (string)GetValue(RouteStateProperty); set => SetValue(RouteStateProperty, value); }
	public string EvidenceState { get => (string)GetValue(EvidenceStateProperty); set => SetValue(EvidenceStateProperty, value); }
	public bool ShowOverflow { get => (bool)GetValue(ShowOverflowProperty); set => SetValue(ShowOverflowProperty, value); }
	public ICommand? OverflowCommand { get => (ICommand?)GetValue(OverflowCommandProperty); set => SetValue(OverflowCommandProperty, value); }
}

public class RtaimeHealthRow : System.Windows.Controls.Control
{
	public static readonly DependencyProperty SubsystemNameProperty = DependencyProperty.Register(nameof(SubsystemName), typeof(string), typeof(RtaimeHealthRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(string), typeof(RtaimeHealthRow), new FrameworkPropertyMetadata(string.Empty));
	public static readonly DependencyProperty EvidenceStateProperty = DependencyProperty.Register(nameof(EvidenceState), typeof(string), typeof(RtaimeHealthRow), new FrameworkPropertyMetadata("UNVERIFIED"));

	static RtaimeHealthRow() =>
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeHealthRow), new FrameworkPropertyMetadata(typeof(RtaimeHealthRow)));

	public string SubsystemName { get => (string)GetValue(SubsystemNameProperty); set => SetValue(SubsystemNameProperty, value); }
	public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
	public string EvidenceState { get => (string)GetValue(EvidenceStateProperty); set => SetValue(EvidenceStateProperty, value); }
}

public class RtaimeAlertRow : RtaimeHealthRow
{
	public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(nameof(Detail), typeof(string), typeof(RtaimeAlertRow), new FrameworkPropertyMetadata(string.Empty));

	static RtaimeAlertRow() =>
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeAlertRow), new FrameworkPropertyMetadata(typeof(RtaimeAlertRow)));

	public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
}

public class RtaimeMetricDial : RtaimeMetricRing
{
}

public class RtaimeSparkline : System.Windows.Controls.Control
{
	private const double SourceWidth = 92d;
	private const double SourceHeight = 24d;

	public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
		nameof(Points),
		typeof(PointCollection),
		typeof(RtaimeSparkline),
		new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty HasValueProperty = DependencyProperty.Register(
		nameof(HasValue),
		typeof(bool),
		typeof(RtaimeSparkline),
		new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
		nameof(Background),
		typeof(Brush),
		typeof(RtaimeSparkline),
		new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty LineThicknessProperty = DependencyProperty.Register(
		nameof(LineThickness),
		typeof(double),
		typeof(RtaimeSparkline),
		new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

	public PointCollection? Points { get => (PointCollection?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
	public bool HasValue { get => (bool)GetValue(HasValueProperty); set => SetValue(HasValueProperty, value); }
	public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
	public double LineThickness { get => (double)GetValue(LineThicknessProperty); set => SetValue(LineThicknessProperty, value); }

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);
		drawingContext.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

		if (!HasValue || Points is not { Count: > 1 } points || ActualWidth <= 0 || ActualHeight <= 0)
			return;

		var scaleX = ActualWidth / SourceWidth;
		var scaleY = ActualHeight / SourceHeight;
		var geometry = new StreamGeometry();
		using (var context = geometry.Open())
		{
			context.BeginFigure(Scale(points[0], scaleX, scaleY), false, false);
			var scaled = new List<Point>(points.Count - 1);
			for (var index = 1; index < points.Count; index++)
				scaled.Add(Scale(points[index], scaleX, scaleY));
			context.PolyLineTo(scaled, true, false);
		}
		geometry.Freeze();

		var thickness = double.IsFinite(LineThickness) ? Math.Clamp(LineThickness, 1d, 4d) : 1d;
		drawingContext.DrawGeometry(null, new Pen(Foreground, thickness), geometry);
	}

	private static Point Scale(Point point, double scaleX, double scaleY) =>
		new(point.X * scaleX, point.Y * scaleY);
}
