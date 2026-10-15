// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.ComponentModel;
using System.Windows.Input;

namespace rtaime.Operator;

public sealed partial class MediaPoolInspectorViewModel
{
    private sealed class InspectorDraftContext(InspectorAttributeValues baseline)
    {
        public InspectorAttributeValues Baseline = baseline, Current = baseline;
        public AuthoringDraftHistory<InspectorAttributeValues> History { get; } = new();
    }
    public const int MaximumInspectorContexts = 8;
    private readonly Dictionary<string, InspectorDraftContext> _inspectorDrafts = new(StringComparer.Ordinal);
    private readonly List<string> _draftContextOrder = [];
    private InspectorDraftContext? _activeDraft;
    private InspectorAttributeValues? _transactionStart;
    private string? _draftRevision;
    private bool _restoringInspector;
    private double _audioGainDraft = 1;
    private string _cueNameDraft = "";
    public double AudioGainDraft
    {
        get => _audioGainDraft;
        set => SetValidated(ref _audioGainDraft, value, 0, 4);
    }
    public string CueNameDraft
    {
        get => _cueNameDraft;
        set
        {
            if (value is null || value.Length > 128) throw new ArgumentOutOfRangeException(nameof(value));
            Set(ref _cueNameDraft, value);
        }
    }
    public ICommand UndoInspectorDraftCommand { get; private set; } = null!;
    public ICommand RedoInspectorDraftCommand { get; private set; } = null!;
    public ICommand ApplyAudioDraftCommand { get; private set; } = null!;
    public ICommand ApplyCueDraftCommand { get; private set; } = null!;
    private string? CurrentRevisionToken => _operator.Client?.Snapshot is { } snapshot
        ? snapshot.Production.ProductionId + ":" + snapshot.Production.Revision.Value : null;
    private bool HasInspectorDraft => _transformDraftDirty || _processingDraftDirty ||
        _activeDraft is { } draft && draft.Current != draft.Baseline;
    private bool TransactionChanged => _transactionStart is not null && _activeDraft?.Current != _transactionStart;
    private bool CanEditInspectorDraft => _activeDraft is not null && InspectorInputsValid && !HasMultipleSelection &&
        _operator.IsConnected && !_operator.IsStale && !_operator.IsBusy && !_mediaDeck.IsBusy && CurrentRevisionToken == _draftRevision;
    public string InspectorDraftHistoryLabel => _activeDraft is null ? "LOCAL DRAFT · no editable context" :
        $"LOCAL UNDO {_activeDraft.History.UndoCount + (TransactionChanged ? 1 : 0)} / REDO {(TransactionChanged ? 0 : _activeDraft.History.RedoCount)} · APPLY / live operations excluded";
    internal int InspectorContextCount => _inspectorDrafts.Count;
    private void InitializeInspectorDrafts()
    {
        UndoInspectorDraftCommand = new OperatorShellCommand(() =>
        {
            EndDraftTransaction();
            if (_activeDraft is { } draft && draft.History.CanUndo) RestoreHistoryValue(draft.History.Undo(draft.Current));
        }, () => CanEditInspectorDraft && (_activeDraft!.History.CanUndo || TransactionChanged));
        RedoInspectorDraftCommand = new OperatorShellCommand(() =>
        {
            EndDraftTransaction();
            if (_activeDraft is { } draft && draft.History.CanRedo) RestoreHistoryValue(draft.History.Redo(draft.Current));
        }, () => CanEditInspectorDraft && !TransactionChanged && _activeDraft!.History.CanRedo);
        ApplyAudioDraftCommand = new OperatorShellCommand(() =>
        {
            EndDraftTransaction();
            if (_operator.SelectedAudioInput is not { } input) return;
            input.Gain = AudioGainDraft;
            _operator.ApplyAudioGainCommand.Execute(null);
        }, () => CanEditInspectorDraft && _activeDraft?.Current is InspectorGainAttributes { IsValid: true } &&
            _operator.ApplyAudioGainCommand.CanExecute(null));
        ApplyCueDraftCommand = new OperatorShellCommand(() =>
        {
            EndDraftTransaction();
            _mediaDeck.CueName = CueNameDraft;
            _mediaDeck.RenameCueCommand.Execute(null);
        }, () => CanEditInspectorDraft && _activeDraft?.Current is InspectorCueAttributes { IsValid: true } &&
            _mediaDeck.SelectedCue?.Id == _timelineCue?.Id && _mediaDeck.IsLoaded);
        PropertyChanged += OnInspectorDraftValueChanged;
    }
    public void BeginDraftTransaction()
    {
        if (CanEditInspectorDraft && _transactionStart is null) _transactionStart = _activeDraft!.Current;
    }
    public void EndDraftTransaction()
    {
        if (_transactionStart is not { } before) return;
        _transactionStart = null;
        if (_activeDraft is { } draft && before != draft.Current) draft.History.Remember(before);
        NotifyInspectorHistory();
    }
    public void CancelDraftTransaction()
    {
        if (_transactionStart is not { } before) return;
        _transactionStart = null;
        RestoreHistoryValue(before);
    }
    private void RestoreHistoryValue(InspectorAttributeValues values)
    {
        if (_activeDraft is null) return;
        RestoreInspectorValues(values); _activeDraft.Current = values; NotifyInspectorHistory();
    }
    private void OnInspectorDraftValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_restoringInspector || !CanEditInspectorDraft ||
            !(e.PropertyName?.StartsWith("SelectedLayer", StringComparison.Ordinal) == true ||
              e.PropertyName?.StartsWith("SelectedColorGrade", StringComparison.Ordinal) == true ||
              e.PropertyName?.StartsWith("SelectedChromaKey", StringComparison.Ordinal) == true ||
              e.PropertyName is nameof(AudioGainDraft) or nameof(CueNameDraft))) return;
        if (CaptureInspectorValues() is not { } current || _activeDraft!.Current == current) return;
        if (_transactionStart is null) _activeDraft.History.Remember(_activeDraft.Current);
        _activeDraft.Current = current; _attributeStatus = ""; NotifyInspectorHistory();
    }
    private void RefreshInspectorDraftContext()
    {
        if (_restoringInspector) return;
        var token = CurrentRevisionToken;
        if (token != _draftRevision || _operator.IsStale || !_operator.IsConnected)
        {
            _inspectorDrafts.Clear(); _draftContextOrder.Clear(); _activeDraft = null;
            _transactionStart = null; _draftRevision = token;
        }
        var binding = InspectorBinding();
        if (binding is null || HasMultipleSelection)
        { EndDraftTransaction(); _activeDraft = null; _transactionStart = null; NotifyInspectorHistory(); return; }
        var (key, baseline) = binding.Value;
        if (!_inspectorDrafts.TryGetValue(key, out var draft) || draft.Baseline != baseline)
        {
            draft = new(baseline);
            _inspectorDrafts[key] = draft;
            _draftContextOrder.Remove(key); _draftContextOrder.Add(key);
            while (_draftContextOrder.Count > MaximumInspectorContexts)
            {
                _inspectorDrafts.Remove(_draftContextOrder[0]); _draftContextOrder.RemoveAt(0);
            }
        }
        if (!ReferenceEquals(_activeDraft, draft)) { EndDraftTransaction(); _transactionStart = null; }
        _activeDraft = draft;
        RestoreInspectorValues(draft.Current);
        NotifyInspectorHistory();
    }
    private (string Key, InspectorAttributeValues Baseline)? InspectorBinding()
    {
        if (CurrentRevisionToken is null || !_operator.IsConnected || _operator.IsStale) return null;
        var prefix = EditingSelectionIdentity;
        if (HasAuthoritativeCompositingSelection && SelectedCompositingLayer is { } layer)
        {
            if (IsSelectedColorGradeProcessingNode && SelectedProcessingNode?.ColorGrade is { } grade)
                return (prefix + ":grade", new InspectorGradeAttributes(SelectedProcessingNode.Enabled, grade.Brightness, grade.Contrast, grade.Saturation));
            if (IsSelectedChromaKeyProcessingNode && SelectedProcessingNode?.ChromaKey is { } key)
                return (prefix + ":key", new InspectorKeyAttributes(SelectedProcessingNode.Enabled, key.KeyRed, key.KeyGreen, key.KeyBlue, key.Tolerance, key.Softness, key.SpillSuppression));
            if (ResolveSelectedProcessingNodeId() is not null) return null;
            return (prefix + ":transform", new InspectorTransformAttributes(layer.PositionX, layer.PositionY, layer.Scale, layer.RotationDegrees,
                layer.AnchorX, layer.AnchorY, layer.CropLeft, layer.CropTop, layer.CropRight, layer.CropBottom));
        }
        if (IsAudioSelection && _operator.SelectedAudioInput is { } input &&
            SelectedItem?.Key == $"audio:{input.SourceId}:{input.StreamId}")
            return (prefix + ":gain", new InspectorGainAttributes(input.ConfirmedGain));
        if (_timelineCue is { } cue && _mediaDeck.Cues.FirstOrDefault(c => c.Id == cue.Id) is { } current &&
            _mediaDeck.SelectedCue?.Id == cue.Id)
            return (prefix + ":cue", new InspectorCueAttributes(current.Name));
        return null;
    }
    private InspectorAttributeValues? CaptureInspectorValues() => _activeDraft?.Current switch
    {
        InspectorTransformAttributes => new InspectorTransformAttributes(SelectedLayerPositionX, SelectedLayerPositionY, SelectedLayerScale,
            SelectedLayerRotationDegrees, SelectedLayerAnchorX, SelectedLayerAnchorY, SelectedLayerCropLeft, SelectedLayerCropTop, SelectedLayerCropRight, SelectedLayerCropBottom),
        InspectorGradeAttributes => new InspectorGradeAttributes(SelectedColorGradeEnabled, SelectedColorGradeBrightness, SelectedColorGradeContrast, SelectedColorGradeSaturation),
        InspectorKeyAttributes => new InspectorKeyAttributes(SelectedChromaKeyEnabled, SelectedChromaKeyRed, SelectedChromaKeyGreen, SelectedChromaKeyBlue,
            SelectedChromaKeyTolerance, SelectedChromaKeySoftness, SelectedChromaKeySpillSuppression),
        InspectorGainAttributes => new InspectorGainAttributes(AudioGainDraft),
        InspectorCueAttributes => new InspectorCueAttributes(CueNameDraft),
        _ => null
    };
    private void RestoreInspectorValues(InspectorAttributeValues values)
    {
        _restoringInspector = true;
        try
        {
            switch (values)
            {
                case InspectorTransformAttributes t:
                    SelectedLayerPositionX = t.X; SelectedLayerPositionY = t.Y; SelectedLayerScale = t.Scale;
                    SelectedLayerRotationDegrees = t.Rotation; SelectedLayerAnchorX = t.AnchorX; SelectedLayerAnchorY = t.AnchorY;
                    SelectedLayerCropLeft = t.Left; SelectedLayerCropTop = t.Top; SelectedLayerCropRight = t.Right; SelectedLayerCropBottom = t.Bottom;
                    _transformDraftDirty = _activeDraft?.Baseline != values; break;
                case InspectorGradeAttributes g:
                    SelectedColorGradeEnabled = g.Enabled; SelectedColorGradeBrightness = g.Brightness;
                    SelectedColorGradeContrast = g.Contrast; SelectedColorGradeSaturation = g.Saturation;
                    _processingDraftDirty = _activeDraft?.Baseline != values; break;
                case InspectorKeyAttributes k:
                    SelectedChromaKeyEnabled = k.Enabled; SelectedChromaKeyRed = (int)k.Red; SelectedChromaKeyGreen = (int)k.Green;
                    SelectedChromaKeyBlue = (int)k.Blue; SelectedChromaKeyTolerance = k.Tolerance;
                    SelectedChromaKeySoftness = k.Softness; SelectedChromaKeySpillSuppression = k.Spill;
                    _processingDraftDirty = _activeDraft?.Baseline != values; break;
                case InspectorGainAttributes a: AudioGainDraft = a.Gain; break;
                case InspectorCueAttributes c: CueNameDraft = c.Name; break;
            }
        }
        finally { _restoringInspector = false; }
        if (_activeDraft is not null) _activeDraft.Current = values;
    }
    private void NotifyInspectorHistory()
    {
        OnPropertyChanged(nameof(InspectorDraftHistoryLabel)); OnPropertyChanged(nameof(InspectorEditState));
        OnPropertyChanged(nameof(AttributeCompatibilityLabel));
        foreach (var command in new[] { UndoInspectorDraftCommand, RedoInspectorDraftCommand, ApplyAudioDraftCommand, ApplyCueDraftCommand })
            (command as OperatorShellCommand)?.RaiseCanExecuteChanged();
        (CopyAttributesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PasteAttributesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
