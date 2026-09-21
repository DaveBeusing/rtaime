// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace rtaime.Operator.Controls;

public sealed class RtaimeMonitorPresentation : ContentControl
{
	public static readonly DependencyProperty MonitorProperty = DependencyProperty.Register(
		nameof(Monitor),
		typeof(MonitorView),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null));

	public static readonly DependencyProperty RoleTextProperty = DependencyProperty.Register(
		nameof(RoleText),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty RoleBrushProperty = DependencyProperty.Register(
		nameof(RoleBrush),
		typeof(Brush),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null));

	public static readonly DependencyProperty RoleStatusProperty = DependencyProperty.Register(
		nameof(RoleStatus),
		typeof(RtaimeStatusKind),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(RtaimeStatusKind.Neutral));

	public static readonly DependencyProperty PlaceholderStateProperty = DependencyProperty.Register(
		nameof(PlaceholderState),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty PlaceholderTitleProperty = DependencyProperty.Register(
		nameof(PlaceholderTitle),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty PlaceholderDetailProperty = DependencyProperty.Register(
		nameof(PlaceholderDetail),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty FrameUnavailableTextProperty = DependencyProperty.Register(
		nameof(FrameUnavailableText),
		typeof(string),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata("Frame unavailable"));

	public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
		nameof(HeaderContent),
		typeof(object),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null));

	public static readonly DependencyProperty StatusContentProperty = DependencyProperty.Register(
		nameof(StatusContent),
		typeof(object),
		typeof(RtaimeMonitorPresentation),
		new FrameworkPropertyMetadata(null));

	public MonitorView? Monitor
	{
		get => (MonitorView?)GetValue(MonitorProperty);
		set => SetValue(MonitorProperty, value);
	}

	public string RoleText
	{
		get => (string)GetValue(RoleTextProperty);
		set => SetValue(RoleTextProperty, value);
	}

	public Brush? RoleBrush
	{
		get => (Brush?)GetValue(RoleBrushProperty);
		set => SetValue(RoleBrushProperty, value);
	}

	public RtaimeStatusKind RoleStatus
	{
		get => (RtaimeStatusKind)GetValue(RoleStatusProperty);
		set => SetValue(RoleStatusProperty, value);
	}

	public string PlaceholderState
	{
		get => (string)GetValue(PlaceholderStateProperty);
		set => SetValue(PlaceholderStateProperty, value);
	}

	public string PlaceholderTitle
	{
		get => (string)GetValue(PlaceholderTitleProperty);
		set => SetValue(PlaceholderTitleProperty, value);
	}

	public string PlaceholderDetail
	{
		get => (string)GetValue(PlaceholderDetailProperty);
		set => SetValue(PlaceholderDetailProperty, value);
	}

	public string FrameUnavailableText
	{
		get => (string)GetValue(FrameUnavailableTextProperty);
		set => SetValue(FrameUnavailableTextProperty, value);
	}

	public object? HeaderContent
	{
		get => GetValue(HeaderContentProperty);
		set => SetValue(HeaderContentProperty, value);
	}

	public object? StatusContent
	{
		get => GetValue(StatusContentProperty);
		set => SetValue(StatusContentProperty, value);
	}
}
