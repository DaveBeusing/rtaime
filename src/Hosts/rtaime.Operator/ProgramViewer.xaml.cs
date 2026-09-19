// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;

namespace rtaime.Operator;

public partial class ProgramViewer : MonitorView
{
	public static readonly DependencyProperty OutputStateProperty = DependencyProperty.Register(
		nameof(OutputState),
		typeof(string),
		typeof(ProgramViewer),
		new PropertyMetadata("OUTPUT DISABLED"));

	public static readonly DependencyProperty RecordingStatusProperty = DependencyProperty.Register(
		nameof(RecordingStatus),
		typeof(string),
		typeof(ProgramViewer),
		new PropertyMetadata("UNKNOWN"));

	public static readonly DependencyProperty CommitStatusProperty = DependencyProperty.Register(
		nameof(CommitStatus),
		typeof(string),
		typeof(ProgramViewer),
		new PropertyMetadata("UNCONFIRMED"));

	public static readonly DependencyProperty TransitionStatusProperty = DependencyProperty.Register(
		nameof(TransitionStatus),
		typeof(string),
		typeof(ProgramViewer),
		new PropertyMetadata("IDLE"));

	public static readonly DependencyProperty AudioLeftPeakProperty = DependencyProperty.Register(
		nameof(AudioLeftPeak),
		typeof(double),
		typeof(ProgramViewer),
		new PropertyMetadata(0.0));

	public static readonly DependencyProperty AudioRightPeakProperty = DependencyProperty.Register(
		nameof(AudioRightPeak),
		typeof(double),
		typeof(ProgramViewer),
		new PropertyMetadata(0.0));

	public static readonly DependencyProperty AudioLeftDbProperty = DependencyProperty.Register(
		nameof(AudioLeftDb),
		typeof(string),
		typeof(ProgramViewer),
		new PropertyMetadata("−∞"));

	public static readonly DependencyProperty AudioRightDbProperty = DependencyProperty.Register(
		nameof(AudioRightDb),
		typeof(string),
		typeof(ProgramViewer),
		new PropertyMetadata("−∞"));

	public static readonly DependencyProperty ClippingProperty = DependencyProperty.Register(
		nameof(Clipping),
		typeof(bool),
		typeof(ProgramViewer),
		new PropertyMetadata(false));

	public ProgramViewer()
	{
		InitializeComponent();
	}

	public string OutputState
	{
		get => (string)GetValue(OutputStateProperty);
		set => SetValue(OutputStateProperty, value);
	}

	public string RecordingStatus
	{
		get => (string)GetValue(RecordingStatusProperty);
		set => SetValue(RecordingStatusProperty, value);
	}

	public string CommitStatus
	{
		get => (string)GetValue(CommitStatusProperty);
		set => SetValue(CommitStatusProperty, value);
	}

	public string TransitionStatus
	{
		get => (string)GetValue(TransitionStatusProperty);
		set => SetValue(TransitionStatusProperty, value);
	}

	public double AudioLeftPeak
	{
		get => (double)GetValue(AudioLeftPeakProperty);
		set => SetValue(AudioLeftPeakProperty, value);
	}

	public double AudioRightPeak
	{
		get => (double)GetValue(AudioRightPeakProperty);
		set => SetValue(AudioRightPeakProperty, value);
	}

	public string AudioLeftDb
	{
		get => (string)GetValue(AudioLeftDbProperty);
		set => SetValue(AudioLeftDbProperty, value);
	}

	public string AudioRightDb
	{
		get => (string)GetValue(AudioRightDbProperty);
		set => SetValue(AudioRightDbProperty, value);
	}

	public bool Clipping
	{
		get => (bool)GetValue(ClippingProperty);
		set => SetValue(ClippingProperty, value);
	}
}
