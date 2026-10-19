// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

/// <summary>
/// Checks newly combined feature ownership on the one productive MainWindow.
/// A disconnected workspace fixture is not live media, physical DPI or Take
/// evidence; the real-host integrations own those separate requirements.
/// </summary>
internal static class OperatorExperienceIntegrationQualification
{
    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        var control = Assert.IsType<OperatorViewModel>(window.DataContext);
        var program = control.ProgramSourceId;
        var preview = control.PreviewSourceId;
        var activeWorkspace = window.Shell.SelectedWorkspace;
        var sequence = window.SequenceTimeline;
        var composition = window.Composition;
        var library = window.MediaPool;
        var monitoring = window.Monitoring;
        var health = window.HealthCenter;

        Assert.Same(control, composition.Live);
        Assert.Same(control, composition.Ai);
        Assert.Same(library, composition.Inspector);
        Assert.Same(library.Selection, composition.Selection);
        Assert.Same(window.InspectorView, composition.InspectorView);
        Assert.Same(window.OutputHealth, composition.Outputs);
        Assert.Same(health, composition.Health);
        Assert.Same(monitoring, composition.Viewer);
        Assert.Same(window.Timeline, composition.Timeline);

        // The actual compiled WPF templates must bind to their single owners.
        var elements = Descendants(window).ToArray();
        var sequenceView = Assert.Single(elements.OfType<SequenceTimelineControl>());
        var mediaDeckView = Assert.Single(elements.OfType<MediaTimelineControl>());
        var ai = Assert.IsType<AIProcessingWorkspaceControl>(
            window.FindName("AIProcessingWorkspace"));
        var canvas = Assert.Single(elements.OfType<OutputCanvasEditorControl>());
        Assert.Same(sequence, sequenceView.DataContext);
        Assert.Same(window.Timeline, mediaDeckView.DataContext);
        Assert.Same(control, ai.DataContext);
        Assert.Same(window.OutputHealth.CanvasEditor, canvas.DataContext);

        var openSequence = Assert.Single(elements.OfType<RtaimeButton>(), button =>
            AutomationProperties.GetName(button) == "Open saved multitrack sequence editor");
        var openLegacy = Assert.Single(elements.OfType<RtaimeButton>(), button =>
            AutomationProperties.GetName(button) == "Open legacy media deck timeline");

        try
        {
            window.Shell.SelectWorkspace("EDIT");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible,
                Assert.IsType<Border>(window.FindName("LowerTimelineRegion")).Visibility);
            Assert.Equal(Visibility.Visible, sequenceView.Visibility);
            Assert.Equal(Visibility.Collapsed, mediaDeckView.Visibility);
            Assert.False(sequence.CanEdit);
            Assert.Null(sequence.Snapshot);
            Assert.False(control.CutCommand.CanExecute(null));

            openLegacy.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, openLegacy));
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, mediaDeckView.Visibility);
            Assert.Equal(Visibility.Collapsed, sequenceView.Visibility);
            openSequence.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, openSequence));
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, sequenceView.Visibility);
            Assert.Equal(Visibility.Collapsed, mediaDeckView.Visibility);
            Assert.Same(sequence, sequenceView.DataContext);
            Assert.True(OperatorKeyboardCommandRegistry.IsSequenceAuthoringFocus(sequenceView));

            window.Shell.SelectWorkspace("OUTPUTS");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, canvas.Visibility);
            Assert.Same(window.OutputHealth.CanvasEditor, canvas.DataContext);

            window.Shell.SelectWorkspace("AI");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Equal(Visibility.Visible, ai.Visibility);
            Assert.Same(control, ai.DataContext);

            window.Shell.SelectWorkspace("HEALTH");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Assert.Same(health, window.HealthCenter);
            Assert.Same(control, window.DataContext);
            Assert.Equal(program, control.ProgramSourceId);
            Assert.Equal(preview, control.PreviewSourceId);
            Assert.False(control.CutCommand.CanExecute(null));

            Console.WriteLine(
                "INTEGRATED OPERATOR STRUCTURE PASS: one Client/Operator owner across saved " +
                "sequence/legacy timeline, Outputs canvas, governed AI and Health; " +
                "disconnected editing and CUT remain disabled. " +
                "No native sequence Preview/Program output or physical visual review is inferred.");
        }
        finally
        {
            window.Shell.SelectWorkspace(activeWorkspace);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child))
                yield return nested;
        }
    }
}
