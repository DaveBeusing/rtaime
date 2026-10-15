// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
namespace rtaime.Operator;
public partial class ShowReadinessWindow : Window
{
	public ShowReadinessWindow(ShowReadinessViewModel model)
	{
		DataContext = model;
		InitializeComponent();
		// Older owned-window close callbacks may still be restoring focus. Establish
		// this view's focus after those queued input callbacks have drained.
		Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
		{
			if (!IsVisible) return;
			Activate();
			Keyboard.Focus(RefreshChecksButton);
		}, DispatcherPriority.Input);
	}
	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		MainWindow.ApplyDarkWindowCaption(this);
		OperatorMonitorGeometry.Restore(this, OperatorMonitorGeometry.Capture(this));
	}
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		if (e.Key == Key.Escape) { e.Handled = true; if (!e.IsRepeat) Close(); return; }
		base.OnPreviewKeyDown(e);
	}
	private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
