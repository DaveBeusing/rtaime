// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Control.Contracts;

namespace rtaime.Operator;

public sealed record OutputCanvasInputChoice(string Label, OutputCanvasInput Input);
public sealed record OutputCanvasTargetChoice(string Label, OutputCanvasTarget Target);
public sealed record OutputCanvasSurfaceChoice(Guid Id, string Label);

public sealed class OutputCanvasNumericEntry : INotifyPropertyChanged
{
    private string _value;
    public OutputCanvasNumericEntry(string key, string label, double value)
    { Key = key; Label = label; _value = value.ToString("G17", CultureInfo.InvariantCulture); }
    public string Key { get; }
    public string Label { get; }
    public string Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One Output workspace draft and one Client transport. Saved authoring and
/// measured Runtime execution are never inferred from one another.
/// </summary>
public sealed class OutputCanvasEditorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly OperatorViewModel _owner;
    private readonly IOutputCanvasGateway? _gateway;
    private OutputCanvasDraftSession? _session;
    private OutputCanvasActivationResult? _evidence;
    private OutputCanvasDefinition? _selectedCanvas;
    private OutputCanvasSurfaceChoice? _selectedSurface;
    private OutputCanvasInputChoice? _selectedInput;
    private OutputCanvasTargetChoice? _selectedTarget;
    private string _status = "Connect to ControlHost to read saved canvas configuration.";
    private string _resultDetail = "Local geometry is not Program output.";
    private bool _busy, _disposed, _rebuilding, _savedConflict, _observing, _awaitingBoundary, _saveOutcomeUnknown, _applyOutcomeUnknown, _unknownDisable, _showGeometryDetails;
    private ulong? _validatedSavedRevision;
    private ulong? _validatedDraftRevision;
    private ulong? _requestedRevision;
    private Guid _pendingSaveTransaction;
    private DateTimeOffset _nextObservationAt = DateTimeOffset.MinValue;
    private OutputCanvasQuad? _previewQuad;
    private double _zoom = 1;
    private double _panX, _panY;
    private bool _snapEnabled = true, _showGuides = true, _showSafeAreas;
    private OutputCanvasPoint? _pointerPosition;
    private bool _workspaceVisible;

    internal OutputCanvasEditorViewModel(OperatorViewModel owner, IOutputCanvasGateway? gateway = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _gateway = gateway ?? (owner.Client is { } client ? new OutputCanvasGateway(client) : null);
        Canvases = new();
        Surfaces = new();
        Inputs = new();
        Targets = new();
        Fields = new();
        Issues = new();
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _gateway is not null && !_busy);
        DiscardAndReloadCommand = new AsyncRelayCommand(() => ReloadAsync(discardDraft: true), () => _gateway is not null && !_busy);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => CanSubmit);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSubmit && IsDirty && !_savedConflict && !_saveOutcomeUnknown && !_applyOutcomeUnknown &&
            _validatedDraftRevision == _session?.Draft.EditRevision);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanSubmit && !IsDirty && !_savedConflict && !_awaitingBoundary && !_applyOutcomeUnknown &&
            !IsActiveMappingConfirmed && _validatedSavedRevision == _session?.Saved.EditRevision);
        DisableCommand = new AsyncRelayCommand(DisableAsync, () => CanSubmit && !_applyOutcomeUnknown && !_saveOutcomeUnknown &&
            (_evidence?.ActiveRenderRevision is not null || _awaitingBoundary));
        ReconcileCommand = new AsyncRelayCommand(ReconcileAsync, () => _gateway is not null && !_busy &&
            _applyOutcomeUnknown && _owner.IsConnected && !_owner.IsStale);
        UndoCommand = new AsyncRelayCommand(() => { Undo(); return Task.CompletedTask; }, () => CanEdit && _session?.CanUndo == true);
        RedoCommand = new AsyncRelayCommand(() => { Redo(); return Task.CompletedTask; }, () => CanEdit && _session?.CanRedo == true);
        AddCanvasCommand = new AsyncRelayCommand(() => { AddCanvas(); return Task.CompletedTask; }, () => CanEdit &&
            _session!.Draft.Canvases.Length < OutputCanvasContract.MaximumCanvases);
        RemoveCanvasCommand = new AsyncRelayCommand(() => { RemoveCanvas(); return Task.CompletedTask; }, () => CanEdit && SelectedCanvas is not null);
        AddSurfaceCommand = new AsyncRelayCommand(() => { AddSurface(); return Task.CompletedTask; }, () => CanEdit && SelectedCanvas is not null &&
            _session!.Draft.Surfaces.Length < OutputCanvasContract.MaximumSurfaces);
        RemoveSurfaceCommand = new AsyncRelayCommand(() => { RemoveSurface(); return Task.CompletedTask; }, () => CanEdit && SelectedSurfaceId is not null);
        BindProgramCommand = new AsyncRelayCommand(() => { BindProgram(); return Task.CompletedTask; }, () => CanEdit &&
            SelectedCanvas is not null && SelectedTarget?.Target.RoleId == "program");
        ApplyNumericCommand = new AsyncRelayCommand(() => { ApplyNumeric(); return Task.CompletedTask; }, () => CanEdit && SelectedSurfaceId is not null);
        FitCommand = new AsyncRelayCommand(() => { Fit(); return Task.CompletedTask; });
        ZoomInCommand = new AsyncRelayCommand(() => { Zoom = Math.Min(16, Zoom * 1.25); return Task.CompletedTask; });
        ZoomOutCommand = new AsyncRelayCommand(() => { Zoom = Math.Max(.1, Zoom / 1.25); return Task.CompletedTask; });
        ToggleGeometryDetailsCommand = new AsyncRelayCommand(() => { ShowGeometryDetails = !ShowGeometryDetails; return Task.CompletedTask; });
        AlignHorizontalCommand = new AsyncRelayCommand(() => { AlignSelected(true, false); return Task.CompletedTask; },
            () => CanEdit && CurrentSurface is not null);
        AlignVerticalCommand = new AsyncRelayCommand(() => { AlignSelected(false, true); return Task.CompletedTask; },
            () => CanEdit && CurrentSurface is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<OutputCanvasDefinition> Canvases { get; }
    public ObservableCollection<OutputCanvasSurfaceChoice> Surfaces { get; }
    public ObservableCollection<OutputCanvasInputChoice> Inputs { get; }
    public ObservableCollection<OutputCanvasTargetChoice> Targets { get; }
    public ObservableCollection<OutputCanvasNumericEntry> Fields { get; }
    public ObservableCollection<OutputCanvasIssue> Issues { get; }
    public IReadOnlyList<string> BlendCurves { get; } = ["smoothstep", "linear"];
    public ICommand ReloadCommand { get; }
    public ICommand DiscardAndReloadCommand { get; }
    public ICommand ValidateCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand ReconcileCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand AddCanvasCommand { get; }
    public ICommand RemoveCanvasCommand { get; }
    public ICommand AddSurfaceCommand { get; }
    public ICommand RemoveSurfaceCommand { get; }
    public ICommand BindProgramCommand { get; }
    public ICommand ApplyNumericCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ToggleGeometryDetailsCommand { get; }
    public ICommand AlignHorizontalCommand { get; }
    public ICommand AlignVerticalCommand { get; }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string ResultDetail { get => _resultDetail; private set => Set(ref _resultDetail, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) RaiseCommandState(); } }
    public bool IsDirty => _session?.IsDirty == true;
    public bool HasSavedConflict => _savedConflict;
    public bool SaveOutcomeUnconfirmed => _saveOutcomeUnknown;
    public bool ApplyOutcomeUnconfirmed => _applyOutcomeUnknown;
    public bool IsDraftValidated => IsDirty && _validatedDraftRevision == _session?.Draft.EditRevision;
    public bool IsActiveMappingConfirmed => !_owner.IsStale && _owner.IsConnected && !_awaitingBoundary &&
        !_applyOutcomeUnknown && _session is not null &&
        _evidence?.ActivationState == "ACTIVE" &&
        _evidence.ActiveRenderRevision == _session.Saved.EditRevision &&
        ObservedTargetMatchesSavedProgram;

    private bool ObservedTargetMatchesSavedProgram => _session is { } session &&
        _evidence is { TargetId: { Length: > 0 }, TargetWidth: > 0, TargetHeight: > 0 } observed &&
        session.Saved.Bindings.Any(binding => binding.Enabled &&
            Targets.Any(choice => choice.Target.RoleId == "program" &&
                choice.Target.ProviderId == binding.ProviderId &&
                choice.Target.TargetId == binding.TargetId &&
                choice.Target.ProfileId == binding.ProfileId &&
                choice.Target.TargetId == observed.TargetId &&
                choice.Target.Width == observed.TargetWidth &&
                choice.Target.Height == observed.TargetHeight));
    public string WorkflowStage => _savedConflict ? "CONFLICT · REVIEW AUTHORITY" :
        _saveOutcomeUnknown ? "SAVE UNKNOWN · RECONCILE" :
        _applyOutcomeUnknown ? "APPLY UNKNOWN · RECONCILE" :
        IsDirty ? (IsDraftValidated ? "DRAFT VALIDATED · SAVE REQUIRED" : "DRAFT · VALIDATE REQUIRED") :
        _awaitingBoundary ? "APPLY ACCEPTED · RUNTIME CONFIRMATION PENDING" :
        IsActiveMappingConfirmed ? "RUNTIME CONFIRMED · ACTIVE MAPPING" :
        _evidence?.ActivationState == "ACTIVE" ? "RUNTIME ACTIVE REPORTED · TARGET OR REVISION UNVERIFIED" :
        _session is null ? "NO SAVED CANVAS" :
        _validatedSavedRevision == _session.Saved.EditRevision ? "SAVED VALIDATED · READY TO APPLY" :
        "SAVED · VALIDATE BEFORE APPLY";
    // Visibility is presentation-only. Hiding the canvas preserves its current
    // unsaved draft, transaction fence and Runtime confirmation evidence.
    public bool IsWorkspaceVisible => _workspaceVisible;
    internal void SetWorkspaceVisible(bool visible) => Set(ref _workspaceVisible, visible, nameof(IsWorkspaceVisible));
    public bool CanEdit => _session is not null && !_busy && !_saveOutcomeUnknown && !_applyOutcomeUnknown;
    public bool CanSubmit => _session is not null && !_busy && _gateway is not null &&
        _owner.IsConnected && !_owner.IsStale && !_owner.IsBusy &&
        _owner.ProgramSafety != OperatorProgramSafetyStates.Blocked &&
        string.Equals(_owner.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase);
    public string SavedRevision => _session?.Saved.EditRevision.ToString(CultureInfo.InvariantCulture) ?? "UNAVAILABLE";
    public string DraftRevision => _session?.Draft.EditRevision.ToString(CultureInfo.InvariantCulture) ?? "UNAVAILABLE";
    public string ActiveRevision => _evidence?.ActiveRenderRevision?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED";
    public string PendingRevision => _evidence?.PendingRenderRevision?.ToString(CultureInfo.InvariantCulture) ??
        _requestedRevision?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string TargetEligibility => SelectedTarget?.Target is { } target
        ? $"CONTROL-AUTHORIZED {target.RoleId?.ToUpperInvariant() ?? "OUTPUT"} · {target.ProviderId} · {target.TargetId} · " +
          $"{target.Width}×{target.Height} · {target.ProfileId} · PHYSICAL SCAN-OUT UNVERIFIED"
        : "NO CONTROL-AUTHORIZED OUTPUT TARGET · DO NOT APPLY";
    public string ActivationState => _evidence is null ? "UNVERIFIED" :
        _evidence.ActivationState == "ACTIVE" && !IsActiveMappingConfirmed
            ? "ACTIVE REPORTED · UNVERIFIED MATCH" : _evidence.ActivationState;
    public string SavedTargetEvidence
    {
        get
        {
            var binding = _session?.Saved.Bindings.FirstOrDefault(b => b.Enabled);
            if (binding is null) return "SAVED BINDING · NONE · NO ACTIVE TARGET IMPLIED";
            var authorised = Targets.FirstOrDefault(t => t.Target.ProviderId == binding.ProviderId &&
                t.Target.TargetId == binding.TargetId && t.Target.ProfileId == binding.ProfileId);
            var declaredSize = authorised is null ? "NO CURRENT AUTHORIZED SIZE" :
                $"{authorised.Target.Width}×{authorised.Target.Height}";
            return $"SAVED TARGET · {binding.ProviderId} / {binding.TargetId} · " +
                $"DECLARED PROFILE {binding.ProfileId} · {declaredSize} · PHYSICAL UNVERIFIED";
        }
    }
    public string ObservedTargetEvidence => _evidence is not { } observation
        ? "RUNTIME TARGET · UNVERIFIED · NO CURRENT OBSERVATION"
        : $"RUNTIME {observation.ActivationState} · TARGET {observation.TargetId ?? "UNVERIFIED"} · " +
          $"{(observation.TargetWidth > 0 && observation.TargetHeight > 0 ? $"{observation.TargetWidth}×{observation.TargetHeight}" : "SIZE UNVERIFIED")} · " +
          $"RENDER SEQ {observation.LastRenderedSequence?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED"} · " +
          (IsActiveMappingConfirmed ? "EXACT SAVED REVISION / TARGET MATCH" :
              "NOT VERIFIED AS CURRENT AUTHORIZED TARGET");
    public string ActivationFailure => _evidence?.FailureCode ?? "—";
    public string RuntimeOutputTarget => _evidence?.TargetId ?? "UNVERIFIED";
    public string RuntimeOutputSize => _evidence is { TargetWidth: > 0, TargetHeight: > 0 } evidence
        ? $"{evidence.TargetWidth}×{evidence.TargetHeight}" : "UNVERIFIED";
    public string AppliedSource => _evidence?.AppliedSourceId ?? "UNVERIFIED";
    public string RenderedSequence => _evidence?.LastRenderedSequence?.ToString(CultureInfo.InvariantCulture) ?? "UNVERIFIED";
    public string EditingSafety => _savedConflict ? "SAVED REVISION CHANGED · DISCARD & RELOAD REQUIRED" :
        _saveOutcomeUnknown ? "SAVE UNCONFIRMED · RELOAD TO RECONCILE" :
        _applyOutcomeUnknown ? "OUTPUT ACTIVATION UNKNOWN · RUNTIME CONFIRMATION REQUIRED" :
        CanSubmit ? "CONTROL / RUNTIME AVAILABLE" :
        _owner.IsStale ? "STALE AUTHORITY · EDIT DRAFT ONLY" :
        !_owner.IsConnected ? "OFFLINE · EDIT DRAFT ONLY" :
        _owner.IsBusy ? "CONTROL BUSY · EDIT DRAFT ONLY" : "SAFE READ-ONLY · NO APPLY";
    public string DraftSafety => IsDirty ? "DRAFT ONLY · NOT SAVED OR LIVE" :
        "SAVED CONFIGURATION · NOT NECESSARILY ACTIVE";
    public bool IsSurfaceEnabled
    {
        get => CurrentSurface?.Enabled == true;
        set
        {
            if (_rebuilding || CurrentSurface is null || CurrentSurface.Enabled == value) return;
            ChangeSurface(s => s with { Enabled = value }, "Surface visibility is a local draft.");
        }
    }
    public string SelectedBlendCurve
    {
        get => CurrentSurface?.EdgeBlend?.Curve ?? "smoothstep";
        set
        {
            if (_rebuilding || CurrentSurface is null || SelectedBlendCurve == value) return;
            if (value is not ("linear" or "smoothstep")) return;
            ChangeSurface(s => s with { EdgeBlend = (s.EdgeBlend ?? new(0,0,0,0)) with { Curve = value } },
                "Blend curve changed in local draft.");
        }
    }

    public OutputCanvasDefinition? SelectedCanvas
    {
        get => _selectedCanvas;
        set
        {
            if (_rebuilding || !Set(ref _selectedCanvas, value)) return;
            RefreshSurfaces(null);
            OnPropertyChanged(nameof(CanvasWidth));
            OnPropertyChanged(nameof(CanvasHeight));
            OnPropertyChanged(nameof(VisibleSurfaces));
            OnPropertyChanged(nameof(SelectedGeometryEvidence));
            OnPropertyChanged(nameof(PointerCoordinates));
            RaiseCommandState();
        }
    }
    public OutputCanvasSurfaceChoice? SelectedSurface
    {
        get => _selectedSurface;
        set
        {
            if (_rebuilding || !Set(ref _selectedSurface, value)) return;
            LoadFields();
            SelectInput();
            OnPropertyChanged(nameof(SelectedSurfaceId));
            OnPropertyChanged(nameof(IsSurfaceEnabled));
            OnPropertyChanged(nameof(SelectedBlendCurve));
            OnPropertyChanged(nameof(SelectedGeometryEvidence));
            RaiseCommandState();
        }
    }
    public OutputCanvasInputChoice? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (_rebuilding || !Set(ref _selectedInput, value) || value is null || CurrentSurface is null) return;
            ChangeSurface(s => s with { Input = value.Input }, "Surface input changed in local draft.");
        }
    }
    public OutputCanvasTargetChoice? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (_rebuilding) return;
            if (Set(ref _selectedTarget, value))
            {
                OnPropertyChanged(nameof(TargetEligibility));
                RaiseCommandState();
            }
        }
    }
    public Guid? SelectedSurfaceId => _selectedSurface?.Id;
    public int CanvasWidth => SelectedCanvas?.Width ?? 0;
    public int CanvasHeight => SelectedCanvas?.Height ?? 0;
    public IReadOnlyList<OutputCanvasSurface> VisibleSurfaces => _session?.Draft.Surfaces
        .Where(s => s.CanvasId == SelectedCanvas?.CanvasId && s.Enabled)
        .OrderBy(s => s.ZOrder).ToArray() ?? [];
    public OutputCanvasQuad? PreviewQuad { get => _previewQuad; private set => Set(ref _previewQuad, value); }

    public double Zoom
    {
        get => _zoom;
        set { if (double.IsFinite(value)) Set(ref _zoom, Math.Clamp(value, .1, 16)); }
    }
    public double PanX { get => _panX; private set => Set(ref _panX, value); }
    public double PanY { get => _panY; private set => Set(ref _panY, value); }
    public bool SnapEnabled { get => _snapEnabled; set => Set(ref _snapEnabled, value); }
    public bool ShowGuides { get => _showGuides; set => Set(ref _showGuides, value); }
    public bool ShowSafeAreas { get => _showSafeAreas; set => Set(ref _showSafeAreas, value); }

    // Stage pixels and physical target pixels are different coordinate systems.
    // Neither pointer inspection nor local alignment sends a Control mutation.
    public string PointerCoordinates => _pointerPosition is not { } p || CanvasWidth <= 0 || CanvasHeight <= 0
        ? "POINTER · hover the stage for logical / normalized destination coordinates"
        : $"POINTER · X {p.X.ToString("0.0", CultureInfo.InvariantCulture)} px · " +
          $"Y {p.Y.ToString("0.0", CultureInfo.InvariantCulture)} px · " +
          $"U {(p.X / CanvasWidth).ToString("0.0000", CultureInfo.InvariantCulture)} · " +
          $"V {(p.Y / CanvasHeight).ToString("0.0000", CultureInfo.InvariantCulture)}" +
          (p.X < 0 || p.X > CanvasWidth || p.Y < 0 || p.Y > CanvasHeight ? " · OFF-CANVAS" : "");

    public string SelectedGeometryEvidence
    {
        get
        {
            if (CurrentSurface is not { } surface || CanvasWidth <= 0 || CanvasHeight <= 0)
                return "NO SELECTED SURFACE · destination coordinates unavailable";
            var q = surface.Destination;
            var centerX = (q.TopLeft.X + q.TopRight.X + q.BottomRight.X + q.BottomLeft.X) / 4;
            var centerY = (q.TopLeft.Y + q.TopRight.Y + q.BottomRight.Y + q.BottomLeft.Y) / 4;
            return $"DESTINATION CENTER · X {centerX.ToString("0.##", CultureInfo.InvariantCulture)} px / " +
                $"Y {centerY.ToString("0.##", CultureInfo.InvariantCulture)} px · " +
                $"U {(centerX / CanvasWidth).ToString("0.0000", CultureInfo.InvariantCulture)} / " +
                $"V {(centerY / CanvasHeight).ToString("0.0000", CultureInfo.InvariantCulture)} · " +
                "normalized to LOGICAL CANVAS, not crop or device";
        }
    }

    public void SetPointerPosition(OutputCanvasPoint? position)
    {
        if (position is { } p && (!double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            position = null;
        if (_pointerPosition == position) return;
        _pointerPosition = position;
        OnPropertyChanged(nameof(PointerCoordinates));
    }
    public bool ShowGeometryDetails { get => _showGeometryDetails; set => Set(ref _showGeometryDetails, value); }
    public string? SelectedSurfaceIssue => Issues.FirstOrDefault(issue =>
        SelectedSurfaceId is { } id && issue.Path.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase))?.Message;

    private OutputCanvasSurface? CurrentSurface => _session?.Draft.Surfaces
        .FirstOrDefault(s => s.SurfaceId == SelectedSurfaceId);

    public void Dispose() { _disposed = true; _awaitingBoundary = false; }

    // Invoked from the existing Operator state projection; no separate timer or poller.
    internal void OnControlChanged(string? property)
    {
        if (_disposed) return;
        if (property is nameof(OperatorViewModel.IsConnected) or nameof(OperatorViewModel.IsStale))
        {
            if (_owner.IsConnected && !_owner.IsStale)
                _ = ReloadAsync();
            if (!_owner.IsConnected || _owner.IsStale)
            {
                _evidence = null;
                PublishEvidence();
            }
        }
        else if (property == nameof(OperatorViewModel.HealthObserved) && _awaitingBoundary &&
            DateTimeOffset.UtcNow >= _nextObservationAt)
        {
            _nextObservationAt = DateTimeOffset.UtcNow.AddSeconds(1);
            _ = ObserveBoundaryAsync();
        }
        RaiseCommandState();
        OnPropertyChanged(nameof(EditingSafety));
    }

    public async Task ReloadAsync() => await ReloadAsync(discardDraft: false);

    internal async Task ReloadAsync(bool discardDraft)
    {
        if (_gateway is null || _busy || _disposed) return;
        IsBusy = true;
        try
        {
            var snapshot = await _gateway.GetAsync();
            if (_disposed) return;
            var retainsDraft = _session is { IsDirty: true } && !discardDraft;
            if (retainsDraft && _session!.MatchesDraftSave(snapshot.Saved))
            {
                _session.ReplaceSaved(snapshot.Saved);
                _savedConflict = false;
                _saveOutcomeUnknown = false;
                _pendingSaveTransaction = Guid.Empty;
                _validatedSavedRevision = null;
                _validatedDraftRevision = null;
                Rebuild();
                Status = "The saved revision now matches the previously unconfirmed local draft. Validate before APPLY.";
            }
            else if (retainsDraft)
            {
                _savedConflict = _session!.Saved.ConfigurationId != snapshot.Saved.ConfigurationId ||
                    _session.Saved.EditRevision != snapshot.SavedRevision;
                _saveOutcomeUnknown = false;
                _validatedSavedRevision = null;
                _validatedDraftRevision = null;
                _pendingSaveTransaction = _savedConflict ? Guid.Empty : _pendingSaveTransaction;
                Status = _savedConflict
                    ? "Control saved revision changed. Your local draft is preserved but cannot be saved; explicitly DISCARD & RELOAD to review current authority."
                    : "Saved revision verified; unsaved local geometry preserved. Validate and SAVE explicitly.";
            }
            else
            {
                _session = new OutputCanvasDraftSession(snapshot.Saved);
                _savedConflict = false;
                _saveOutcomeUnknown = false;
                _validatedSavedRevision = null;
                _validatedDraftRevision = null;
                _pendingSaveTransaction = Guid.Empty;
                Rebuild();
                Status = "Saved canvas loaded. Editing changes only the local geometry draft.";
            }
            try { SetEvidence(await _gateway.EvidenceAsync()); }
            catch (Exception ex) when (RemoteFailure(ex)) { _evidence = null; PublishEvidence(); }
            ResultDetail = $"{snapshot.AvailableTargets.Length} Control-authorized Runtime target(s); hardware qualification is independent.";
            RebuildTargets(snapshot.AvailableTargets);
        }
        catch (Exception exception) when (RemoteFailure(exception))
        {
            Status = "Cannot read the saved canvas: " + exception.Message;
            _evidence = null;
            PublishEvidence();
        }
        finally { IsBusy = false; RaiseAll(); }
    }

    public async Task ValidateAsync()
    {
        if (!CanSubmit || _gateway is null || _session is null) return;
        IsBusy = true;
        try
        {
            var validation = await _gateway.ValidateAsync(_session.Draft);
            if (_disposed) return;
            PublishIssues(validation.Issues);
            if (!validation.IsValid)
            {
                _validatedSavedRevision = null;
                _validatedDraftRevision = null;
                if (validation.Issues.Any(issue => issue.Code == "canvas.revision_conflict")) _savedConflict = true;
                Status = $"Validation rejected {validation.Issues.Length} issue(s). Inspect the affected surface.";
            }
            else
            {
                if (IsDirty) _validatedDraftRevision = _session.Draft.EditRevision;
                else _validatedSavedRevision = _session.Saved.EditRevision;
                Status = IsDirty ? "Draft validated. SAVE to persist; Program remains unchanged." :
                    "Saved geometry validated for current Control authority. APPLY is a separate operation.";
            }
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            _validatedSavedRevision = null; _validatedDraftRevision = null;
            Status = "Validation unavailable: " + ex.Message;
        }
        finally { IsBusy = false; RaiseAll(); }
    }

    public async Task SaveAsync()
    {
        if (!CanSubmit || _gateway is null || _session is null || !IsDraftValidated ||
            _savedConflict || _saveOutcomeUnknown || _applyOutcomeUnknown) return;
        IsBusy = true;
        try
        {
            var validation = await _gateway.ValidateAsync(_session.Draft);
            if (_disposed) return;
            PublishIssues(validation.Issues);
            if (!validation.IsValid)
            {
                if (validation.Issues.Any(issue => issue.Code == "canvas.revision_conflict")) _savedConflict = true;
                Status = "Save blocked by Control validation."; return;
            }
            _pendingSaveTransaction = _pendingSaveTransaction == Guid.Empty ? Guid.NewGuid() : _pendingSaveTransaction;
            var request = _session.CreateSaveRequest(_pendingSaveTransaction);
            var result = await _gateway.SaveAsync(request);
            if (_disposed) return;
            PublishIssues(result.Validation.Issues);
            if (!result.Saved || !result.Validation.IsValid)
            {
                if (result.Validation.Issues.Any(issue => issue.Code == "canvas.revision_conflict")) _savedConflict = true;
                _pendingSaveTransaction = Guid.Empty;
                Status = "Save rejected. Keep or explicitly discard this local draft.";
                return;
            }
            _session.ReplaceSaved(result.Configuration);
            // The new saved revision still needs a separate authoritative validation
            // before APPLY; a successful durable save alone is not Runtime eligibility.
            _validatedSavedRevision = null;
            _validatedDraftRevision = null;
            _pendingSaveTransaction = Guid.Empty;
            _saveOutcomeUnknown = false;
            _savedConflict = false;
            _requestedRevision = null;
            Rebuild();
            Status = $"Saved revision {result.SavedRevision}. Mapping is NOT active until explicit APPLY.";
            ResultDetail = "Stored configuration is authoritative; active Runtime revision is independent.";
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            _validatedSavedRevision = null;
            _validatedDraftRevision = null;
            _saveOutcomeUnknown = _pendingSaveTransaction != Guid.Empty;
            Status = "Save outcome unconfirmed. RELOAD to reconcile before any retry: " + ex.Message;
        }
        finally { IsBusy = false; RaiseAll(); }
    }

    public async Task ApplyAsync()
    {
        if (!CanSubmit || _gateway is null || _session is null || IsDirty || _savedConflict ||
            IsActiveMappingConfirmed || _applyOutcomeUnknown || _awaitingBoundary ||
            _validatedSavedRevision != _session.Saved.EditRevision) return;
        IsBusy = true;
        try
        {
            var result = await _gateway.ActivateAsync(
                new(_session.Saved.ConfigurationId, _session.Saved.EditRevision, Enabled: true));
            if (_disposed) return;
            SetEvidence(result);
            _awaitingBoundary = result.ActivationState == "PENDING_BOUNDARY";
            _applyOutcomeUnknown = false;
            _unknownDisable = false;
            _requestedRevision = _awaitingBoundary ? _session.Saved.EditRevision : null;
            _nextObservationAt = DateTimeOffset.MinValue;
            Status = _awaitingBoundary
                ? "Activation accepted for the next Runtime frame boundary. Await confirmed execution evidence."
                : "Activation not committed: " + (result.FailureCode ?? result.ActivationState);
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            // A lost APPLY reply is not a rejection. Keep observing this exact
            // revision through the existing health refresh; suppress retries.
            _applyOutcomeUnknown = true;
            _unknownDisable = false;
            _awaitingBoundary = true;
            _requestedRevision = _session.Saved.EditRevision;
            _nextObservationAt = DateTimeOffset.MinValue;
            _evidence = null; PublishEvidence();
            Status = "Activation outcome UNKNOWN. Refresh Runtime evidence before further mutations: " + ex.Message;
        }
        finally { IsBusy = false; RaiseAll(); }
    }

    public async Task DisableAsync()
    {
        if (!CanSubmit || _gateway is null || _session is null ||
            _applyOutcomeUnknown || _saveOutcomeUnknown) return;
        IsBusy = true;
        try
        {
            var result = await _gateway.ActivateAsync(
                new(_session.Saved.ConfigurationId, _session.Saved.EditRevision, Enabled: false));
            if (_disposed) return;
            SetEvidence(result);
            _awaitingBoundary = result.ActivationState == "PENDING_BOUNDARY";
            _applyOutcomeUnknown = false;
            _unknownDisable = false;
            _requestedRevision = null;
            _nextObservationAt = DateTimeOffset.MinValue;
            Status = _awaitingBoundary ? "Disable requested; the active Program route remains until Runtime confirms the next boundary."
                : "Disable not committed: " + (result.FailureCode ?? result.ActivationState);
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            // A missing response must not turn into an automatic second disable.
            _applyOutcomeUnknown = true;
            _unknownDisable = true;
            _awaitingBoundary = true;
            _requestedRevision = null;
            _nextObservationAt = DateTimeOffset.MinValue;
            _evidence = null; PublishEvidence();
            Status = "Disable outcome UNKNOWN. Inspect Runtime evidence before further mutations: " + ex.Message;
        }
        finally { IsBusy = false; RaiseAll(); }
    }

    internal async Task ObserveBoundaryAsync()
    {
        if (_gateway is null || _disposed || _observing || !_awaitingBoundary ||
            !_owner.IsConnected || _owner.IsStale) return;
        _observing = true;
        try
        {
            var evidence = await _gateway.EvidenceAsync();
            if (_disposed) return;
            SetEvidence(evidence);
            if (_applyOutcomeUnknown)
            {
                // No observation alone can causally prove a lost activation reply.
                // Stop passive probing after a plausible terminal state; require
                // an explicit read-only reconcile of both saved and Runtime owners.
                if (ObservedIntendedTerminal(evidence)) _awaitingBoundary = false;
                Status = $"Runtime reports {evidence.ActivationState}; uncertain " +
                    (_unknownDisable ? "DISABLE" : "APPLY") +
                    " remains UNKNOWN. Request explicit read-only RECONCILE; never retry implicitly.";
            }
            else if (evidence.ActivationState != "PENDING_BOUNDARY")
            {
                _awaitingBoundary = false;
                _requestedRevision = null;
                Status = evidence.ActivationState == "ACTIVE" &&
                    evidence.ActiveRenderRevision == _session?.Saved.EditRevision
                    ? $"Runtime confirmed mapped Program revision {evidence.ActiveRenderRevision}."
                    : evidence.ActivationState == "UNMAPPED"
                        ? "Runtime confirmed unchanged legacy Program output."
                        : "Runtime mapping state: " + evidence.ActivationState +
                          (evidence.FailureCode is null ? "" : " · " + evidence.FailureCode);
            }
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            _evidence = null; PublishEvidence();
            Status = "Output evidence unavailable: " + ex.Message;
            // Keep the pending intent unconfirmed until a fresh Runtime observation.
        }
        finally { _observing = false; RaiseAll(); }
    }

    private bool ObservedIntendedTerminal(OutputCanvasActivationResult observed) =>
        _unknownDisable
            ? observed.ActivationState == "UNMAPPED" && observed.ActiveRenderRevision is null
            : _requestedRevision is { } requested && observed.ActivationState == "ACTIVE" &&
              observed.ActiveRenderRevision == requested && ObservedTargetMatchesSavedProgram;

    /// <summary>
    /// Explicit read-only reconciliation after an uncertain activation result.
    /// Passive/potentially stale observations never unlock another mutation.
    /// </summary>
    public async Task ReconcileAsync()
    {
        if (_gateway is null || _busy || _disposed || !_applyOutcomeUnknown ||
            _session is null || !_owner.IsConnected || _owner.IsStale) return;
        IsBusy = true;
        try
        {
            var current = await _gateway.GetAsync();
            if (_disposed) return;
            if (current.Saved.ConfigurationId != _session.Saved.ConfigurationId ||
                current.SavedRevision != _session.Saved.EditRevision ||
                current.Saved.EditRevision != current.SavedRevision)
            {
                _savedConflict = true;
                Status = "RECONCILE BLOCKED · saved revision changed. Preserve local intent and explicitly reload.";
                return;
            }
            var observed = await _gateway.EvidenceAsync();
            if (_disposed) return;
            SetEvidence(observed);
            if (!ObservedIntendedTerminal(observed))
            {
                Status = "RECONCILE UNCONFIRMED · current Runtime state does not verify the requested outcome.";
                return;
            }
            _applyOutcomeUnknown = false;
            _unknownDisable = false;
            _awaitingBoundary = false;
            _requestedRevision = null;
            Status = observed.ActivationState == "ACTIVE"
                ? "RECONCILED · exact saved mapping revision and Program target currently confirmed."
                : "RECONCILED · Runtime currently reports unmapped Program output.";
        }
        catch (Exception ex) when (RemoteFailure(ex))
        {
            Status = "RECONCILE UNAVAILABLE · activation remains UNKNOWN: " + ex.Message;
        }
        finally { IsBusy = false; RaiseAll(); PublishEvidence(); }
    }

    private void SetEvidence(OutputCanvasActivationResult evidence)
    {
        _evidence = evidence;
        PublishEvidence();
    }

    private void PublishEvidence()
    {
        foreach (var name in new[] { nameof(ActiveRevision), nameof(PendingRevision),
            nameof(ActivationState), nameof(ActivationFailure), nameof(RuntimeOutputTarget),
            nameof(RuntimeOutputSize), nameof(AppliedSource), nameof(RenderedSequence),
            nameof(SavedTargetEvidence), nameof(ObservedTargetEvidence) })
            OnPropertyChanged(name);
        RaiseCommandState();
        OnPropertyChanged(nameof(WorkflowStage));
        OnPropertyChanged(nameof(IsActiveMappingConfirmed));
        OnPropertyChanged(nameof(ApplyOutcomeUnconfirmed));
    }

    private void PublishIssues(IEnumerable<OutputCanvasIssue> issues)
    {
        Issues.Clear();
        foreach (var issue in issues.Take(64)) Issues.Add(issue);
        OnPropertyChanged(nameof(SelectedSurfaceIssue));
    }

    private void RebuildTargets(IEnumerable<OutputCanvasTarget> targets)
    {
        var previous = SelectedTarget?.Target.TargetId;
        _rebuilding = true;
        Targets.Clear();
        foreach (var target in targets)
            Targets.Add(new($"{target.RoleId?.ToUpperInvariant() ?? "OUTPUT"} · {target.ProfileId} · {target.Width}×{target.Height} · {target.TargetId}",
                target));
        _selectedTarget = Targets.FirstOrDefault(t => t.Target.TargetId == previous) ??
            Targets.FirstOrDefault(t => t.Target.RoleId == "program");
        _rebuilding = false;
        OnPropertyChanged(nameof(SelectedTarget));
        OnPropertyChanged(nameof(TargetEligibility));
        RaiseCommandState();
    }

    private void Rebuild(Guid? canvasId = null, Guid? surfaceId = null)
    {
        if (_session is null) return;
        canvasId ??= _selectedCanvas?.CanvasId;
        surfaceId ??= _selectedSurface?.Id;
        _rebuilding = true;
        Canvases.Clear();
        foreach (var canvas in _session.Draft.Canvases) Canvases.Add(canvas);
        _selectedCanvas = Canvases.FirstOrDefault(c => c.CanvasId == canvasId) ?? Canvases.FirstOrDefault();
        _rebuilding = false;
        OnPropertyChanged(nameof(SelectedCanvas));
        RefreshSurfaces(surfaceId);
        RebuildInputs();
        RaiseAll();
    }

    private void RefreshSurfaces(Guid? selectedId)
    {
        _rebuilding = true;
        Surfaces.Clear();
        if (_session is not null && SelectedCanvas is not null)
        {
            foreach (var surface in _session.Draft.Surfaces
                .Where(s => s.CanvasId == SelectedCanvas.CanvasId)
                .OrderBy(s => s.ZOrder).ThenBy(s => s.SurfaceId))
                Surfaces.Add(new(surface.SurfaceId,
                    $"{(surface.Enabled ? "ON" : "OFF")} · Z{surface.ZOrder} · " +
                    (surface.Input.BusRoleId ?? surface.Input.SourceId ?? "UNASSIGNED")));
        }
        _selectedSurface = Surfaces.FirstOrDefault(s => s.Id == selectedId) ?? Surfaces.FirstOrDefault();
        _rebuilding = false;
        OnPropertyChanged(nameof(SelectedSurface));
        OnPropertyChanged(nameof(SelectedSurfaceId));
        OnPropertyChanged(nameof(VisibleSurfaces));
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(PointerCoordinates));
        OnPropertyChanged(nameof(SelectedGeometryEvidence));
        LoadFields();
        SelectInput();
        RaiseCommandState();
    }

    private void RebuildInputs()
    {
        _rebuilding = true;
        Inputs.Clear();
        Inputs.Add(new("Program bus (post-composite)", new(BusRoleId: "program")));
        if (_owner.OutputRoles.Any(r => r.RoleId == "aux"))
            Inputs.Add(new("Aux bus (governed)", new(BusRoleId: "aux")));
        foreach (var source in _owner.Sources.Where(s => Guid.TryParse(s.Id, out _)))
            Inputs.Add(new($"{source.Name} · {source.Id}", new(SourceId: source.Id)));
        _rebuilding = false;
        SelectInput();
    }

    private void SelectInput()
    {
        var input = CurrentSurface?.Input;
        _rebuilding = true;
        _selectedInput = Inputs.FirstOrDefault(i =>
            i.Input.SourceId == input?.SourceId && i.Input.BusRoleId == input?.BusRoleId);
        _rebuilding = false;
        OnPropertyChanged(nameof(SelectedInput));
        OnPropertyChanged(nameof(IsSurfaceEnabled));
        OnPropertyChanged(nameof(SelectedBlendCurve));
    }

    private void LoadFields()
    {
        var surface = CurrentSurface;
        Fields.Clear();
        if (surface is null) return;
        var q = surface.Destination;
        var crop = surface.Crop;
        var blend = surface.EdgeBlend ?? new(0,0,0,0);
        foreach (var (key,label,value) in new (string,string,double)[]
        {
            ("tlx","TL · X",q.TopLeft.X), ("tly","TL · Y",q.TopLeft.Y),
            ("trx","TR · X",q.TopRight.X), ("try","TR · Y",q.TopRight.Y),
            ("brx","BR · X",q.BottomRight.X), ("bry","BR · Y",q.BottomRight.Y),
            ("blx","BL · X",q.BottomLeft.X), ("bly","BL · Y",q.BottomLeft.Y),
            ("cx","CROP · X",crop.X), ("cy","CROP · Y",crop.Y),
            ("cw","CROP · W",crop.Width), ("ch","CROP · H",crop.Height),
            ("el","BLEND · LEFT",blend.Left), ("et","BLEND · TOP",blend.Top),
            ("er","BLEND · RIGHT",blend.Right), ("eb","BLEND · BOTTOM",blend.Bottom)
        })
            Fields.Add(new(key,label,value));
    }

    private void ApplyNumeric()
    {
        if (!CanEdit || CurrentSurface is null) return;
        var numbers = new Dictionary<string,double>(StringComparer.Ordinal);
        foreach (var field in Fields)
            if (!double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                !double.TryParse(field.Value, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                !double.IsFinite(value))
            {
                Status = $"Invalid numeric value in {field.Label}. Use a finite number.";
                return;
            }
            else numbers[field.Key] = value;

        var quad = new OutputCanvasQuad(
            new(numbers["tlx"],numbers["tly"]), new(numbers["trx"],numbers["try"]),
            new(numbers["brx"],numbers["bry"]), new(numbers["blx"],numbers["bly"]));
        var crop = new OutputCanvasCrop(numbers["cx"],numbers["cy"],numbers["cw"],numbers["ch"]);
        var blend = new OutputCanvasEdgeBlend(numbers["el"],numbers["et"],numbers["er"],numbers["eb"],
            SelectedBlendCurve);
        if (!OutputCanvasDraftSession.IsValidQuad(quad) ||
            !OutputCanvasDraftSession.IsValidCrop(crop) ||
            !OutputCanvasDraftSession.IsValidBlend(blend))
        {
            Status = "Invalid quad, crop or edge blend. Geometry must remain convex and within contract bounds.";
            return;
        }
        ChangeSurface(s => s with { Destination = quad, Crop = crop,
            EdgeBlend = blend.Left == 0 && blend.Top == 0 && blend.Right == 0 && blend.Bottom == 0
                ? null : blend }, "Numeric surface geometry committed to local draft.");
    }

    private void ChangeSurface(Func<OutputCanvasSurface,OutputCanvasSurface> edit, string explanation)
    {
        if (!CanEdit || _session is null || CurrentSurface is not { } current) return;
        try
        {
            _session.Mutate(draft => draft with { Surfaces = draft.Surfaces
                .Select(s => s.SurfaceId == current.SurfaceId ? edit(s) : s).ToArray() });
            ChangedDraft(explanation, current.SurfaceId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException)
        {
            Status = "Surface edit rejected: " + ex.Message;
        }
    }

    public void SelectSurface(Guid id)
    {
        var found = Surfaces.FirstOrDefault(s => s.Id == id);
        if (found is not null) SelectedSurface = found;
    }

    public void PreviewSurfaceQuad(Guid surfaceId, OutputCanvasQuad? quad)
    {
        if (surfaceId != SelectedSurfaceId) return;
        PreviewQuad = quad is not null && OutputCanvasDraftSession.IsValidQuad(quad) ? quad : null;
    }

    public void CommitSurfaceQuad(Guid surfaceId, OutputCanvasQuad quad)
    {
        if (surfaceId != SelectedSurfaceId) return;
        PreviewQuad = null;
        if (!OutputCanvasDraftSession.IsValidQuad(quad))
        { Status = "Invalid four-corner mapping rejected; saved and active output remain unchanged."; return; }
        ChangeSurface(s => s with { Destination = quad }, "Canvas geometry committed as local draft only.");
    }

    public void AlignSelected(bool horizontal, bool vertical)
    {
        if (!CanEdit || CurrentSurface is not { } surface || SelectedCanvas is not { } canvas ||
            (!horizontal && !vertical)) return;
        var q = surface.Destination;
        var centerX = (q.TopLeft.X + q.TopRight.X + q.BottomRight.X + q.BottomLeft.X) / 4;
        var centerY = (q.TopLeft.Y + q.TopRight.Y + q.BottomRight.Y + q.BottomLeft.Y) / 4;
        NudgeSelected(horizontal ? canvas.Width / 2d - centerX : 0,
            vertical ? canvas.Height / 2d - centerY : 0);
    }

    public void NudgeSelected(double x, double y)
    {
        if (!CanEdit || CurrentSurface is not { } s) return;
        var q = s.Destination;
        OutputCanvasPoint Move(OutputCanvasPoint p) => new(p.X+x,p.Y+y);
        CommitSurfaceQuad(s.SurfaceId, new(Move(q.TopLeft),Move(q.TopRight),
            Move(q.BottomRight),Move(q.BottomLeft)));
    }

    public void Pan(double dx, double dy)
    {
        if (double.IsFinite(dx) && double.IsFinite(dy))
        {
            PanX = Math.Clamp(PanX+dx,-32768,32768);
            PanY = Math.Clamp(PanY+dy,-32768,32768);
        }
    }

    public void Fit() { Zoom=1; PanX=0; PanY=0; }

    private void AddCanvas()
    {
        if (!CanEdit || _session is null ||
            _session.Draft.Canvases.Length >= OutputCanvasContract.MaximumCanvases) return;
        var target = SelectedTarget?.Target;
        var width = Math.Clamp(target?.Width ?? CanvasWidth, 1, OutputCanvasContract.MaximumDimension);
        var height = Math.Clamp(target?.Height ?? CanvasHeight, 1, OutputCanvasContract.MaximumDimension);
        var canvas = new OutputCanvasDefinition(Guid.NewGuid(),
            $"Canvas {_session.Draft.Canvases.Length+1}", width, height);
        try
        {
            _session.Mutate(d => d with { Canvases = [..d.Canvases, canvas] });
            ChangedDraft("Canvas added to local draft.", canvasId: canvas.CanvasId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { Status = ex.Message; }
    }

    private void RemoveCanvas()
    {
        if (!CanEdit || _session is null || SelectedCanvas is not { } canvas) return;
        _session.Mutate(d => d with
        {
            Canvases = d.Canvases.Where(c => c.CanvasId != canvas.CanvasId).ToArray(),
            Surfaces = d.Surfaces.Where(s => s.CanvasId != canvas.CanvasId).ToArray(),
            Bindings = d.Bindings.Where(b => b.CanvasId != canvas.CanvasId).ToArray()
        });
        ChangedDraft("Canvas, associated surfaces and target bindings removed in local draft.");
    }

    private void AddSurface()
    {
        if (!CanEdit || _session is null || SelectedCanvas is not { } canvas) return;
        var number = _session.Draft.Surfaces.Count(s => s.CanvasId == canvas.CanvasId);
        var width = canvas.Width * .65;
        var height = canvas.Height * .65;
        var x = (canvas.Width-width)/2;
        var y = (canvas.Height-height)/2;
        var surface = new OutputCanvasSurface(Guid.NewGuid(),canvas.CanvasId,
            new(BusRoleId:"program"),
            _session.Draft.Surfaces.DefaultIfEmpty().Max(s => s?.ZOrder ?? -1) + 1,
            new(0,0,1,1), OutputCanvasQuad.Rectangle(x,y,width,height));
        try
        {
            _session.Mutate(d => d with { Surfaces = [..d.Surfaces,surface] });
            ChangedDraft("Surface added to local draft.", surface.SurfaceId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { Status = ex.Message; }
    }

    private void RemoveSurface()
    {
        if (!CanEdit || _session is null || SelectedSurfaceId is not { } id) return;
        _session.Mutate(d => d with { Surfaces = d.Surfaces.Where(s => s.SurfaceId != id).ToArray() });
        ChangedDraft("Surface removed locally. Bound output will be transactionally unbound and restored on save.");
    }

    private void BindProgram()
    {
        if (!CanEdit || _session is null || SelectedCanvas is not { } canvas ||
            SelectedTarget?.Target is not { RoleId: "program" } target) return;
        var existing = _session.Draft.Bindings.FirstOrDefault(b =>
            b.TargetId == target.TargetId && b.ProviderId == target.ProviderId);
        var binding = new OutputCanvasBinding(existing?.OutputId ?? Guid.NewGuid(),
            canvas.CanvasId, target.ProviderId, target.TargetId, target.ProfileId, Enabled: true);
        _session.Mutate(d => d with { Bindings = d.Bindings
            .Where(b => b.OutputId != binding.OutputId)
            .Select(b => b with { Enabled = false })
            .Append(binding).ToArray() });
        ChangedDraft("Selected canvas assigned to governed PROGRAM target only; AUX binding retained but disabled.");
    }

    private void Undo()
    {
        if (_session?.Undo() == true) ChangedDraft("Local canvas edit undone.");
    }

    private void Redo()
    {
        if (_session?.Redo() == true) ChangedDraft("Local canvas edit restored.");
    }

    private void ChangedDraft(string explanation, Guid? surfaceId = null, Guid? canvasId = null)
    {
        _previewQuad = null;
        _validatedSavedRevision = null;
        _validatedDraftRevision = null;
        // A remote saved-revision conflict cannot be cleared by editing another local field.
        _pendingSaveTransaction = Guid.Empty;
        _saveOutcomeUnknown = false;
        PublishIssues([]);
        Rebuild(canvasId, surfaceId);
        Status = explanation;
        ResultDetail = "Only SAVE persists this configuration; only APPLY can request Runtime mapping.";
    }

    private void RaiseAll()
    {
        foreach (var name in new[] { nameof(IsDirty), nameof(CanEdit), nameof(CanSubmit),
            nameof(HasSavedConflict), nameof(SaveOutcomeUnconfirmed), nameof(ApplyOutcomeUnconfirmed),
            nameof(IsDraftValidated), nameof(IsActiveMappingConfirmed), nameof(WorkflowStage),
            nameof(SavedRevision), nameof(DraftRevision),
            nameof(DraftSafety), nameof(EditingSafety), nameof(SelectedSurfaceIssue),
            nameof(VisibleSurfaces), nameof(SelectedBlendCurve), nameof(IsSurfaceEnabled),
            nameof(SelectedGeometryEvidence), nameof(PointerCoordinates),
            nameof(SavedTargetEvidence), nameof(ObservedTargetEvidence), nameof(ActivationState) })
            OnPropertyChanged(name);
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        foreach (var command in new[] { ReloadCommand,DiscardAndReloadCommand,ValidateCommand,
            SaveCommand,ApplyCommand,DisableCommand,ReconcileCommand,UndoCommand,RedoCommand,
            AddCanvasCommand,RemoveCanvasCommand,AddSurfaceCommand,RemoveSurfaceCommand,
            BindProgramCommand,ApplyNumericCommand,ToggleGeometryDetailsCommand,
            AlignHorizontalCommand,AlignVerticalCommand })
            if (command is AsyncRelayCommand relay) relay.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field,value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this,new(name));

    private static bool RemoteFailure(Exception ex) =>
        ex is IOException or TimeoutException or InvalidOperationException or
            NotSupportedException or ArgumentException or UnauthorizedAccessException;
}
