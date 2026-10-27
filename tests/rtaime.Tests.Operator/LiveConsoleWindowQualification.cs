// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var rootWidth = root.Width; var rootHeight = root.Height; var transform = root.LayoutTransform;
        var monitoring = window.Monitoring;
        var width = window.Width; var height = window.Height;
        var workspace = window.Shell.SelectedWorkspace;
        window.Shell.SelectWorkspace("MULTIVIEW");
        if (window.Shell.IsCenterMaximized) window.Shell.ToggleCenterMaximizeCommand.Execute(null);
        if (window.Shell.IsLeftCollapsed) window.Shell.ToggleLeftPanelCommand.Execute(null);
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
                root.Width = size.Item1; root.Height = size.Item2; root.LayoutTransform = Transform.Identity;
                window.Width = size.Item1; window.Height = size.Item2;
                await dispatcher.InvokeAsync(() => {
                    window.Shell.UpdateViewportSize(size.Item1, size.Item2);
                    window.UpdateLayout();
                }, DispatcherPriority.Loaded);
                cut.BringIntoView();
                Assert.True(cut.IsVisible);
                Assert.True(cut.Focus());
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(cut)!, 0, Key.Enter);
                Assert.False(registry.TryHandle(enter));
                Assert.Equal(0, autoCalls);
                cut.Command.Execute(null);
                // The actual window retains distinct commands and confirmed target bindings at both sizes.
                Assert.NotSame(window.Rundown.GoCommand, window.ShowControl.GoCommand);
                Assert.True(cut.ActualWidth > 0, $"CUT width {cut.ActualWidth}; viewport compact {window.Shell.IsCompactViewport}; right {window.Shell.RightColumnWidth}; center {window.Shell.IsCenterMaximized}");
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

            // Compact LIVE keeps the deliberate guarded switching actions first.
            // The rest of the status/selection/navigation row may scroll locally.
            window.Shell.SelectWorkspace("LIVE");
            root.Width = 960; root.Height = 540; root.LayoutTransform = Transform.Identity;
            window.Width = 960; window.Height = 540;
            await dispatcher.InvokeAsync(() => {
                window.Shell.UpdateViewportSize(960, 540);
                window.UpdateLayout();
            }, DispatcherPriority.Loaded);
            var strip = Assert.IsAssignableFrom<ScrollViewer>(window.FindName("LiveActionStrip"));
            Assert.True(strip.IsVisible);
            Assert.True(strip.ViewportWidth > 0);
            strip.ScrollToHorizontalOffset(0);
            var liveButtons = Descendants(strip).OfType<RtaimeButton>().ToArray();
            Assert.True(liveButtons.Length >= 6);
            Assert.Equal(new[] { "SET PVW", "CUT", "AUTO" },
                liveButtons.Take(3).Select(button => button.Content?.ToString()).ToArray());
            Assert.Same(((OperatorViewModel)window.DataContext).SetPreviewCommand, liveButtons[0].Command);
            Assert.Same(((OperatorViewModel)window.DataContext).CutCommand, liveButtons[1].Command);
            Assert.Same(((OperatorViewModel)window.DataContext).DissolveCommand, liveButtons[2].Command);
            Assert.True(liveButtons[1].Focus());
            var compactEnter = new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(liveButtons[1])!, 0, Key.Enter);
            Assert.False(registry.TryHandle(compactEnter));
            Assert.Equal(0, autoCalls); // Focused CUT cannot accidentally trigger global AUTO.
            Assert.Same(liveButtons[1], LiveConsoleInput.FindButton(liveButtons[1]));
            Assert.Equal(((OperatorViewModel)window.DataContext).LiveTakeEvidence,
                Assert.IsType<TextBlock>(window.FindName("LiveAuthorityDetail")).Text);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                AutomationProperties.GetName(text) == "Program monitoring receipt age" &&
                text.Text == monitoring.ProgramFrameAge);
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text =>
                AutomationProperties.GetName(text) == "Preview monitoring receipt age" &&
                text.Text == monitoring.PreviewFrameAge);
            var programViewer = Descendants(window).OfType<ProgramViewer>().First(viewer => viewer.IsVisible);
            Assert.DoesNotContain(Descendants(programViewer).OfType<RtaimeIconButton>(), button =>
                AutomationProperties.GetName(button) is "Stop Preview" or "Play or pause" or "Set IN");
            Assert.Same(monitoring, window.Monitoring);
        }
        finally {
            cut.Command = original;
            root.Width = rootWidth; root.Height = rootHeight; root.LayoutTransform = transform;
            window.Shell.UpdateViewportSize(width, height);
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
