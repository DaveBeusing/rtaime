// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public partial class ProgramViewer : UserControl
{
	public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
		nameof(Frame), typeof(ImageSource), typeof(ProgramViewer), new PropertyMetadata(null));

	public static readonly DependencyProperty SourceNameProperty = DependencyProperty.Register(
		nameof(SourceName), typeof(string), typeof(ProgramViewer), new PropertyMetadata("—"));

	public static readonly DependencyProperty SourceIdProperty = DependencyProperty.Register(
		nameof(SourceId), typeof(string), typeof(ProgramViewer), new PropertyMetadata("—"));

	public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
		nameof(Format), typeof(string), typeof(ProgramViewer), new PropertyMetadata("No Program monitor frame received."));

	public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
		nameof(State), typeof(string), typeof(ProgramViewer), new PropertyMetadata("DISCONNECTED"));

	public static readonly DependencyProperty OutputStateProperty = DependencyProperty.Register(
		nameof(OutputState), typeof(string), typeof(ProgramViewer), new PropertyMetadata("OUTPUT DISABLED"));

	public static readonly DependencyProperty RecordingStatusProperty = DependencyProperty.Register(
		nameof(RecordingStatus), typeof(string), typeof(ProgramViewer), new PropertyMetadata("UNKNOWN"));

	public static readonly DependencyProperty CommitStatusProperty = DependencyProperty.Register(
		nameof(CommitStatus), typeof(string), typeof(ProgramViewer), new PropertyMetadata("UNCONFIRMED"));

	public static readonly DependencyProperty TransitionStatusProperty = DependencyProperty.Register(
		nameof(TransitionStatus), typeof(string), typeof(ProgramViewer), new PropertyMetadata("IDLE"));

	public static readonly DependencyProperty AudioLeftPeakProperty = DependencyProperty.Register(
		nameof(AudioLeftPeak), typeof(double), typeof(ProgramViewer), new PropertyMetadata(0.0));

	public static readonly DependencyProperty AudioRightPeakProperty = DependencyProperty.Register(
		nameof(AudioRightPeak), typeof(double), typeof(ProgramViewer), new PropertyMetadata(0.0));

	public static readonly DependencyProperty AudioLeftDbProperty = DependencyProperty.Register(
		nameof(AudioLeftDb), typeof(string), typeof(ProgramViewer), new PropertyMetadata("−∞"));

	public static readonly DependencyProperty AudioRightDbProperty = DependencyProperty.Register(
		nameof(AudioRightDb), typeof(string), typeof(ProgramViewer), new PropertyMetadata("−∞"));

	public static readonly DependencyProperty ClippingProperty = DependencyProperty.Register(
		nameof(Clipping), typeof(bool), typeof(ProgramViewer), new PropertyMetadata(false));

	public static readonly DependencyProperty MaximizeCommandProperty = DependencyProperty.Register(
		nameof(MaximizeCommand), typeof(ICommand), typeof(ProgramViewer), new PropertyMetadata(null));

	public ProgramViewer()
	{
		InitializeComponent();
	}

	public ImageSource? Frame { get => (ImageSource?)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
	public string SourceName { get => (string)GetValue(SourceNameProperty); set => SetValue(SourceNameProperty, value); }
	public string SourceId { get => (string)GetValue(SourceIdProperty); set => SetValue(SourceIdProperty, value); }
	public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
	public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
	public string OutputState { get => (string)GetValue(OutputStateProperty); set => SetValue(OutputStateProperty, value); }
	public string RecordingStatus { get => (string)GetValue(RecordingStatusProperty); set => SetValue(RecordingStatusProperty, value); }
	public string CommitStatus { get => (string)GetValue(CommitStatusProperty); set => SetValue(CommitStatusProperty, value); }
	public string TransitionStatus { get => (string)GetValue(TransitionStatusProperty); set => SetValue(TransitionStatusProperty, value); }
	public double AudioLeftPeak { get => (double)GetValue(AudioLeftPeakProperty); set => SetValue(AudioLeftPeakProperty, value); }
	public double AudioRightPeak { get => (double)GetValue(AudioRightPeakProperty); set => SetValue(AudioRightPeakProperty, value); }
	public string AudioLeftDb { get => (string)GetValue(AudioLeftDbProperty); set => SetValue(AudioLeftDbProperty, value); }
	public string AudioRightDb { get => (string)GetValue(AudioRightDbProperty); set => SetValue(AudioRightDbProperty, value); }
	public bool Clipping { get => (bool)GetValue(ClippingProperty); set => SetValue(ClippingProperty, value); }
	public ICommand? MaximizeCommand { get => (ICommand?)GetValue(MaximizeCommandProperty); set => SetValue(MaximizeCommandProperty, value); }
}