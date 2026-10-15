// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Operator;
using rtaime.Operator.Controls;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class InspectorDraftTransactionTests
{
    private static CompositingGraphNodeProjection Node(string id) => new(id, CompositingGraphNodeKind.Layer, id, "", "CONFIRMED", CompositingGraphHealth.Normal, []);
    private static string Token(Fixture f) => f.Client.Snapshot!.Production.ProductionId + ":" + f.Client.Snapshot.Production.Revision.Value;
    [Fact]
    public async Task Grouped_replay_independent_contexts_and_redo_invalidation_are_local_only()
    {
        await using var f = await Fixture.Create();
        var model = f.Model; var program = f.Owner.ProgramSourceId;
        model.BeginDraftTransaction(); model.SelectedLayerPositionX = 0.3; model.SelectedLayerPositionY = 0.4; model.EndDraftTransaction();
        Assert.Contains("UNDO 1", model.InspectorDraftHistoryLabel);
        model.UndoInspectorDraftCommand.Execute(null);
        Assert.Equal(0.1, model.SelectedLayerPositionX); Assert.Equal(0.2, model.SelectedLayerPositionY);
        model.RedoInspectorDraftCommand.Execute(null); Assert.Equal(0.3, model.SelectedLayerPositionX);
        model.UndoInspectorDraftCommand.Execute(null); model.SelectedLayerPositionX = 0.5;
        Assert.False(model.RedoInspectorDraftCommand.CanExecute(null));
        model.SelectCompositingNode(Node("layer:other")); model.SelectedLayerPositionX = 0.8;
        model.UndoInspectorDraftCommand.Execute(null); Assert.Equal(0.1, model.SelectedLayerPositionX);
        model.SelectCompositingNode(Node("layer:bitmap-graphics")); Assert.Equal(0.5, model.SelectedLayerPositionX);
        model.UndoInspectorDraftCommand.Execute(null); Assert.Equal(0.1, model.SelectedLayerPositionX);
        Assert.Equal(0, f.Transport.MutationCalls); Assert.Equal(program, f.Owner.ProgramSourceId);
    }
    [Fact]
    public async Task Cancelled_group_preserves_redo_and_bounds_history_and_context_retention()
    {
        await using var f = await Fixture.Create();
        var model = f.Model;
        model.SelectedLayerPositionX = 0.4; model.UndoInspectorDraftCommand.Execute(null);
        model.BeginDraftTransaction(); model.SelectedLayerPositionX = 0.7; model.CancelDraftTransaction();
        Assert.Equal(0.1, model.SelectedLayerPositionX); Assert.True(model.RedoInspectorDraftCommand.CanExecute(null));
        model.RedoInspectorDraftCommand.Execute(null); Assert.Equal(0.4, model.SelectedLayerPositionX);
        for (var i = 0; i < 60; i++) model.SelectedLayerPositionX = i / 100d;
        var undos = 0; while (model.UndoInspectorDraftCommand.CanExecute(null)) { model.UndoInspectorDraftCommand.Execute(null); undos++; }
        Assert.Equal(32, undos);
        f.Transport.ManyLayers = true; f.Transport.RevisionValue++; f.Owner.SynchronizeCommand.Execute(null);
        await Until(() => !f.Owner.IsBusy);
        foreach (var layer in f.Owner.CompositingLayers)
        {
            model.SelectCompositingNode(Node("layer:" + layer.LayerId)); model.SelectedLayerPositionX = 0.6;
            model.SelectCompositingNode(Node("processing:" + layer.LayerId + ":grade")); model.SelectedColorGradeBrightness = 0.2;
        }
        Assert.Equal(MediaPoolInspectorViewModel.MaximumInspectorContexts, model.InspectorContextCount);
        model.SelectCompositingNode(Node("layer:layer0"));
        Assert.Equal(0.1, model.SelectedLayerPositionX); Assert.False(model.UndoInspectorDraftCommand.CanExecute(null));
        Assert.Equal(0, f.Transport.MutationCalls);
    }
    [Fact]
    public async Task Authoritative_rebase_and_remote_cut_invalidate_history_and_clipboard_without_reversing_commit()
    {
        await using var f = await Fixture.Create();
        f.Model.SelectedLayerPositionX = 0.6; f.Model.CopyAttributesCommand.Execute(null);
        var program = f.Owner.ProgramSourceId;
        f.Transport.RemoteCut = true; f.Transport.RevisionValue++; f.Owner.SynchronizeCommand.Execute(null);
        await Until(() => !f.Owner.IsBusy);
        Assert.NotEqual(program, f.Owner.ProgramSourceId); var committedProgram = f.Owner.ProgramSourceId;
        Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null)); Assert.False(f.Model.RedoInspectorDraftCommand.CanExecute(null));
        Assert.False(f.Model.PasteAttributesCommand.CanExecute(null)); Assert.Contains("STALE CLIPBOARD", f.Model.AttributeCompatibilityLabel);
        f.Model.UndoInspectorDraftCommand.Execute(null); f.Model.PasteAttributesCommand.Execute(null);
        Assert.Equal(committedProgram, f.Owner.ProgramSourceId); Assert.Equal(0.1, f.Model.SelectedLayerPositionX);
        Assert.Equal(0, f.Transport.MutationCalls);
    }
    [Fact]
    public async Task Typed_payloads_validate_atomically_before_changing_any_value_or_history()
    {
        await using var f = await Fixture.Create();
        var good = new InspectorTransformAttributes(0.6, 0.5, 1, 0, 0, 0, 0, 0, 0, 0);
        var token = Token(f); var initial = f.Model.InspectorDraftHistoryLabel;
        foreach (var bad in new[]
        {
            new InspectorAttributePayload(2, token, good), new InspectorAttributePayload(1, "stale", good),
            new InspectorAttributePayload(1, token, new InspectorGainAttributes(2)),
            new InspectorAttributePayload(1, token, good with { X = double.NaN }),
            new InspectorAttributePayload(1, token, good with { Left = 0.8, Right = 0.8 })
        })
        {
            Assert.False(f.Model.TryPasteAttributeSet(bad)); Assert.Equal(0.1, f.Model.SelectedLayerPositionX);
            Assert.Equal(initial, f.Model.InspectorDraftHistoryLabel); Assert.Contains("Paste rejected", f.Model.AttributeCompatibilityLabel);
        }
        Assert.True(f.Model.TryPasteAttributeSet(new(1, token, good))); Assert.Equal(0.6, f.Model.SelectedLayerPositionX);
        Assert.Contains("UNDO 1", f.Model.InspectorDraftHistoryLabel);
        f.Model.UndoInspectorDraftCommand.Execute(null); Assert.Equal(0.1, f.Model.SelectedLayerPositionX);
        Assert.Equal(0, f.Transport.MutationCalls);
    }
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(4.01)]
    public void Invalid_gain_and_other_typed_sets_are_not_transferable(double value)
    {
        Assert.False(new InspectorGainAttributes(value).IsValid);
        Assert.False(new InspectorKeyAttributes(true, 1.5, 255, 0, 0.1, 0.1, 0).IsValid);
        Assert.False(new InspectorGradeAttributes(true, 0, 3, 1).IsValid);
        Assert.False(new InspectorCueAttributes(new string('x', 65)).IsValid);
        Assert.False(new InspectorCueAttributes("bad\nname").IsValid);
        Assert.True(new InspectorCueAttributes(new string('x', 64)).IsValid);
    }
    [Fact]
    public async Task Audio_gain_transfer_is_draft_only_and_explicit_apply_cannot_be_undone()
    {
        await using var f = await Fixture.Create();
        void Select(string id) => f.Model.UpdateSelection([f.Model.FilteredItems.Single(i => i.Key == "audio:" + id + ":audio-stream")]);
        Select(f.Transport.SourceId); f.Model.AudioGainDraft = 2; f.Model.CopyAttributesCommand.Execute(null);
        Assert.Equal(1, f.Owner.SelectedAudioInput!.ConfirmedGain); Assert.Equal(1, f.Owner.SelectedAudioInput.Gain);
        Select(f.Transport.OtherSource.ToString()); Assert.Equal(1, f.Model.AudioGainDraft);
        f.Model.PasteAttributesCommand.Execute(null); Assert.Equal(2, f.Model.AudioGainDraft);
        Assert.Equal(0, f.Transport.MutationCalls); Assert.Equal(1, f.Owner.SelectedAudioInput!.ConfirmedGain);
        f.Model.UndoInspectorDraftCommand.Execute(null); Assert.Equal(1, f.Model.AudioGainDraft);
        f.Model.RedoInspectorDraftCommand.Execute(null);
        Assert.True(f.Model.ApplyAudioDraftCommand.CanExecute(null)); f.Model.ApplyAudioDraftCommand.Execute(null);
        await Until(() => !f.Owner.IsBusy);
        Assert.Equal(1, f.Transport.MutationCalls); Assert.Equal(2, f.Owner.SelectedAudioInput!.ConfirmedGain);
        Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
    }
    [Fact]
    public async Task Cue_name_transfer_uses_stable_identity_requires_rename_and_preserves_rejected_draft()
    {
        await using var f = await Fixture.Create();
        f.Deck.ApplyConfirmedSnapshot(f.Transport.MediaSnapshot());
        void Select(int index)
        {
            var cue = f.Deck.Cues[index]; f.Deck.SelectedCue = cue;
            f.Model.SelectTimelineCue(new(cue.Id, cue.Name, cue.Frame, cue.Timecode, TimelineCueType.Media, null));
        }
        Select(0); var identity = f.Model.EditingSelectionIdentity;
        f.Model.CueNameDraft = "Copied name"; f.Model.CopyAttributesCommand.Execute(null);
        Select(1); Assert.NotEqual(identity, f.Model.EditingSelectionIdentity);
        f.Model.PasteAttributesCommand.Execute(null); Assert.Equal("Copied name", f.Model.CueNameDraft);
        Assert.Equal("Same name", f.Deck.SelectedCue!.Name); Assert.Equal(0, f.Transport.MutationCalls);
        f.Model.CueNameDraft = ""; Assert.False(f.Model.ApplyCueDraftCommand.CanExecute(null));
        f.Model.UndoInspectorDraftCommand.Execute(null); Assert.Equal("Copied name", f.Model.CueNameDraft);
        f.Transport.Reject = true; f.Model.ApplyCueDraftCommand.Execute(null);
        await Until(() => !f.Deck.IsBusy);
        Assert.Equal("Same name", f.Deck.Cues[1].Name); Assert.Equal("Copied name", f.Model.CueNameDraft);
        Assert.Contains("REJECTED", f.Model.InspectorEditState);
        f.Transport.Reject = false; f.Model.ApplyCueDraftCommand.Execute(null); await Until(() => !f.Deck.IsBusy);
        Assert.Equal("Copied name", f.Deck.Cues[1].Name); Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
    }
    [Fact]
    public async Task Stale_invalid_multi_selection_and_pending_confirmation_block_history_and_paste()
    {
        await using var f = await Fixture.Create();
        f.Model.SelectedLayerPositionX = 0.6; f.Model.CopyAttributesCommand.Execute(null);
        f.Model.InspectorInputsValid = false; Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null)); Assert.False(f.Model.PasteAttributesCommand.CanExecute(null));
        f.Model.InspectorInputsValid = true;
        f.Transport.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously); f.Model.ApplyCompositingTransformCommand.Execute(null);
        Assert.True(f.Owner.IsBusy); Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null)); Assert.False(f.Model.PasteAttributesCommand.CanExecute(null));
        f.Transport.Hold.SetResult(); await Until(() => !f.Owner.IsBusy);
        Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
        f.Model.SelectedLayerPositionX = 0.3; f.Transport.FailSnapshots = true; f.Owner.SynchronizeCommand.Execute(null);
        await Until(() => !f.Owner.IsBusy); Assert.True(f.Owner.IsStale); Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
        Assert.Equal(1, f.Transport.MutationCalls);
    }
    internal static async Task VerifyWindowAsync(MainWindow main, Dispatcher dispatcher)
    {
        main.Shell.SelectWorkspace("MEDIA");
        await dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.Loaded);
        var control = Descendants(main).OfType<OperatorInspectorControl>().First(c => c.IsVisible);
        var original = control.Inspector;
        await using var f = await Fixture.Create();
        var surface = (Grid)((Border)control.Content).Child;
        var field = new RtaimeNumericField { Definition = InspectorProperties.LayerPositionX, Width = 180, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        field.SetBinding(RtaimeNumericField.ValueProperty, new Binding(nameof(MediaPoolInspectorViewModel.SelectedLayerPositionX))
            { Source = f.Model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Panel.SetZIndex(field, 100); control.Inspector = f.Model; surface.Children.Add(field);
        try
        {
            await dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.Loaded);
            Assert.True(field.Focus());
            var up = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field)!, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            field.RaiseEvent(up); field.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field)!, 0, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            var undo = Descendants(control).OfType<RtaimeButton>().Single(b => AutomationProperties.GetName(b) == "Undo selected Inspector draft");
            Assert.True(undo.Command!.CanExecute(null)); undo.Focus(); undo.Command.Execute(null);
            Assert.Equal(0.1, f.Model.SelectedLayerPositionX); Assert.True(f.Model.RedoInspectorDraftCommand.CanExecute(null));
            Assert.Equal(0, f.Transport.MutationCalls);
            Console.WriteLine("INSPECTOR DRAFT PASS: productive themed controls, focused numeric grouping, accessible undo/redo, unchanged production transport.");
        }
        finally { surface.Children.Remove(field); control.Inspector = original; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is null) continue; yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5); while (!condition() && DateTime.UtcNow < end) await Task.Delay(10); Assert.True(condition());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public InspectorPrecisionTests.InspectorTransport Transport { get; } = new();
        public OperatorControlClient Client { get; }
        public OperatorViewModel Owner { get; }
        public MediaDeckViewModel Deck { get; }
        public MediaPoolInspectorViewModel Model { get; }
        private Fixture()
        {
            Client = new(Transport); Owner = new(Client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
            Deck = new(new MediaDeckController(Client), () => null, () => null); Model = new(Owner, Deck);
        }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture(); f.Owner.SynchronizeCommand.Execute(null); await Until(() => f.Owner.IsConnected && !f.Owner.IsBusy);
            f.Model.SelectCompositingNode(Node("layer:bitmap-graphics")); return f;
        }
        public async ValueTask DisposeAsync() { Model.Dispose(); await Deck.DisposeAsync(); await Owner.DisposeAsync(); }
    }
}
