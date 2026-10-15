// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace rtaime.Operator;

public partial class OperatorCommandPaletteWindow : Window
{
	private readonly OperatorCommandPaletteViewModel _model;
	private readonly IInputElement? _previousFocus;
	private bool _executed;
	public OperatorCommandPaletteWindow(OperatorCommandPaletteViewModel model)
	{
		_model = model;
		_previousFocus = Keyboard.FocusedElement;
		InitializeComponent();
		DataContext = model;
		model.ExecutionCompleted += OnExecuted;
		Loaded += (_, _) => FocusSearch();
		Closed += (_, _) =>
		{
			model.ExecutionCompleted -= OnExecuted;
			if (!_executed && _previousFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
				previous.Dispatcher.BeginInvoke(() => previous.Focus(), DispatcherPriority.Input);
		};
	}
	public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		MainWindow.ApplyDarkWindowCaption(this);
		OperatorMonitorGeometry.Restore(this, OperatorMonitorGeometry.Capture(this));
	}
	protected override void OnPreviewKeyDown(KeyEventArgs e)
	{
		var key = e.Key == Key.System ? e.SystemKey : e.Key;
		var modifiers = Keyboard.Modifiers;
		if (key == Key.Escape && Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) { base.OnPreviewKeyDown(e); return; }
		if ((key == Key.Escape && modifiers == ModifierKeys.None) || (key == Key.K && modifiers == ModifierKeys.Control))
		{ e.Handled = true; if (!e.IsRepeat) Close(); return; }
		if (e.IsRepeat && key is Key.Return or Key.Space) { e.Handled = true; return; }
		var searching = SearchBox.IsKeyboardFocusWithin || CommandResults.IsKeyboardFocusWithin;
		if (searching && modifiers == ModifierKeys.None && key is Key.Up or Key.Down)
		{
			e.Handled = true; _model.MoveSelection(key == Key.Up ? -1 : 1);
			if (_model.Selected is { } selected) CommandResults.ScrollIntoView(selected);
			return;
		}
		// This window never forwards keys to the production router. Enter in an
		// editor or result list cannot become AUTO/CUT, even with modifiers.
		if (key == Key.Return && (searching || OperatorKeyboardCommandRegistry.IsTextEntryContext(Keyboard.FocusedElement as DependencyObject)))
		{
			e.Handled = true;
			if (searching && modifiers == ModifierKeys.None && !e.IsRepeat) _model.ExecuteSelected(true);
			return;
		}
		base.OnPreviewKeyDown(e);
	}
	private void OnExecuted(object? sender, EventArgs e)
	{
		_executed = true;
		Close();
	}
	private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
