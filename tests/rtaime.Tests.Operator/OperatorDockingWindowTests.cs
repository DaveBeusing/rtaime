// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

internal static class OperatorDockingWindowTests
{
    internal static async Task VerifyAsync(MainWindow owner, Dispatcher dispatcher)
    {
        owner.Shell.SelectWorkspace("LIVE");
        owner.Shell.SetDocking(OperatorDockLayout.Default);
        var identities = owner.Docking.Panels.ToDictionary(p => p.Key, p => p.Value);
        var preview = ((OperatorViewModel)owner.DataContext).PreviewSourceId;
        var program = ((OperatorViewModel)owner.DataContext).ProgramSourceId;
        var watch = Stopwatch.StartNew();
        Console.WriteLine("DOCK WORKSPACE BEGIN: loaded-window synchronization while live rendering remains active.");
        for (var cycle = 0; cycle < 3; cycle++)
        {
            owner.Docking.Dock("TOOLS", "CENTER");
            owner.Docking.Dock("MONITORS", "CENTER");
            Assert.Equal(2, owner.Shell.Docking.Groups.Single(g => g.Id == "CENTER").Panels.Length);
            owner.Docking.Float("MONITORS");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Console.WriteLine($"DOCK CYCLE {cycle}: floating window loaded.");
            var floating = owner.Docking.FloatingWindows["MONITORS"];
            Assert.Same(owner, floating.Owner);
            Assert.Same(owner.Monitoring, floating.Monitoring);
            Assert.Same(owner.Shortcuts, floating.Shortcuts);
            Assert.Same(owner.DataContext, floating.DataContext);
            Assert.True(floating.IsVisible);
            var probe = new TextBlock();
            BindingOperations.SetBinding(probe, TextBlock.TextProperty,
                new Binding("Shell.SelectedWorkspace") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
            var chrome = Assert.IsType<Grid>(floating.Content);
            chrome.Children.Add(probe);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(owner.Shell.SelectedWorkspace, probe.Text);
            chrome.Children.Remove(probe);
            floating.Close();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(owner.Docking.FloatingWindows);
            foreach (var identity in identities) Assert.Same(identity.Value, owner.Docking.Panels[identity.Key]);
            owner.Docking.NextCommand.Execute(null);
            owner.Docking.FocusCommand.Execute(null);
            owner.Docking.FocusCommand.Execute(null);
            owner.Shell.UpdateViewportSize(960, 500);
            owner.Docking.Dock("INSPECTOR", "CENTER");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task.WaitAsync(TimeSpan.FromSeconds(5));
            owner.Shell.UpdateViewportSize(1600, 900);
            owner.Shell.ResetLayoutCommand.Execute(null);
            Assert.False(owner.Shell.Docking.Enabled);
            Assert.All(identities.Values, panel => Assert.NotNull(panel.Parent));
        }
        Assert.Equal(preview, ((OperatorViewModel)owner.DataContext).PreviewSourceId);
        Assert.Equal(program, ((OperatorViewModel)owner.DataContext).ProgramSourceId);
        Assert.False(((OperatorViewModel)owner.DataContext).CutCommand.CanExecute(null));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 10);
        owner.Shell.SelectWorkspace("MEDIA");
        owner.Docking.Float("TOOLS");
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Docking.Capture();
        Assert.Single(owner.Docking.FloatingWindows);
        // The caller closes MainWindow through its actual asynchronous lifecycle, exercising owned-window teardown.
        Console.WriteLine("DOCK WORKSPACE PASS: existing panels, tabs, float/dock cycles, shared bindings/shortcuts, compact layout and unchanged source identities.");
    }
}
