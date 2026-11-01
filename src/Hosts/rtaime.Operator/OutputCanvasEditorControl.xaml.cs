// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace rtaime.Operator;

public partial class OutputCanvasEditorControl : UserControl
{
    public OutputCanvasEditorControl() => InitializeComponent();

    private void OpenEditorMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement target)
            OpenEditorMenu(target);
    }

    private void OnEditorMenuKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Apps && !(e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            return;
        e.Handled = true;
        if (!e.IsRepeat && sender is FrameworkElement target)
            OpenEditorMenu(target);
    }

    private static void OpenEditorMenu(FrameworkElement target)
    {
        if (target.ContextMenu is not { } menu)
            return;

        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    private void ToggleAlignmentTools_Click(object sender, RoutedEventArgs e) =>
        ToggleDetails("AlignmentTools");

    private void ToggleGuideDetails_Click(object sender, RoutedEventArgs e) =>
        ToggleDetails("GuideDetails");

    private void ToggleDetails(string name)
    {
        if (FindName(name) is not FrameworkElement panel)
            return;
        panel.Visibility = panel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }
}
