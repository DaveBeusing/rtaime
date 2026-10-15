// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace rtaime.Operator;

public partial class OperatorLibraryWindow : Window
{
    private readonly IInputElement? _previousFocus = Keyboard.FocusedElement;
    public OperatorLibraryWindow(OperatorLibraryViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => { if (IsVisible) { Activate(); FocusSearch(); } }, DispatcherPriority.Input);
        Closed += (_, _) =>
        {
            if (_previousFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
                previous.Dispatcher.BeginInvoke(() =>
                {
                    var newer = Application.Current?.Windows.OfType<Window>().Any(w => w.IsVisible &&
                        w is OperatorLibraryWindow or OperatorCommandPaletteWindow or ShowReadinessWindow) == true;
                    if (!newer && previous.IsVisible && previous.IsEnabled) previous.Focus();
                }, DispatcherPriority.Input);
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
        if (key == Key.Escape && Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true })
        { base.OnPreviewKeyDown(e); return; }
        if (key == Key.Escape) { e.Handled = true; if (!e.IsRepeat) Close(); return; }
        // Text and list keys never enter the MainWindow production command router.
        if (key == Key.Return && (SearchBox.IsKeyboardFocusWithin || LibraryRows.IsKeyboardFocusWithin ||
            OperatorKeyboardCommandRegistry.IsTextEntryContext(Keyboard.FocusedElement as DependencyObject)))
        { e.Handled = true; return; }
        if (e.IsRepeat && key is Key.Space or Key.Return) { e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
