// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed partial class OperatorCombinedVisualQualification
{
    internal static async Task VerifyShellStatesAsync(MainWindow? main, Dispatcher dispatcher)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rtaime-shell-states-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var transport = new ShellStateTransport();
        var model = new OperatorViewModel(new OperatorControlClient(transport),
            synchronizationContext: SynchronizationContext.Current, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
        MainWindow? window = null;
        var evidence = Environment.GetEnvironmentVariable("RTAIME_OPERATOR_VISUAL_EVIDENCE");
        var observations = new List<object>();
        var surfaces = new HashSet<OperatorDrawingSurface>();
        try
        {
            var store = new OperatorLayoutStore(Path.Combine(directory, "layout.json"));
            await store.SaveAsync(OperatorLayoutSettings.Default with { IsFullscreen = false });
            window = new MainWindow(model, store, new OperatorSessionRecoveryStore(Path.Combine(directory, "session.json")),
                new OperatorLibraryPreferenceStore(Path.Combine(directory, "library.json"))) { Owner = main };
            window.Show();
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await UntilShellState(() => !model.IsBusy);
            var stressRoot = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            stressRoot.Width = 1600; stressRoot.Height = 900;
            window.Width = 1600; window.Height = 900;
            window.Shell.SelectWorkspace("LIVE");
            window.Shell.UpdateViewportSize(1600,900);
            await dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.Loaded);
            await VerifyRepeatedShellMovesAsync(window,dispatcher);
            var stressMetrics = await MeasureSettledAsync(window,stressRoot,dispatcher);
            Console.WriteLine("SHELL UI METRICS: " + JsonSerializer.Serialize(stressMetrics));
            async Task CaptureState(string state)
            {
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var identity = (model.PreviewSourceId, model.ProgramSourceId);
                var monitoring = window.Monitoring;
                foreach (var size in new[] { new Size(1920,1080), new Size(960,540) })
                {
                    root.Width = size.Width; root.Height = size.Height; root.LayoutTransform = Transform.Identity;
                    window.Width = size.Width; window.Height = size.Height;
                    foreach (var id in OperatorWorkspaceNames.All)
                    {
                        window.Shell.SelectWorkspace(id);
                        await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                        var top = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("TopBar"));
                        var program = Descendants(top).OfType<RtaimeStatusBadge>().Single(b => b.Status == RtaimeStatusKind.Program);
                        Assert.True(Fits(program.TransformToAncestor(root).TransformBounds(new Rect(program.RenderSize)), root.RenderSize));
                        Assert.True(Fits(top.TransformToAncestor(root).TransformBounds(new Rect(top.RenderSize)), root.RenderSize));
                        Assert.Equal(identity, (model.PreviewSourceId, model.ProgramSourceId));
                        Assert.Same(monitoring, window.Monitoring);
                        if (state != "connected") Assert.False(model.CutCommand.CanExecute(null));
                        foreach (var surface in Descendants(window).OfType<OperatorDrawingSurface>()) surfaces.Add(surface);
                        if(window.Shell.IsCompactViewport && window.Shell.HasAuxiliaryWorkspaceColumn && !window.Shell.IsAiWorkspace)
                        {
                            window.Shell.SelectCompactAuxiliaryCommand.Execute("DETAILS");
                            await dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.Loaded);
                            Assert.Equal(0,window.Shell.PrimaryWorkspaceWidth.Value);
                            Assert.True(window.Shell.AuxiliaryWorkspaceColumnWidth.IsStar);
                            window.Shell.SelectCompactAuxiliaryCommand.Execute("PRIMARY");
                            await dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.Loaded);
                        }
                        if(id == "LIVE")
                        {
                            var rundown=Descendants(window).OfType<RundownControl>().Single(r=>r.IsVisible && r.ActualWidth>0);
                            var go=Descendants(rundown).OfType<ButtonBase>().Single(b=>ReferenceEquals(b.Command,window.Rundown.GoCommand));
                            Assert.True(Fits(go.TransformToAncestor(rundown).TransformBounds(new Rect(go.RenderSize)),rundown.RenderSize),
                                "LIVE rundown GO must fit the allocated lower region.");
                        }
                        observations.Add(new { state, workspace=id, width=size.Width, height=size.Height,
                            connected=model.IsConnected, busy=model.IsBusy, stale=model.IsStale });
                        if (id == "LIVE" && !string.IsNullOrWhiteSpace(evidence))
                            SaveImage(root,size.Width,size.Height,1,Path.Combine(evidence,$"shell-{state}-{size.Width:0}.png"));
                    }
                }
            }
            Assert.False(model.IsConnected);
            await CaptureState("no-data");
            transport.Offline = false;
            model.SynchronizeCommand.Execute(null);
            await UntilShellState(() => model.IsConnected && !model.IsBusy);
            await CaptureState("connected");
            transport.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            model.SynchronizeCommand.Execute(null);
            await UntilShellState(() => model.IsBusy);
            await CaptureState("busy");
            transport.Pending.TrySetResult();
            await UntilShellState(() => !model.IsBusy);
            transport.Pending = null; transport.Offline = true;
            model.SynchronizeCommand.Execute(null);
            await UntilShellState(() => model.IsStale && !model.IsBusy);
            await CaptureState("stale");
            if (!string.IsNullOrWhiteSpace(evidence))
                File.WriteAllText(Path.Combine(evidence,"shell-states.json"),JsonSerializer.Serialize(new {
                    sourceCommit=Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
                    scope="Real compiled MainWindow with controlled Client responses; presentation only, no real-media/device/readiness certification.",
                    stressCycles=100, stressMetrics, cases=observations },new JsonSerializerOptions { WriteIndented=true }));
            Assert.Equal(80,observations.Count);
            Assert.Equal(0,transport.MutationCalls);
            // The shared router must leave Enter on a focused shell button to that explicit button.
            transport.Offline = false;
            model.SynchronizeCommand.Execute(null);
            await UntilShellState(() => model.IsConnected && !model.IsBusy);
            window.Shell.SelectWorkspace("LIVE");
            await dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.Loaded);
            window.Activate();
            var strip=Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LiveActionStrip"));
            var library=Descendants(strip).OfType<ButtonBase>().Single(b => ReferenceEquals(b.Command,window.Shell.ToggleLeftPanelCommand));
            Assert.True(library.Focus());
            Assert.True(window.Shortcuts.TryApplyProfile(OperatorShortcutProfile.Defaults,out _));
            var enter=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(library)!,0,Key.Enter);
            Assert.False(window.Shortcuts.TryHandle(enter));
            var space=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(library)!,0,Key.Space);
            Assert.False(window.Shortcuts.TryHandle(space));
            window.Shell.ToggleLeftPanelCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.Loaded);
            foreach (var action in Descendants(strip).OfType<ButtonBase>().Where(button => button.IsVisible))
                Assert.True(Fits(action.TransformToAncestor(strip).TransformBounds(new Rect(action.RenderSize)),strip.RenderSize),
                    "Compact LIVE actions must remain visible with Library open.");
            var mediaList = Descendants(window.MediaLibraryView).OfType<ListBox>().First(list => list.IsVisible);
            Assert.True(mediaList.Focus());
            foreach (var key in new[] { Key.Enter, Key.Space, Key.Up, Key.Down, Key.Delete })
            {
                var nativeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(mediaList)!, 0, key);
                Assert.False(window.Shortcuts.TryHandle(nativeKey));
            }
            Assert.Equal(0,transport.MutationCalls);
            Console.WriteLine("SHELL STATES PASS: 80 real-window workspace/state/viewport cases, retained routing, focused-button Enter/Space and native Library selection; controlled Client presentation only.");
        }
        finally
        {
            transport.Pending?.TrySetResult();
            if(window is not null)
            {
                var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed+=(_,_)=>closed.TrySetResult();
                window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
                Assert.All(surfaces,s=>Assert.Equal(0,s.RenderingSubscriptionBalance));
                Assert.Equal(0,window.InspectorView.InspectorSubscriptionCount);
            }
            else await model.DisposeAsync();
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
            main?.Activate();
        }
    }
    private static async Task UntilShellState(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(5);
        while(!condition() && DateTime.UtcNow<deadline) await Task.Delay(5);
        Assert.True(condition(),"Controlled shell state did not settle.");
    }
    private sealed class ShellStateTransport : IOperatorControlTransport
    {
        private readonly InspectorPrecisionTests.InspectorTransport _snapshot = new() { RemoteCut=true };
        internal bool Offline=true;
        internal TaskCompletionSource? Pending;
        internal int MutationCalls;
        public async ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken=default)
        {
            if(Pending is {} pending) await pending.Task.WaitAsync(cancellationToken);
            if(Offline) throw new TimeoutException("Controlled unavailable Client observation.");
            return await _snapshot.GetSnapshotAsync(cancellationToken);
        }
        private ValueTask<OperatorMutationResponse> UnexpectedMutation()
        { MutationCalls++; return ValueTask.FromException<OperatorMutationResponse>(new InvalidOperationException("Shell navigation must not mutate production.")); }
        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command,CancellationToken cancellationToken=default)=>UnexpectedMutation();
        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command,CancellationToken cancellationToken=default)=>UnexpectedMutation();
        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command,CancellationToken cancellationToken=default)=>UnexpectedMutation();
    }
}
