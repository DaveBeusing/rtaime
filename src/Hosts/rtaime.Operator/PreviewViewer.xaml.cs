// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class PreviewViewer : MonitorView
{
	public static readonly DependencyProperty TransportStateProperty = DependencyProperty.Register(
		nameof(TransportState),
		typeof(string),
		typeof(PreviewViewer),
		new PropertyMetadata("UNLOADED"));

	public static readonly DependencyProperty PlayPauseCommandProperty = DependencyProperty.Register(
		nameof(PlayPauseCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public static readonly DependencyProperty StopCommandProperty = DependencyProperty.Register(
		nameof(StopCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public static readonly DependencyProperty SetInCommandProperty = DependencyProperty.Register(
		nameof(SetInCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public static readonly DependencyProperty SetOutCommandProperty = DependencyProperty.Register(
		nameof(SetOutCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public static readonly DependencyProperty JumpInCommandProperty = DependencyProperty.Register(
		nameof(JumpInCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public static readonly DependencyProperty JumpOutCommandProperty = DependencyProperty.Register(
		nameof(JumpOutCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public static readonly DependencyProperty PreviousCueCommandProperty = DependencyProperty.Register(
		nameof(PreviousCueCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public static readonly DependencyProperty NextCueCommandProperty = DependencyProperty.Register(
		nameof(NextCueCommand),
		typeof(ICommand),
		typeof(PreviewViewer),
		new PropertyMetadata(null));

	public PreviewViewer()
	{
		InitializeComponent();
	}

	public string TransportState
	{
		get => (string)GetValue(TransportStateProperty);
		set => SetValue(TransportStateProperty, value);
	}

	public ICommand? PlayPauseCommand
	{
		get => (ICommand?)GetValue(PlayPauseCommandProperty);
		set => SetValue(PlayPauseCommandProperty, value);
	}

	public ICommand? StopCommand
	{
		get => (ICommand?)GetValue(StopCommandProperty);
		set => SetValue(StopCommandProperty, value);
	}

	public ICommand? SetInCommand
	{
		get => (ICommand?)GetValue(SetInCommandProperty);
		set => SetValue(SetInCommandProperty, value);
	}

	public ICommand? SetOutCommand
	{
		get => (ICommand?)GetValue(SetOutCommandProperty);
		set => SetValue(SetOutCommandProperty, value);
	}

	public ICommand? JumpInCommand
	{
		get => (ICommand?)GetValue(JumpInCommandProperty);
		set => SetValue(JumpInCommandProperty, value);
	}

	public ICommand? JumpOutCommand
	{
		get => (ICommand?)GetValue(JumpOutCommandProperty);
		set => SetValue(JumpOutCommandProperty, value);
	}

	public ICommand? PreviousCueCommand
	{
		get => (ICommand?)GetValue(PreviousCueCommandProperty);
		set => SetValue(PreviousCueCommandProperty, value);
	}

	public ICommand? NextCueCommand
	{
		get => (ICommand?)GetValue(NextCueCommandProperty);
		set => SetValue(NextCueCommandProperty, value);
	}
}
