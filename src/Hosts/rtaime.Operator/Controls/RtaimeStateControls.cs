// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;

namespace rtaime.Operator.Controls;

public class RtaimeStatePlaceholder : Control
{
	public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
		nameof(State),
		typeof(string),
		typeof(RtaimeStatePlaceholder),
		new FrameworkPropertyMetadata("EMPTY"));

	public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
		nameof(Title),
		typeof(string),
		typeof(RtaimeStatePlaceholder),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
		nameof(Detail),
		typeof(string),
		typeof(RtaimeStatePlaceholder),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
		nameof(IsActive),
		typeof(bool),
		typeof(RtaimeStatePlaceholder),
		new FrameworkPropertyMetadata(true));

	static RtaimeStatePlaceholder()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeStatePlaceholder),
			new FrameworkPropertyMetadata(typeof(RtaimeStatePlaceholder)));
	}

	public string State
	{
		get => (string)GetValue(StateProperty);
		set => SetValue(StateProperty, value);
	}

	public string Title
	{
		get => (string)GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string Detail
	{
		get => (string)GetValue(DetailProperty);
		set => SetValue(DetailProperty, value);
	}

	public bool IsActive
	{
		get => (bool)GetValue(IsActiveProperty);
		set => SetValue(IsActiveProperty, value);
	}
}

public class RtaimeInlineStatus : RtaimeStatePlaceholder
{
	static RtaimeInlineStatus()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeInlineStatus),
			new FrameworkPropertyMetadata(typeof(RtaimeStatePlaceholder)));
	}
}

public class RtaimeLoadingIndicator : RtaimeStatePlaceholder
{
	static RtaimeLoadingIndicator()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeLoadingIndicator),
			new FrameworkPropertyMetadata(typeof(RtaimeStatePlaceholder)));
	}
}

public class RtaimeEmptyStatePanel : RtaimeStatePlaceholder
{
	static RtaimeEmptyStatePanel()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeEmptyStatePanel),
			new FrameworkPropertyMetadata(typeof(RtaimeStatePlaceholder)));
	}
}

public class RtaimeRecoveryStatePanel : RtaimeStatePlaceholder
{
	static RtaimeRecoveryStatePanel()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeRecoveryStatePanel),
			new FrameworkPropertyMetadata(typeof(RtaimeStatePlaceholder)));
	}
}
