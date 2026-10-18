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
    public string NextCue
    {
        get
        {
            var snapshot = _confirmedLiveSnapshot;
            if (snapshot is null) return "NONE";
            var list = snapshot.CueLists.FirstOrDefault(item => item.CueListId == snapshot.Execution.CueListId);
            var next = snapshot.Execution.CueIndex is { } index ? index + 1 : -1;
            return list is not null && next >= 0 && next < list.Cues.Count ? list.Cues[next].Name : "NONE";
        }
    }
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
        if (SelectedCueList.Name != saved.Name || SelectedCueList.Cues.Count != saved.Cues.Count) return false;
        try
        {
            for (var cueIndex = 0; cueIndex < saved.Cues.Count; cueIndex++)
            {
                var local = SelectedCueList.Cues[cueIndex]; var confirmed = saved.Cues[cueIndex];
                if (local.CueId != confirmed.CueId || local.Name != confirmed.Name ||
                    local.Actions.Count != confirmed.Actions.Count) return false;
                for (var actionIndex = 0; actionIndex < confirmed.Actions.Count; actionIndex++)
                    if (local.Actions[actionIndex].ToContract() != confirmed.Actions[actionIndex]) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException) { return false; }
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
