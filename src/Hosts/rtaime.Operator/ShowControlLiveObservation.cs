// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public sealed partial class ShowControlViewModel
{
    private readonly OperatorViewModel? _operator;
    private ShowControlWorkspaceSnapshot? _confirmedLiveSnapshot;
    private bool LiveAuthorityAvailable => _operator?.CanOperateLive ?? true;
    public bool CanEditAuthoring => !IsBusy && _serverExecutionState is not
        (ShowControlExecutionState.Armed or ShowControlExecutionState.Executing or
         ShowControlExecutionState.Waiting or ShowControlExecutionState.RecoveryRequired);
    public string LiveActionDetail => !LiveAuthorityAvailable ? _operator?.LiveAvailability ?? "Synchronize first." :
        IsBusy ? "Show Control command pending; wait for confirmation." :
        RequiresAcknowledgement ? "Review Program, then explicitly Resume or Cancel recovery." :
        CanGo() ? $"SHOW GO → {CurrentCue} · {CurrentAction}" :
        $"Select the saved list and ARM. Confirmed state: {ExecutionState}.";
    private bool HasSavedSelection()
    {
        if (SelectedCueList is null || _confirmedLiveSnapshot is null) return false;
        var saved = _confirmedLiveSnapshot.CueLists.FirstOrDefault(list => list.CueListId == SelectedCueList.CueListId);
        if (saved is null) return false;
        try { return ShowControlCanonicalSerializer.Serialize(SelectedCueList.ToContract()) == ShowControlCanonicalSerializer.Serialize(saved); }
        catch (ArgumentException) { return false; }
    }
    internal void NotifyLiveAuthorityChanged() => RaiseCommandState();
    internal void ApplyLiveObservation(ShowControlWorkspaceSnapshot snapshot)
    {
        if (IsBusy) return;
        var previous = _confirmedLiveSnapshot;
        if (previous is not null && snapshot.Execution.ExecutionId == previous.Execution.ExecutionId &&
            snapshot.Execution.ExecutionRevision < previous.Execution.ExecutionRevision) return;
        if (previous is null) { ApplyConfirmedSnapshot(snapshot); return; }
        // Live observations update execution only. Text edits, local cue selection and editor rows survive.
        _confirmedLiveSnapshot = snapshot;
        _serverSelectedCueListId = snapshot.SelectedCueListId;
        ApplyExecution(snapshot);
        RaiseCommandState();
    }
}
