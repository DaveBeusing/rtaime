// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using rtaime.Client;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

[Collection("Operator application")]
public sealed class OperatorShellMenuTests
{
    private const string ChildVariable = "RTAIME_GLOBAL_SHELL_MENU_CHILD";

    [Fact]
    public void Seven_menus_have_distinct_canonical_action_slots()
    {
        var groups = OperatorCommandCatalog.GlobalShellMenus;
        Assert.Equal(new[] { "Project", "Edit", "View", "Media", "Tools", "Window", "Help" },
            groups.Select(group => group.Name));
        Assert.Equal(groups.Count, groups.Select(group => group.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (var group in groups)
        {
            var actions = group.Slots.Where(slot => slot.CommandId is not null).Select(slot => slot.CommandId).ToArray();
            Assert.NotEmpty(actions);
            Assert.Equal(actions.Length, actions.Distinct(StringComparer.Ordinal).Count());
        }

        var view = groups.Single(group => group.Name == "View");
        foreach (var action in new[] { "pane-library", "pane-inspector", "view-scopes",
            "preview-view", "program-view", "dual-view" })
            Assert.Contains(view.Slots, slot => slot.CommandId == action);

        var project = groups.Single(group => group.Name == "Project");
        Assert.Contains(project.Slots, slot => slot.CommandId == "show-identity");
        Assert.Contains(project.Slots, slot => slot.CommandId == "demo-open");

        var window = groups.Single(group => group.Name == "Window");
        foreach (var action in new[] { "dock-toggle", "dock-float", "dock-focus",
            "dock-cinema", "workspace-restore", "workspace-templates" })
            Assert.Contains(window.Slots, slot => slot.CommandId == action);

        var allWorkspaces = OperatorWorkspaceRegistry.Destinations.Select(item => item.Id).ToArray();
        Assert.Equal(new[] { "LIVE", "SCENES", "MULTIVIEW", "MEDIA", "EDIT",
            "COMPOSITING", "OUTPUTS", "HEALTH", "AI", "SETTINGS" }, allWorkspaces);
        foreach (var workspace in allWorkspaces)
            Assert.Contains(groups.SelectMany(group => group.Slots), slot =>
                slot.CommandId == "workspace-" + workspace.ToLowerInvariant());
    }

    [Fact]
    public void Header_is_read_only_and_compact_menu_preserves_permanent_evidence()
    {
        var doc = XDocument.Load(RepositoryFile("src", "Hosts", "rtaime.Operator", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var bar = Assert.Single(doc.Descendants(), node => node.Attribute(x + "Name")?.Value == "TopBar");
        var identity = Assert.Single(bar.Descendants(), node => node.Attribute(x + "Name")?.Value == "TopBarProjectIdentity");
        Assert.Equal("Border", identity.Name.LocalName);
        Assert.Null(identity.Attribute("Command"));
        Assert.Contains(identity.Descendants(), node => node.Attribute("Text")?.Value == "{Binding ShowProjectName}");
        Assert.Contains(identity.Descendants(), node => node.Attribute("Text")?.Value?.Contains("DemoProduction.State", StringComparison.Ordinal) == true);

        var demo = Assert.Single(identity.Descendants(), node => node.Name.LocalName == "RtaimeButton");
        Assert.Equal("DEMO", demo.Attribute("Content")?.Value);
        Assert.Contains("DemoProduction.OpenCommand", demo.Attribute("Command")?.Value);
        Assert.Equal("Open Demo Production", demo.Attribute("AutomationProperties.Name")?.Value);

        var desktop = Assert.Single(bar.Descendants(), node => node.Attribute(x + "Name")?.Value == "GlobalDesktopMenuBar");
        Assert.Contains("Shell.SecondaryMetricVisibility", desktop.Attribute("Visibility")?.Value);
        foreach (var name in OperatorCommandCatalog.GlobalShellMenus.Select(group => group.Name))
        {
            var button = Assert.Single(desktop.Descendants(), node =>
                node.Attribute(x + "Name")?.Value == "Global" + name + "MenuButton");
            Assert.Equal(name, button.Attribute("Tag")?.Value);
            Assert.Equal("OnShellMenuButtonClick", button.Attribute("Click")?.Value);
            Assert.Equal("OnShellMenuButtonPreviewKeyDown", button.Attribute("PreviewKeyDown")?.Value);
        }

        var overflow = Assert.Single(bar.Descendants(), node => node.Attribute(x + "Name")?.Value == "GlobalMenuOverflowButton");
        Assert.Equal("Overflow", overflow.Attribute("Tag")?.Value);
        Assert.Contains(overflow.Descendants(), node => node.Name.LocalName == "DataTrigger" &&
            node.Attribute("Binding")?.Value?.Contains("Shell.SecondaryMetricVisibility", StringComparison.Ordinal) == true &&
            node.Attribute("Value")?.Value == "Collapsed");
        foreach (var required in new[] { "GlobalReadinessState", "GlobalReadinessLabel",
            "GlobalReadinessSummary", "ProgramSourceName", "RecordingStatus", "ProgramTransmissionState" })
            Assert.Contains(bar.Descendants(), node => node.Attributes().Any(attribute =>
                attribute.Value.Contains(required, StringComparison.Ordinal)));
        Assert.Single(bar.Descendants(), node => node.Name.LocalName == "RtaimeGlobalStatusButton");
        Assert.Single(bar.Descendants(), node => node.Attribute(x + "Name")?.Value == "LiveProgramIdentity");
        Assert.Single(bar.Descendants(), node => node.Attribute(x + "Name")?.Value == "TopBarRecordingState");

        // The new top-level menus use the existing top-bar row instead of
        // adding a second header row or reducing the video viewport.
        var rootGrid = Assert.Single(doc.Root!.Elements(), node => node.Name.LocalName == "Grid");
        var rows = Assert.Single(rootGrid.Elements(), node => node.Name.LocalName == "Grid.RowDefinitions");
        Assert.Equal(3, rows.Elements().Count());
    }

    [Fact]
    public void Menu_code_reuses_palette_entries_and_does_not_submit_production_mutations()
    {
        var code = File.ReadAllText(RepositoryFile("src", "Hosts", "rtaime.Operator", "MainWindow.Shell.cs"));
        Assert.Contains("CommandPalette.Entries.FirstOrDefault", code);
        Assert.Contains("entry.Descriptor.Command", code);
        Assert.Contains("entry.Descriptor.Parameter", code);
        Assert.Contains("Monitoring.ToggleScopesCommand", code);
        Assert.Contains("DemoProduction.OpenCommand", code);
        Assert.Contains("CreateMoreNavigationMenu()", code);
        Assert.Contains("e.Key == Key.Escape", code);
        Assert.DoesNotContain("CutCommand.Execute", code);
        Assert.DoesNotContain("DissolveCommand.Execute", code);
        Assert.DoesNotContain("SetPreviewCommand.Execute", code);
        Assert.DoesNotContain("StartRecordingCommand.Execute", code);
    }

    [Fact]
    public async Task Real_operator_menus_preserve_focus_navigation_and_live_authority_at_compact_sizes()
    {
        if (Environment.GetEnvironmentVariable(ChildVariable) != "1")
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(OperatorShellMenuTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.OperatorShellMenuTests." +
                nameof(Real_operator_menus_preserve_focus_navigation_and_live_authority_at_compact_sizes));
            start.ArgumentList.Add("--logger:console;verbosity=detailed");
            start.Environment[ChildVariable] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(process.ExitCode == 0,
                    (await stdout) + Environment.NewLine + await stderr);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            return;
        }

        await VerifyRealWindowAsync();
    }

    private static async Task VerifyRealWindowAsync()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                OperatorResourceApplication? app = null;
                MainWindow? window = null;
                var folder = Path.Combine(Path.GetTempPath(), "rtaime-shell-menu-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(folder);
                    app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                    var owner = new OperatorViewModel();
                    window = new MainWindow(owner,
                        new OperatorLayoutStore(Path.Combine(folder, "layout.json")),
                        new OperatorSessionRecoveryStore(Path.Combine(folder, "session.json")),
                        new OperatorLibraryPreferenceStore(Path.Combine(folder, "library.json")));
                    window.Width = 1600;
                    window.Height = 900;
                    window.Show();
                    window.Activate();
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);

                    var route = (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId);
                    var recording = owner.RecordingStatus;
                    foreach (var section in OperatorCommandCatalog.GlobalShellMenus)
                    {
                        var button = Assert.IsType<RtaimeButton>(window.FindName("Global" + section.Name + "MenuButton"));
                        Assert.True(button.IsVisible);
                        Assert.True(button.Focusable);
                        Assert.NotNull(button.ContextMenu);
                        var menu = window.CreateGlobalShellMenu(section.Name);
                        Assert.NotEmpty(menu.Items);
                        Assert.All(menu.Items.OfType<RtaimeMenuItem>(), item =>
                            Assert.IsNotType<MenuItem>(item)); // custom menu items, not stock chrome
                    }

                    var project = window.CreateGlobalShellMenu("Project");
                    var demo = Assert.Single(project.Items.OfType<RtaimeMenuItem>(), item =>
                        Equals(item.Header, "Prepare Demo Production (explicit)"));
                    Assert.Same(window.DemoProduction.OpenCommand, demo.Command);
                    Assert.Contains(project.Items.OfType<RtaimeMenuItem>(), item =>
                        item.Command == window.OpenShowChecksCommand);
                    var identity = Assert.Single(project.Items.OfType<RtaimeMenuItem>(), item =>
                        item.Header?.ToString()?.StartsWith("Current show", StringComparison.Ordinal) == true);
                    Assert.False(identity.IsEnabled);
                    Assert.Null(identity.Command);

                    var view = window.CreateGlobalShellMenu("View");
                    var library = Assert.Single(view.Items.OfType<RtaimeMenuItem>(), item =>
                        item.Command == window.Shell.ToggleLeftPanelCommand);
                    Assert.Same(window.Shell.ToggleLeftPanelCommand,
                        window.CommandPalette.Entries.Single(entry => entry.Id == "pane-library").Descriptor.Command);
                    Assert.Contains(view.Items.OfType<RtaimeMenuItem>(), item =>
                        item.Command == window.Monitoring.ToggleScopesCommand);
                    var outputs = Assert.Single(view.Items.OfType<RtaimeMenuItem>(), item =>
                        Equals(item.CommandParameter, "OUTPUTS"));
                    Assert.Same(window.Shell.SelectWorkspaceCommand, outputs.Command);

                    var tools = window.CreateGlobalShellMenu("Tools");
                    foreach (var id in new[] { "SCENES", "MULTIVIEW", "COMPOSITING", "AI", "HEALTH", "SETTINGS" })
                        Assert.Contains(tools.Items.OfType<RtaimeMenuItem>(), item =>
                            Equals(item.CommandParameter, id) && item.Command == window.Shell.SelectWorkspaceCommand);

                    var win = window.CreateGlobalShellMenu("Window");
                    Assert.Contains(win.Items.OfType<RtaimeMenuItem>(), item =>
                        item.Command == window.Docking.CinemaCommand);
                    Assert.Contains(win.Items.OfType<RtaimeMenuItem>(), item =>
                        item.Command == window.OpenWorkspaceTemplatesCommand);

                    var more = window.CreateMoreNavigationMenu();
                    Assert.Contains(more.Items.OfType<RtaimeMenuItem>(), item => Equals(item.CommandParameter, "SCENES"));
                    Assert.Contains(more.Items.OfType<RtaimeMenuItem>(), item => Equals(item.CommandParameter, "SETTINGS"));
                    Assert.Equal(route, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
                    Assert.Equal(recording, owner.RecordingStatus);

                    foreach (var (width, height) in new[] { (960d, 540d), (960d, 500d), (1280d, 720d) })
                    {
                        window.Width = width;
                        window.Height = height;
                        window.Shell.UpdateViewportSize(width, height);
                        await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                        var overflowButton = Assert.IsType<RtaimeButton>(window.FindName("GlobalMenuOverflowButton"));
                        var desktop = Assert.IsType<StackPanel>(window.FindName("GlobalDesktopMenuBar"));
                        Assert.True(overflowButton.IsVisible);
                        Assert.False(desktop.IsVisible);
                        Assert.True(overflowButton.Focus());
                        var overflow = window.CreateGlobalShellMenu("Overflow");
                        Assert.Equal(7, overflow.Items.OfType<RtaimeMenuItem>().Count());
                        Assert.All(overflow.Items.OfType<RtaimeMenuItem>(), item =>
                            Assert.NotEmpty(item.Items));
                        Assert.True(overflowButton.IsVisible);
                        var status = Assert.Single(Descendants<RtaimeGlobalStatusButton>(window));
                        Assert.True(status.IsVisible);
                        var programIdentity = Assert.IsType<TextBlock>(window.FindName("LiveProgramIdentity"));
                        Assert.True(programIdentity.IsVisible);
                        Assert.Equal(owner.ProgramSourceName, programIdentity.Text);
                        Assert.Equal(owner.RecordingStatus,
                            Assert.IsType<TextBlock>(window.FindName("TopBarRecordingState")).Text);
                        Assert.Equal(route, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
                    }

                    var compactMenu = Assert.IsType<RtaimeButton>(window.FindName("GlobalMenuOverflowButton"));
                    var source = PresentationSource.FromVisual(compactMenu);
                    Assert.NotNull(source);
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Apps)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    compactMenu.RaiseEvent(key);
                    Assert.True(key.Handled);
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    Assert.True(compactMenu.ContextMenu.IsOpen);
                    Assert.Same(compactMenu, compactMenu.ContextMenu.PlacementTarget);
                    compactMenu.ContextMenu.IsOpen = false;
                    Assert.Equal(route, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));

                    window.Shell.SelectWorkspaceCommand.Execute("OUTPUTS");
                    Assert.Equal("OUTPUTS", window.Shell.SelectedWorkspace);
                    Assert.Equal(route, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
                }
                catch (Exception error) { completed.TrySetException(error); }
                finally
                {
                    try
                    {
                        if (window is not null)
                        {
                            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            window.Closed += (_, _) => closed.TrySetResult();
                            window.Close();
                            await closed.Task.WaitAsync(TimeSpan.FromSeconds(18));
                        }
                        app?.Shutdown();
                    }
                    catch (Exception error) { completed.TrySetException(error); }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            try { Dispatcher.Run(); }
            catch (Exception error) { completed.TrySetException(error); }
            finally { completed.TrySetResult(); }
        }) { IsBackground = true, Name = "Operator global shell menu qualification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(50)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Global menu STA did not terminate."); }
    }

    private static string RepositoryFile(params string[] parts)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine([root.FullName, .. parts]);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject owner) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(owner); i++)
        {
            var child = VisualTreeHelper.GetChild(owner, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
