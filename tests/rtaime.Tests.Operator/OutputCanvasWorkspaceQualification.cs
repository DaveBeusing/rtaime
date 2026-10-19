// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Operator;
using rtaime.Operator.Controls;

using Xunit;

namespace rtaime.Tests.Operator;

// Runs inside the existing productive MainWindow STA/dispatcher qualification.
// No independent WPF Application, GPU renderer or production authority is created.
internal static class OutputCanvasWorkspaceQualification
{
    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        var previous = window.Shell.SelectedWorkspace;
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
            Assert.Same(window.OutputHealth.CanvasEditor, editor.DataContext);
            Assert.Equal(Visibility.Visible, editor.Visibility);

            var stage = Assert.Single(Descendants<OutputCanvasStageSurface>(editor));
            Assert.Same(window.OutputHealth.CanvasEditor, stage.Editor);
            Assert.True(stage.Focusable);
            Assert.True(stage.ActualWidth > 100,
                $"Canvas stage collapsed at {stage.ActualWidth} DIPs; workspace={window.Shell.SelectedWorkspace}, " +
                $"compact={window.Shell.IsCompactViewport}, auxiliary={window.Shell.AuxiliaryWorkspaceColumnWidth}, " +
                $"editor visible={editor.IsVisible}.");
            Assert.True(stage.ActualHeight >= 180);
            Assert.False(window.OutputHealth.CanvasEditor.ApplyCommand.CanExecute(null));
            Assert.Equal("UNVERIFIED", window.OutputHealth.CanvasEditor.ActiveRevision);

            // The compact/dual-monitor output surfaces must not create separate
            // editors or a WPF production compositor while hidden.
            Assert.Single(Descendants<OutputCanvasEditorControl>(window));
            Assert.Empty(Descendants<OperatorDrawingSurface>(editor));
        }
        finally
        {
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
