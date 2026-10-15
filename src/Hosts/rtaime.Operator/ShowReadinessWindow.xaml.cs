// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Input;
namespace rtaime.Operator;
public partial class ShowReadinessWindow : Window
{
	public ShowReadinessWindow(ShowReadinessViewModel model)
	{
		DataContext = model;
		InitializeComponent();
		Loaded += (_, _) => { if (ReadCatalogueButton.IsEnabled) ReadCatalogueButton.Focus(); else CloseChecksButton.Focus(); };
	}
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
		base.OnPreviewKeyDown(e);
	}
	private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
