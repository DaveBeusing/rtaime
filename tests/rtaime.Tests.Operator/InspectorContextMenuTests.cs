// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Xml.Linq;
using rtaime.Client;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class InspectorContextMenuTests
{
    [Fact]
    public void Selected_object_actions_use_a_keyboard_reachable_owned_menu_and_existing_commands()
    {
        var inspector = Load("src", "Hosts", "rtaime.Operator", "OperatorInspectorControl.xaml");
        var menu = Assert.Single(inspector.Descendants().Where(element =>
            element.Name.LocalName == "RtaimeContextMenu"));
        Assert.Equal("{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource Self}}",
            menu.Attribute("DataContext")?.Value);
        Assert.Equal("OnInspectorActionsMenuOpened", menu.Attribute("Opened")?.Value);
        Assert.Equal("OnInspectorActionsMenuClosed", menu.Attribute("Closed")?.Value);

        var button = Assert.Single(inspector.Descendants().Where(element =>
            element.Name.LocalName == "RtaimeIconButton" &&
            element.Attribute("Click")?.Value == "OnInspectorActionsClick"));
        Assert.Equal("Inspector actions", button.Attribute("AutomationProperties.Name")?.Value);
        Assert.Contains(button.Descendants(), element => ReferenceEquals(element, menu));
        Assert.Equal("{Binding CanEditSelection}", button.Attribute("IsEnabled")?.Value);

        var commands = menu.Descendants().Where(element => element.Name.LocalName == "RtaimeMenuItem")
            .Select(element => element.Attribute("Command")?.Value).Where(value => value is not null).ToArray();
        foreach (var command in new[]
        {
            "CopyAttributesCommand", "PasteAttributesCommand", "UndoInspectorDraftCommand",
            "RedoInspectorDraftCommand", "AddColorGradeProcessingNodeCommand",
            "AddChromaKeyProcessingNodeCommand", "MoveProcessingNodeEarlierCommand",
            "MoveProcessingNodeLaterCommand", "RemoveProcessingNodeCommand"
        })
        {
            Assert.Contains("{Binding " + command + "}", commands);
            Assert.Equal(1, inspector.Descendants().Count(element =>
                element.Attribute("Command")?.Value == "{Binding " + command + "}"));
        }

        var menuStyle = inspector.Descendants().Single(element =>
            element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value ==
            "InspectorProcessingMenuItem");
        Assert.Contains(menuStyle.Descendants(), element => element.Attribute("Binding")?.Value ==
            "{Binding HasAuthoritativeCompositingSelection}");
        var nodeStyle = inspector.Descendants().Single(element =>
            element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value ==
            "InspectorSelectedNodeMenuItem");
        Assert.Contains(nodeStyle.Descendants(), element => element.Attribute("Binding")?.Value ==
            "{Binding HasSelectedProcessingNode}");
        Assert.DoesNotContain(inspector.Descendants(), element => element.Name.LocalName == "RtaimeMenuItem" &&
            element.Attribute("IsEnabled")?.Value == "True");
    }

    [Fact]
    public void Draft_review_and_its_recovery_controls_remain_outside_the_overflow()
    {
        var inspector = Load("src", "Hosts", "rtaime.Operator", "OperatorInspectorControl.xaml");
        var template = inspector.Descendants().Single(element =>
            element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value ==
            "InspectorAttributeActionsTemplate");
        Assert.Contains(template.Descendants(), element => element.Name.LocalName == "InspectorDraftReviewControl");
        Assert.Contains(template.Descendants(), element => element.Attribute("Binding")?.Value == "{Binding HasDraftReviews}");
        Assert.Contains(template.Descendants(), element => element.Attribute("Binding")?.Value == "{Binding HasInspectorBatch}");
        Assert.Contains(template.Descendants(), element => element.Attribute("Text")?.Value == "{Binding InspectorEditState}");
        Assert.DoesNotContain(template.Descendants(), element => element.Name.LocalName == "RtaimeButton");

        var review = Load("src", "Hosts", "rtaime.Operator", "InspectorDraftReviewControl.xaml");
        foreach (var command in new[] { "ApplyDraftReviewCommand", "DiscardDraftReviewCommand",
            "KeepDraftReviewCommand", "RebaseDraftReviewCommand", "RestoreDraftReviewCommand" })
            Assert.Contains(review.Descendants(), element => element.Attribute("Command")?.Value ==
                "{Binding " + command + "}");

        var effect = inspector.Descendants().Single(element =>
            element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value ==
            "InspectorEffectTemplate");
        Assert.DoesNotContain(effect.Descendants(), element => element.Name.LocalName == "RtaimeIconButton" &&
            element.Attribute("IsEnabled")?.Value == "False");

        var source = Read("src", "Hosts", "rtaime.Operator", "OperatorInspectorControl.xaml.cs");
        Assert.Contains("if (_openActionsSelectionIdentity != _selectionIdentity) CloseInspectorActionsMenu();", source);
        Assert.Contains("CloseInspectorActionsMenu();", source);
        Assert.Contains("OperatorKeyboardCommandRegistry.IsTextEntryContext", source);
        Assert.Contains("Keyboard.Modifiers != ModifierKeys.Control", source);
    }

    [Fact]
    public async Task Secondary_actions_modify_local_drafts_only_and_context_expansion_survives_selection_changes()
    {
        var transport = new InspectorPrecisionTests.InspectorTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var inspector = new MediaPoolInspectorViewModel(owner, deck);
        owner.SynchronizeCommand.Execute(null);
        await Until(() => owner.IsConnected && !owner.IsBusy);

        inspector.SelectCompositingNode(Node("layer:bitmap-graphics"));
        var initialIdentity = inspector.EditingSelectionIdentity;
        var program = owner.ProgramSourceId;
        inspector.IsEffectsExpanded = false;
        inspector.SelectedLayerPositionX = 0.62;
        Assert.True(inspector.CopyAttributesCommand.CanExecute(null));
        inspector.CopyAttributesCommand.Execute(null);
        inspector.SelectedLayerPositionX = 0.3;
        Assert.True(inspector.PasteAttributesCommand.CanExecute(null));
        inspector.PasteAttributesCommand.Execute(null);
        Assert.Equal(0.62, inspector.SelectedLayerPositionX);
        inspector.UndoInspectorDraftCommand.Execute(null);
        Assert.Equal(0.3, inspector.SelectedLayerPositionX);
        inspector.RedoInspectorDraftCommand.Execute(null);
        Assert.Equal(0.62, inspector.SelectedLayerPositionX);

        inspector.SelectCompositingNode(Node("processing:bitmap-graphics:grade"));
        Assert.NotEqual(initialIdentity, inspector.EditingSelectionIdentity);
        Assert.False(inspector.PasteAttributesCommand.CanExecute(null));
        inspector.SelectCompositingNode(Node("layer:other"));
        inspector.IsEffectsExpanded = true;
        inspector.SelectCompositingNode(Node("layer:bitmap-graphics"));
        Assert.False(inspector.IsEffectsExpanded);
        Assert.Equal(0.62, inspector.SelectedLayerPositionX);
        Assert.Equal(0, transport.MutationCalls);
        Assert.Equal(program, owner.ProgramSourceId);
    }

    private static CompositingGraphNodeProjection Node(string id) =>
        new(id, CompositingGraphNodeKind.Layer, id, "", "CONFIRMED", CompositingGraphHealth.Normal, []);

    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(predicate());
    }

    private static XDocument Load(params string[] path) => XDocument.Load(Resolve(path));
    private static string Read(params string[] path) => File.ReadAllText(Resolve(path));
    private static string Resolve(string[] path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
                return Path.Combine(new[] { directory.FullName }.Concat(path).ToArray());
        throw new InvalidOperationException("Repository root containing rtaime.slnx could not be located.");
    }
}
