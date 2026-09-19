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

public class RtaimeTimecode : ContentControl
{
	static RtaimeTimecode()
	{
		DefaultStyleKeyProperty.OverrideMetadata(typeof(RtaimeTimecode), new FrameworkPropertyMetadata(typeof(RtaimeTimecode)));
	}
}

public class RtaimeIcon : Control
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
