// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;
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
        var list = new ShowControlCueList(ShowControlContractVersion.Current, ShowControlCueListId.New(), "Show", [cue]);
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
        Assert.Contains("Confirmed opening", model.LiveActionDetail);
        Assert.False(model.CanEditAuthoring);
    }

    private sealed class ReadOnlyTransport : IOperatorControlTransport
    {
        public int Mutations { get; private set; }
        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
    }
}
