// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
namespace rtaime.Operator;

public partial class MainWindow
{
    // Client content excludes native caption/borders; viewer height comes from its actual scroll viewport.
    private void OnWorkspaceViewportChanged(object sender, SizeChangedEventArgs e)
    {
        Shell.UpdateViewportSize(e.NewSize.Width, e.NewSize.Height);
        if (CenterWorkspaceScroll is not null)
            Shell.UpdateProductionViewportHeight(CenterWorkspaceScroll.ViewportHeight);
    }
    private void OnProductionViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CenterWorkspaceScroll)) return;
        if (Content is FrameworkElement root)
            Shell.UpdateViewportSize(root.ActualWidth, root.ActualHeight);
        Shell.UpdateProductionViewportHeight(e.ViewportHeight);
    }
}
