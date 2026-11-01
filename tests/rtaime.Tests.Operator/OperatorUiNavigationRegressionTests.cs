// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

/// <summary>
/// Source-bound global-menu regression plus a real-window journey invoked by
/// the existing isolated Operator startup group. No second Application,
/// renderer, Control transport or production command owner is constructed.
/// </summary>
public sealed class OperatorUiNavigationRegressionTests
{
    private static readonly string[] Destinations =
        ["LIVE", "MEDIA", "EDIT", "OUTPUTS", "SCENES", "MULTIVIEW",
         "COMPOSITING", "HEALTH", "AI", "SETTINGS"];

    [Fact]
    public void All_registered_destinations_are_reachable_without_new_mutation_commands()
    {
        var menuSections = OperatorCommandCatalog.GlobalShellMenus;
        Assert.Equal(["Project", "Edit", "View", "Media", "Tools", "Window", "Help"],
            menuSections.Select(section => section.Name));
        // Registry order is persisted/navigation metadata; these assertions
        // check reachability without imposing the primary-rail presentation order
        // on the complete registry.
        Assert.Equal(Destinations.Order(StringComparer.Ordinal),
            OperatorWorkspaceRegistry.Destinations.Select(destination => destination.Id)
                .Order(StringComparer.Ordinal));
        Assert.Equal(["LIVE", "MEDIA", "EDIT", "OUTPUTS"],
            OperatorWorkspaceRegistry.PrimaryDestinations.Select(destination => destination.Id));
        Assert.Equal(["SCENES", "MULTIVIEW", "COMPOSITING", "HEALTH", "AI", "SETTINGS"],
            OperatorWorkspaceRegistry.SecondaryDestinations.Select(destination => destination.Id));
        foreach (var id in Destinations)
            Assert.Contains(menuSections.SelectMany(section => section.Slots),
                slot => slot.CommandId == "workspace-" + id.ToLowerInvariant());

        foreach (var section in menuSections)
        {
            var ids = section.Slots.Where(slot => slot.CommandId is not null)
                .Select(slot => slot.CommandId).ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.DoesNotContain(menuSections.SelectMany(section => section.Slots),
            slot => slot.CommandId is "cut" or "auto" or "record" or
                "show-control-go" or "sequence-take");
        Assert.Contains(menuSections.Single(section => section.Name == "View").Slots,
            slot => slot.CommandId == "pane-library");
        Assert.Contains(menuSections.Single(section => section.Name == "View").Slots,
            slot => slot.CommandId == "pane-inspector");
        Assert.Contains(menuSections.Single(section => section.Name == "View").Slots,
            slot => slot.CommandId == "view-scopes");
        Assert.Contains(menuSections.Single(section => section.Name == "Window").Slots,
            slot => slot.CommandId == "dock-cinema");
        Assert.Contains(menuSections.Single(section => section.Name == "Project").Slots,
            slot => slot.CommandId == "demo-open");
    }

    [Fact]
    public void Merged_feature_surfaces_retain_their_explicit_authoritative_actions()
    {
        var root = FindRepositoryRoot();
        string Read(string name) => File.ReadAllText(Path.Combine(root, "src", "Hosts", "rtaime.Operator", name));

        var shell = Read("MainWindow.xaml");
        var live = Read("LiveControlsControl.xaml");
        var mixer = Read("AudioMixerControl.xaml");
        var inspector = Read("OperatorInspectorControl.xaml");
        var outputs = Read("OutputCanvasEditorControl.xaml");
        var timeline = Read("SequenceTimelineControl.xaml");
        var compositing = Read("CompositingGraphControl.xaml");
        var library = Read("OperatorMediaLibraryView.xaml");

        Assert.Contains("x:Name=\"GlobalMenuOverflowButton\"", shell);
        Assert.Contains("x:Name=\"TopBarProjectIdentity\"", shell);
        Assert.Contains("Binding GlobalReadinessLabel", shell);
        Assert.Contains("Binding ProgramSourceName", shell);
        Assert.Contains("Binding RecordingStatus", shell);
        Assert.Contains("Binding OutputHealth.ProgramTransmissionState", shell);
        foreach (var command in new[] { "SetPreviewCommand", "CutCommand", "DissolveCommand" })
            Assert.Contains("Command=\"{Binding " + command + "}\"", live);
        Assert.Contains("Binding LiveTakeEvidence", live);
        Assert.Contains("Binding Notifications.ActiveProblems", live);
        Assert.Contains("Binding StopRecordingCommand", live);
        Assert.Contains("RtaimeContextMenu", mixer);
        Assert.Contains("ToggleGraphicsCommand", live);
        Assert.Contains("InspectorEffectTemplate", inspector);
        Assert.Contains("InspectorProcessingMenuItem", inspector);
        Assert.Contains("RtaimeContextMenu", outputs);
        Assert.Contains("Binding ValidateCommand", outputs);
        Assert.Contains("Binding SaveCommand", outputs);
        Assert.Contains("Binding ApplyCommand", outputs);
        Assert.Contains("Binding ReconcileCommand", outputs);
        Assert.Contains("Binding DisableCommand", outputs);
        Assert.Contains("RtaimeContextMenu", timeline);
        Assert.Contains("RtaimeContextMenu", compositing);
        Assert.Contains("RtaimeContextMenu", library);
        Assert.DoesNotContain("Command=\"{Binding CutCommand}\"", outputs);
        Assert.DoesNotContain("Command=\"{Binding DissolveCommand}\"", outputs);
    }

    [Fact]
    public void Top_bar_has_one_noninteractive_show_identity_and_never_hides_safety_evidence()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Hosts",
            "rtaime.Operator", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var bar = Assert.Single(document.Descendants(),
            node => node.Attribute(x + "Name")?.Value == "TopBar");
        var identity = Assert.Single(bar.Descendants(),
            node => node.Attribute(x + "Name")?.Value == "TopBarProjectIdentity");
        Assert.Equal("Border", identity.Name.LocalName);
        Assert.Null(identity.Attribute("Command"));
        Assert.Single(identity.Descendants(), node =>
            node.Name.LocalName == "RtaimeButton" &&
            node.Attribute("AutomationProperties.Name")?.Value == "Open Demo Production");

        foreach (var name in new[] { "LiveProgramIdentity", "TopBarRecordingState", "GlobalMenuOverflowButton" })
            Assert.Single(bar.Descendants(), node => node.Attribute(x + "Name")?.Value == name);
        Assert.Single(bar.Descendants(), node => node.Name.LocalName == "RtaimeGlobalStatusButton");

        var grid = Assert.Single(document.Root!.Elements(), node => node.Name.LocalName == "Grid");
        Assert.Equal(3, Assert.Single(grid.Elements(), node =>
            node.Name.LocalName == "Grid.RowDefinitions").Elements().Count());
    }

    /// <summary>
    /// Hosted by OperatorXamlStartupTests.Navigation on the already-loaded
    /// Dispatcher. The five main sizes are independently covered by the
    /// CombinedVisual matrix; this journey covers both presentation modes
    /// and the documented 960x500 minimum without another WPF process.
    /// </summary>
    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var owner = Assert.IsType<OperatorViewModel>(window.DataContext);
        var original = (RootWidth: root.Width, RootHeight: root.Height,
            Transform: root.LayoutTransform, WindowWidth: window.Width,
            WindowHeight: window.Height, Workspace: window.Shell.SelectedWorkspace);
        var beforeRouting = (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId);
        var beforeRecording = owner.RecordingStatus;
        var monitoring = window.Monitoring;
        var inspector = window.InspectorView;
        var panedOwners = window.Docking.Panels.ToDictionary(panel => panel.Key, panel => panel.Value);
        var observations = new List<object>();

        try
        {
            Assert.False(owner.CutCommand.CanExecute(null));
            Assert.False(owner.DissolveCommand.CanExecute(null));
            Assert.False(owner.StartRecordingCommand.CanExecute(null));
            Assert.False(window.Shortcuts.TryHandle(Key.Return, ModifierKeys.None, false, true));
            Assert.False(window.Shortcuts.TryHandle(Key.Return, ModifierKeys.Control, false, true));
            Assert.False(window.Shortcuts.TryHandle(Key.R, ModifierKeys.None, false, true));
            Assert.True(OperatorKeyboardCommandRegistry.IsTextEntryContext(new RtaimeTextBox()));

            // The menu may reference existing Commands but cannot become a
            // second CUT/AUTO/record/scene authority.
            // The real custom-control preference fixture supports reduced
            // motion and high-contrast presentation without touching the
            // application's native Windows accessibility settings.
            var launcher = Assert.IsType<RtaimeButton>(
                window.FindName("GlobalProjectMenuButton"));
            var previousReducedMotion = OperatorControlPresentation.GetReducedMotion(window);
            try
            {
                OperatorControlPresentation.SetReducedMotion(window, true);
                Assert.True(OperatorControlPresentation.GetReducedMotion(launcher));
                OperatorControlPresentation.ApplyPreferenceSnapshot(launcher,
                    clientAreaAnimation: false, highContrast: true);
                Assert.True(OperatorControlPresentation.GetHighContrast(launcher));
                Assert.Equal(beforeRouting,
                    (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
            }
            finally
            {
                OperatorControlPresentation.ApplyPreferenceSnapshot(launcher,
                    SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);
                OperatorControlPresentation.SetReducedMotion(window, previousReducedMotion);
            }
            var initialTargets = Descendants<OperatorDrawingSurface>(root)
                .Sum(surface => surface.TargetCreateCount);
            foreach (var section in OperatorCommandCatalog.GlobalShellMenus)
            {
                var menu = window.CreateGlobalShellMenu(section.Name);
                Assert.NotEmpty(menu.Items);
                var items = menu.Items.OfType<RtaimeMenuItem>().ToArray();
                Assert.Equal(section.Slots.Count(slot => slot.CommandId is not null), items.Length);
                foreach (var item in items.Where(item => item.Command is not null))
                {
                    var existing = window.CommandPalette.Entries.FirstOrDefault(entry =>
                        ReferenceEquals(entry.Descriptor.Command, item.Command) &&
                        Equals(entry.Descriptor.Parameter, item.CommandParameter));
                    Assert.True(existing is not null ||
                        ReferenceEquals(item.Command, window.Monitoring.ToggleScopesCommand) ||
                        ReferenceEquals(item.Command, window.DemoProduction.OpenCommand),
                        section.Name + ": command does not match the existing canonical owner.");
                }
            }
            Assert.Equal(initialTargets, Descendants<OperatorDrawingSurface>(root)
                .Sum(surface => surface.TargetCreateCount));
            Assert.Equal(beforeRouting, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));

            var project = window.CreateGlobalShellMenu("Project");
            var show = Assert.Single(project.Items.OfType<RtaimeMenuItem>(), item =>
                item.Header?.ToString()?.StartsWith("Current show", StringComparison.Ordinal) == true);
            Assert.Null(show.Command);
            Assert.False(show.IsEnabled);

            var import = Assert.Single(window.CreateGlobalShellMenu("Media").Items.OfType<RtaimeMenuItem>(),
                item => ReferenceEquals(item.Command, window.MediaPool.ImportCommand));
            Assert.False(import.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(import.ToolTip?.ToString()));

            var more = window.CreateMoreNavigationMenu().Items.OfType<RtaimeMenuItem>()
                .Where(item => item.CommandParameter is string).ToArray();
            Assert.Equal(Destinations.Skip(4), more.Select(item => (string)item.CommandParameter!));
            Assert.All(more, item => Assert.Same(window.Shell.SelectWorkspaceCommand, item.Command));

            foreach (var (width, height) in new[]
            {
                (1920d, 1080d), (1280d, 720d), (960d, 500d)
            })
            {
                root.Width = width; root.Height = height;
                root.LayoutTransform = Transform.Identity;
                window.Width = width; window.Height = height;
                window.Shell.UpdateViewportSize(width, height);

                foreach (var destination in Destinations)
                {
                    window.Shell.SelectWorkspace(destination);
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded)
                        .Task.WaitAsync(TimeSpan.FromSeconds(5));

                    var compactMenu = window.Shell.SecondaryMetricVisibility == Visibility.Collapsed;
                    var overflow = Assert.IsType<RtaimeButton>(
                        window.FindName("GlobalMenuOverflowButton"));
                    var desktop = Assert.IsType<StackPanel>(
                        window.FindName("GlobalDesktopMenuBar"));
                    Assert.Equal(compactMenu, overflow.IsVisible);
                    Assert.Equal(!compactMenu, desktop.IsVisible);
                    var activeMenu = compactMenu ? (FrameworkElement)overflow : desktop;
                    Assert.True(activeMenu.ActualWidth > 0 && activeMenu.ActualHeight > 0);

                    var topBar = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("TopBar"));
                    var menuBounds = activeMenu.TransformToAncestor(root)
                        .TransformBounds(new Rect(activeMenu.RenderSize));
                    var barBounds = topBar.TransformToAncestor(root)
                        .TransformBounds(new Rect(topBar.RenderSize));
                    Assert.True(menuBounds.Left >= barBounds.Left - .5 &&
                        menuBounds.Right <= barBounds.Right + .5 &&
                        menuBounds.Top >= barBounds.Top - .5 &&
                        menuBounds.Bottom <= barBounds.Bottom + .5,
                        $"{width}x{height}/{destination}: menu overlaps the video workspace.");

                    var status = Assert.Single(Descendants<RtaimeGlobalStatusButton>(topBar));
                    Assert.True(status.IsVisible);
                    var program = Assert.IsType<TextBlock>(window.FindName("LiveProgramIdentity"));
                    Assert.True(program.IsVisible);
                    Assert.Equal(owner.ProgramSourceName, program.Text);
                    Assert.Equal(owner.RecordingStatus,
                        Assert.IsType<TextBlock>(window.FindName("TopBarRecordingState")).Text);

                    Assert.Equal(beforeRouting,
                        (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
                    Assert.Equal(beforeRecording, owner.RecordingStatus);
                    Assert.Same(monitoring, window.Monitoring);
                    Assert.Same(inspector, window.InspectorView);
                    foreach (var panel in panedOwners)
                        Assert.Same(panel.Value, window.Docking.Panels[panel.Key]);

                    if (destination == "LIVE")
                    {
                        var actionStrip = Assert.IsAssignableFrom<ScrollViewer>(
                            window.FindName("LiveActionStrip"));
                        Assert.True(actionStrip.IsVisible && actionStrip.ActualHeight >= 24);
                        var cutCount = Descendants<RtaimeButton>(actionStrip).Count(button =>
                            button.IsVisible && ReferenceEquals(button.Command, owner.CutCommand));
                        Assert.Equal(1, cutCount);
                        Assert.False(owner.CutCommand.CanExecute(null));
                    }
                    if (destination == "OUTPUTS")
                    {
                        Assert.Same(window.OutputHealth.CanvasEditor,
                            Assert.Single(Descendants<OutputCanvasEditorControl>(window)).DataContext);
                        Assert.False(window.OutputHealth.CanvasEditor.ApplyCommand.CanExecute(null));
                    }

                    observations.Add(new
                    {
                        logicalWidth = width, logicalHeight = height, workspace = destination,
                        activeMenu = compactMenu ? "MENU" : "Project/Edit/View/Media/Tools/Window/Help",
                        statusVisible = status.IsVisible, programVisible = program.IsVisible,
                        recording = owner.RecordingStatus, activeMenuWidth = activeMenu.ActualWidth,
                        actualDpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX
                    });
                }

                // Open the actual compact custom menu with the keyboard; opening,
                // focus and closing cannot issue any Program or recording action.
                if (width == 960)
                {
                    var overflow = Assert.IsType<RtaimeButton>(
                        window.FindName("GlobalMenuOverflowButton"));
                    Assert.True(overflow.Focus());
                    var source = PresentationSource.FromVisual(overflow);
                    Assert.NotNull(source);
                    var menuKey = new KeyEventArgs(Keyboard.PrimaryDevice, source!, 0, Key.Apps)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    overflow.RaiseEvent(menuKey);
                    Assert.True(menuKey.Handled);
                    Assert.NotNull(overflow.ContextMenu);
                    Assert.True(overflow.ContextMenu.IsOpen);
                    Assert.Same(overflow, overflow.ContextMenu.PlacementTarget);
                    overflow.ContextMenu.IsOpen = false;
                    Assert.Equal(beforeRouting,
                        (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
                    Assert.Equal(beforeRecording, owner.RecordingStatus);
                }
            }
            Console.WriteLine("OPERATOR MENU NAVIGATION PASS: 30 real workspace/viewport cases; " +
                "command ownership, compact overflow, unchanged Program and recording.");
            var evidenceRoot = Environment.GetEnvironmentVariable("RTAIME_OPERATOR_VISUAL_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                Directory.CreateDirectory(evidenceRoot);
                File.WriteAllText(Path.Combine(evidenceRoot, "navigation-ui.json"),
                    JsonSerializer.Serialize(new
                    {
                        sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
                        sdk = Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"),
                        scope = "Real Windows WPF logical layouts; no physical mixed-DPI or hardware output claim",
                        status = "PASS",
                        cases = observations
                    }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            root.Width = original.RootWidth;
            root.Height = original.RootHeight;
            root.LayoutTransform = original.Transform;
            window.Width = original.WindowWidth;
            window.Height = original.WindowHeight;
            window.Shell.SelectWorkspace(original.Workspace);
            window.Shell.UpdateViewportSize(window.Width, window.Height);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
