// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;

namespace rtaime.Operator;

public partial class OutputRoutingHealthControl : UserControl
{
	public static readonly DependencyProperty CompactModeProperty = DependencyProperty.Register(
		nameof(CompactMode),
		typeof(bool),
		typeof(OutputRoutingHealthControl),
		new FrameworkPropertyMetadata(false));

	public OutputRoutingHealthControl()
	{
		InitializeComponent();
	}

	public bool CompactMode
	{
		get => (bool)GetValue(CompactModeProperty);
		set => SetValue(CompactModeProperty, value);
	}
}
