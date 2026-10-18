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
        model.SelectCompositingNode(Node("transform:bitmap-graphics")); Assert.Equal(0.5, model.SelectedLayerPositionX);
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
        f.Model.SelectedLayerCropLeft = 0.8; f.Model.SelectedLayerCropRight = 0.8;
        Assert.False(f.Model.ApplyCompositingTransformCommand.CanExecute(null));
        f.Model.ApplyCompositingTransformCommand.Execute(null); Assert.Equal(0, f.Transport.MutationCalls);
    }
    [Fact]
    public async Task Grade_and_key_sets_paste_and_replay_as_single_compatible_transactions()
    {
        await using var f = await Fixture.Create();
        f.Model.SelectCompositingNode(Node("processing:bitmap-graphics:grade"));
        Assert.False(f.Model.IsTransformDraftContext); Assert.False(f.Model.ApplyCompositingTransformCommand.CanExecute(null));
        Assert.True(f.Model.TryPasteAttributeSet(new(1, Token(f), new InspectorGradeAttributes(false, 0.4, 1.2, 0.8))));
        Assert.Equal(0.4, f.Model.SelectedColorGradeBrightness); Assert.False(f.Model.SelectedColorGradeEnabled);
        f.Model.UndoInspectorDraftCommand.Execute(null);
        Assert.Equal(0, f.Model.SelectedColorGradeBrightness); Assert.True(f.Model.SelectedColorGradeEnabled);
        f.Model.SelectCompositingNode(Node("processing:bitmap-graphics:key"));
        Assert.True(f.Model.TryPasteAttributeSet(new(1, Token(f), new InspectorKeyAttributes(false, 12, 255, 23, 0.4, 0.2, 0.3))));
        Assert.Equal(12, f.Model.SelectedChromaKeyRed); Assert.Equal(0.4, f.Model.SelectedChromaKeyTolerance);
        f.Model.UndoInspectorDraftCommand.Execute(null); Assert.Equal(0, f.Model.SelectedChromaKeyRed);
        Assert.Equal(0.1, f.Model.SelectedChromaKeyTolerance); Assert.True(f.Model.SelectedChromaKeyEnabled);
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
        f.Model.ResetAudioGainCommand.Execute(null); Assert.Equal(1, f.Model.AudioGainDraft);
        Assert.Equal(1, f.Owner.SelectedAudioInput!.Gain); Assert.Equal(1, f.Owner.SelectedAudioInput.ConfirmedGain);
        Assert.Equal(0, f.Transport.MutationCalls);
        f.Model.UndoInspectorDraftCommand.Execute(null); Assert.Equal(2, f.Model.AudioGainDraft);
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
        f.Model.CueNameDraft = "Unsaved"; Assert.True(f.Model.UndoInspectorDraftCommand.CanExecute(null));
        f.Transport.OmitSecondCue = true; f.Deck.ApplyConfirmedSnapshot(f.Transport.MediaSnapshot());
        Assert.False(f.Model.CopyAttributesCommand.CanExecute(null)); Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
        f.Transport.OmitSecondCue = false; f.Deck.ApplyConfirmedSnapshot(f.Transport.MediaSnapshot());
        Assert.Equal("Copied name", f.Model.CueNameDraft); Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
    }
    [Fact]
    public async Task Stale_invalid_multi_selection_and_pending_confirmation_block_history_and_paste()
    {
        await using var f = await Fixture.Create();
        f.Model.SelectedLayerPositionX = 0.6; f.Model.CopyAttributesCommand.Execute(null);
        f.Model.UpdateSelection(f.Model.FilteredItems.Where(i => i.Kind == MediaPoolItemKind.Audio).ToArray());
        Assert.True(f.Model.HasMultipleSelection); Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
        Assert.False(f.Model.PasteAttributesCommand.CanExecute(null));
        f.Model.SelectCompositingNode(Node("layer:bitmap-graphics"));
        Assert.Equal(0.6, f.Model.SelectedLayerPositionX);
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

    [Fact]
    public async Task Dirty_selection_retains_audio_intent_and_applies_original_identity_without_changing_library_selection()
    {
        await using var f = await Fixture.Create();
        var audio = f.Model.FilteredItems.Where(item => item.Kind == MediaPoolItemKind.Audio).ToArray();
        f.Model.Selection.Select([audio[0]]);
        f.Model.AudioGainDraft = 2;
        f.Model.Selection.Select([audio[1]]);
        var identity = f.Model.EditingSelectionIdentity;
        var review = Assert.Single(f.Model.InspectorDraftReviews, row => row.Target.Kind == "gain");
        Assert.Contains("CONFIRMED", review.Summary); Assert.Contains("LOCAL", review.Summary);
        f.Model.SelectedDraftReview = review;
        f.Model.KeepDraftReviewCommand.Execute(null);
        Assert.Equal(identity, f.Model.EditingSelectionIdentity);
        Assert.True(f.Model.ApplyDraftReviewCommand.CanExecute(null));
        f.Model.ApplyDraftReviewCommand.Execute(null);
        await Until(() => f.Transport.MutationCalls == 1 && !f.Owner.IsBusy);
        Assert.Equal(2, f.Transport.Gains[review.Target.Id]);
        Assert.Equal(identity, f.Model.EditingSelectionIdentity);
        Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
    }

    [Fact]
    public async Task Retained_cue_review_renames_only_original_marker_after_selection_changes()
    {
        await using var f = await Fixture.Create();
        f.Deck.ApplyConfirmedSnapshot(f.Transport.MediaSnapshot());
        var cue = f.Deck.Cues[0]; f.Deck.SelectedCue = cue;
        f.Model.SelectTimelineCue(new(cue.Id, cue.Name, cue.Frame, cue.Timecode, TimelineCueType.Media, null));
        f.Model.CueNameDraft = "Reviewed cue";
        var audio = f.Model.FilteredItems.First(item => item.Kind == MediaPoolItemKind.Audio);
        f.Model.Selection.Select([audio]);
        f.Model.SelectedDraftReview = Assert.Single(f.Model.InspectorDraftReviews, row => row.Target.Kind == "cue");
        var identity = f.Model.EditingSelectionIdentity;
        Assert.True(f.Model.ApplyDraftReviewCommand.CanExecute(null));
        f.Model.ApplyDraftReviewCommand.Execute(null);
        await Until(() => f.Transport.MutationCalls == 1 && !f.Owner.IsBusy);
        Assert.Equal("Reviewed cue", f.Deck.Cues[0].Name);
        Assert.Equal("Same name", f.Deck.Cues[1].Name);
        Assert.Equal(identity, f.Model.EditingSelectionIdentity);
    }
    [Fact]
    public async Task Revision_conflict_keeps_values_and_requires_explicit_rebase_before_restore_or_apply()
    {
        await using var f = await Fixture.Create();
        f.Model.SelectedLayerPositionX = 0.73;
        f.Model.SelectCompositingNode(Node("layer:other"));
        f.Model.SelectedDraftReview = Assert.Single(f.Model.InspectorDraftReviews, row => row.Target.Id == "bitmap-graphics");
        f.Transport.RevisionValue++; f.Transport.RemoteCut = true;
        f.Owner.SynchronizeCommand.Execute(null);
        await Until(() => !f.Owner.IsBusy);
        Assert.False(f.Model.ApplyDraftReviewCommand.CanExecute(null));
        Assert.Contains("REBASE REQUIRED", f.Model.DraftReviewGate);
        Assert.Equal(0.73, Assert.IsType<InspectorTransformAttributes>(f.Model.SelectedDraftReview!.Draft).X);
        Assert.Equal(0, f.Transport.MutationCalls);
        f.Model.RebaseDraftReviewCommand.Execute(null);
        Assert.True(f.Model.ApplyDraftReviewCommand.CanExecute(null));
        f.Model.SelectCompositingNode(Node("transform:bitmap-graphics"));
        f.Model.RestoreDraftReviewCommand.Execute(null);
        Assert.Equal(0.73, f.Model.SelectedLayerPositionX);
        f.Model.UndoInspectorDraftCommand.Execute(null);
        Assert.Equal(0.1, f.Model.SelectedLayerPositionX);
        Assert.Equal(0, f.Transport.MutationCalls);
    }
    [Fact]
    public async Task Sequential_audio_batch_reports_partial_failure_and_explicit_retry_skips_confirmed_rows()
    {
        await using var f = await Fixture.Create();
        var audio = f.Model.FilteredItems.Where(item => item.Kind == MediaPoolItemKind.Audio).ToArray();
        f.Transport.Gains[audio[1].ReferenceId!] = 0.5;
        f.Owner.SynchronizeCommand.Execute(null);
        audio = f.Model.FilteredItems.Where(item => item.Kind == MediaPoolItemKind.Audio).ToArray();
        f.Model.Selection.Select([audio[0]]); f.Model.AudioGainDraft = 2; f.Model.CopyAttributesCommand.Execute(null);
        f.Model.Selection.Select(audio);
        Assert.Contains("MIXED", f.Model.InspectorBatchSelectionEvidence);
        f.Model.ReviewInspectorBatchCommand.Execute(null);
        Assert.Equal(2, f.Model.InspectorBatchItems.Count);
        Assert.Equal(0, f.Transport.MutationCalls);
        f.Transport.RejectMutationCall = 2;
        await f.Model.ApplyInspectorBatchAsync();
        Assert.Equal("CONFIRMED", f.Model.InspectorBatchItems[0].Result);
        Assert.Contains("REJECTED", f.Model.InspectorBatchItems[1].Result);
        Assert.False(f.Model.ApplyInspectorBatchCommand.CanExecute(null));
        Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
        f.Transport.RejectMutationCall = 0;
        f.Model.ReviewRemainingInspectorBatchCommand.Execute(null);
        await f.Model.ApplyInspectorBatchAsync();
        Assert.All(f.Model.InspectorBatchItems, row => Assert.Equal("CONFIRMED", row.Result));
        Assert.Equal(3, f.Transport.MutationCalls);
        Assert.All(f.Model.InspectorBatchItems, row => Assert.Equal(2, f.Transport.Gains[row.Target.Id]));
    }
    [Fact]
    public async Task Batch_stop_and_revision_between_review_and_apply_never_send_later_targets_without_review()
    {
        await using var f = await Fixture.Create();
        var audio = f.Model.FilteredItems.Where(item => item.Kind == MediaPoolItemKind.Audio).ToArray();
        f.Model.Selection.Select([audio[0]]); f.Model.AudioGainDraft = 2; f.Model.CopyAttributesCommand.Execute(null);
        f.Model.Selection.Select(audio); f.Model.ReviewInspectorBatchCommand.Execute(null);
        f.Transport.RevisionValue++;
        f.Owner.SynchronizeCommand.Execute(null);
        Assert.False(f.Model.ApplyInspectorBatchCommand.CanExecute(null));
        await f.Model.ApplyInspectorBatchAsync(); Assert.Equal(0, f.Transport.MutationCalls);
        f.Model.ReviewRemainingInspectorBatchCommand.Execute(null);
        f.Transport.OnMutation = _ => f.Model.StopInspectorBatchCommand.Execute(null);
        await f.Model.ApplyInspectorBatchAsync();
        Assert.Equal(1, f.Transport.MutationCalls);
        Assert.Equal("CONFIRMED", f.Model.InspectorBatchItems[0].Result);
        Assert.Contains("NOT ATTEMPTED", f.Model.InspectorBatchItems[1].Result);
        Assert.Contains("NOT ATOMIC", f.Model.InspectorBatchEvidence);
    }
    [Fact]
    public async Task Inspector_disposal_finishes_current_request_and_stops_later_batch_mutations()
    {
        await using var f = await Fixture.Create();
        var audio = f.Model.FilteredItems.Where(item => item.Kind == MediaPoolItemKind.Audio).ToArray();
        f.Model.Selection.Select([audio[0]]); f.Model.AudioGainDraft = 2; f.Model.CopyAttributesCommand.Execute(null);
        f.Model.Selection.Select(audio); f.Model.ReviewInspectorBatchCommand.Execute(null);
        f.Transport.OnMutation = _ => f.Model.Dispose();
        await f.Model.ApplyInspectorBatchAsync();
        Assert.Equal(1, f.Transport.MutationCalls);
        Assert.Equal("CONFIRMED", f.Model.InspectorBatchItems[0].Result);
        Assert.Contains("NOT ATTEMPTED", f.Model.InspectorBatchItems[1].Result);
        Assert.False(f.Model.ApplyInspectorBatchCommand.CanExecute(null));
        Assert.False(f.Model.UndoInspectorDraftCommand.CanExecute(null));
    }
    [Fact]
    public async Task Mixed_incompatible_empty_and_removed_targets_remain_read_only_with_explicit_reasons()
    {
        await using var f = await Fixture.Create();
        f.Model.AudioGainDraft = 2; // no gain context: cannot copy a mismatched set
        f.Model.CopyAttributesCommand.Execute(null); // transform set
        var audio = f.Model.FilteredItems.Where(item => item.Kind == MediaPoolItemKind.Audio).ToArray();
        f.Model.Selection.Select(audio); f.Model.ReviewInspectorBatchCommand.Execute(null);
        Assert.All(f.Model.InspectorBatchItems, row => Assert.Contains("EXCLUDED", row.Result));
        Assert.False(f.Model.ApplyInspectorBatchCommand.CanExecute(null));
        f.Model.Selection.Select([]);
        Assert.False(f.Model.ReviewInspectorBatchCommand.CanExecute(null));
        f.Model.ReviewCompositingLayers(f.Owner.CompositingLayers);
        f.Model.ReviewInspectorBatchCommand.Execute(null);
        f.Transport.OmitBitmap = true; f.Owner.SynchronizeCommand.Execute(null);
        Assert.False(f.Model.ApplyInspectorBatchCommand.CanExecute(null));
        Assert.Equal(0, f.Transport.MutationCalls);
    }
    [Fact]
    public async Task Hundred_local_undo_redo_selection_cycles_bound_reviews_and_do_not_mutate_production()
    {
        await using var f = await Fixture.Create();
        for (var i = 0; i < 100; i++)
        {
            f.Model.SelectCompositingNode(Node(i % 2 == 0 ? "layer:bitmap-graphics" : "layer:other"));
            f.Model.BeginDraftTransaction(); f.Model.SelectedLayerPositionX = (i + 1) / 101d; f.Model.EndDraftTransaction();
            Assert.True(f.Model.UndoInspectorDraftCommand.CanExecute(null));
            f.Model.UndoInspectorDraftCommand.Execute(null); f.Model.RedoInspectorDraftCommand.Execute(null);
            Assert.InRange(f.Model.InspectorContextCount, 1, MediaPoolInspectorViewModel.MaximumInspectorContexts);
            Assert.InRange(f.Model.InspectorDraftReviews.Count, 0, MediaPoolInspectorViewModel.MaximumInspectorContexts);
        }
        Assert.Equal(0, f.Transport.MutationCalls);
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
            Assert.True(undo.Command!.CanExecute(null));
            var tabs = Assert.IsType<RtaimeTabControl>(control.FindName("InspectorTopTabs"));
            var selectedTab = tabs.SelectedIndex;
            tabs.SelectedIndex = 1; await dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.Loaded);
            Assert.True(undo.IsVisible);
            tabs.SelectedIndex = 2; await dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.Loaded);
            Assert.True(undo.IsVisible);
            tabs.SelectedIndex = selectedTab;
            undo.Focus(); undo.Command.Execute(null);
            Assert.Equal(0.1, f.Model.SelectedLayerPositionX); Assert.True(f.Model.RedoInspectorDraftCommand.CanExecute(null));
            Assert.Equal(0, f.Transport.MutationCalls);
            f.Model.SelectedLayerPositionX = 0.61;
            f.Model.SelectCompositingNode(Node("layer:other"));
            await dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.Loaded);
            var reviewSurface = Assert.Single(Descendants(control).OfType<InspectorDraftReviewControl>());
            Assert.Same(f.Model, reviewSurface.DataContext);
            Assert.Equal("bitmap-graphics", f.Model.SelectedDraftReview!.Target.Id);
            var keep = Descendants(reviewSurface).OfType<RtaimeButton>().Single(button =>
                AutomationProperties.GetName(button) == "Keep retained draft locally");
            keep.BringIntoView(); Assert.True(keep.Focus()); keep.Command!.Execute(null);
            Assert.Contains("KEPT", f.Model.SelectedDraftReview!.State);
            Assert.Equal(1, control.InspectorSubscriptionCount);
            var audioItems = f.Model.FilteredItems.Where(item => item.Kind == MediaPoolItemKind.Audio).ToArray();
            f.Model.Selection.Select([audioItems[0]]); f.Model.AudioGainDraft = 2; f.Model.CopyAttributesCommand.Execute(null);
            f.Model.Selection.Select(audioItems);
            await dispatcher.InvokeAsync(main.UpdateLayout, DispatcherPriority.Loaded);
            var reviewBatch = Descendants(reviewSurface).OfType<RtaimeButton>().Single(button =>
                AutomationProperties.GetName(button) == "Review compatible Inspector batch targets");
            reviewBatch.BringIntoView(); Assert.True(reviewBatch.Focus()); reviewBatch.Command!.Execute(null);
            Assert.Equal(2, f.Model.InspectorBatchItems.Count);
            Assert.True(f.Model.ApplyInspectorBatchCommand.CanExecute(null));
            Assert.Equal(0, f.Transport.MutationCalls);
            Assert.Equal(1, control.InspectorSubscriptionCount);
            Console.WriteLine("INSPECTOR DRAFT PASS: productive themed controls, focused numeric grouping, accessible undo/redo, retained dirty selection, explicit common-property/batch review, stable subscriptions and unchanged production transport.");
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
