// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

internal static class LiveConsoleWindowQualification
{
    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        var monitoring = window.Monitoring;
        var width = window.Width; var height = window.Height;
        var workspace = window.Shell.SelectedWorkspace;
        window.Shell.SelectWorkspace("LIVE");
        if (window.Shell.IsRightCollapsed) window.Shell.ToggleRightPanelCommand.Execute(null);
        await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        var live = Descendants(window).OfType<LiveControlsControl>().First();
        var cut = Descendants(live).OfType<RtaimeButton>().Single(button => Equals(button.Content, "CUT"));
        var original = cut.Command;
        var autoCalls = 0; var cutCalls = 0;
        var registry = new OperatorKeyboardCommandRegistry([
            new("auto", "AUTO", Key.Enter, ModifierKeys.None, new OperatorShellCommand(() => autoCalls++))]);
        try
        {
            cut.Command = new OperatorShellCommand(() => cutCalls++);
            foreach (var size in new[] { (1280d, 720d), (1920d, 1080d) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                cut.BringIntoView();
                Assert.True(cut.IsVisible);
                Assert.True(cut.Focus());
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cut)!, 0, Key.Enter);
                Assert.False(registry.TryHandle(enter));
                Assert.Equal(0, autoCalls);
                cut.Command.Execute(null);
                // The actual window retains distinct commands and confirmed target bindings at both sizes.
                Assert.NotSame(window.Rundown.GoCommand, window.ShowControl.GoCommand);
                Assert.True(cut.ActualWidth > 0);
            }
            Assert.Equal(2, cutCalls);
            var scenes = Descendants(window).OfType<LiveSceneCueControl>().First();
            var rundownTab = Descendants(scenes).OfType<RtaimeTabItem>().Single(tab => Equals(tab.Header, "RUNDOWN"));
            rundownTab.IsSelected = true;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            var rundown = Descendants(scenes).OfType<RundownControl>().Single();
            Assert.Same(window.Rundown, rundown.DataContext);
            var name = Descendants(rundown).OfType<RtaimeTextBox>().First();
            name.BringIntoView();
            Assert.True(name.Focus());
            var textEnter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(name)!, 0, Key.Enter);
            registry.TryHandle(textEnter);
            Assert.Equal(0, autoCalls);
            Assert.True(OperatorKeyboardCommandRegistry.IsTextEntryContext(name));
            Assert.Same(monitoring, window.Monitoring);
        }
        finally {
            cut.Command = original;
            window.Width = width; window.Height = height;
            window.Shell.SelectWorkspace(workspace);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
