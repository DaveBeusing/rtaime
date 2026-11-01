// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Xml.Linq;
using rtaime.Client;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class CompositingContextMenuTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Empty_graph_still_has_compact_keyboard_accessible_navigation()
    {
        var xaml = Markup();
        var toolbar = Assert.Single(xaml.Descendants().Where(element =>
            element.Name.LocalName == "Border" &&
            element.Attribute("Grid.Row")?.Value == "0" &&
            element.Descendants().Any(button => Name(button) == "GraphActionsButton")));
        var buttons = toolbar.Descendants().Where(element => element.Name.LocalName is "RtaimeButton" or "RtaimeToggleButton").ToArray();
        Assert.Contains(buttons, element => element.Attribute("Command")?.Value == "{Binding ToggleLayerListCommand}");
        Assert.Contains(buttons, element => element.Attribute("IsChecked")?.Value.Contains("ShowAdvancedGraph", StringComparison.Ordinal) == true);
        Assert.Contains(buttons, element => element.Attribute("Command")?.Value == "{Binding SelectModeCommand}");
        Assert.Contains(buttons, element => element.Attribute("Command")?.Value == "{Binding PanModeCommand}");
        Assert.Contains(buttons, element => element.Attribute("Command")?.Value == "{Binding FitCommand}");
        Assert.DoesNotContain(toolbar.Descendants(), element => element.Name.LocalName == "RtaimeScrollViewer");
        var more = Assert.Single(buttons.Where(button => Name(button) == "GraphActionsButton"));
        Assert.Equal("OnGraphActionsClick", more.Attribute("Click")?.Value);
        Assert.Equal("{StaticResource GraphActionsMenu}", more.Attribute("ContextMenu")?.Value);
        Assert.NotNull(more.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void Shared_selected_object_menu_routes_only_to_existing_graph_commands()
    {
        var xaml = Markup();
        var menu = Assert.Single(xaml.Descendants().Where(element =>
            element.Name.LocalName == "RtaimeContextMenu" &&
            element.Attribute(Xaml + "Key")?.Value == "GraphActionsMenu"));
        Assert.Equal("False", menu.Attribute(Xaml + "Shared")?.Value);
        Assert.Contains("PlacementTarget.Tag", menu.Attribute("DataContext")?.Value);
        var commands = menu.Descendants().Where(element => element.Name.LocalName == "RtaimeMenuItem")
            .Select(element => element.Attribute("Command")?.Value)
            .Where(value => value is not null).ToArray();

        foreach (var name in new[]
        {
            "ResetViewCommand", "AutoLayoutCommand", "MoveSelectedLayerUpCommand",
            "MoveSelectedLayerDownCommand", "ToggleSelectedLayerCommand",
            "DecreaseSelectedLayerOpacityCommand", "IncreaseSelectedLayerOpacityCommand",
            "MoveProcessingEarlierDraftCommand", "MoveProcessingLaterDraftCommand",
            "ApplyProcessingOrderDraftCommand", "DiscardProcessingOrderDraftCommand",
            "ApplyLayerDraftCommand", "ReviewLayerDraftCommand", "DiscardLayerDraftCommand",
            "ApplyOrderDraftCommand", "DiscardOrderDraftCommand"
        })
            Assert.Contains("{Binding " + name + "}", commands);

        Assert.All(menu.Descendants().Where(element => element.Name.LocalName == "RtaimeMenuItem" &&
            element.Attribute("Command") is not null), element =>
            Assert.NotNull(element.Attribute("AutomationProperties.Name")));
        Assert.DoesNotContain(menu.Descendants(), element =>
            element.Attribute("Click") is not null ||
            element.Attribute("MouseDown") is not null);
    }

    [Fact]
    public void Layer_node_and_blank_canvas_use_one_context_menu_and_one_selection_path()
    {
        var xaml = Markup();
        var layerStyle = Assert.Single(xaml.Descendants().Where(element =>
            element.Name.LocalName == "Style" &&
            element.Attribute("TargetType")?.Value.Contains("RtaimeListBoxItem", StringComparison.Ordinal) == true));
        Assert.Contains(layerStyle.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            element.Attribute("Property")?.Value == "ContextMenu" &&
            element.Attribute("Value")?.Value == "{StaticResource GraphActionsMenu}");
        Assert.Contains(layerStyle.Descendants(), element =>
            element.Name.LocalName == "EventSetter" &&
            element.Attribute("Handler")?.Value == "OnSelectedObjectContextOpening");

        var node = Assert.Single(xaml.Descendants().Where(element =>
            element.Name.LocalName == "RtaimeButton" &&
            element.Attribute("PreviewMouseLeftButtonDown")?.Value == "OnNodeSelectionModifiers"));
        Assert.Equal("OnSelectedObjectContextOpening", node.Attribute("ContextMenuOpening")?.Value);
        Assert.Equal("{StaticResource GraphActionsMenu}", node.Attribute("ContextMenu")?.Value);
        var viewport = Assert.Single(xaml.Descendants().Where(element => Name(element) == "GraphViewport"));
        Assert.Equal("{StaticResource GraphActionsMenu}", viewport.Attribute("ContextMenu")?.Value);

        var source = CodeBehind();
        Assert.Contains("model.SelectNodeCommand.Execute(node)", source, StringComparison.Ordinal);
        Assert.Contains("model.ExtendSelection = false", source, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.Apps", source, StringComparison.Ordinal);
        Assert.Contains("e.Key == Key.F10", source, StringComparison.Ordinal);
        Assert.Contains("e.OriginalSource is TextBox", source, StringComparison.Ordinal);
        Assert.Contains("menu.IsOpen = true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetCompositingLayerStateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetCompositingLayerProcessingStackAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Dirty_order_fx_and_layer_drafts_have_visible_recovery_outside_the_menu()
    {
        var xaml = Markup();
        var footer = Assert.Single(xaml.Descendants().Where(element => Name(element) == "GraphDraftFooter"));
        Assert.Equal("2", footer.Attribute("Grid.Row")?.Value);
        Assert.Contains(footer.Descendants(), element =>
            element.Attribute("Binding")?.Value.Contains("HasUnresolvedLayerDrafts", StringComparison.Ordinal) == true);
        Assert.Contains(footer.Descendants(), element =>
            element.Attribute("Binding")?.Value.Contains("HasFxOrderDraft", StringComparison.Ordinal) == true);
        Assert.Contains(footer.Descendants(), element =>
            element.Attribute("Binding")?.Value.Contains("OrderEvidence", StringComparison.Ordinal) == true);
        Assert.Contains(footer.Descendants(), element =>
            element.Attribute("Text")?.Value.Contains("LayerCommandStatus", StringComparison.Ordinal) == true);
        Assert.Contains(footer.Descendants(), element =>
            element.Attribute("Text")?.Value.Contains("DraftEvidence", StringComparison.Ordinal) == true);
        Assert.Contains(footer.Descendants(), element =>
            element.Attribute("Text")?.Value.Contains("FxOrderEvidence", StringComparison.Ordinal) == true);
        Assert.Contains(footer.Descendants(), element =>
            element.Attribute("Text")?.Value.Contains("OrderEvidence", StringComparison.Ordinal) == true);

        foreach (var command in new[] { "ApplyLayerDraftCommand", "DiscardLayerDraftCommand",
            "ReviewLayerDraftCommand", "ApplyOrderDraftCommand", "DiscardOrderDraftCommand",
            "ApplyProcessingOrderDraftCommand", "DiscardProcessingOrderDraftCommand" })
            Assert.Contains(footer.Descendants(), element =>
                element.Name.LocalName == "RtaimeButton" &&
                element.Attribute("Command")?.Value == "{Binding " + command + "}");

        var fxApply = Assert.Single(footer.Descendants().Where(element => Name(element) == "ApplyFxOrderButton"));
        var fxDiscard = Assert.Single(footer.Descendants().Where(element => Name(element) == "DiscardFxOrderButton"));
        Assert.Equal("{Binding ApplyProcessingOrderDraftCommand}", fxApply.Attribute("Command")?.Value);
        Assert.Equal("{Binding DiscardProcessingOrderDraftCommand}", fxDiscard.Attribute("Command")?.Value);
        var source = CodeBehind();
        Assert.Contains("row.HasStateDraft", source, StringComparison.Ordinal);
        Assert.Contains("model.Layers.CollectionChanged += OnDraftLayersChanged", source, StringComparison.Ordinal);
        Assert.Contains("row.PropertyChanged += OnDraftRowChanged", source, StringComparison.Ordinal);
        Assert.Contains("StopObservingLayerDrafts();", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Layer_selection_changes_do_not_discard_dirty_draft_or_change_program()
    {
        var transport = new InspectorPrecisionTests.InspectorTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        var layer = graph.Layers.Single(row => row.Id == "layer:bitmap-graphics");
        graph.SelectNodeCommand.Execute(layer);
        var originalProgram = owner.ProgramSourceId;
        graph.ToggleSelectedLayerCommand.Execute(null);
        Assert.True(layer.HasStateDraft);
        Assert.True(graph.Layers.Any(row => row.HasStateDraft));
        graph.SelectNodeCommand.Execute(graph.Layers.Single(row => row.Id == "layer:other"));
        Assert.True(layer.HasStateDraft);
        Assert.Equal(originalProgram, owner.ProgramSourceId);
        Assert.Equal(0, transport.MutationCalls);
        graph.SelectNodeCommand.Execute(layer);
        graph.DiscardLayerDraftCommand.Execute(null);
        Assert.False(graph.Layers.Any(row => row.HasStateDraft));
        Assert.Equal(0, transport.MutationCalls);
    }

    [Fact]
    public async Task Revisions_fence_dirty_fx_and_layer_order_until_explicit_discard()
    {
        var transport = new InspectorPrecisionTests.InspectorTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        using var graph = new CompositingGraphViewModel(owner, inspector);
        var program = owner.ProgramSourceId;
        graph.StageLayerOrder("other", "bitmap-graphics");
        graph.ShowAdvancedGraph = true;
        graph.SelectNodeCommand.Execute(graph.Nodes.Single(row => row.Id == "processing:bitmap-graphics:grade"));
        graph.MoveProcessingLaterDraftCommand.Execute(null);
        Assert.True(graph.HasFxOrderDraft);
        Assert.Contains("LOCAL ORDER DRAFT", graph.OrderEvidence, StringComparison.Ordinal);
        Assert.Equal(0, transport.MutationCalls);

        transport.RevisionValue++;
        owner.SynchronizeCommand.Execute(null);
        Assert.Contains("STALE FX ORDER DRAFT", graph.FxOrderEvidence, StringComparison.Ordinal);
        Assert.Contains("STALE ORDER DRAFT", graph.OrderEvidence, StringComparison.Ordinal);
        Assert.False(graph.ApplyProcessingOrderDraftCommand.CanExecute(null));
        Assert.False(graph.ApplyOrderDraftCommand.CanExecute(null));
        Assert.Equal(program, owner.ProgramSourceId);
        Assert.Equal(0, transport.MutationCalls);

        graph.DiscardProcessingOrderDraftCommand.Execute(null);
        graph.DiscardOrderDraftCommand.Execute(null);
        Assert.False(graph.HasFxOrderDraft);
        Assert.DoesNotContain("DRAFT", graph.OrderEvidence, StringComparison.Ordinal);
        Assert.Equal(0, transport.MutationCalls);
    }

    private static XDocument Markup() =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Hosts", "rtaime.Operator",
            "CompositingGraphControl.xaml"));

    private static string CodeBehind() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Hosts",
            "rtaime.Operator", "CompositingGraphControl.xaml.cs"));

    private static string? Name(XElement element) => element.Attribute(Xaml + "Name")?.Value;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root containing rtaime.slnx could not be located.");
    }
}
