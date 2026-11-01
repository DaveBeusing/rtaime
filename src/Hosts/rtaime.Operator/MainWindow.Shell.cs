// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using rtaime.Operator.Controls;
namespace rtaime.Operator;

public partial class MainWindow
{
    private bool _shellStripInputAttached;

    // All global menus are lightweight projections over the existing command
    // catalogue. They have no independent selection state or production authority.
    internal RtaimeContextMenu CreateGlobalShellMenu(string sectionName)
    {
        var menu = new RtaimeContextMenu();
        if (string.Equals(sectionName, "Overflow", StringComparison.Ordinal))
        {
            // The project-owned menu-item template intentionally has no
            // nested popup. Show seven choices, then open the chosen existing
            // section as a separate themed context menu.
            foreach (var section in OperatorCommandCatalog.GlobalShellMenus)
            {
                var selectedName = section.Name;
                var item = new RtaimeMenuItem
                {
                    Header = selectedName,
                    ToolTip = "Open " + selectedName + " actions"
                };
                System.Windows.Automation.AutomationProperties.SetName(item, "Open " + selectedName + " menu");
                item.Click += (_, _) =>
                {
                    menu.IsOpen = false;
                    GlobalMenuOverflowButton.Dispatcher.BeginInvoke(
                        new Action(() => OpenGlobalShellMenu(GlobalMenuOverflowButton, selectedName)),
                        System.Windows.Threading.DispatcherPriority.Background);
                };
                menu.Items.Add(item);
            }
        }
        else
        {
            var section = OperatorCommandCatalog.GlobalShellMenus
                .FirstOrDefault(candidate => string.Equals(candidate.Name, sectionName, StringComparison.Ordinal));
            if (section is not null)
                foreach (var item in CreateGlobalShellMenuItems(section))
                    menu.Items.Add(item);
        }

        return menu;
    }

    private IEnumerable<object> CreateGlobalShellMenuItems(OperatorShellMenuSection section)
    {
        foreach (var slot in section.Slots)
        {
            if (slot.CommandId is null)
            {
                yield return new RtaimeMenuSeparator();
                continue;
            }

            if (slot.CommandId == "show-identity")
            {
                var show = DataContext is OperatorViewModel owner ? owner.ShowProjectName : "UNAVAILABLE";
                var detail = DataContext is OperatorViewModel detailOwner
                    ? detailOwner.ShowProjectDetail : "Project identity unavailable.";
                yield return new RtaimeMenuItem
                {
                    Header = "Current show · " + show,
                    IsEnabled = false,
                    ToolTip = detail
                };
                continue;
            }

            if (slot.CommandId == "demo-open")
            {
                var item = new RtaimeMenuItem
                {
                    Header = "Prepare Demo Production (explicit)",
                    Command = DemoProduction.OpenCommand,
                    ToolTip = "Deliberately prepare the bundled demo; does not TAKE Program."
                };
                System.Windows.Automation.AutomationProperties.SetName(item, "Prepare demo production");
                ToolTipService.SetShowOnDisabled(item, true);
                yield return item;
                continue;
            }

            if (slot.CommandId == "view-scopes")
            {
                var item = new RtaimeMenuItem
                {
                    Header = (Monitoring.ScopesEnabled ? "✓ " : "   ") + "Monitoring scopes (sampled)",
                    Command = Monitoring.ToggleScopesCommand,
                    ToolTip = "Toggle the existing sampled monitoring scopes; never changes Program."
                };
                System.Windows.Automation.AutomationProperties.SetName(item, "Toggle sampled monitoring scopes");
                yield return item;
                continue;
            }

            var entry = CommandPalette.Entries.FirstOrDefault(candidate => candidate.Id == slot.CommandId);
            if (entry is null)
            {
                // An unknown command never becomes an unguarded shell action.
                yield return new RtaimeMenuItem
                {
                    Header = slot.Label ?? "Unavailable command: " + slot.CommandId,
                    IsEnabled = false,
                    ToolTip = "The command is not present in the active Operator catalogue."
                };
                continue;
            }

            var reason = entry.Reason;
            var currentWorkspace = entry.Descriptor.Parameter is string id &&
                string.Equals(Shell.SelectedWorkspace, id, StringComparison.Ordinal);
            var itemFromCatalogue = new RtaimeMenuItem
            {
                Header = (currentWorkspace ? "✓ " : "") + (slot.Label ?? entry.Label),
                Command = entry.Descriptor.Command,
                CommandParameter = entry.Descriptor.Parameter,
                InputGestureText = entry.Gesture,
                IsEnabled = reason.Length == 0,
                ToolTip = reason.Length == 0
                    ? "Explicit menu selection invokes the existing command; opening the menu is read-only."
                    : reason
            };
            System.Windows.Automation.AutomationProperties.SetName(itemFromCatalogue, entry.Label);
            ToolTipService.SetShowOnDisabled(itemFromCatalogue, true);
            yield return itemFromCatalogue;
        }
    }

    private void OnShellMenuButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionName } anchor && anchor.ContextMenu is null)
            anchor.ContextMenu = CreateGlobalShellMenu(sectionName);
    }

    private void OnShellMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionName } anchor)
            OpenGlobalShellMenu(anchor, sectionName);
    }

    private void OnShellMenuButtonPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            return;
        }

        if (e.Handled || e.IsRepeat ||
            e.Key is not Key.Apps && !(e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            return;

        if (sender is FrameworkElement { Tag: string sectionName } anchor)
        {
            e.Handled = true;
            OpenGlobalShellMenu(anchor, sectionName);
        }
    }

    private void OpenGlobalShellMenu(FrameworkElement anchor, string sectionName)
    {
        if (!anchor.IsVisible) return;
        var menu = CreateGlobalShellMenu(sectionName);
        if (ReferenceEquals(anchor, GlobalMenuOverflowButton) && sectionName != "Overflow")
        {
            var back = new RtaimeMenuItem { Header = "← All menus",
                ToolTip = "Return to Project, Edit, View, Media, Tools, Window and Help." };
            System.Windows.Automation.AutomationProperties.SetName(back, "Return to all menus");
            back.Click += (_, _) =>
            {
                menu.IsOpen = false;
                anchor.Dispatcher.BeginInvoke(
                    new Action(() => OpenGlobalShellMenu(anchor, "Overflow")),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            menu.Items.Insert(0, new RtaimeMenuSeparator());
            menu.Items.Insert(0, back);
        }
        var closedByEscape = false;
        menu.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) closedByEscape = true;
        };
        menu.Closed += (_, _) =>
        {
            if (closedByEscape && anchor.IsVisible && !_shutdownStarted)
                anchor.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (anchor.IsVisible && !_shutdownStarted) anchor.Focus();
                }), System.Windows.Threading.DispatcherPriority.Input);
        };
        anchor.ContextMenu = menu;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

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

    private void OnLiveViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor)
            OpenLiveViewMenu(anchor);
    }

    private void OnLiveActionsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.IsRepeat ||
            e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
            return;
        if (e.Key is Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            OpenLiveViewMenu(LiveViewButton);
            e.Handled = true;
        }
    }

    private static void OpenLiveViewMenu(FrameworkElement anchor)
    {
        if (anchor.ContextMenu is not RtaimeContextMenu menu || !anchor.IsVisible)
            return;

        // Presentation-only access: commands remain owned by the existing shell.
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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
