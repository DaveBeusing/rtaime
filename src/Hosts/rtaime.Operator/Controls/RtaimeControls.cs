// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public enum RtaimeStatusKind
{
	Neutral,
	Selection,
	Action,
	Healthy,
	Ready,
	Warning,
	Armed,
	Preview,
	Program,
	OnAir
}

public class RtaimeButton : Button
{
	public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
		nameof(IsCompact),
		typeof(bool),
		typeof(RtaimeButton),
		new FrameworkPropertyMetadata(false));

	static RtaimeButton()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeButton), new FrameworkPropertyMetadata(typeof(RtaimeButton)));
	}

	public bool IsCompact
	{
		get => (bool)GetValue(IsCompactProperty);
		set => SetValue(IsCompactProperty, value);
	}
}

public class RtaimeIconButton : RtaimeButton
{
	public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
		nameof(IconData),
		typeof(Geometry),
		typeof(RtaimeIconButton));

	static RtaimeIconButton()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeIconButton), new FrameworkPropertyMetadata(typeof(RtaimeIconButton)));
	}

	public Geometry? IconData
	{
		get => (Geometry?)GetValue(IconDataProperty);
		set => SetValue(IconDataProperty, value);
	}
}

public class RtaimeToggleButton : ToggleButton
{
	public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
		nameof(IsCompact),
		typeof(bool),
		typeof(RtaimeToggleButton),
		new FrameworkPropertyMetadata(false));

	static RtaimeToggleButton()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeToggleButton), new FrameworkPropertyMetadata(typeof(RtaimeToggleButton)));
	}

	public bool IsCompact
	{
		get => (bool)GetValue(IsCompactProperty);
		set => SetValue(IsCompactProperty, value);
	}
}

public class RtaimeTransportButton : RtaimeButton
{
	public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
		nameof(IconData),
		typeof(Geometry),
		typeof(RtaimeTransportButton));

	static RtaimeTransportButton()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeTransportButton), new FrameworkPropertyMetadata(typeof(RtaimeTransportButton)));
	}

	public Geometry? IconData
	{
		get => (Geometry?)GetValue(IconDataProperty);
		set => SetValue(IconDataProperty, value);
	}
}

public class RtaimeNavigationItem : RtaimeButton
{
	public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
		nameof(IconData),
		typeof(Geometry),
		typeof(RtaimeNavigationItem));

	public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
		nameof(Label),
		typeof(string),
		typeof(RtaimeNavigationItem),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
		nameof(IsActive),
		typeof(bool),
		typeof(RtaimeNavigationItem),
		new FrameworkPropertyMetadata(false));

	static RtaimeNavigationItem()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeNavigationItem), new FrameworkPropertyMetadata(typeof(RtaimeNavigationItem)));
	}

	public Geometry? IconData
	{
		get => (Geometry?)GetValue(IconDataProperty);
		set => SetValue(IconDataProperty, value);
	}

	public string Label
	{
		get => (string)GetValue(LabelProperty);
		set => SetValue(LabelProperty, value);
	}

	public bool IsActive
	{
		get => (bool)GetValue(IsActiveProperty);
		set => SetValue(IsActiveProperty, value);
	}
}

public class RtaimePanelHeader : HeaderedContentControl
{
	static RtaimePanelHeader()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimePanelHeader), new FrameworkPropertyMetadata(typeof(RtaimePanelHeader)));
	}
}

public class RtaimeStatusBadge : ContentControl
{
	public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
		nameof(Status),
		typeof(RtaimeStatusKind),
		typeof(RtaimeStatusBadge),
		new FrameworkPropertyMetadata(RtaimeStatusKind.Neutral));

	public static readonly DependencyProperty ShowIndicatorProperty = DependencyProperty.Register(
		nameof(ShowIndicator),
		typeof(bool),
		typeof(RtaimeStatusBadge),
		new FrameworkPropertyMetadata(true));

	static RtaimeStatusBadge()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeStatusBadge), new FrameworkPropertyMetadata(typeof(RtaimeStatusBadge)));
	}

	public RtaimeStatusKind Status
	{
		get => (RtaimeStatusKind)GetValue(StatusProperty);
		set => SetValue(StatusProperty, value);
	}

	public bool ShowIndicator
	{
		get => (bool)GetValue(ShowIndicatorProperty);
		set => SetValue(ShowIndicatorProperty, value);
	}
}

public class RtaimeMetricBar : ProgressBar
{
	static RtaimeMetricBar()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeMetricBar), new FrameworkPropertyMetadata(typeof(RtaimeMetricBar)));
	}
}

public class RtaimeMetricRing : System.Windows.Controls.Control
{
	static RtaimeMetricRing()
	{
		IsHitTestVisibleProperty.OverrideMetadata(typeof(RtaimeMetricRing), new FrameworkPropertyMetadata(false));
	}

	public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
		nameof(Value),
		typeof(double),
		typeof(RtaimeMetricRing),
		new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
		nameof(Minimum),
		typeof(double),
		typeof(RtaimeMetricRing),
		new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
		nameof(Maximum),
		typeof(double),
		typeof(RtaimeMetricRing),
		new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty HasValueProperty = DependencyProperty.Register(
		nameof(HasValue),
		typeof(bool),
		typeof(RtaimeMetricRing),
		new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
		nameof(RingThickness),
		typeof(double),
		typeof(RtaimeMetricRing),
		new FrameworkPropertyMetadata(5d, FrameworkPropertyMetadataOptions.AffectsRender));

	public double Value
	{
		get => (double)GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	public double Minimum
	{
		get => (double)GetValue(MinimumProperty);
		set => SetValue(MinimumProperty, value);
	}

	public double Maximum
	{
		get => (double)GetValue(MaximumProperty);
		set => SetValue(MaximumProperty, value);
	}

	public bool HasValue
	{
		get => (bool)GetValue(HasValueProperty);
		set => SetValue(HasValueProperty, value);
	}

	public double RingThickness
	{
		get => (double)GetValue(RingThicknessProperty);
		set => SetValue(RingThicknessProperty, value);
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		base.OnRender(drawingContext);

		var thickness = Math.Clamp(double.IsFinite(RingThickness) ? RingThickness : 5d, 1d, 12d);
		var radius = Math.Max(0d, (Math.Min(ActualWidth, ActualHeight) - thickness) / 2d);
		if (radius <= 0)
			return;

		var center = new Point(ActualWidth / 2d, ActualHeight / 2d);
		var trackBrush = BorderBrush ?? Brushes.Transparent;
		var valueBrush = Foreground ?? Brushes.Transparent;
		drawingContext.DrawEllipse(null, new Pen(trackBrush, thickness), center, radius, radius);

		if (!HasValue)
			return;

		var span = Maximum - Minimum;
		if (!double.IsFinite(span) || span <= 0)
			return;

		var normalized = Math.Clamp((Value - Minimum) / span, 0d, 1d);
		if (normalized <= 0)
			return;

		var angle = normalized * 359.99d;
		var start = PointOnRing(center, radius, -90d);
		var end = PointOnRing(center, radius, -90d + angle);
		var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
		figure.Segments.Add(new ArcSegment(
			end,
			new Size(radius, radius),
			0,
			angle > 180d,
			SweepDirection.Clockwise,
			true));
		var geometry = new PathGeometry();
		geometry.Figures.Add(figure);
		drawingContext.DrawGeometry(null, new Pen(valueBrush, thickness)
		{
			StartLineCap = PenLineCap.Round,
			EndLineCap = PenLineCap.Round
		}, geometry);
	}

	private static Point PointOnRing(Point center, double radius, double degrees)
	{
		var radians = degrees * Math.PI / 180d;
		return new Point(
			center.X + (Math.Cos(radians) * radius),
			center.Y + (Math.Sin(radians) * radius));
	}
}

public class RtaimeTimecode : ContentControl
{
	static RtaimeTimecode()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeTimecode), new FrameworkPropertyMetadata(typeof(RtaimeTimecode)));
	}
}

public class RtaimeIcon : System.Windows.Controls.Control
{
	public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
		nameof(Data),
		typeof(Geometry),
		typeof(RtaimeIcon));

	static RtaimeIcon()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeIcon), new FrameworkPropertyMetadata(typeof(RtaimeIcon)));
	}

	public Geometry? Data
	{
		get => (Geometry?)GetValue(DataProperty);
		set => SetValue(DataProperty, value);
	}
}
