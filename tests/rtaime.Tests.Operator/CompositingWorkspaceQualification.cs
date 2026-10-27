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
        // The Color Review submode borrows exactly the existing monitoring and
        // Inspector owners and preserves all registered workspace identities.
        var review = Assert.IsType<ColorReviewWorkspaceControl>(
            window.FindName("ColorReviewWorkspace"));
        var color = window.ColorReview;
        var scopesBeforeReview = monitoring.ScopesEnabled;
        Assert.Same(monitoring, color.Monitoring);
        Assert.Same(window.MediaPool, color.Inspector);
        Assert.Equal(Visibility.Collapsed, color.ReviewVisibility);
        Assert.True(control.IsVisible);
        Assert.False(color.AnalysisEnabled);
        color.ShowReviewCommand.Execute(null);
        await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        Assert.True(review.IsVisible);
        Assert.Same(color, review.DataContext);
        Assert.False(control.IsVisible);
        Assert.True(color.AnalysisEnabled);
        Assert.True(monitoring.ScopesEnabled);
        Assert.Equal(rtaime.Media.Contracts.MonitoringStreamKind.Program, monitoring.ActiveScopeStream);
        var scopes = Descendants(review).OfType<RtaimeMediaScope>().ToArray();
        Assert.Equal(4, scopes.Length);
        Assert.All(scopes, scope => Assert.Null(scope.Snapshot));
        Assert.Contains("COLOR EVIDENCE PENDING", color.ColorBasisLabel);
        var gpuScopes = Assert.Single(Descendants(review).OfType<GpuScopeAnalysisSurface>());
        Assert.True(gpuScopes.AnalysisEnabled);
        var viewer = Assert.IsType<MonitorView>(review.FindName("ColorProgramMonitor"));
        Assert.Same(monitoring.ProgramImage, viewer.Frame);
        Assert.Contains(Descendants(review).OfType<RtaimeMediaCompareView>(),
            compare => ReferenceEquals(compare.FrameA, monitoring.ProgramImage) &&
                ReferenceEquals(compare.FrameB, monitoring.PreviewImage));
        color.SelectScopeSourceCommand.Execute("PREVIEW");
        await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        Assert.Equal(rtaime.Media.Contracts.MonitoringStreamKind.Preview, monitoring.ActiveScopeStream);
        Assert.True(gpuScopes.AnalyzePreview);
        Assert.Null(color.SelectedScopes);
        Assert.Contains("NO CURRENT", color.ScopeFrameLabel);
        Assert.Equal("COMPOSITING", window.Shell.SelectedWorkspace);
        color.ShowGraphCommand.Execute(null);
        await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
        Assert.True(control.IsVisible);
        Assert.False(review.IsVisible);
        Assert.False(color.AnalysisEnabled);
        Assert.Equal(rtaime.Media.Contracts.MonitoringStreamKind.Program, monitoring.ActiveScopeStream);
        Assert.Equal(scopesBeforeReview, monitoring.ScopesEnabled);
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
            Assert.False(graph.ShowAdvancedGraph);
            Assert.Empty(graph.VisibleNodes);
            Assert.Contains("layers", graph.LayerStackOverview);
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
            Assert.InRange(graph.LayerPanelWidth, 300, 320);
            var viewport = Assert.IsType<Grid>(control.FindName("GraphViewport"));
            Assert.False(viewport.IsVisible);
            var savedSelection = graph.SelectedNode;
            graph.ToggleAdvancedGraphCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.True(graph.ShowAdvancedGraph);
            Assert.True(viewport.IsVisible);
            Assert.InRange(graph.LayerPanelWidth, 0, 220);
            Assert.Same(savedSelection, graph.SelectedNode);
            Assert.Same(graph.ApplyProcessingOrderDraftCommand,
                Assert.IsType<RtaimeButton>(control.FindName("ApplyFxOrderButton")).Command);
            Assert.Same(graph.DiscardProcessingOrderDraftCommand,
                Assert.IsType<RtaimeButton>(control.FindName("DiscardFxOrderButton")).Command);
            Assert.Contains(graph.Nodes, node => node.Projection.Kind == CompositingGraphNodeKind.Processing &&
                node.CapabilityLabel.Contains("STATIC FX", StringComparison.Ordinal));
            Assert.Contains("READ ONLY", graph.TopologyEvidence);
            graph.CollapseLayerCommand.Execute(graph.Layers[0]);
            Assert.True(graph.Layers[0].IsCollapsed);
            graph.Fit();
            Assert.InRange(graph.Zoom, 0.35, 2.5);
            OperatorControlPresentation.SetReducedMotion(control, true);
            OperatorControlPresentation.ApplyPreferenceSnapshot(control, false, true);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.Equal(SystemColors.WindowColor, Assert.IsType<SolidColorBrush>(viewport.Background).Color);
            graph.ToggleAdvancedGraphCommand.Execute(null);
            await dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            Assert.False(graph.ShowAdvancedGraph);
            Assert.False(viewport.IsVisible);
            Assert.True(graph.LayerPanelWidth > 220);
            Assert.Same(savedSelection, graph.SelectedNode);
            // Repeated opening must not retain realized offscreen graph elements.
            for (var iteration = 0; iteration < 12; iteration++)
            {
                graph.ShowAdvancedGraph = true;
                graph.ShowAdvancedGraph = false;
                Assert.Empty(graph.VisibleNodes);
                Assert.Empty(graph.VisibleConnections);
            }
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
