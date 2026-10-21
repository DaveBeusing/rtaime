// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class LiveConsoleTests
{
    [Fact]
    public async Task Observations_preserve_local_reorder_undo_and_selection_but_surface_storage_conflict()
    {
        var transport = new ReadOnlyTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client);
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck);
        var model = new RundownViewModel(client, owner, pool);
        var a = new RundownHoldItem(RundownItemId.New(), "A", 25);
        var b = new RundownHoldItem(RundownItemId.New(), "B", 50);
        var c = new RundownHoldItem(RundownItemId.New(), "C", 75);
        var definition = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Show", [a,b,c]);
        var snapshot = new RundownWorkspaceSnapshot(definition, RundownExecutionSnapshot.Idle, 1);
        model.ApplyConfirmedObservation(snapshot);
        var selected = model.Items[2]; model.SelectedItem = selected;
        Assert.True(model.MoveDraftBefore(selected, model.Items[0]));
        Assert.Equal(new[] {c.ItemId,a.ItemId,b.ItemId}, model.Items.Select(row => row.Item.ItemId));
        model.ApplyConfirmedObservation(snapshot with { Execution = snapshot.Execution with { Revision = 2, RemainingFollowFrames = 9, FollowTargetFrameSequence = 100 } });
        Assert.Same(selected, model.SelectedItem);
        Assert.Equal(c.ItemId, model.Items[0].Item.ItemId);
        Assert.Contains("9f remaining", model.FollowCountdown);
        Assert.True(model.UndoDraftCommand.CanExecute(null));
        model.UndoDraftCommand.Execute(null);
        Assert.Equal(a.ItemId, model.Items[0].Item.ItemId);
        model.RedoDraftCommand.Execute(null);
        model.ApplyConfirmedObservation(snapshot with { StorageVersion = 2 });
        Assert.Contains("CONFLICT", model.StorageLabel);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.Equal(c.ItemId, model.Items[0].Item.ItemId);
        Assert.Equal(0, transport.Mutations);
    }

    [Fact]
    public async Task Disconnected_prepared_evidence_cannot_enable_go_or_prepare_and_recovery_locks_authoring()
    {
        var client = new OperatorControlClient(new ReadOnlyTransport());
        await using var owner = new OperatorViewModel(client);
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck);
        var model = new RundownViewModel(client, owner, pool);
        var item = new RundownHoldItem(RundownItemId.New(), "Hold", 25);
        var definition = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Show", [item]);
        var execution = RundownExecutionSnapshot.Idle with { RundownId = definition.RundownId, PreparedItemId = item.ItemId, State = RundownExecutionState.Prepared, Revision = 4 };
        model.ApplyConfirmedObservation(new(definition, execution, 1));
        Assert.False(model.GoCommand.CanExecute(null));
        Assert.False(model.PrepareCommand.CanExecute(null));
        Assert.Contains("stale", model.LiveActionDetail);
        model.ApplyConfirmedObservation(new(definition, execution with { State = RundownExecutionState.RecoveryRequired, RequiresAcknowledgement = true, Revision = 5 }, 1));
        Assert.False(model.CanEditAuthoring);
        Assert.False(model.GoCommand.CanExecute(null));
        model.ApplyConfirmedObservation(new(definition, execution, 1));
        Assert.Equal("RECOVERYREQUIRED", model.State);
    }

    [Fact]
    public void Show_observations_preserve_editor_identity_and_report_actual_armed_target()
    {
        var model = new ShowControlViewModel(new OperatorControlClient(new ReadOnlyTransport()));
        var cue = new ShowControlCue(ShowControlCueId.New(), "Confirmed opening", [new(ShowControlActionId.New(), ShowControlActionKind.Cut)]);
        var next = new ShowControlCue(ShowControlCueId.New(), "Confirmed next", [new(ShowControlActionId.New(), ShowControlActionKind.StopRecording)]);
        var list = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(), "Show", [cue, next]);
        var idle = new ShowControlWorkspaceSnapshot([list], list.CueListId, ShowControlExecutionSnapshot.Idle);
        model.ApplyConfirmedSnapshot(idle);
        var local = model.SelectedCue!;
        local.Name = "Unsaved local rename";
        var armed = idle.Execution with { ExecutionId = ShowControlExecutionId.New(), CueListId = list.CueListId,
            State = ShowControlExecutionState.Armed, CueIndex = 0, ActionIndex = 0, ExecutionRevision = 1 };
        model.ApplyLiveObservation(new([list], list.CueListId, armed));
        Assert.Same(local, model.SelectedCue);
        Assert.Equal("Unsaved local rename", local.Name);
        Assert.Equal("Confirmed opening", model.CurrentCue);
        Assert.Equal("Confirmed next", model.NextCue);
        Assert.Contains("Confirmed opening", model.LiveActionDetail);
        Assert.False(model.CanEditAuthoring);
    }

    [Fact]
    public async Task Rundown_now_next_and_local_selection_remain_distinct_during_hold_follow_and_recovery()
    {
        var transport = new ReadOnlyTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client);
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck);
        var model = new RundownViewModel(client, owner, pool);
        var a = new RundownHoldItem(RundownItemId.New(), "Opening hold", 25);
        var b = new RundownHoldItem(RundownItemId.New(), "Next hold", 50);
        var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Live", [a, b]);
        var prepared = RundownExecutionSnapshot.Idle with
        {
            RundownId = rundown.RundownId, CurrentItemId = a.ItemId, PreparedItemId = b.ItemId,
            NextItemId = b.ItemId, SelectedItemId = b.ItemId, State = RundownExecutionState.Prepared,
            Revision = 1
        };
        model.ApplyConfirmedObservation(new(rundown, prepared, 1));
        Assert.Equal("Opening hold", model.NowItemName);
        Assert.Equal("Next hold", model.NextItemName);
        Assert.Contains("PREPARED", model.NextItemStatus);
        Assert.Contains("NOT PROGRAM", model.NextItemStatus);
        model.SelectedItem = model.Items[0];
        Assert.Equal("Opening hold", model.SelectedItemName);
        Assert.Equal("Next hold", model.NextItemName);
        Assert.Equal(0, transport.Mutations);

        var follow = prepared with
        {
            State = RundownExecutionState.Executing, CurrentItemId = b.ItemId,
            PendingNextItemId = a.ItemId, AutoAdvanceArmed = true, Revision = 2
        };
        model.ApplyConfirmedObservation(new(rundown, follow, 1));
        Assert.Equal("Next hold", model.NowItemName);
        Assert.Equal("Opening hold", model.NextItemName);
        Assert.Contains("AUTO FOLLOW ARMED", model.NextItemStatus);

        var held = follow with
        {
            State = RundownExecutionState.Held, AutoAdvanceArmed = false,
            PendingNextItemId = null, Revision = 3
        };
        model.ApplyConfirmedObservation(new(rundown, held, 1));
        Assert.Contains("HOLD", model.NowItemStatus);
        Assert.Contains("FOLLOW DISARMED", model.NextItemStatus);
        model.ApplyConfirmedObservation(new(rundown, held with
        {
            State = RundownExecutionState.RecoveryRequired,
            RequiresAcknowledgement = true, Revision = 4
        }, 1));
        Assert.Contains("RECOVERY", model.NextItemStatus);
        Assert.False(model.GoCommand.CanExecute(null));
        Assert.Equal(0, transport.Mutations);
    }

    [Fact]
    public async Task Rundown_media_next_ready_remains_host_qualified_and_expiry_requires_reprepare()
    {
        var transport = new ReadOnlyTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client);
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck);
        var model = new RundownViewModel(client, owner, pool);
        var clip = new RundownMediaItem(RundownItemId.New(), "Studio clip",
            Identity.New(), ProductionSourceId.New(), RundownTransition.Cut);
        var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Live", [clip]);
        var prepared = RundownExecutionSnapshot.Idle with
        {
            RundownId = rundown.RundownId, State = RundownExecutionState.Prepared,
            PreparedItemId = clip.ItemId, NextItemId = clip.ItemId, Revision = 8,
            PreparedMediaState = "NEXT_READY",
            PreparedMediaExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        model.ApplyConfirmedObservation(new(rundown, prepared, 1));
        Assert.Contains("HOST NEXT_READY", model.NextItemStatus);
        Assert.Contains("RECHECK AT GO", model.Items[0].Readiness);
        Assert.Contains("ASSET UNVERIFIED", model.Items[0].Readiness);
        Assert.DoesNotContain("PROGRAM CONFIRMED", model.NextItemStatus);

        model.ApplyConfirmedObservation(new(rundown, prepared with
        {
            PreparedMediaExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1), Revision = 9
        }, 1));
        Assert.Contains("EXPIRED", model.NextItemStatus);
        Assert.Contains("PREPARE AGAIN", model.Items[0].Readiness);
        Assert.Equal(0, transport.Mutations);

        await model.RefreshAsync(); // Explicit refresh fails: old Program evidence cannot remain actionable.
        Assert.Equal("UNVERIFIED", model.State);
        Assert.Equal("UNVERIFIED", model.NextItemName);
        Assert.Contains("SYNC REQUIRED", model.NextItemStatus);
        Assert.False(model.GoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Interrupted_rundown_observation_retracts_old_ready_state_without_replaying_GO()
    {
        var transport = new ReadOnlyTransport { CancelRundownReads = true };
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client);
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck);
        var model = new RundownViewModel(client, owner, pool);
        var item = new RundownHoldItem(RundownItemId.New(), "Prepared hold", 25);
        var definition = new RundownDefinition(RundownContractVersion.Current,
            RundownId.New(), "Show", [item]);
        var prepared = RundownExecutionSnapshot.Idle with
        {
            RundownId = definition.RundownId, State = RundownExecutionState.Prepared,
            PreparedItemId = item.ItemId, Revision = 5
        };
        model.ApplyConfirmedObservation(new(definition, prepared, 1));
        Assert.Equal("PREPARED", model.State);
        await model.RefreshAsync(); // Cancellation is not proof that Program was unchanged.
        Assert.Equal("UNVERIFIED", model.State);
        Assert.Equal("UNVERIFIED", model.NowItemName);
        Assert.Contains("SYNC REQUIRED", model.NextItemStatus);
        Assert.False(model.GoCommand.CanExecute(null));
        Assert.Equal(0, transport.Mutations);
    }

    [Fact]
    public async Task Uncertain_refresh_with_local_draft_keeps_live_authority_blocked_after_undo()
    {
        var transport = new ReadOnlyTransport { CancelRundownReads = true };
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client);
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck);
        var model = new RundownViewModel(client, owner, pool);
        var a = new RundownHoldItem(RundownItemId.New(), "A", 25);
        var b = new RundownHoldItem(RundownItemId.New(), "B", 50);
        var rundown = new RundownDefinition(RundownContractVersion.Current,
            RundownId.New(), "Show", [a, b]);
        var prepared = RundownExecutionSnapshot.Idle with
        {
            RundownId = rundown.RundownId,
            State = RundownExecutionState.Prepared,
            PreparedItemId = a.ItemId,
            Revision = 2
        };
        model.ApplyConfirmedObservation(new(rundown, prepared, 1));
        Assert.True(model.MoveDraftBefore(model.Items[1], model.Items[0]));
        await model.RefreshAsync();
        Assert.Equal("UNVERIFIED", model.NowItemName);
        Assert.True(model.UndoDraftCommand.CanExecute(null));
        model.UndoDraftCommand.Execute(null);
        Assert.Equal("UNVERIFIED", model.NowItemName);
        Assert.Equal("UNVERIFIED", model.NextItemName);
        Assert.False(model.GoCommand.CanExecute(null));
        Assert.Equal(0, transport.Mutations);
    }

    [Fact]
    public void Show_control_run_target_is_confirmed_and_never_the_unsaved_selected_editor_cue()
    {
        var transport = new ReadOnlyTransport();
        var model = new ShowControlViewModel(new OperatorControlClient(transport));
        var first = new ShowControlCue(ShowControlCueId.New(), "First",
            [new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.Cut)]);
        var second = new ShowControlCue(ShowControlCueId.New(), "Second",
            [new ShowControlAction(ShowControlActionId.New(), ShowControlActionKind.StopRecording)]);
        var list = new ShowControlCueList(ShowControlContractVersion.Current,
            ShowControlCueListId.New(), "Live", [first, second]);
        var idle = new ShowControlWorkspaceSnapshot([list], list.CueListId,
            ShowControlExecutionSnapshot.Idle);
        model.ApplyConfirmedSnapshot(idle);
        model.SelectedCue = model.SelectedCueList!.Cues[1];
        model.SelectedCue.Name = "Local unsaved rename";
        var armed = idle.Execution with
        {
            ExecutionId = ShowControlExecutionId.New(), CueListId = list.CueListId,
            State = ShowControlExecutionState.Armed, CueIndex = 0, ActionIndex = 0,
            CurrentCueId = first.CueId, CurrentActionId = first.Actions[0].ActionId,
            ExecutionRevision = 1
        };
        model.ApplyLiveObservation(new([list], list.CueListId, armed));
        Assert.Equal("First", model.CurrentCue);
        Assert.Equal("Local unsaved rename", model.LocalCueSelection);
        Assert.Contains("ARMED", model.CueRunStatus);
        Assert.Contains("NOT ARMED", model.NextCueStatus);
        Assert.Equal("Second", model.NextCue);
        Assert.Equal(0, transport.Mutations);
    }

    private sealed class ReadOnlyTransport : IOperatorControlTransport
    {
        public int Mutations { get; private set; }
        public bool CancelRundownReads { get; init; }
        public ValueTask<RundownWorkspaceSnapshot> GetRundownSnapshotAsync(CancellationToken cancellationToken = default) =>
            CancelRundownReads
                ? ValueTask.FromException<RundownWorkspaceSnapshot>(new OperationCanceledException("Control observation interrupted."))
                : ValueTask.FromException<RundownWorkspaceSnapshot>(new NotSupportedException());
        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
    }
}
