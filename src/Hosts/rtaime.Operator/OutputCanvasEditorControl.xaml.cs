// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows;
using System.Windows.Controls;

namespace rtaime.Operator;

public partial class OutputCanvasEditorControl : UserControl
{
    public OutputCanvasEditorControl() => InitializeComponent();

    private void OpenEditorMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } target)
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
