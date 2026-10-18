// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

/// <summary>Geometry goldens and review images; deliberately no driver-dependent pixel equality.</summary>
public sealed class OperatorCombinedVisualQualification
{
    private static readonly string[] Regions =
        ["TopBar", "WorkspaceNavigation", "CenterWorkspace", "StatusBar"];

    [Fact]
    public void Geometry_golden_rejects_clipping_empty_nonfinite_and_displaced_regions()
    {
        var viewport = new Size(960, 540);
        Assert.True(Fits(new Rect(0, 0, 960, 60), viewport));
        Assert.True(Fits(new Rect(-0.25, 0, 960, 60), viewport));
        Assert.False(Fits(new Rect(0, 0, 0, 60), viewport));
        Assert.False(Fits(new Rect(0, 0, 961, 60), viewport));
        Assert.False(Fits(new Rect(0, 539, 960, 30), viewport));
        Assert.False(Fits(Rect.Empty, viewport));
        Assert.False(Fits(new Rect(0, 0, double.PositiveInfinity, 30), viewport));
        Assert.False(Near(new Rect(0, 0, 960, 60), new Rect(0, 2, 960, 60)));
    }

    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var owner = Assert.IsType<OperatorViewModel>(window.DataContext);
        var original = (RootWidth: root.Width, RootHeight: root.Height, Transform: root.LayoutTransform,
            Width: window.Width, Height: window.Height, MinWidth: window.MinWidth, MinHeight: window.MinHeight,
            Workspace: window.Shell.SelectedWorkspace);
        var program = owner.ProgramSourceId;
        var preview = owner.PreviewSourceId;
        var evidenceRoot = Environment.GetEnvironmentVariable("RTAIME_OPERATOR_VISUAL_EVIDENCE");
        var observations = new List<object>();
        var interactionCases = new List<object>();
        if (!string.IsNullOrWhiteSpace(evidenceRoot)) Directory.CreateDirectory(evidenceRoot);
        var watch = Stopwatch.StartNew();
        try
        {
            Assert.False(owner.CutCommand.CanExecute(null));
            Assert.Empty(OperatorKeyboardCommandRegistry.FindConflicts(window.Shortcuts.Definitions));
            foreach (var id in new[] { "cut", "auto", "record", "show-control-go" })
            {
                var definition = window.Shortcuts.Definitions.Single(item => item.Id == id);
                Assert.False(definition.AllowInTextEntry);
                Assert.Equal(OperatorShortcutScope.Global, definition.Scope);
                Assert.False(window.Shortcuts.TryHandle(definition.Key, definition.Modifiers, false, true));
            }
            Assert.Same(owner.CutCommand, window.Shortcuts.Definitions.Single(item => item.Id == "cut").Command);
            Assert.Same(owner.DissolveCommand, window.Shortcuts.Definitions.Single(item => item.Id == "auto").Command);
            VerifyModalRouting(window);
            window.MinWidth = 960; window.MinHeight = 500;
            foreach (var row in OperatorVisualQualificationTests.QualificationMatrix())
            {
                var name = (string)row[0];
                var width = (double)row[1]; var height = (double)row[2];
                var dpi = (int)row[3]; var scale = dpi / 100.0;
                // Inject layout scaling, not WM_DPICHANGED or a physical monitor transition.
                root.Width = width; root.Height = height;
                root.LayoutTransform = new ScaleTransform(scale, scale);
                window.Width = width * scale; window.Height = height * scale;
                foreach (var workspace in OperatorWorkspaceNames.All)
                {
                    window.Shell.SelectWorkspace(workspace);
                    await dispatcher.InvokeAsync(() =>
                    {
                        window.Shell.UpdateViewportSize(width, height);
                        window.UpdateLayout();
                    }, DispatcherPriority.Loaded);
                    Assert.Equal(width, root.ActualWidth, 1);
                    Assert.Equal(height, root.ActualHeight, 1);
                    // Fold the original eight-workspace pane/viewer warmup into this stronger matrix;
                    // avoid eight extra native layout/target cycles without dropping an assertion.
                    foreach (var paneName in new[]
                    {
                        "TopBar", "WorkspaceNavigation", "LeftToolRegion", "CenterWorkspace",
                        "RightInspectorRegion", "LowerTimelineRegion", "BottomTransportRegion"
                    })
                    {
                        var pane = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(paneName));
                        Assert.True(pane.IsLoaded, $"{workspace}/{paneName}: pane was omitted from the real window.");
                        var allocated = paneName switch
                        {
                            "RightInspectorRegion" => window.Shell.RightColumnWidth.Value > 0,
                            "LeftToolRegion" => window.Shell.LeftColumnWidth.Value > 0,
                            _ => true
                        };
                        if (pane.IsVisible && allocated)
                        {
                            Assert.True(pane.ActualWidth > 0 && pane.ActualHeight > 0,
                                $"{workspace}/{paneName}: visible pane has empty layout.");
                        }
                    }
                    Assert.NotEmpty(Descendants(window).OfType<PreviewViewer>());
                    Assert.NotEmpty(Descendants(window).OfType<ProgramViewer>());
                    if (workspace == "LIVE") VerifyLiveComposition(window, root, width);
                    var first = Capture(window, root);
                    Assert.All(Descendants(root).OfType<OperatorDrawingSurface>(),
                        surface => Assert.InRange(surface.RenderingSubscriptionBalance, 0, 1));
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    var second = Capture(window, root);
                    foreach (var region in Regions)
                    {
                        Assert.True(Fits(first[region], new Size(width, height)),
                            name + "/" + workspace + "/" + region + ": clipped or empty " + first[region]);
                        Assert.True(Near(first[region], second[region]),
                            name + "/" + workspace + "/" + region + ": unstable settled layout.");
                    }
                    // Stable anchors independent of font, rasterizer and shell scale calculations.
                    Assert.InRange(Math.Abs(first["TopBar"].Y), 0, 0.5);
                    Assert.InRange(Math.Abs(first["TopBar"].Width - width), 0, 0.5);
                    Assert.InRange(Math.Abs(first["TopBar"].Height - 60), 0, 0.5);
                    Assert.InRange(Math.Abs(first["StatusBar"].Bottom - height), 0, 0.5);
                    Assert.InRange(Math.Abs(first["StatusBar"].Height - 30), 0, 0.5);
                    Assert.True(first["WorkspaceNavigation"].Right <= first["CenterWorkspace"].Left + 0.5);
                    Assert.True(first["CenterWorkspace"].Top >= first["TopBar"].Bottom - 0.5);
                    Assert.True(first["CenterWorkspace"].Bottom <= first["StatusBar"].Top + 0.5);
                    Assert.Equal(program, owner.ProgramSourceId);
                    Assert.Equal(preview, owner.PreviewSourceId);
                    Assert.False(owner.CutCommand.CanExecute(null));
                    var overlay = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("StartupOverlay"));
                    observations.Add(new
                    {
                        name, workspace, width, height, injectedScale = scale,
                        actualDpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX,
                        startupOverlayVisible = overlay.IsVisible,
                        renderingSubscriptions = Descendants(root).OfType<OperatorDrawingSurface>().Sum(surface => surface.RenderingSubscriptionBalance),
                        regions = first.ToDictionary(pair => pair.Key, pair => new
                        { x = pair.Value.X, y = pair.Value.Y, width = pair.Value.Width, height = pair.Value.Height })
                    });
                    if (workspace == "LIVE" && !string.IsNullOrWhiteSpace(evidenceRoot))
                        SaveImage(root, width, height, scale, Path.Combine(evidenceRoot, name + ".png"));
                }
                await VerifyPreferenceTransitionsAsync(window, dispatcher, name, width, height, evidenceRoot, interactionCases);
            }

            await VerifyRepeatedShellMovesAsync(window, dispatcher);

            // Warm up all workspaces above before measuring a stable viewport. No forced GC:
            // these are process observations, not an exclusive heap or physical latency claim.
            root.LayoutTransform = Transform.Identity; root.Width = 1600; root.Height = 900;
            window.Width = 1600; window.Height = 900;
            window.Shell.SelectWorkspace("LIVE");
            window.Shell.UpdateViewportSize(1600, 900);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            var metrics = await MeasureSettledAsync(window, root, dispatcher);
            Assert.Equal(program, owner.ProgramSourceId);
            Assert.Equal(preview, owner.PreviewSourceId);
            Console.WriteLine($"COMBINED VISUAL PASS: {observations.Count} workspace/viewport cases; injected 100/125/150/200% layout; media-first LIVE, compact bus focus and 100 shell moves; Program unchanged.");
            Console.WriteLine("COMBINED UI METRICS: " + JsonSerializer.Serialize(metrics));
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
                File.WriteAllText(Path.Combine(evidenceRoot, "combined-ui.json"), JsonSerializer.Serialize(new
                {
                    sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
                    sdk = Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"),
                    scope = "Windows WPF layout scale injection; screenshots exclude hardware D3DImage content; physical mixed-DPI UNVERIFIED",
                    cases = observations, interactionCases, metrics, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            root.Width = original.RootWidth; root.Height = original.RootHeight; root.LayoutTransform = original.Transform;
            window.Width = original.Width; window.Height = original.Height;
            window.MinWidth = original.MinWidth; window.MinHeight = original.MinHeight;
            window.Shell.SelectWorkspace(original.Workspace);
            window.Shell.UpdateViewportSize(window.Width, window.Height);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        }
    }


    private static void VerifyLiveComposition(MainWindow window, FrameworkElement root, double width)
    {
        var shell = window.Shell;
        var owner = (OperatorViewModel)window.DataContext;
        var viewers = Descendants(root).OfType<MonitorView>().Where(v => v.IsVisible && v.ActualWidth > 0).ToArray();
        var previews = viewers.OfType<PreviewViewer>().ToArray();
        var programs = viewers.OfType<ProgramViewer>().ToArray();
        if (shell.IsCompactViewport)
        {
            Assert.Single(programs); Assert.Empty(previews);
        }
        else
        {
            var preview = Assert.Single(previews); var program = Assert.Single(programs);
            Assert.InRange(Math.Abs(preview.ActualWidth - program.ActualWidth), 0, 1);
            Assert.InRange(Math.Abs(preview.ActualHeight - program.ActualHeight), 0, 1);
            var previewBounds = preview.TransformToAncestor(root).TransformBounds(new Rect(preview.RenderSize));
            var programBounds = program.TransformToAncestor(root).TransformBounds(new Rect(program.RenderSize));
            Assert.True(previewBounds.Right <= programBounds.Left + 1);
            if (width == 1920) Assert.True(preview.ActualWidth + program.ActualWidth >= (width - shell.NavigationRailWidth) / 2);
        }
        foreach (var viewer in viewers)
            Assert.True(Fits(viewer.TransformToAncestor(root).TransformBounds(new Rect(viewer.RenderSize)), root.RenderSize),
                "LIVE role viewer must fit the useful viewport.");
        var actions = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LiveActionStrip"));
        Assert.True(actions.IsVisible && actions.ActualHeight >= 24);
        Assert.True(Fits(actions.TransformToAncestor(root).TransformBounds(new Rect(actions.RenderSize)), root.RenderSize));
        var playback = Descendants(root).OfType<System.Windows.Controls.Primitives.ButtonBase>()
            .Count(button => button.IsVisible && ReferenceEquals(button.Command, window.MediaDeck.TogglePlayPauseCommand));
        Assert.Equal(previews.Length, playback);
        var cuts = Descendants(root).OfType<System.Windows.Controls.Primitives.ButtonBase>()
            .Count(button => button.IsVisible && ReferenceEquals(button.Command, owner.CutCommand));
        Assert.Equal(1, cuts);
    }
    private static async Task VerifyRepeatedShellMovesAsync(MainWindow window, Dispatcher dispatcher)
    {
        var shell = window.Shell;
        var monitoring = window.Monitoring;
        var inspector = window.InspectorView;
        var selection = window.Composition.Selection;
        var panels = window.Docking.Panels.ToDictionary(p => p.Key, p => p.Value);
        var owner = (OperatorViewModel)window.DataContext;
        var routing = (owner.PreviewSourceId, owner.ProgramSourceId);
        var original = shell.SelectedWorkspace;
        var templateName = shell.TemplateName;
        shell.TemplateName = "Retained local layout draft";
        try
        {
            for (var cycle = 0; cycle < 100; cycle++)
            {
                shell.SelectWorkspace(OperatorWorkspaceNames.All[cycle % OperatorWorkspaceNames.All.Count]);
                window.Docking.Float("INSPECTOR");
                await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                Assert.Same(monitoring, window.Docking.FloatingWindows["INSPECTOR"].Monitoring);
                Assert.Same(inspector, window.Docking.Panels["INSPECTOR"]);
                Assert.Same(selection, window.Composition.Selection);
                Assert.Equal("Retained local layout draft", shell.TemplateName);
                Assert.Equal(1, inspector.InspectorSubscriptionCount);
                window.Docking.Dock("INSPECTOR", "RIGHT");
                await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                Assert.Empty(window.Docking.FloatingWindows);
                foreach (var panel in panels) Assert.Same(panel.Value, window.Docking.Panels[panel.Key]);
                Assert.Equal(routing, (owner.PreviewSourceId, owner.ProgramSourceId));
                shell.SetDocking(OperatorDockLayout.Default);
            }
            await shell.SaveAsync();
            Console.WriteLine("SHELL CYCLES PASS: 100 real workspace/float/dock/restore cycles; same monitoring, selection, Inspector and panel instances; local layout draft and routing retained.");
        }
        finally
        {
            shell.TemplateName = templateName; shell.SelectWorkspace(original);
            shell.SetDocking(OperatorDockLayout.Default);
        }
    }

    private static async Task VerifyPreferenceTransitionsAsync(MainWindow main, Dispatcher dispatcher,
        string viewport, double width, double height, string? evidenceRoot, List<object> observations)
    {
        var owner = (OperatorViewModel)main.DataContext;
        var identity = (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId);
        var monitoring = main.Monitoring;
        var panels = main.Docking.Panels.ToDictionary(pair => pair.Key, pair => pair.Value);
        var anchor = Descendants(main).OfType<RtaimeNavigationItem>().Single(item =>
            AutomationProperties.GetName(item) == "Open commands and shortcut profiles");
        var ownerKeys = 0;
        KeyEventHandler received = (_, _) => ownerKeys++;
        main.AddHandler(Keyboard.PreviewKeyDownEvent, received, true);
        var watch = Stopwatch.StartNew();
        async Task Check(Window dialog, string surface, string focusName)
        {
            dialog.Width = Math.Max(dialog.MinWidth, Math.Min(dialog.Width, width - 40));
            dialog.Height = Math.Max(dialog.MinHeight, Math.Min(dialog.Height, height - 80));
            await dispatcher.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Input).Task.WaitAsync(TimeSpan.FromSeconds(5));
            var focus = Assert.IsAssignableFrom<FrameworkElement>(dialog.FindName(focusName));
            Assert.True(focus.IsKeyboardFocusWithin, viewport + "/" + surface + ": focus escaped the current editor.");
            Assert.True(focus.IsVisible && focus.ActualWidth > 0 && focus.ActualHeight > 0);
            var content = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
            Assert.True(Fits(focus.TransformToAncestor(content).TransformBounds(new Rect(focus.RenderSize)), content.RenderSize),
                viewport + "/" + surface + ": focused control clipped.");
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(focus)!, 0, Key.Return)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            focus.RaiseEvent(enter);
            Assert.Equal(0, ownerKeys);
            Assert.Equal(identity, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
            Assert.False(owner.CutCommand.CanExecute(null));
            Assert.Same(monitoring, main.Monitoring);
            foreach (var panel in panels) Assert.Same(panel.Value, main.Docking.Panels[panel.Key]);
            observations.Add(new { viewport, surface, width = dialog.ActualWidth, height = dialog.ActualHeight,
                actualDpi = VisualTreeHelper.GetDpi(dialog).PixelsPerInchX, focusedControl = focusName,
                ownerKeyEvents = ownerKeys, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
                SaveImage(content, content.ActualWidth, content.ActualHeight, 1,
                    Path.Combine(evidenceRoot, viewport + "-" + surface + ".png"));
        }
        static void Escape(Window dialog)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            dialog.RaiseEvent(key); Assert.True(key.Handled); Assert.False(dialog.IsVisible);
        }
        try
        {
            main.Activate();
            Assert.True(anchor.IsVisible && anchor.IsEnabled && anchor.Focus(), viewport + ": enabled Commands navigation must accept focus.");
            main.OpenCommandPalette();
            main.CommandPalette.Query = "cut";
            await Check(Assert.IsType<OperatorCommandPaletteWindow>(main.CommandPaletteWindow), "palette", "SearchBox");
            Escape(main.CommandPaletteWindow!);
            // No dispatcher yield: the older close callback is still queued when the next editor opens.
            main.OpenWorkspaceTemplatesCommand.Execute(null);
            await Check(Assert.IsType<WorkspaceTemplatesWindow>(main.WorkspaceTemplatesWindow), "templates", "TemplateNameBox");
            Escape(main.WorkspaceTemplatesWindow!);
            main.OpenLibrary("Help");
            await Check(Assert.IsType<OperatorLibraryWindow>(main.LibraryWindow), "help", "SearchBox");
            Escape(main.LibraryWindow!);
            main.OpenShowChecksCommand.Execute(null);
            await Check(Assert.IsType<ShowReadinessWindow>(main.ShowReadinessWindow), "preflight", "RefreshChecksButton");
            Escape(main.ShowReadinessWindow!);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            Assert.True(anchor.IsKeyboardFocusWithin, viewport + ": original focus was not restored.");
            Assert.Equal(identity, (owner.PreviewSourceId, owner.ProgramSourceId, owner.ActiveSceneId));
            Console.WriteLine("QOL TRANSITIONS PASS [" + viewport + "]: palette -> templates -> help -> preflight; protected Return, Escape and final focus restore.");
        }
        finally
        {
            main.RemoveHandler(Keyboard.PreviewKeyDownEvent, received);
            main.CommandPaletteWindow?.Close(); main.WorkspaceTemplatesWindow?.Close();
            main.LibraryWindow?.Close(); main.ShowReadinessWindow?.Close();
        }
    }

    private static void VerifyModalRouting(MainWindow owner)
    {
        var editor = new RtaimeTextBox { Width = 220, Height = 32 };
        var dialog = new Window
        {
            Owner = owner, Content = editor, Width = 300, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var ownerKeys = 0;
        Exception? failure = null;
        KeyEventHandler ownerKey = (_, _) => ownerKeys++;
        owner.AddHandler(Keyboard.PreviewKeyDownEvent, ownerKey, true);
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Assert.True(editor.Focus());
                Assert.True(dialog.IsKeyboardFocusWithin);
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, Key.Return)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                editor.RaiseEvent(enter);
                Assert.Equal(0, ownerKeys);
                Assert.True(dialog.IsVisible);
                Console.WriteLine("MODAL ROUTING PASS: focused custom editor owns Return; owner shortcut route receives no event.");
            }
            catch (Exception exception) { failure = exception; }
            finally { dialog.Close(); }
        }), DispatcherPriority.Input);
        try
        {
            dialog.ShowDialog();
            if (failure is not null) throw new InvalidOperationException("Modal keyboard qualification failed.", failure);
            Assert.Equal(0, ownerKeys);
        }
        finally
        {
            owner.RemoveHandler(Keyboard.PreviewKeyDownEvent, ownerKey);
            if (dialog.IsVisible) dialog.Close();
            owner.Activate();
        }
    }

    private static async Task<object> MeasureSettledAsync(MainWindow window, FrameworkElement root, Dispatcher dispatcher)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var handles = process.HandleCount; var memory = process.PrivateMemorySize64;
        var managed = GC.GetTotalMemory(false);
        var surfaces = Descendants(root).OfType<OperatorDrawingSurface>().ToArray();
        Assert.NotEmpty(surfaces);
        var creates = surfaces.Sum(surface => surface.TargetCreateCount);
        var draws = 0; var layouts = 0;
        EventHandler layout = (_, _) => layouts++;
        EventHandler<OperatorSurfaceDrawEventArgs> draw = (_, _) => draws++;
        root.LayoutUpdated += layout;
        foreach (var surface in surfaces) surface.Draw += draw;
        var latency = new List<double>();
        try
        {
            for (var iteration = 0; iteration < 24; iteration++)
            {
                // Repeating the same viewport must not create native targets on each update.
                window.Shell.UpdateViewportSize(1600, 900);
                window.InvalidateVisual();
                var posted = Stopwatch.GetTimestamp();
                await dispatcher.InvokeAsync(() => latency.Add(Stopwatch.GetElapsedTime(posted).TotalMilliseconds),
                    DispatcherPriority.Input).Task.WaitAsync(TimeSpan.FromSeconds(5));
                await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            }
        }
        finally
        {
            root.LayoutUpdated -= layout;
            foreach (var surface in surfaces) surface.Draw -= draw;
        }
        process.Refresh();
        var handleGrowth = process.HandleCount - handles;
        var privateGrowth = process.PrivateMemorySize64 - memory;
        var managedGrowth = GC.GetTotalMemory(false) - managed;
        var targetGrowth = surfaces.Sum(surface => surface.TargetCreateCount) - creates;
        var sorted = latency.Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        Assert.InRange(handleGrowth, int.MinValue, 64);
        Assert.InRange(privateGrowth, long.MinValue, 128L * 1024 * 1024);
        Assert.InRange(managedGrowth, long.MinValue, 32L * 1024 * 1024);
        Assert.InRange(targetGrowth, 0, surfaces.Length);
        Assert.InRange(layouts, 0, 24 * 8);
        Assert.InRange(draws, 0, 24 * Math.Max(1, surfaces.Length) * 8);
        Assert.InRange(sorted[^1], 0, 5000);
        return new { samples = sorted.Length, dispatcherP95Milliseconds = p95,
            dispatcherMaxMilliseconds = sorted[^1], layouts, draws, surfaceCount = surfaces.Length,
            targetGrowth, handleGrowth, privateGrowthBytes = privateGrowth, managedGrowthBytes = managedGrowth };
    }

    private static Dictionary<string, Rect> Capture(MainWindow window, Visual root) =>
        Regions.ToDictionary(name => name, name =>
        {
            var region = name == "StatusBar"
                ? Descendants(root).OfType<RuntimePerformanceStatusBarControl>().Single()
                : Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
            Assert.True(region.IsLoaded && region.IsVisible, name + ": expected visible region.");
            return region.TransformToAncestor(root).TransformBounds(new Rect(region.RenderSize));
        });

    private static bool Fits(Rect bounds, Size viewport) => !bounds.IsEmpty &&
        double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -0.5 && bounds.Top >= -0.5 &&
        bounds.Right <= viewport.Width + 0.5 && bounds.Bottom <= viewport.Height + 0.5;

    private static bool Near(Rect a, Rect b) => Math.Abs(a.X - b.X) <= 0.5 &&
        Math.Abs(a.Y - b.Y) <= 0.5 && Math.Abs(a.Width - b.Width) <= 0.5 && Math.Abs(a.Height - b.Height) <= 0.5;

    private static void SaveImage(FrameworkElement root, double width, double height, double scale, string path)
    {
        // VisualBrush avoids applying the root LayoutTransform twice.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        var image = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
