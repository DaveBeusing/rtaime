// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;

namespace rtaime.Operator;

public partial class ProductionMacroControl : UserControl
{
	private bool _loadedOnce;

	public ProductionMacroControl() => InitializeComponent();

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (_loadedOnce || DataContext is not ProductionMacroViewModel viewModel)
			return;
		_loadedOnce = true;
		await viewModel.RefreshAsync();
	}
}
