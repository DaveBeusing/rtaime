// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public sealed partial class RundownViewModel
{
    private long _observationGeneration;
    private RundownWorkspaceSnapshot? _confirmedSnapshot;
    private bool _hasLocalDraft, _draftConflict, _confirmedExecutionActive;
    private bool LiveAuthorityAvailable => _observationAvailable && _operator.CanOperateLive;
    public string CurrentItemName => ResolveItem(_confirmedSnapshot?.Execution.CurrentItemId);
    public string PreparedItemName => ResolveItem(_confirmedSnapshot?.Execution.PreparedItemId);
    private string ResolveItem(RundownItemId? id) => id is { } value
        ? _confirmedSnapshot?.Rundown?.Items.FirstOrDefault(item => item.ItemId == value)?.Name ?? value.ToString()
        : "NONE";

    // NOW and NEXT come exclusively from the confirmed ControlHost execution.
    // Local row selection is authoring intent and must never be presented as Program.
    public string NowItemName => _observationAvailable
        ? ResolveItem(_confirmedSnapshot?.Execution.CurrentItemId) : "UNVERIFIED";
    public string NowItemStatus => !_observationAvailable ? "SYNC REQUIRED · PROGRAM NOT VERIFIED" :
        _confirmedSnapshot?.Execution.State switch
        {
            RundownExecutionState.Executing => "RUNDOWN EXECUTING · CONTROL CONFIRMED",
            RundownExecutionState.Held => "HOLD · AUTO DISARMED",
            RundownExecutionState.Completed => "LAST ITEM · EXECUTION COMPLETE",
            RundownExecutionState.Failed => "FAILED · FOLLOW HALTED",
            RundownExecutionState.RecoveryRequired => "RECOVERY · INSPECT PROGRAM",
            _ => "NO ACTIVE RUNDOWN EXECUTION"
        };
    private RundownItemId? NextConfirmedId
    {
        get
        {
            var execution = _confirmedSnapshot?.Execution;
            return execution?.AutoAdvanceArmed == true && execution.PendingNextItemId is { } pending
                ? pending : execution?.PreparedItemId ?? execution?.NextItemId;
        }
    }
    public string NextItemName => _observationAvailable ? ResolveItem(NextConfirmedId) : "UNVERIFIED";
    public string SelectedItemName => SelectedItem?.Name ?? "NONE";
    public string NextItemStatus
    {
        get
        {
            if (!_observationAvailable) return "SYNC REQUIRED · NO EXECUTION ASSUMED";
            var execution = _confirmedSnapshot?.Execution;
            if (execution is null) return "UNVERIFIED · WAITING FOR CONTROL";
            if (execution.RequiresAcknowledgement ||
                execution.State == RundownExecutionState.RecoveryRequired)
                return "RECOVERY REQUIRED · GO BLOCKED";
            if (execution.State == RundownExecutionState.Failed)
                return "FAILED · FOLLOW HALTED";
            if (execution.State == RundownExecutionState.Held)
                return "HELD · FOLLOW DISARMED";
            if (NextConfirmedId is null) return "NO CONFIRMED NEXT ITEM";
            if (execution.PreparedItemId == NextConfirmedId &&
                execution.PreparedMediaState == "NEXT_READY" &&
                execution.PreparedMediaExpiresAtUtc is { } expiredAt &&
                expiredAt <= DateTimeOffset.UtcNow)
                return "RESIDENT PREPARATION EXPIRED · PREPARE AGAIN";
            if (execution.AutoAdvanceArmed && execution.PendingNextItemId == NextConfirmedId)
                return "AUTO FOLLOW ARMED · HOST REVALIDATES";
            if (execution.PreparedItemId != NextConfirmedId)
                return "NEXT CANDIDATE · NOT PREPARED";
            var next = _confirmedSnapshot?.Rundown?.Items.FirstOrDefault(item => item.ItemId == NextConfirmedId);
            if (next is not RundownMediaItem)
                return "PREPARED · CONTROL CONFIRMED · NOT PROGRAM";
            if (execution.PreparedMediaState != "NEXT_READY")
                return "MEDIA · NATIVE READINESS UNVERIFIED";
            if (execution.PreparedMediaExpiresAtUtc is { } expires &&
                expires <= DateTimeOffset.UtcNow)
                return "RESIDENT PREPARATION EXPIRED · PREPARE AGAIN";
            return "HOST NEXT_READY · REVALIDATED AT GO";
        }
    }
    public string LiveActionDetail => !_observationAvailable ? "Rundown observation unavailable. Synchronize before executing." : !LiveAuthorityAvailable ? _operator.LiveAvailability :
        IsBusy ? "Rundown command pending; wait for confirmation." :
        _draftConflict ? "Saved rundown changed. Refresh to discard the local draft and reload." :
        _hasLocalDraft ? "Local draft; save before Prepare or GO." :
        RequiresAcknowledgement ? "Recovery required. Review Program, then explicitly Resume or Cancel recovery." :
        GoCommand.CanExecute(null) ? $"RUNDOWN GO → {PreparedItemName} · pending {PendingNext}" :
        $"Prepare selected item: {SelectedItem?.Name ?? "select a row first"}.";
    internal void NotifyLiveAuthorityChanged() => RaiseCommandState();

    // Borrowed by the existing Operator synchronization loop. No timer or second connection.
    internal async Task<Action?> CaptureLiveObservationAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return null;
        var generation = Interlocked.Read(ref _observationGeneration);
        try
        {
            var snapshot = await _client.GetRundownSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return () => {
                if (!IsBusy && generation == Interlocked.Read(ref _observationGeneration))
                    ApplyConfirmedObservation(snapshot);
            };
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or TimeoutException or InvalidOperationException)
        {
            return () => {
                if (generation != Interlocked.Read(ref _observationGeneration) || IsBusy) return;
                Failure = exception.Message;
                Detail = "Rundown observation unavailable. Synchronize before executing.";
                _observationAvailable = false;
                RaiseCommandState();
            };
        }
    }
    private bool _observationAvailable = true;

    public void ApplyConfirmedObservation(RundownWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (IsBusy) return;
        var previous = _confirmedSnapshot;
        if (previous is not null && (snapshot.StorageVersion < previous.StorageVersion ||
            (snapshot.StorageVersion == previous.StorageVersion && snapshot.Execution.RundownId == previous.Execution.RundownId &&
             snapshot.Execution.Revision < previous.Execution.Revision))) return;
        _observationAvailable = true;
        if (!_hasLocalDraft && (previous is null || snapshot.StorageVersion != _storageVersion ||
            snapshot.Rundown?.RundownId != previous.Rundown?.RundownId))
        {
            ApplySnapshot(snapshot);
            return;
        }
        _confirmedSnapshot = snapshot;
        _confirmedExecutionActive = snapshot.Execution.State is RundownExecutionState.Executing or RundownExecutionState.RecoveryRequired;
        if (_hasLocalDraft)
        {
            _draftConflict |= snapshot.StorageVersion != _storageVersion;
            // Execution evidence must continue while authored rows and undo history remain local.
            ApplyObservedExecution(snapshot);
            if (!_confirmedExecutionActive) State = "DRAFT";
        }
        else ApplyObservedExecution(snapshot);
        UpdateRowEvidence();
        RaiseCommandState();
    }

    private void ApplyObservedExecution(RundownWorkspaceSnapshot snapshot)
    {
        var execution = snapshot.Execution;
        foreach (var item in Items) item.ApplyExecution(execution);
        State = execution.State.ToString().ToUpperInvariant();
        RequiresAcknowledgement = execution.RequiresAcknowledgement;
        Failure = execution.Failure?.Message ?? "NONE";
        AutomationState = execution.AutoAdvanceArmed ? "AUTO" : execution.State == RundownExecutionState.Held ? "HOLD" : execution.FollowActionKind == RundownFollowActionKind.Manual ? "MANUAL" : "HOLD";
        FollowStatus = execution.FollowActionKind.ToString().ToUpperInvariant();
        PendingNext = ResolveItem(execution.PendingNextItemId);
        FollowCountdown = execution.FollowTargetFrameSequence is { } target
            ? execution.RemainingFollowFrames is { } remaining ? $"{remaining}f remaining · target {target}" : $"target frame {target}" : "—";
        RepeatStatus = execution.RemainingItemRepeats > 0 ? $"ITEM ×{execution.RemainingItemRepeats}"
            : execution.RemainingRundownRepeats > 0 ? $"RUNDOWN ×{execution.RemainingRundownRepeats}" : "NONE";
        Detail = execution.State == RundownExecutionState.RecoveryRequired
            ? "Recovery required. Review Program and acknowledge explicitly; reconnect never replays GO."
            : _hasLocalDraft ? "Local draft retained; Save is explicit." : $"Confirmed {State} · {FollowStatus}";
    }

    // Dragging changes bounded authoring order only; Save and production actions stay explicit.
    public bool MoveDraftBefore(RundownOperatorItemViewModel dragged, RundownOperatorItemViewModel target)
    {
        if (!CanEditDraft() || ReferenceEquals(dragged, target)) return false;
        var from = Items.IndexOf(dragged);
        var to = Items.IndexOf(target);
        if (from < 0 || to < 0) return false;
        if (from < to && from == to - 1) return false;
        RememberDraft();
        Items.Move(from, from < to ? to - 1 : to);
        SelectedItem = dragged;
        MarkDraft();
        return true;
    }
}
