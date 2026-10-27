// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using Xunit;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;

namespace rtaime.Tests.Operator;

// Runs inside the existing productive MainWindow STA/dispatcher qualification.
// No independent WPF Application, GPU renderer or production authority is created.
internal static class OutputCanvasWorkspaceQualification
{
    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        var previous = window.Shell.SelectedWorkspace;
        var previousCanvasMode = window.OutputHealth.IsCanvasMode;
        var selectedCompactDetails = false;
        try
        {
            window.Shell.SelectWorkspace(OperatorWorkspaceNames.Outputs);
            // The selected workspace and responsive column bindings need a layout
            // pass before deciding which navigation mode is actually presented.
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            // The compact shell intentionally hides its DETAILS column behind the
            // separate action. Follow that real Operator path before measurement.
            if (window.Shell.AuxiliaryNavigationVisibility == Visibility.Visible)
            {
                window.Shell.SelectCompactAuxiliaryCommand.Execute("DETAILS");
                selectedCompactDetails = true;
                await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                Assert.True(window.Shell.AuxiliaryWorkspaceColumnWidth.IsStar);
            }

            var editors = Descendants<OutputCanvasEditorControl>(window).ToArray();
            var editor = Assert.Single(editors);
            var output = Assert.Single(Descendants<OutputRoutingHealthControl>(window),
                control => !control.CompactMode);
            var routing = Assert.IsAssignableFrom<FrameworkElement>(output.FindName("RoutingModeSurface"));
            var stage = Assert.Single(Descendants<OutputCanvasStageSurface>(editor));
            window.OutputHealth.ShowRoutingModeCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.False(window.OutputHealth.IsCanvasMode);
            Assert.False(window.OutputHealth.CanvasEditor.IsWorkspaceVisible);
            Assert.True(routing.IsVisible);
            Assert.False(stage.IsVisible);

            var savedRevision = window.OutputHealth.CanvasEditor.SavedRevision;
            var wasDirty = window.OutputHealth.CanvasEditor.IsDirty;
            window.OutputHealth.ShowCanvasModeCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.True(window.OutputHealth.IsCanvasMode);
            Assert.True(window.OutputHealth.CanvasEditor.IsWorkspaceVisible);
            Assert.Equal(savedRevision, window.OutputHealth.CanvasEditor.SavedRevision);
            Assert.Equal(wasDirty, window.OutputHealth.CanvasEditor.IsDirty);
            Assert.Same(window.OutputHealth.CanvasEditor, editor.DataContext);
            Assert.Equal(Visibility.Visible, editor.Visibility);
            Assert.False(routing.IsVisible);
            Assert.True(stage.IsVisible,
                "Selecting CANVAS must update the nested visibility binding before measuring the stage.");

            // In compact OUTPUTS the editor lives inside the existing scrolling
            // details region. Its stage must be scrolled into the actual viewport
            // before asserting native layout dimensions.
            stage.BringIntoView();
            window.UpdateLayout();
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.Same(window.OutputHealth.CanvasEditor, stage.Editor);
            Assert.True(stage.Focusable);
            Assert.True(stage.ActualWidth > 100,
                $"Canvas stage collapsed at {stage.ActualWidth} DIPs; workspace={window.Shell.SelectedWorkspace}, " +
                $"compact={window.Shell.IsCompactViewport}, auxiliary={window.Shell.AuxiliaryWorkspaceColumnWidth}, " +
                $"editor visible={editor.IsVisible}.");
            Assert.True(stage.ActualHeight >= 180);
            var canvas = window.OutputHealth.CanvasEditor;
            Assert.Contains(Descendants<RtaimeButton>(editor), button =>
                Equals(button.Content, "CENTER X") && ReferenceEquals(button.Command, canvas.AlignHorizontalCommand));
            Assert.Contains(Descendants<RtaimeButton>(editor), button =>
                Equals(button.Content, "CENTER Y") && ReferenceEquals(button.Command, canvas.AlignVerticalCommand));
            Assert.Contains(Descendants<RtaimeButton>(editor), button =>
                Equals(button.Content, "RECONCILE UNKNOWN") &&
                ReferenceEquals(button.Command, canvas.ReconcileCommand));
            Assert.Contains(Descendants<RtaimeCheckBox>(editor), check =>
                AutomationProperties.GetName(check) == "Show optional logical design safe area guides" &&
                BindingOperations.GetBinding(check, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path.Path == "ShowSafeAreas");
            Assert.Contains(Descendants<TextBlock>(editor), label =>
                BindingOperations.GetBinding(label, TextBlock.TextProperty)?.Path.Path == "PointerCoordinates");
            Assert.Contains(Descendants<TextBlock>(editor), label =>
                BindingOperations.GetBinding(label, TextBlock.TextProperty)?.Path.Path == "ObservedTargetEvidence");
            Assert.False(canvas.ReconcileCommand.CanExecute(null));
            Assert.False(window.OutputHealth.CanvasEditor.ApplyCommand.CanExecute(null));
            Assert.Equal("UNVERIFIED", window.OutputHealth.CanvasEditor.ActiveRevision);

            // Both transitions must reach the existing bound controls. A true
            // view-model flag alone does not make a collapsed ancestor visible.
            window.OutputHealth.ShowRoutingModeCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.True(routing.IsVisible);
            Assert.False(stage.IsVisible);
            Assert.False(stage.IsMouseCaptured);
            window.OutputHealth.ShowCanvasModeCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.False(routing.IsVisible);
            Assert.True(stage.IsVisible);
            Assert.True(stage.ActualWidth > 100);
            Assert.True(stage.ActualHeight >= 180);
            Assert.Equal(savedRevision, window.OutputHealth.CanvasEditor.SavedRevision);
            Assert.Equal(wasDirty, window.OutputHealth.CanvasEditor.IsDirty);

            // The compact/dual-monitor output surfaces must not create separate
            // editors or a WPF production compositor while hidden.
            Assert.Single(Descendants<OutputCanvasEditorControl>(window));
            Assert.Empty(Descendants<OperatorDrawingSurface>(editor));
        }
        finally
        {
            if (previousCanvasMode)
                window.OutputHealth.ShowCanvasModeCommand.Execute(null);
            else
                window.OutputHealth.ShowRoutingModeCommand.Execute(null);
            if (selectedCompactDetails)
                window.Shell.SelectCompactAuxiliaryCommand.Execute("PRIMARY");
            window.Shell.SelectWorkspace(previous);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T matched)yield return matched;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
}
