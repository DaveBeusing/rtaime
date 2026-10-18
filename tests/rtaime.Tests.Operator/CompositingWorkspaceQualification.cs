// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

internal static class CompositingWorkspaceQualification
{
    internal static async Task VerifyWindowAsync(MainWindow window, Dispatcher dispatcher)
    {
        window.Shell.SelectWorkspace("COMPOSITING");
        if (window.Shell.IsRightCollapsed) window.Shell.ToggleRightPanelCommand.Execute(null);
        await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        var control = Descendants(window).OfType<CompositingGraphControl>().First();
        var originalGraph = control.DataContext;
        var inspectorView = window.InspectorView;
        var originalInspector = inspectorView.Inspector;
        var originalOwner = inspectorView.ControlState;
        var originalDeck = inspectorView.MediaDeck;
        var monitoring = window.Monitoring;
        var transport = new InspectorPrecisionTests.InspectorTransport { ManyLayers = true };
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        try
        {
            control.DataContext = graph;
            inspectorView.Inspector = inspector;
            inspectorView.ControlState = owner;
            inspectorView.MediaDeck = deck;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            var layers = Assert.IsType<RtaimeListBox>(control.FindName("LayerList"));
            Assert.Equal(8, layers.Items.Count);
            Assert.True(VirtualizingPanel.GetIsVirtualizing(layers));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(layers));
            layers.Focus(); layers.SelectedIndex = 0;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            graph.SelectNodeCommand.Execute(graph.Layers[0]);
            var opacity = Assert.IsType<RtaimeNumericField>(control.FindName("LayerOpacityEditor"));
            Assert.True(opacity.Focus());
            opacity.Text = "128";
            Assert.Equal(128, graph.SelectedLayerRow!.OpacityDraft);
            Assert.Equal(255, owner.CompositingLayers[0].Opacity);
            opacity.Text = "NaN";
            Assert.False(graph.ApplyLayerDraftCommand.CanExecute(null));
            opacity.Text = "128";
            Assert.True(graph.ApplyLayerDraftCommand.CanExecute(null));
            var transform = graph.Nodes.Single(node => node.Id == "transform:layer0");
            graph.SelectNodeCommand.Execute(transform);
            inspector.IsTransformExpanded = true;
            Descendants(inspectorView).OfType<RtaimeTabItem>().First(tab => Equals(tab.Header, "Inspector")).IsSelected = true;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Descendants(inspectorView).OfType<RtaimeTabItem>().First(tab => Equals(tab.Header, "Transform")).IsSelected = true;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            var x = Descendants(inspectorView).OfType<RtaimeNumericField>()
                .FirstOrDefault(field => field.IsVisible && field.Definition.Id == "layer.x");
            Assert.NotNull(x);
            Assert.True(x!.Focus()); x.Text = 0.375.ToString("G17", System.Globalization.CultureInfo.CurrentCulture);
            Assert.Equal(0.375, inspector.SelectedLayerPositionX);
            owner.SynchronizeCommand.Execute(null);
            Assert.Equal(0.375, inspector.SelectedLayerPositionX);
            inspector.UndoInspectorDraftCommand.Execute(null);
            Assert.Equal(0.1, inspector.SelectedLayerPositionX);
            var search = Assert.IsType<RtaimeTextBox>(control.FindName("LayerSearch"));
            search.Focus(); search.Text = "layer7";
            Assert.Same(transform, graph.SelectedNode);
            Assert.True(graph.Layers[7].MatchesSearch);
            graph.SearchText = "";
            graph.UpdateWorkspaceWidth(320);
            Assert.InRange(graph.LayerPanelWidth, 0, 122);
            graph.CollapseLayerCommand.Execute(graph.Layers[0]);
            Assert.True(graph.Layers[0].IsCollapsed);
            graph.Fit();
            Assert.InRange(graph.Zoom, 0.35, 2.5);
            OperatorControlPresentation.SetReducedMotion(control, true);
            OperatorControlPresentation.ApplyPreferenceSnapshot(control, false, true);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            var viewport = Assert.IsType<Grid>(control.FindName("GraphViewport"));
            Assert.Equal(SystemColors.WindowColor, Assert.IsType<SolidColorBrush>(viewport.Background).Color);
            Assert.Same(monitoring, window.Monitoring);
            Assert.Equal(0, transport.MutationCalls);
            Console.WriteLine("COMPOSITING WORKSPACE PASS: productive controls, precision drafts/undo, stable refresh, filter, finite layer virtualization, compact bounds and shared High Contrast/Reduced Motion; production authority unchanged.");
        }
        finally
        {
            OperatorControlPresentation.ApplyPreferenceSnapshot(control, SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast);
            OperatorControlPresentation.SetReducedMotion(control, false);
            control.DataContext = originalGraph;
            inspectorView.Inspector = originalInspector;
            inspectorView.ControlState = originalOwner;
            inspectorView.MediaDeck = originalDeck;
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
