// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using rtaime.Operator.Controls;
namespace rtaime.Operator;

public partial class MainWindow
{
    private bool _shellStripInputAttached;

    private void OnMoreNavigationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = CreateMoreNavigationMenu();
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Right;
        menu.IsOpen = true;
    }

    // Recreate the small, keyboard-addressable menu from the same links as the rail.
    // No destination, selection owner, production command or persisted layout is duplicated.
    internal RtaimeContextMenu CreateMoreNavigationMenu()
    {
        var menu = new RtaimeContextMenu();
        foreach (var group in Shell.SecondaryNavigationGroups)
        {
            if (menu.Items.Count != 0) menu.Items.Add(new RtaimeMenuSeparator());
            menu.Items.Add(new RtaimeMenuItem { Header = group.Label, IsEnabled = false });
            foreach (var link in group.Items)
            {
                var item = new RtaimeMenuItem
                {
                    Header = (link.IsActive ? "✓ " : "   ") + link.Label,
                    Command = link.Command,
                    CommandParameter = link.Id,
                    ToolTip = link.Description
                };
                System.Windows.Automation.AutomationProperties.SetName(item, link.Label + " workspace");
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new RtaimeMenuSeparator());
        menu.Items.Add(new RtaimeMenuItem
        {
            Header = "Show checks and activity",
            Command = OpenShowChecksCommand
        });
        menu.Items.Add(new RtaimeMenuItem
        {
            Header = "Commands and shortcuts (Ctrl+K)",
            Command = OpenCommandPaletteCommand
        });
        return menu;
    }

    private void OnShellLiveStripLoaded(object sender, RoutedEventArgs e)
    {
        if (_shellStripInputAttached) return;
        _shellStripInputAttached = true;
        LiveConsoleInput.Attach(LiveActionStrip);
    }
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
