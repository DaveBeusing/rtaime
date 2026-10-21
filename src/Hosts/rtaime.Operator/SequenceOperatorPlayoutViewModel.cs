// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

/// <summary>A read-only projection of current Control and Runtime authority.</summary>
public sealed record SequenceOperatorAuthority(
    bool Connected, bool Stale, bool Busy, bool Ready,
    string PreviewSourceId, string ProgramSourceId, ulong Revision)
{
    public bool CanStage => Connected && !Stale && !Busy && Ready &&
        Guid.TryParse(PreviewSourceId, out var id) && id != Guid.Empty &&
        !string.Equals(PreviewSourceId, ProgramSourceId, StringComparison.OrdinalIgnoreCase);

    public static SequenceOperatorAuthority From(OperatorViewModel owner)
    {
        var current = owner.Client?.Snapshot;
        return new(owner.IsConnected && current is not null, owner.IsStale, owner.IsBusy,
            string.Equals(owner.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase) &&
                owner.ProgramSafety != OperatorProgramSafetyStates.Blocked,
            current?.Production.Routing.PreviewSourceId.ToString() ?? string.Empty,
            current?.Production.Routing.ProgramSourceId.ToString() ?? string.Empty,
            current?.Production.Revision.Value ?? 0);
    }
}

/// <summary>
/// Single-user-action native sequence playout. It never supplies its own Program
/// clock, routes a selected clip, or converts PREPARED/shuttle PLAYING to output.
/// A Take uses exactly one immutable transaction; recovery never replays it.
/// </summary>
public sealed class SequenceOperatorPlayoutViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SequenceTimelineEditorViewModel _editor;
    private readonly SequenceAuthoringClient? _authoring;
    private readonly SequencePreparationClient? _preparation;
    private readonly SequenceShuttleClient? _shuttleClient;
    private readonly SequenceOutputClient? _outputClient;
    private readonly Func<SequenceOperatorAuthority> _authority;
    private readonly INotifyPropertyChanged? _authorityNotifications;
    private readonly CancellationTokenSource _lifetime = new();
    private SequencePreparationReference? _reference;
    private SequenceShuttleObservation? _shuttle;
    private SequenceOutputObservation? _output;
    private SequenceOutputCommand? _take;
    private string _source = "";
    private ulong _revision;
    private bool _activated, _armed, _submitted, _busy, _disposed;
    private string _status = "UNAVAILABLE";
    private string _detail = "Connect and load an authoritative saved sequence.";

    public SequenceOperatorPlayoutViewModel(
        SequenceTimelineEditorViewModel editor, SequenceAuthoringClient? authoring,
        SequencePreparationClient? preparation, SequenceShuttleClient? shuttle,
        SequenceOutputClient? output, Func<SequenceOperatorAuthority> authority,
        INotifyPropertyChanged? authorityNotifications = null)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _authoring = authoring; _preparation = preparation;
        _shuttleClient = shuttle; _outputClient = output;
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _authorityNotifications = authorityNotifications;
        editor.PropertyChanged += EditorChanged;
        if (authorityNotifications is not null)
            authorityNotifications.PropertyChanged += AuthorityChanged;

        PrepareCommand = new AsyncRelayCommand(PrepareAsync, () => CanPrepare);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => CanCancel);
        ActivatePreviewCommand = new AsyncRelayCommand(ActivatePreviewAsync, () => CanActivate);
        ObserveCommand = new AsyncRelayCommand(ObserveAsync, () => CanObserve);
        CueCommand = new AsyncRelayCommand(() => ShuttleAsync(SequenceShuttleAction.Cue, _editor.EditCursorTick), () => CanShuttle);
        PlayCommand = new AsyncRelayCommand(() => ShuttleAsync(SequenceShuttleAction.Play), () => CanShuttle);
        PauseCommand = new AsyncRelayCommand(() => ShuttleAsync(SequenceShuttleAction.Pause), () => CanShuttle);
        StopCommand = new AsyncRelayCommand(() => ShuttleAsync(SequenceShuttleAction.Stop), () => CanShuttle);
        ArmCommand = new AsyncRelayCommand(ArmAsync, () => CanArm);
        TakeCommand = new AsyncRelayCommand(TakeAsync, () => CanTake);
        RecoverCommand = new AsyncRelayCommand(RecoverAsync, () => CanRecover);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand PrepareCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ActivatePreviewCommand { get; }
    public ICommand ObserveCommand { get; }
    public ICommand CueCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ArmCommand { get; }
    public ICommand TakeCommand { get; }
    public ICommand RecoverCommand { get; }

    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string Detail { get => _detail; private set { _detail = value; Changed(); } }
    public string NativeOwner => _reference is null ? "NO NATIVE OWNER" :
        $"SAVED REV {_reference.AuthoringRevision} · GENERATION {_reference.Generation} · PREVIEW SOURCE {_source}";
    public string ShuttleEvidence => _shuttle is null ? "SHUTTLE UNAVAILABLE" :
        $"NATIVE {_shuttle.State} · {_shuttle.PositionTicks} authored ticks · DECODE {_shuttle.DecodeGeneration}";
    public string OutputEvidence => _output is null ? "NO CONFIRMED OUTPUT" :
        $"OUTPUT {_output.OutputState} · FRAMES {_output.PublishedSourceFrames} · " +
        $"SOURCE SLOT {_output.LastSourceSlot?.ToString() ?? "—"} · " +
        $"PROGRAM SLOT {_output.LastProgramSlot?.ToString() ?? "—"} · REV {_output.ProductionRevision}";

    private bool Available => !_disposed && !_busy &&
        _authoring is not null && _preparation is not null &&
        _shuttleClient is not null && _outputClient is not null;
    private bool SavedOwner => _editor.Snapshot is { Validation.IsValid: true } saved &&
        _editor.CanEdit && _editor.GestureLabel == "NO PENDING EDIT" &&
        _reference is not null && saved.Sequence.SequenceId == _reference.SequenceId &&
        saved.AuthoringRevision == _reference.AuthoringRevision;
    private bool CurrentPreview => SavedOwner && _authority() is { CanStage: true } current &&
        current.Revision == _revision &&
        string.Equals(current.PreviewSourceId, _source, StringComparison.OrdinalIgnoreCase);
    private bool RealPreview => _activated && CurrentPreview &&
        _output is { OutputState: "PREVIEW_READY", PublishedSourceFrames: > 0, LastSourceSlot: not null, LastProgramSlot: null } observed &&
        observed.ProductionRevision == _revision &&
        string.Equals(observed.PreviewSourceId, _source, StringComparison.OrdinalIgnoreCase);

    public bool CanPrepare => Available && _reference is null && _authority().CanStage &&
        _editor.CanEdit && _editor.GestureLabel == "NO PENDING EDIT";
    public bool CanCancel => Available && _reference is not null && !_submitted;
    public bool CanActivate => Available && Status == "PREPARED · NO OUTPUT" &&
        CurrentPreview && !_activated && !_submitted;
    public bool CanObserve => Available && _reference is not null &&
        _authority() is { Connected: true, Stale: false };
    public bool CanShuttle => Available && Status is not ("STALE" or "ERROR") &&
        CurrentPreview && _shuttle is not null &&
        _take is null && _output?.OutputState is not ("PROGRAM_CONFIRMED" or "TAKE_COMMITTED");
    public bool CanArm => Available && Status == "PREVIEW READY" && RealPreview && _take is null;
    public bool CanTake => Available && Status == "TAKE ARMED" &&
        _armed && !_submitted && _take is not null && RealPreview;
    public bool CanRecover => Available && _submitted && _take is not null && SavedOwner &&
        _authority() is { Connected: true, Stale: false, Busy: false, Ready: true } &&
        Status != "PROGRAM CONFIRMED";

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _disposed) return;
        _busy = true; RaiseCommands();
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Status = _submitted ? "TAKE UNCONFIRMED" : "ERROR";
            Detail = error.Message.Length > 480 ? error.Message[..480] : error.Message;
        }
        finally { _busy = false; RaiseCommands(); }
    }

    public Task PrepareAsync() => RunAsync(async ct =>
    {
        if (_reference is not null || _editor.Snapshot is not { Validation.IsValid: true } snapshot ||
            !_editor.CanEdit || !_authority().CanStage || _authoring is null || _preparation is null) return;
        Status = "PREPARING";
        var previewAtRequest = _authority();
        var saved = await _authoring.GetAsync(snapshot.Sequence.SequenceId, ct);
        if (!saved.Validation.IsValid || saved.Sequence.SequenceId != snapshot.Sequence.SequenceId ||
            saved.AuthoringRevision != snapshot.AuthoringRevision)
        {
            Status = "STALE"; Detail = "Saved revision changed; reload the timeline."; return;
        }
        var context = await _preparation.ContextAsync(ct);
        if (!_authority().CanStage || context.AuthoringRevision != snapshot.AuthoringRevision)
        {
            Status = "STALE"; Detail = "Saved revision, Preview or Runtime epoch must be reconciled."; return;
        }
        var result = await _preparation.PrepareAsync(new(
            SequencePreparationContract.Version, saved.Sequence.SequenceId,
            saved.AuthoringRevision, context.RuntimeHostInstanceId), ct);
        if (result.State != "PREPARED")
        {
            Status = result.State;
            Detail = result.ErrorCode is null ? result.Detail : result.ErrorCode + ": " + result.Detail;
            return;
        }
        _reference = new(result.Version, result.SequenceId, result.AuthoringRevision,
            result.RuntimeHostInstanceId, result.PreparedExecutionId, result.Generation);
        _reference.Validate();
        _shuttle = await _shuttleClient!.GetAsync(_reference, ct);
        _source = previewAtRequest.PreviewSourceId;
        _revision = previewAtRequest.Revision;
        _activated = false; _armed = false; _submitted = false; _take = null; _output = null;
        if (!CurrentPreview)
        {
            // Keep the admitted owner visible for explicit RELEASE; never
            // silently rebind a prepared reader to a different live source.
            Status = "STALE";
            Detail = "Preview route or authority changed during native preparation. RELEASE and prepare again.";
        }
        else
        {
            Status = "PREPARED · NO OUTPUT"; Detail = result.Detail;
        }
        NotifyEvidence();
    });

    public Task CancelAsync() => RunAsync(async ct =>
    {
        if (_reference is null || _preparation is null || _submitted) return;
        var canceled = await _preparation.CancelAsync(_reference, ct);
        if (canceled.State != "CANCELED")
        {
            Status = canceled.State; Detail = canceled.Detail; return;
        }
        ClearOwner();
        Status = "SAVED · NOT PREPARED";
        Detail = "Native owner released. No production change.";
    });

    public Task ActivatePreviewAsync() => RunAsync(async ct =>
    {
        if (Status != "PREPARED · NO OUTPUT" || !CurrentPreview ||
            _activated || _submitted || _outputClient is null) return;
        var result = await _outputClient.ActivatePreviewAsync(
            new(SequenceOutputContract.Version, _reference!, _source, _revision, Guid.NewGuid()), ct);
        _activated = true;
        RecordOutput(result);
    });

    public Task ShuttleAsync(SequenceShuttleAction action, long? ticks = null) =>
        RunAsync(async ct =>
        {
            if (_shuttle is null || _reference is null || _shuttleClient is null ||
                Status is "STALE" or "ERROR" || !CurrentPreview || _take is not null) return;
            var request = new SequenceShuttleRequest(SequenceShuttleContract.Version,
                _reference, _shuttle.StateRevision, action, ticks);
            _shuttle = await _shuttleClient.ApplyAsync(request, ct);
            _output = null; _armed = false;
            Status = "NATIVE " + _shuttle.State + " · PREVIEW UNCONFIRMED";
            Detail = "Shuttle controls staging only; select OBSERVE for real Preview output.";
            NotifyEvidence();
        });

    public Task ObserveAsync() => RunAsync(async ct =>
    {
        if (_reference is null || _preparation is null || _shuttleClient is null ||
            _outputClient is null || _authority() is not { Connected: true, Stale: false }) return;
        var prepared = await _preparation.GetAsync(_reference, ct);
        if (prepared.State != "PREPARED")
        {
            _output = null;
            _armed = false;
            _take = null;
            Changed(nameof(OutputEvidence));
            Status = "STALE"; Detail = prepared.Detail; return;
        }
        _shuttle = await _shuttleClient.GetAsync(_reference, ct);
        Changed(nameof(ShuttleEvidence));
        if (!_activated)
        {
            Status = "PREPARED · NO OUTPUT";
            return;
        }
        RecordOutput(await _outputClient.GetAsync(_reference, ct));
    });

    public Task ArmAsync() => RunAsync(async ct =>
    {
        if (Status != "PREVIEW READY" || !RealPreview || _reference is null ||
            _authoring is null || _preparation is null) return;
        var saved = await _authoring.GetAsync(_reference.SequenceId, ct);
        var context = await _preparation.ContextAsync(ct);
        if (saved.AuthoringRevision != _reference.AuthoringRevision ||
            !saved.Validation.IsValid || !CurrentPreview ||
            context.AuthoringRevision != _reference.AuthoringRevision ||
            context.RuntimeHostInstanceId != _reference.RuntimeHostInstanceId)
        {
            _armed = false;
            _output = null;
            Changed(nameof(OutputEvidence));
            Status = "STALE";
            Detail = "Saved revision, Preview route or RuntimeHost instance changed. RELEASE and reprepare.";
            return;
        }
        _take = new(SequenceOutputContract.Version, _reference, _source, _revision, Guid.NewGuid());
        _armed = true; Status = "TAKE ARMED";
        Detail = "Only an explicit TAKE mutates Program. This transaction is single-use.";
    });

    public Task TakeAsync() => RunAsync(async ct =>
    {
        if (Status != "TAKE ARMED" || !_armed || _submitted ||
            _take is null || !RealPreview || _outputClient is null || _preparation is null) return;
        var command = _take;
        // A restart can preserve apparent routing/revision while retiring its
        // native owner. Recheck the actual Runtime epoch before first submission.
        var context = await _preparation.ContextAsync(ct);
        if (_reference is null ||
            context.RuntimeHostInstanceId != _reference.RuntimeHostInstanceId ||
            context.AuthoringRevision != _reference.AuthoringRevision || !RealPreview)
        {
            _armed = false;
            _output = null;
            Changed(nameof(OutputEvidence));
            Status = "STALE";
            Detail = "Saved revision or RuntimeHost instance changed before TAKE. No mutation was sent.";
            return;
        }
        _armed = false;
        // Never retry an ambiguous authority mutation, even if the IPC reply is lost.
        _submitted = true;
        Status = "TAKE PENDING"; Detail = "Await committed Program sink confirmation.";
        try { RecordOutput(await _outputClient.TakeAsync(command, ct)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Status = "TAKE UNCONFIRMED";
            Detail = "Response uncertain (" + error.GetType().Name +
                "). Synchronize and explicitly RECOVER the same transaction; do not retake.";
        }
    });

    public Task RecoverAsync() => RunAsync(async ct =>
    {
        if (!_submitted || _take is not { } command || _reference is null ||
            !SavedOwner || _authoring is null || _outputClient is null) return;
        var live = _authority();
        var saved = await _authoring.GetAsync(_reference.SequenceId, ct);
        if (!live.Connected || live.Stale || live.Busy || !live.Ready ||
            !saved.Validation.IsValid || saved.AuthoringRevision != _reference.AuthoringRevision)
        {
            Status = "STALE"; Detail = "Recovery requires saved revision and synchronized authority."; return;
        }
        if (live.Revision != checked(command.ExpectedProductionRevision + 1) ||
            !live.ProgramSourceId.Equals(command.PreviewSourceId, StringComparison.OrdinalIgnoreCase))
        {
            Status = "TAKE UNCONFIRMED";
            Detail = "The exact Program successor is not committed. Never replay TAKE.";
            return;
        }
        RecordOutput(await _outputClient.RecoverAsync(command, ct));
    });

    private void RecordOutput(SequenceOutputObservation evidence)
    {
        if (_reference is null || evidence.Preparation != _reference ||
            !evidence.PreviewSourceId.Equals(_source, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native output observation belongs to another prepared source.");
        _output = evidence;
        Changed(nameof(OutputEvidence));
        Detail = evidence.FailureCode is null ? evidence.Detail : evidence.FailureCode + ": " + evidence.Detail;
        if (_submitted && _take is { } take)
        {
            Status = evidence.OutputState == "PROGRAM_CONFIRMED" &&
                evidence.PublishedSourceFrames > 0 && evidence.LastProgramSlot is not null &&
                evidence.ProductionRevision == checked(take.ExpectedProductionRevision + 1)
                ? "PROGRAM CONFIRMED" : evidence.OutputState is "STALE" or "FAILED"
                ? evidence.OutputState : "TAKE PENDING";
        }
        else if (RealPreview)
            Status = _armed && _take is not null ? "TAKE ARMED" : "PREVIEW READY";
        else
        {
            // A lost Preview source/sink observation revokes any unsubmitted
            // TAKE arm. Regaining actual output requires an explicit new ARM.
            _armed = false;
            _take = null;
            Status = evidence.OutputState is "STALE" or "FAILED" ? evidence.OutputState :
                "PREVIEW " + evidence.OutputState + " · UNCONFIRMED";
        }
    }

    private void ClearOwner()
    {
        _reference = null; _shuttle = null; _output = null; _take = null;
        _source = ""; _revision = 0;
        _activated = false; _armed = false; _submitted = false;
        NotifyEvidence();
    }

    private void NotifyEvidence()
    {
        Changed(nameof(NativeOwner)); Changed(nameof(ShuttleEvidence)); Changed(nameof(OutputEvidence));
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SequenceTimelineEditorViewModel.Snapshot) or
            nameof(SequenceTimelineEditorViewModel.GestureLabel) &&
            _reference is not null && !SavedOwner)
        {
            _armed = false;
            Status = "STALE"; Detail = "Saved revision or authoring gesture changed. Reconcile playout.";
        }
        RaiseCommands();
    }

    private void AuthorityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperatorViewModel.IsConnected) or nameof(OperatorViewModel.IsStale) or
            nameof(OperatorViewModel.IsBusy) or nameof(OperatorViewModel.ProgramSafety) or
            nameof(OperatorViewModel.RuntimeStatus) or nameof(OperatorViewModel.RevisionLabel) or
            nameof(OperatorViewModel.PreviewSourceId) or nameof(OperatorViewModel.ProgramSourceId))
        {
            if (_reference is not null && !_submitted && !CurrentPreview)
            {
                _armed = false;
                Status = "STALE";
                Detail = "Confirmed Preview or production revision changed. Reprepare before Take.";
            }
            RaiseCommands();
        }
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { PrepareCommand, CancelCommand, ActivatePreviewCommand,
            ObserveCommand, CueCommand, PlayCommand, PauseCommand, StopCommand,
            ArmCommand, TakeCommand, RecoverCommand })
            if (command is AsyncRelayCommand relay) relay.RaiseCanExecuteChanged();
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _editor.PropertyChanged -= EditorChanged;
        if (_authorityNotifications is not null)
            _authorityNotifications.PropertyChanged -= AuthorityChanged;
        _lifetime.Dispose();
    }
}
