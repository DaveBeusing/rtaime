// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

public partial class PreviewViewer : UserControl
{
	public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
		nameof(Frame), typeof(ImageSource), typeof(PreviewViewer), new PropertyMetadata(null));

	public static readonly DependencyProperty SourceNameProperty = DependencyProperty.Register(
		nameof(SourceName), typeof(string), typeof(PreviewViewer), new PropertyMetadata("—"));

	public static readonly DependencyProperty SourceIdProperty = DependencyProperty.Register(
		nameof(SourceId), typeof(string), typeof(PreviewViewer), new PropertyMetadata("—"));

	public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
		nameof(Format), typeof(string), typeof(PreviewViewer), new PropertyMetadata("No Preview monitor frame received."));

	public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
		nameof(State), typeof(string), typeof(PreviewViewer), new PropertyMetadata("DISCONNECTED"));

	public static readonly DependencyProperty MaximizeCommandProperty = DependencyProperty.Register(
		nameof(MaximizeCommand), typeof(ICommand), typeof(PreviewViewer), new PropertyMetadata(null));

	public PreviewViewer()
	{
		InitializeComponent();
	}

	public ImageSource? Frame
	{
		get => (ImageSource?)GetValue(FrameProperty);
		set => SetValue(FrameProperty, value);
	}

	public string SourceName
	{
		get => (string)GetValue(SourceNameProperty);
		set => SetValue(SourceNameProperty, value);
	}

	public string SourceId
	{
		get => (string)GetValue(SourceIdProperty);
		set => SetValue(SourceIdProperty, value);
	}

	public string Format
	{
		get => (string)GetValue(FormatProperty);
		set => SetValue(FormatProperty, value);
	}

	public string State
	{
		get => (string)GetValue(StateProperty);
		set => SetValue(StateProperty, value);
	}

	public ICommand? MaximizeCommand
	{
		get => (ICommand?)GetValue(MaximizeCommandProperty);
		set => SetValue(MaximizeCommandProperty, value);
	}
}