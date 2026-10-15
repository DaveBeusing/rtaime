// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace rtaime.Operator;

public partial class OperatorCommandPaletteWindow : Window
{
	private readonly OperatorCommandPaletteViewModel _model;
	private readonly IInputElement? _previousFocus;
	private bool _executed;
    private bool _focusQueued;
	public OperatorCommandPaletteWindow(OperatorCommandPaletteViewModel model)
	{
		_model = model;
		_previousFocus = Keyboard.FocusedElement;
		InitializeComponent();
		DataContext = model;
		model.ExecutionCompleted += OnExecuted;
		model.PropertyChanged += OnModelChanged;
		Loaded += (_, _) => QueueFocus();
		Closed += (_, _) =>
		{
			model.ExecutionCompleted -= OnExecuted;
            model.PropertyChanged -= OnModelChanged;
			if (!_executed && _previousFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
				previous.Dispatcher.BeginInvoke(() =>
                {
                    var newerSurface = Application.Current?.Windows.OfType<Window>().Any(window =>
                        window.IsVisible && window is OperatorCommandPaletteWindow or ShowReadinessWindow) == true;
                    if (!newerSurface && previous.IsVisible && previous.IsEnabled) previous.Focus();
                }, DispatcherPriority.Input);
		};
	}
	public void FocusSearch() { FocusCurrentMode(); }
    private void FocusCurrentMode()
    {
        if (_model.IsBrowsingCommands) { SearchBox.Focus(); SearchBox.SelectAll(); }
        else { ProfileNameBox.Focus(); ProfileNameBox.SelectAll(); }
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperatorCommandPaletteViewModel.IsBrowsingCommands)) QueueFocus();
    }
    private void QueueFocus()
    {
        if (_focusQueued) return;
        _focusQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _focusQueued = false;
            if (IsVisible) { Activate(); FocusCurrentMode(); }
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
		var key = e.Key == Key.System ? e.SystemKey : e.Key;
		var modifiers = Keyboard.Modifiers;
		if (key == Key.Escape && Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) { base.OnPreviewKeyDown(e); return; }
		if ((key == Key.Escape && modifiers == ModifierKeys.None) || (key == Key.K && modifiers == ModifierKeys.Control))
		{ e.Handled = true; if (!e.IsRepeat) Close(); return; }
		if (e.IsRepeat && key is Key.Return or Key.Space) { e.Handled = true; return; }
		var searching = _model.IsBrowsingCommands && (SearchBox.IsKeyboardFocusWithin || CommandResults.IsKeyboardFocusWithin);
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
