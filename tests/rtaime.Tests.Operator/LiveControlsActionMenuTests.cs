// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO;
using System.Reflection;
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
public sealed class LiveControlsActionMenuTests
{
    private const string ChildVariable = "RTAIME_LIVE_CONTROLS_QUALIFICATION_CHILD";

    [Fact]
    public void Multiview_keeps_its_own_guarded_take_commands_while_live_uses_the_action_strip()
    {
        var controls = Surface();
        var main = File.ReadAllText(RepositoryFile("src", "Hosts", "rtaime.Operator", "MainWindow.xaml"));
        var shell = File.ReadAllText(RepositoryFile("src", "Hosts", "rtaime.Operator", "OperatorShellViewModel.cs"));
        var adaptive = File.ReadAllText(RepositoryFile("src", "Hosts", "rtaime.Operator", "OperatorWorkspaceRegistry.cs"));

        Assert.Contains("LiveControlsVisibility => IsMultiviewWorkspace ? Visibility.Visible : Visibility.Collapsed", shell);
        Assert.Contains("LiveActionVisibility => IsLiveWorkspace ? Visibility.Visible : Visibility.Collapsed", adaptive);
        Assert.Contains("<local:LiveControlsControl", main, StringComparison.Ordinal);
        Assert.Contains("Shell.LiveControlsVisibility", main, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"LiveActionStrip\"", main, StringComparison.Ordinal);

        foreach (var (content, command) in new[]
        {
            ("SET PVW", "SetPreviewCommand"), ("CUT", "CutCommand"), ("AUTO", "DissolveCommand")
        })
        {
            var button = Assert.Single(controls.Descendants().Where(node =>
                node.Name.LocalName == "RtaimeButton" && node.Attribute("Content")?.Value == content));
            Assert.Equal("{Binding " + command + "}", button.Attribute("Command")?.Value);
        }
        Assert.Contains(controls.Descendants(), node => node.Attribute("Text")?.Value == "{Binding PreviewSourceName}");
        Assert.Contains(controls.Descendants(), node => node.Attribute("Text")?.Value == "{Binding ProgramSourceName}");
        Assert.Contains(controls.Descendants(), node => node.Attribute("Text")?.Value == "{Binding LiveTakeEvidence}");
        Assert.Contains(controls.Descendants(), node => node.Attribute("Text")?.Value == "{Binding LastError}");

        Assert.DoesNotContain(controls.Descendants(), node => node.Name.LocalName is
            "Button" or "ToggleButton" or "CheckBox" or "TextBox" or "MenuItem" or "ContextMenu" or "ComboBox" or "Slider");
    }

    [Fact]
    public void Recording_stop_and_persistent_incidents_are_not_inside_optional_details()
    {
        var controls = Surface();
        var start = Button(controls, "START REC");
        var stop = Button(controls, "STOP REC");
        Assert.Equal("{Binding StartRecordingCommand}", start.Attribute("Command")?.Value);
        Assert.Equal("{Binding StopRecordingCommand}", stop.Attribute("Command")?.Value);
        Assert.DoesNotContain(stop.Ancestors(), node => node.Attribute("Visibility")?.Value == "Collapsed");
        Assert.Contains(stop.Descendants(), node =>
            node.Name.LocalName == "DataTrigger" && node.Attribute("Binding")?.Value == "{Binding RecordingStatus}" &&
            node.Attribute("Value")?.Value == "RECORDING");
        Assert.Contains(start.Descendants(), node =>
            node.Name.LocalName == "DataTrigger" && node.Attribute("Binding")?.Value == "{Binding RecordingStatus}" &&
            node.Attribute("Value")?.Value == "FINALIZING");
        Assert.Contains("OperatorErrorBrush", stop.Attribute("Background")?.Value);
        Assert.Contains(controls.Descendants(), node => node.Attribute("Text")?.Value == "{Binding RecordingError}");

        var incidents = Assert.Single(controls.Descendants().Where(node =>
            node.Name.LocalName == "ItemsControl" &&
            (node.Attribute("ItemsSource")?.Value?.Contains("Notifications.ActiveProblems", StringComparison.Ordinal) ?? false)));
        Assert.DoesNotContain(incidents.Ancestors(), node => node.Attribute("Visibility")?.Value == "Collapsed");
        Assert.Equal(3, controls.Descendants().Count(node => node.Name.LocalName == "RtaimeAlertRow"));
        Assert.Contains(controls.Descendants(), node => node.Attribute("AutomationProperties.Name")?.Value ==
            "Persistent operator problem count");

        foreach (var detail in new[] { "TakeDetails", "LayerDetails", "MonitoringDetails", "TransmissionDetails" })
        {
            var section = Assert.Single(controls.Descendants().Where(node =>
                node.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == detail)));
            Assert.Equal("Collapsed", section.Attribute("Visibility")?.Value);
            var toggle = Assert.Single(controls.Descendants().Where(node =>
                node.Name.LocalName == "RtaimeButton" && node.Attribute("Tag")?.Value == detail));
            Assert.Equal("ToggleSecondaryDetails_Click", toggle.Attribute("Click")?.Value);
            Assert.Null(toggle.Attribute("Command"));
        }
    }

    [Fact]
    public void Clean_monitoring_and_program_aux_transmission_keep_separate_evidence_owners()
    {
        var controls = Surface();
        var monitoring = NamedSection(controls, "MonitoringDetails");
        Assert.Contains(monitoring.Descendants(), node =>
            node.Attribute("Command")?.Value?.Contains("ProgramOutput.StartCommand", StringComparison.Ordinal) == true);
        Assert.Contains(monitoring.Descendants(), node =>
            node.Attribute("Command")?.Value?.Contains("ProgramOutput.StopCommand", StringComparison.Ordinal) == true);
        Assert.Contains(controls.Descendants(), node =>
            node.Attribute("AutomationProperties.Name")?.Value == "Local clean monitoring state");

        var transmission = NamedSection(controls, "TransmissionDetails");
        foreach (var binding in new[] { "ProgramTransmissionDetail", "AuxTransmissionDetail" })
            Assert.Contains(transmission.Descendants(), node =>
                node.Attributes().Any(attribute => attribute.Value.Contains(binding, StringComparison.Ordinal)));
        foreach (var binding in new[] { "ProgramTransmissionState", "AuxTransmissionState" })
            Assert.Contains(controls.Descendants(), node =>
                node.Attributes().Any(attribute => attribute.Value.Contains(binding, StringComparison.Ordinal)));
        Assert.Contains(transmission.Descendants(), node =>
            node.Attribute("Text")?.Value?.Contains("not proof of physical display output", StringComparison.Ordinal) == true);

        var code = File.ReadAllText(RepositoryFile("src", "Hosts", "rtaime.Operator", "LiveControlsControl.xaml.cs"));
        Assert.Contains("LiveConsoleInput.Attach(this)", code, StringComparison.Ordinal);
        Assert.Contains("FindName(sectionName)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SetPreviewCommand", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CutCommand", code, StringComparison.Ordinal);
        Assert.DoesNotContain("StartRecordingCommand", code, StringComparison.Ordinal);
        Assert.DoesNotContain("StopRecordingCommand", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_wpf_panel_retains_context_commands_focus_and_recording_stop()
    {
        if (Environment.GetEnvironmentVariable(ChildVariable) != "1")
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(LiveControlsActionMenuTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=rtaime.Tests.Operator.LiveControlsActionMenuTests." +
                nameof(Real_wpf_panel_retains_context_commands_focus_and_recording_stop));
            start.ArgumentList.Add("--logger:console;verbosity=detailed");
            start.Environment[ChildVariable] = "1";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                var report = await output;
                Assert.True(process.ExitCode == 0, report + Environment.NewLine + await errors);
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

        await RunRealWpfAsync();
    }

    private static async Task RunRealWpfAsync()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                OperatorResourceApplication? application = null;
                MainWindow? window = null;
                var folder = Path.Combine(Path.GetTempPath(), "rtaime-live-controls", Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(folder);
                    application = new OperatorResourceApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.InitializeComponent();
                    var owner = new OperatorViewModel(synchronizationContext: SynchronizationContext.Current);
                    window = new MainWindow(owner,
                        new OperatorLayoutStore(Path.Combine(folder, "layout.json")),
                        new OperatorSessionRecoveryStore(Path.Combine(folder, "session.json")),
                        new OperatorLibraryPreferenceStore(Path.Combine(folder, "library.json")));
                    window.Width = 1280;
                    window.Height = 720;
                    window.Shell.SelectWorkspace("MULTIVIEW");
                    if (window.Shell.IsRightCollapsed) window.Shell.ToggleRightPanelCommand.Execute(null);
                    window.Show();
                    window.Activate();
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);

                    var panel = Assert.Single(Descendants<LiveControlsControl>(window));
                    Assert.True(panel.IsVisible);
                    Assert.Same(owner, panel.DataContext);
                    var cut = Assert.Single(Descendants<RtaimeButton>(panel).Where(button => Equals(button.Content, "CUT")));
                    var auto = Assert.Single(Descendants<RtaimeButton>(panel).Where(button => Equals(button.Content, "AUTO")));
                    var setPreview = Assert.Single(Descendants<RtaimeButton>(panel).Where(button => Equals(button.Content, "SET PVW")));
                    Assert.Same(owner.CutCommand, cut.Command);
                    Assert.Same(owner.DissolveCommand, auto.Command);
                    Assert.Same(owner.SetPreviewCommand, setPreview.Command);
                    Assert.Same(cut, LiveConsoleInput.FindButton(cut));
                    Assert.True(cut.Focus());
                    var unauthorizedAuto = 0;
                    var keyboard = new OperatorKeyboardCommandRegistry(
                        [new("auto", "AUTO", Key.Enter, ModifierKeys.None, new OperatorShellCommand(() => unauthorizedAuto++))]);
                    var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cut)!, 0, Key.Enter);
                    Assert.False(keyboard.TryHandle(enter));
                    Assert.Equal(0, unauthorizedAuto); // Enter on an explicit CUT button must not route to AUTO.
                    Assert.False(cut.Command.CanExecute(null)); // no authoritative Control connection

                    var recordingStart = Assert.Single(Descendants<RtaimeButton>(panel).Where(button => Equals(button.Content, "START REC")));
                    var recordingStop = Assert.Single(Descendants<RtaimeButton>(panel).Where(button => Equals(button.Content, "STOP REC")));
                    Assert.True(recordingStart.IsVisible);
                    Assert.False(recordingStop.IsVisible);
                    Assert.Same(owner.StartRecordingCommand, recordingStart.Command);
                    Assert.Same(owner.StopRecordingCommand, recordingStop.Command);

                    var before = (owner.LiveTakeEvidence, owner.RecordingStatus, owner.CommitStatus, owner.ProgramSourceId);
                    foreach (var name in new[] { "TakeDetails", "LayerDetails", "MonitoringDetails", "TransmissionDetails" })
                    {
                        var section = Assert.IsAssignableFrom<FrameworkElement>(panel.FindName(name));
                        var toggle = Assert.Single(Descendants<RtaimeButton>(panel).Where(button => Equals(button.Tag, name)));
                        Assert.Equal(Visibility.Collapsed, section.Visibility);
                        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
                        Assert.Equal(Visibility.Visible, section.Visibility);
                        Assert.Contains("HIDE", toggle.Content?.ToString());
                        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
                        Assert.Equal(Visibility.Collapsed, section.Visibility);
                    }
                    Assert.Equal(before, (owner.LiveTakeEvidence, owner.RecordingStatus, owner.CommitStatus, owner.ProgramSourceId));
                    var monitoringToggle = Assert.Single(Descendants<RtaimeButton>(panel).Where(button =>
                        Equals(button.Tag, "MonitoringDetails")));
                    monitoringToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, monitoringToggle));
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    var monitoringDetails = Assert.IsAssignableFrom<StackPanel>(panel.FindName("MonitoringDetails"));
                    var openClean = Assert.Single(Descendants<RtaimeButton>(monitoringDetails).Where(button =>
                        Equals(button.Content, "OPEN CLEAN")));
                    var stopClean = Assert.Single(Descendants<RtaimeButton>(monitoringDetails).Where(button =>
                        Equals(button.Content, "STOP CLEAN")));
                    Assert.Same(window.ProgramOutput.StartCommand, openClean.Command);
                    Assert.Same(window.ProgramOutput.StopCommand, stopClean.Command);
                    monitoringToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, monitoringToggle));
                    Assert.Equal(Visibility.Collapsed, monitoringDetails.Visibility);

                    // Synthetic presentation changes only; no recording request is made.
                    var setter = typeof(OperatorViewModel).GetProperty(nameof(OperatorViewModel.RecordingStatus),
                        BindingFlags.Instance | BindingFlags.Public)!.GetSetMethod(nonPublic: true)!;
                    setter.Invoke(owner, ["RECORDING"]);
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    Assert.True(recordingStop.IsVisible);
                    Assert.False(recordingStart.IsVisible);
                    Assert.False(recordingStop.Command.CanExecute(null)); // stale/disconnected remains disabled
                    setter.Invoke(owner, ["FINALIZING"]);
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    Assert.False(recordingStop.IsVisible);
                    Assert.False(recordingStart.IsVisible);
                    setter.Invoke(owner, ["FAILED"]);
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    Assert.True(recordingStart.IsVisible);
                    Assert.False(recordingStop.IsVisible);

                    window.Shell.SelectWorkspace("LIVE");
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    Assert.False(panel.IsVisible);
                    var actionStrip = Assert.IsAssignableFrom<ScrollViewer>(window.FindName("LiveActionStrip"));
                    Assert.True(actionStrip.IsVisible);
                    window.Shell.SelectWorkspace("MULTIVIEW");
                    await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    Assert.True(panel.IsVisible);
                    Assert.Same(owner.CutCommand, cut.Command);
                }
                catch (Exception exception) { complete.TrySetException(exception); }
                finally
                {
                    try
                    {
                        if (window is not null)
                        {
                            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            window.Closed += (_, _) => closed.TrySetResult();
                            window.Close();
                            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        }
                        application?.Shutdown();
                    }
                    catch (Exception exception) { complete.TrySetException(exception); }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            try { Dispatcher.Run(); }
            catch (Exception exception) { complete.TrySetException(exception); }
            finally { complete.TrySetResult(); }
        }) { IsBackground = true, Name = "LIVE controls visual qualification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await complete.Task.WaitAsync(TimeSpan.FromSeconds(40)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "LIVE controls STA did not terminate."); }
    }

    private static XElement Button(XDocument document, string content) =>
        Assert.Single(document.Descendants().Where(node =>
            node.Name.LocalName == "RtaimeButton" && node.Attribute("Content")?.Value == content));

    private static XElement NamedSection(XDocument document, string name) =>
        Assert.Single(document.Descendants().Where(node =>
            node.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == name)));

    private static XDocument Surface() => XDocument.Load(
        RepositoryFile("src", "Hosts", "rtaime.Operator", "LiveControlsControl.xaml"));

    private static string RepositoryFile(params string[] path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "rtaime.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine([root.FullName, .. path]);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
