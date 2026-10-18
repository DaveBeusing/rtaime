// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace rtaime.Operator;

internal static class LiveConsoleInput
{
    internal static void Attach(UIElement surface)
    {
        surface.PreviewMouseLeftButtonDown += (_, e) => {
            if (e.ClickCount > 1 && FindButton(e.OriginalSource as DependencyObject) is not null)
                e.Handled = true;
        };
        surface.PreviewKeyDown += (_, e) => {
            if (e.IsRepeat && e.Key is Key.Enter or Key.Space &&
                FindButton(Keyboard.FocusedElement as DependencyObject) is not null) e.Handled = true;
        };
    }
    internal static ButtonBase? FindButton(DependencyObject? element)
    {
        for (var current = element; current is not null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase button) return button;
        return null;
    }
    internal static bool IsCueSurface(DependencyObject? element)
    {
        for (var current = element; current is not null; current = current is Visual
            ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is LiveControlsControl or LiveSceneCueControl or RundownControl or ShowControlCueStackControl) return true;
        return false;
    }
}
