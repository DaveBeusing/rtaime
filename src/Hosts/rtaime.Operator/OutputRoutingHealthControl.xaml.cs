// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace rtaime.Operator;

public partial class OutputRoutingHealthControl : UserControl
{
	public static readonly DependencyProperty CompactModeProperty = DependencyProperty.Register(
		nameof(CompactMode),
		typeof(bool),
		typeof(OutputRoutingHealthControl),
		new FrameworkPropertyMetadata(false));

	public static readonly DependencyProperty ProgramImageProperty = DependencyProperty.Register(
		nameof(ProgramImage),
		typeof(ImageSource),
		typeof(OutputRoutingHealthControl));

	public static readonly DependencyProperty PreviewImageProperty = DependencyProperty.Register(
		nameof(PreviewImage),
		typeof(ImageSource),
		typeof(OutputRoutingHealthControl));

	public OutputRoutingHealthControl()
	{
		InitializeComponent();
	}

	public ImageSource? ProgramImage
	{
		get => (ImageSource?)GetValue(ProgramImageProperty);
		set => SetValue(ProgramImageProperty, value);
	}

	public ImageSource? PreviewImage
	{
		get => (ImageSource?)GetValue(PreviewImageProperty);
		set => SetValue(PreviewImageProperty, value);
	}

	public bool CompactMode
	{
		get => (bool)GetValue(CompactModeProperty);
		set => SetValue(CompactModeProperty, value);
	}
}
