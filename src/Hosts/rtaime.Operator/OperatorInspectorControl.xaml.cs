// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class OperatorInspectorControl : UserControl
{
	public static readonly DependencyProperty InspectorProperty = DependencyProperty.Register(
		nameof(Inspector),
		typeof(MediaPoolInspectorViewModel),
		typeof(OperatorInspectorControl));

	public static readonly DependencyProperty ControlStateProperty = DependencyProperty.Register(
		nameof(ControlState),
		typeof(OperatorViewModel),
		typeof(OperatorInspectorControl));

	public static readonly DependencyProperty MediaDeckProperty = DependencyProperty.Register(
		nameof(MediaDeck),
		typeof(MediaDeckViewModel),
		typeof(OperatorInspectorControl));

	public static readonly DependencyProperty QuickControlsProperty = DependencyProperty.Register(
		nameof(QuickControls),
		typeof(OperatorQuickControlsViewModel),
		typeof(OperatorInspectorControl));

	public static readonly DependencyProperty CollapseCommandProperty = DependencyProperty.Register(
		nameof(CollapseCommand),
		typeof(ICommand),
		typeof(OperatorInspectorControl));

	public OperatorInspectorControl()
	{
		InitializeComponent();
	}

	public MediaPoolInspectorViewModel? Inspector
	{
		get => (MediaPoolInspectorViewModel?)GetValue(InspectorProperty);
		set => SetValue(InspectorProperty, value);
	}

	public OperatorViewModel? ControlState
	{
		get => (OperatorViewModel?)GetValue(ControlStateProperty);
		set => SetValue(ControlStateProperty, value);
	}

	public MediaDeckViewModel? MediaDeck
	{
		get => (MediaDeckViewModel?)GetValue(MediaDeckProperty);
		set => SetValue(MediaDeckProperty, value);
	}

	public OperatorQuickControlsViewModel? QuickControls
	{
		get => (OperatorQuickControlsViewModel?)GetValue(QuickControlsProperty);
		set => SetValue(QuickControlsProperty, value);
	}

	public ICommand? CollapseCommand
	{
		get => (ICommand?)GetValue(CollapseCommandProperty);
		set => SetValue(CollapseCommandProperty, value);
	}
}
