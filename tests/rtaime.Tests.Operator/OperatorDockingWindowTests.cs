// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Text.Json;
using System.Windows.Automation;
using rtaime.Operator.Controls;
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
            var retainedContent = floating.Content;
            var unloads = 0;
            RoutedEventHandler unloaded = (_, _) => unloads++;
            identities["MONITORS"].Unloaded += unloaded;
            try
            {
                owner.Docking.Dock("TOOLS", "LEFT");
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                Assert.Same(floating, owner.Docking.FloatingWindows["MONITORS"]);
                Assert.Same(retainedContent, floating.Content); Assert.Equal(0, unloads);
            }
            finally { identities["MONITORS"].Unloaded -= unloaded; }
            var probe = new TextBlock();
            BindingOperations.SetBinding(probe, TextBlock.TextProperty,
                new Binding("Shell.SelectedWorkspace") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
            var chrome = Assert.IsType<Grid>(floating.Content);
            chrome.Children.Add(probe);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind).Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(owner.Shell.SelectedWorkspace, probe.Text);
            var templateCommand = new RtaimeButton();
            BindingOperations.SetBinding(templateCommand, System.Windows.Controls.Primitives.ButtonBase.CommandProperty,
                new Binding("OpenWorkspaceTemplatesCommand") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1) });
            chrome.Children.Add(templateCommand);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Same(owner.OpenWorkspaceTemplatesCommand, templateCommand.Command);
            Assert.Same(owner.Docking, floating.Docking);
            chrome.Children.Remove(templateCommand);
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
        // Move the actual extracted Library and existing Inspector, never replacement controls.
        owner.Shell.SelectWorkspace("MEDIA");
        var library = owner.MediaLibraryView;
        var context = library.Context;
        var inspector = owner.InspectorView;
        var selection = owner.MediaPool.Selection;
        var monitoring = owner.Monitoring;
        try
        {
            foreach (var panel in new[] { "TOOLS", "INSPECTOR" })
            {
                owner.Docking.Float(panel);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.DataBind);
                Assert.Same(context, library.Context);
                Assert.Same(selection, owner.Composition.Selection);
                Assert.Same(owner.MediaPool, inspector.Inspector);
                Assert.Same(owner.MediaDeck, inspector.MediaDeck);
                Assert.Same(owner.DataContext, inspector.ControlState);
                Assert.Same(monitoring, owner.Monitoring);
                var grid = Assert.IsType<RtaimeListBox>(library.FindName("MediaAssetGrid"));
                Assert.Same(owner.MediaPool.FilteredItems, grid.ItemsSource);
                Assert.Equal(1, inspector.InspectorSubscriptionCount);
                owner.Docking.FloatingWindows[panel].Close();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                Assert.Same(library, owner.MediaLibraryView);
                Assert.Same(inspector, owner.InspectorView);
            }
        }
        finally { owner.Shell.ResetLayoutCommand.Execute(null); }
        Console.WriteLine("COMPOSITION SEAMS PASS: retained Library/Inspector contexts, projection, selection, commands and monitoring owner across floating/re-docking.");
        Assert.Equal(preview, ((OperatorViewModel)owner.DataContext).PreviewSourceId);
        Assert.Equal(program, ((OperatorViewModel)owner.DataContext).ProgramSourceId);
        Assert.False(((OperatorViewModel)owner.DataContext).CutCommand.CanExecute(null));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 10);
        var beforeMode = JsonSerializer.Serialize(owner.Shell.CurrentWorkspaceLayout());
        var fullscreen = owner.Shell.IsFullscreen;
        owner.Docking.FocusCommand.Execute(null);
        Assert.Equal("FOCUS", owner.Shell.PresentationMode);
        owner.Shell.ExitFullscreenCommand.Execute(null);
        Assert.False(owner.Shell.PresentationActive);
        Assert.Equal(beforeMode, JsonSerializer.Serialize(owner.Shell.CurrentWorkspaceLayout()));
        owner.Docking.CinemaCommand.Execute(null);
        Assert.Equal("CINEMA", owner.Shell.PresentationMode); Assert.Equal("PROGRAM", owner.Shell.ViewerMode);
        await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
        Assert.Contains(Descendants(owner).OfType<TextBlock>(), t => AutomationProperties.GetName(t) == "Current Program identity, connection and safety" && t.IsVisible);
        owner.Docking.RestoreCommand.Execute(null);
        Assert.Equal(fullscreen, owner.Shell.IsFullscreen);
        Assert.Equal(beforeMode, JsonSerializer.Serialize(owner.Shell.CurrentWorkspaceLayout()));
        await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
        owner.Docking.FocusCommand.Execute(null);
        owner.Docking.Float("INSPECTOR"); Assert.False(owner.Shell.PresentationActive);
        owner.Docking.FloatingWindows["INSPECTOR"].Close();
        owner.Shell.ResetLayoutCommand.Execute(null);
        var workspace = owner.Shell.SelectedWorkspace;
        owner.OpenWorkspaceTemplatesCommand.Execute(null);
        await dispatcher.InvokeAsync(owner.UpdateLayout, DispatcherPriority.Loaded);
        var templates = Assert.IsType<WorkspaceTemplatesWindow>(owner.WorkspaceTemplatesWindow);
        Assert.Same(owner.Shell, templates.DataContext); Assert.Same(owner, templates.Owner);
        var readOnlyBindings = Descendants(templates).OfType<TextBlock>()
            .SelectMany(text => text.Inlines.OfType<System.Windows.Documents.Run>())
            .Select(run => BindingOperations.GetBindingExpression(run, System.Windows.Documents.Run.TextProperty))
            .Where(binding => binding is not null).ToArray();
        Assert.NotEmpty(readOnlyBindings);
        Assert.All(readOnlyBindings, binding => Assert.Equal(BindingMode.OneWay, binding!.ParentBinding.Mode));

        Assert.True(Assert.IsType<RtaimeTextBox>(templates.FindName("TemplateNameBox")).Focus());
        owner.OpenWorkspaceTemplatesCommand.Execute(null); Assert.Same(templates, owner.WorkspaceTemplatesWindow);
        Assert.Equal(workspace, owner.Shell.SelectedWorkspace);
        templates.Close();
        Assert.Null(owner.WorkspaceTemplatesWindow);
        foreach (var identity in identities) Assert.Same(identity.Value, owner.Docking.Panels[identity.Key]);
        Assert.Equal(preview, ((OperatorViewModel)owner.DataContext).PreviewSourceId);
        Assert.Equal(program, ((OperatorViewModel)owner.DataContext).ProgramSourceId);
        Console.WriteLine("WORKSPACE PRESENTATION PASS: retained floating native window/content, no monitor unload on unrelated moves, focus/cinema exact restore, visible Program status, shared bounded template editor.");
        owner.Shell.SelectWorkspace("MEDIA");
        owner.Docking.Float("TOOLS");
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded).Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Docking.Capture();
        Assert.Single(owner.Docking.FloatingWindows);
        // The caller closes MainWindow through its actual asynchronous lifecycle, exercising owned-window teardown.
        Console.WriteLine("DOCK WORKSPACE PASS: existing panels, tabs, float/dock cycles, shared bindings/shortcuts, compact layout and unchanged source identities.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

}
