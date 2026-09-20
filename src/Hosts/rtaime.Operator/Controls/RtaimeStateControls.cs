// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using rtaime.Client;

namespace rtaime.Operator.Controls;

public abstract class RtaimeStateControl : Control
{
	public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
		nameof(State),
		typeof(OperatorUiStateKind),
		typeof(RtaimeStateControl),
		new FrameworkPropertyMetadata(OperatorUiStateKind.Ready));

	public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
		nameof(Title),
		typeof(string),
		typeof(RtaimeStateControl),
		new FrameworkPropertyMetadata(string.Empty));

	public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
		nameof(Detail),
		typeof(string),
		typeof(RtaimeStateControl),
		new FrameworkPropertyMetadata(string.Empty));

	public OperatorUiStateKind State
	{
		get => (OperatorUiStateKind)GetValue(StateProperty);
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
}

public class RtaimeStatePlaceholder : RtaimeStateControl
{
	static RtaimeStatePlaceholder()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeStatePlaceholder),
			new FrameworkPropertyMetadata(typeof(RtaimeStatePlaceholder)));
	}
}

public class RtaimeInlineStatus : RtaimeStateControl
{
	static RtaimeInlineStatus()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeInlineStatus),
			new FrameworkPropertyMetadata(typeof(RtaimeInlineStatus)));
	}
}

public class RtaimeLoadingIndicator : Control
{
	static RtaimeLoadingIndicator()
	{
		DefaultStyleKeyProperty.OverrideMetadata(
			typeof(RtaimeLoadingIndicator),
			new FrameworkPropertyMetadata(typeof(RtaimeLoadingIndicator)));
	}
}

public class RtaimeEmptyStatePanel : RtaimeStatePlaceholder
{
	public RtaimeEmptyStatePanel()
	{
		State = OperatorUiStateKind.Empty;
	}
}

public class RtaimeRecoveryStatePanel : RtaimeStatePlaceholder
{
	public RtaimeRecoveryStatePanel()
	{
		State = OperatorUiStateKind.Recovering;
	}
}
