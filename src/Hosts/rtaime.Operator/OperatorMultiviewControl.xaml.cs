// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace rtaime.Operator;

public partial class OperatorMultiviewControl : UserControl
{
	public static readonly DependencyProperty PreviewImageProperty = DependencyProperty.Register(
		nameof(PreviewImage),
		typeof(ImageSource),
		typeof(OperatorMultiviewControl));

	public static readonly DependencyProperty ProgramImageProperty = DependencyProperty.Register(
		nameof(ProgramImage),
		typeof(ImageSource),
		typeof(OperatorMultiviewControl));

	public static readonly DependencyProperty PreviewStateProperty = DependencyProperty.Register(
		nameof(PreviewState),
		typeof(string),
		typeof(OperatorMultiviewControl),
		new PropertyMetadata("NO SIGNAL"));

	public static readonly DependencyProperty ProgramStateProperty = DependencyProperty.Register(
		nameof(ProgramState),
		typeof(string),
		typeof(OperatorMultiviewControl),
		new PropertyMetadata("NO SIGNAL"));

	public static readonly DependencyProperty SourcesProperty = DependencyProperty.Register(
		nameof(Sources),
		typeof(IEnumerable),
		typeof(OperatorMultiviewControl));

	public OperatorMultiviewControl()
	{
		InitializeComponent();
	}

	public ImageSource? PreviewImage
	{
		get => (ImageSource?)GetValue(PreviewImageProperty);
		set => SetValue(PreviewImageProperty, value);
	}

	public ImageSource? ProgramImage
	{
		get => (ImageSource?)GetValue(ProgramImageProperty);
		set => SetValue(ProgramImageProperty, value);
	}

	public string PreviewState
	{
		get => (string)GetValue(PreviewStateProperty);
		set => SetValue(PreviewStateProperty, value);
	}

	public string ProgramState
	{
		get => (string)GetValue(ProgramStateProperty);
		set => SetValue(ProgramStateProperty, value);
	}

	public IEnumerable? Sources
	{
		get => (IEnumerable?)GetValue(SourcesProperty);
		set => SetValue(SourcesProperty, value);
	}
}
