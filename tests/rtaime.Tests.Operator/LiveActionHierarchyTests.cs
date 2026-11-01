// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
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
public sealed class LiveActionHierarchyTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Primary_live_row_keeps_take_actions_and_confirmation_without_horizontal_scrolling()
    {
        var strip = Named(Markup(), "LiveActionStrip");
        Assert.Equal("RtaimeScrollViewer", strip.Name.LocalName);
        Assert.Equal("Disabled", strip.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Equal("Disabled", strip.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("OnLiveActionsPreviewKeyDown", strip.Attribute("PreviewKeyDown")?.Value);

        var buttons = strip.Descendants().Where(node => node.Name.LocalName == "RtaimeButton").ToArray();
        Assert.True(buttons.Length >= 7);
        Assert.Equal(new[] { "SET PVW", "CUT", "AUTO" },
            buttons.Take(3).Select(node => node.Attribute("Content")?.Value).ToArray());
        Assert.Equal(new[] { "{Binding SetPreviewCommand}", "{Binding CutCommand}", "{Binding DissolveCommand}" },
            buttons.Take(3).Select(node => node.Attribute("Command")?.Value).ToArray());
        Assert.All(buttons.Take(3), button =>
        {
            Assert.True((button.Attribute("ToolTip")?.Value ?? "").Contains("Detail", StringComparison.Ordinal));
            Assert.Equal("True", button.Attribute("ToolTipService.ShowOnDisabled")?.Value);
            Assert.NotNull(button.Attribute("AutomationProperties.Name"));
        });

        var preview = Named(strip, "LiveNextIdentity");
        Assert.Equal("{Binding PreviewSourceName}", preview.Attribute("Text")?.Value);
        var take = Named(strip, "LiveAuthorityDetail");
        Assert.Equal("{Binding LiveTakeEvidence}", take.Attribute("Text")?.Value);
        Assert.Equal("{Binding PreviewTakeDetail}", take.Attribute("ToolTip")?.Value);
        Assert.Equal("Polite", take.Attribute("AutomationProperties.LiveSetting")?.Value);
        Assert.Equal("{Binding Rundown.PendingNext, ElementName=RootWindow}",
            Named(strip, "LiveCueIdentity").Attribute("Text")?.Value);
    }

    [Fact]
    public void View_menu_uses_existing_shell_commands_and_exposes_direct_restore()
    {
        var strip = Named(Markup(), "LiveActionStrip");
        var view = Named(strip, "LiveViewButton");
        Assert.Equal("OnLiveViewClick", view.Attribute("Click")?.Value);
        Assert.Equal("{Binding ElementName=RootWindow}", view.Attribute("Tag")?.Value);
        var menu = Assert.Single(view.Descendants().Where(node => node.Name.LocalName == "RtaimeContextMenu"));
        Assert.Equal("{Binding PlacementTarget.Tag, RelativeSource={RelativeSource Self}}",
            menu.Attribute("DataContext")?.Value);
        var menuCommands = menu.Descendants().Where(node => node.Name.LocalName == "RtaimeMenuItem" &&
            node.Attribute("Command") is not null).Select(node => (Header: node.Attribute("Header")?.Value,
                Command: node.Attribute("Command")?.Value, Argument: node.Attribute("CommandParameter")?.Value)).ToArray();
        Assert.Equal(new[]
        {
            ("Show / hide Media Library", "{Binding Shell.ToggleLeftPanelCommand}", (string?)null),
            ("Show / hide Inspector", "{Binding Shell.ToggleRightPanelCommand}", (string?)null),
            ("Restore saved workspace layout", "{Binding Shell.ResetLayoutCommand}", (string?)null),
            ("Restore dual Preview and Program viewers", "{Binding Shell.RestoreViewersCommand}", (string?)null),
            ("Show checks and activity", "{Binding OpenShowChecksCommand}", (string?)null),
            ("Open Outputs details", "{Binding Shell.SelectWorkspaceCommand}", "OUTPUTS")
        }, menuCommands);

        Assert.Contains(strip.Descendants(), node => node.Name.LocalName == "RtaimeButton" &&
            node.Attribute("Content")?.Value == "RESTORE" &&
            node.Attribute("Command")?.Value == "{Binding Shell.ResetLayoutCommand, ElementName=RootWindow}");
        Assert.All(menu.Descendants().Where(node => node.Name.LocalName == "RtaimeMenuItem" &&
            node.Attribute("Command") is not null), node =>
                Assert.NotNull(node.Attribute("AutomationProperties.Name")));
        Assert.DoesNotContain(menu.Descendants(), node =>
            node.Attribute("Command")?.Value is "{Binding CutCommand}" or "{Binding DissolveCommand}" or
                "{Binding SetPreviewCommand}");
    }

    [Fact]
    public void Detailed_signals_live_in_view_menu_and_program_identity_stays_in_top_shell()
    {
        var root = Markup();
        var strip = Named(root, "LiveActionStrip");
        var menu = Assert.Single(Named(strip, "LiveViewButton").Descendants().Where(node =>
            node.Name.LocalName == "RtaimeContextMenu"));
        foreach (var name in new[] { "LiveSelectedSource", "LivePreviewEvidence", "LiveProgramEvidence",
            "LiveSendEvidence", "LiveRecordingEvidence", "LiveAudioLeftEvidence" })
        {
            Assert.Single(root.Descendants().Where(node => node.Attribute(Xaml + "Name")?.Value == name));
            Assert.Contains(menu.Descendants(), node => node.Attribute(Xaml + "Name")?.Value == name);
        }
        Assert.Equal("{Binding ProgramSourceName}", Named(root, "LiveProgramIdentity").Attribute("Text")?.Value);
        Assert.DoesNotContain(strip.Descendants().Where(node =>
            node.Name.LocalName == "TextBlock" && !node.Ancestors().Contains(menu)), node =>
                node.Attribute("Text")?.Value is "{Binding DataContext.AudioLeftDb}" or
                    "{Binding DataContext.RecordingStatus}" or "{Binding OutputHealth.ProgramTransmissionState}");
        Assert.Equal("True", Named(root, "LiveViewButton").Attribute("IsCompact")?.Value);
    }

    [Fact]
    public void View_keyboard_path_does_not_execute_a_take_or_capture_text_editor_keys()
    {
        var code = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Hosts", "rtaime.Operator", "MainWindow.Shell.cs"));
        Assert.Contains("OnLiveActionsPreviewKeyDown", code, StringComparison.Ordinal);
        Assert.Contains("e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase", code, StringComparison.Ordinal);
        Assert.Contains("e.IsRepeat", code, StringComparison.Ordinal);
        Assert.Contains("e.Key is Key.Apps", code, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.F10", code, StringComparison.Ordinal);
        Assert.Contains("menu.PlacementTarget = anchor;", code, StringComparison.Ordinal);
        Assert.Contains("menu.IsOpen = true;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CutCommand.Execute", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DissolveCommand.Execute", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SetPreviewCommand.Execute", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Take_status_keeps_missing_stale_pending_rejected_and_unresolved_conditions_distinct()
    {
        static string Status(bool connected = true, bool stale = false, bool pending = false,
            bool snapshot = true, string transition = "READY") =>
            LiveProductionPresentation.TakeEvidence(connected, stale, pending, snapshot,
                false, "READY", transition, true, 10);

        Assert.Contains("UNVERIFIED", Status(connected: false));
        Assert.Contains("UNVERIFIED", Status(stale: true));
        Assert.Contains("PENDING", Status(pending: true));
        Assert.Contains("REJECTED", Status(transition: "CUT REJECTED"));
        Assert.Contains("UNCONFIRMED TAKE", Status(transition: "CUT FAILED"));
        Assert.Contains("REQUEST ELIGIBLE", Status());
        Assert.DoesNotContain("CONFIRMED", Status(connected: false));
        Assert.DoesNotContain("CONTROL COMMIT CONFIRMED", Status(pending: true));
    }

    private const string ChildMarker = "RTAIME_LIVE_ACTION_HIERARCHY_CHILD";

    [Fact]
    public async Task Real_Wpf_live_actions_remain_visible_and_view_menu_preserves_program_at_960_and_1600()
    {
        if (Environment.GetEnvironmentVariable(ChildMarker) != "1")
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(LiveActionHierarchyTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.LiveActionHierarchyTests." +
                nameof(Real_Wpf_live_actions_remain_visible_and_view_menu_preserves_program_at_960_and_1600));
            start.Environment[ChildMarker] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(75));
                Assert.True(process.ExitCode == 0,
                    $"LIVE WPF qualification failed. Exit={process.ExitCode}{Environment.NewLine}{await output}{Environment.NewLine}{await errors}");
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

        OnSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "rtaime-live-hierarchy", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            OperatorResourceApplication? app = null;
            OperatorViewModel? owner = null;
            MainWindow? window = null;
            RtaimeContextMenu? menu = null;
            try
            {
                app = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                var transport = new InspectorPrecisionTests.InspectorTransport();
                owner = new OperatorViewModel(new OperatorControlClient(transport),
                    runtimeReadiness: new InspectorPrecisionTests.ReadyService());
                window = new MainWindow(owner,
                    new OperatorLayoutStore(Path.Combine(root, "layout.json")),
                    new OperatorSessionRecoveryStore(Path.Combine(root, "session.json")),
                    new OperatorLibraryPreferenceStore(Path.Combine(root, "library.json")));
                app.MainWindow = window;
                window.Show();
                window.Shell.SelectWorkspace("LIVE");
                DrainDispatcher();
                var initialProgram = owner.ProgramSourceId;
                var initialPreview = owner.PreviewSourceId;
                var monitoring = window.Monitoring;
                var strip = Assert.IsAssignableFrom<ScrollViewer>(window.FindName("LiveActionStrip"));
                var actions = Descendants<RtaimeButton>(strip).ToArray();
                Assert.Equal(new[] { "SET PVW", "CUT", "AUTO" },
                    actions.Take(3).Select(button => button.Content?.ToString()).ToArray());
                Assert.Same(owner.SetPreviewCommand, actions[0].Command);
                Assert.Same(owner.CutCommand, actions[1].Command);
                Assert.Same(owner.DissolveCommand, actions[2].Command);

                foreach (var (width, height) in new[] { (1600d, 900d), (960d, 540d) })
                {
                    window.Width = width;
                    window.Height = height;
                    window.Shell.UpdateViewportSize(width, height);
                    window.UpdateLayout();
                    DrainDispatcher();
                    Assert.True(strip.IsVisible);
                    Assert.InRange(strip.ScrollableWidth, 0, 1);
                    Assert.InRange(strip.ScrollableHeight, 0, 1);
                    foreach (var action in actions.Take(3))
                    {
                        Assert.True(action.IsVisible && action.ActualWidth > 0);
                        var bounds = action.TransformToAncestor(strip).TransformBounds(new Rect(action.RenderSize));
                        Assert.True(bounds.Left >= -1 && bounds.Right <= strip.ViewportWidth + 1,
                            $"LIVE action {action.Content} is outside {strip.ViewportWidth} DIP at {width}x{height}: {bounds}");
                    }

                    var view = Assert.IsType<RtaimeButton>(window.FindName("LiveViewButton"));
                    Assert.True(view.IsVisible && view.Focus());
                    view.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, view));
                    DrainDispatcher();
                    menu = Assert.IsType<RtaimeContextMenu>(view.ContextMenu);
                    Assert.True(menu.IsOpen);
                    Assert.Same(view, menu.PlacementTarget);
                    var items = menu.Items.OfType<RtaimeMenuItem>().Where(item => item.Command is not null).ToArray();
                    Assert.Same(window.Shell.ToggleLeftPanelCommand, items[0].Command);
                    Assert.Same(window.Shell.ToggleRightPanelCommand, items[1].Command);
                    Assert.Same(window.Shell.ResetLayoutCommand, items[2].Command);
                    Assert.Same(window.OpenShowChecksCommand, items[4].Command);
                    menu.IsOpen = false;
                    Assert.Equal(initialProgram, owner.ProgramSourceId);
                    Assert.Equal(initialPreview, owner.PreviewSourceId);
                    Assert.Equal(0, transport.MutationCalls);
                }

                window.Shell.SelectCompactBusCommand.Execute("PREVIEW");
                window.UpdateLayout();
                DrainDispatcher();
                Assert.Contains(Descendants<PreviewViewer>(window), viewer => viewer.IsVisible);
                window.Shell.SelectCompactBusCommand.Execute("PROGRAM");
                window.UpdateLayout();
                DrainDispatcher();
                Assert.Contains(Descendants<ProgramViewer>(window), viewer => viewer.IsVisible);
                Assert.Same(monitoring, window.Monitoring);
                Assert.Equal(initialProgram, owner.ProgramSourceId);
                Assert.Equal(initialPreview, owner.PreviewSourceId);
                Assert.Equal(0, transport.MutationCalls);
            }
            finally
            {
                if (menu is not null) menu.IsOpen = false;
                window?.Close();
                if (owner is not null) owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                app?.Shutdown();
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        });
    }

    private static XDocument Markup() =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Hosts", "rtaime.Operator", "MainWindow.xaml"));

    private static XElement Named(XContainer document, string name) =>
        Assert.Single(document.Descendants().Where(element => element.Attribute(Xaml + "Name")?.Value == name));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root with rtaime.slnx not found.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T selected) yield return selected;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        {
            IsBackground = true,
            Name = "LIVE action hierarchy WPF qualification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(50)), "LIVE WPF qualification did not finish.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
