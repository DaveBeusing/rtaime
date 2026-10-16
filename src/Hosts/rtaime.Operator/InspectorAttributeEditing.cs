// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;

namespace rtaime.Operator;

public sealed partial class MediaPoolInspectorViewModel
{
    private InspectorAttributePayload? _attributeClipboard;
    private bool _inspectorInputsValid = true;
    private string _attributeStatus = "";
    public ICommand CopyAttributesCommand { get; }
    public ICommand PasteAttributesCommand { get; }
    public string EditingSelectionIdentity => (_operator.Client?.Snapshot?.Production.ProductionId.ToString() ?? "offline") + ":" +
        (_compositingNode?.Id ?? (_timelineCue is { } cue ? "cue:" + _mediaDeck.AssetId + ":" + cue.Id :
            _timelineItem is { } item ? "item:" + _mediaDeck.AssetId + ":" + item.Id : SelectedItem?.Key ?? "none"));
    public bool InspectorInputsValid
    {
        get => _inspectorInputsValid;
        internal set { if (Set(ref _inspectorInputsValid, value)) RaiseSelectionState(); }
    }
    public string InspectorEditState => _operator.IsBusy || _mediaDeck.IsBusy ? "PENDING · awaiting confirmation" :
        _operator.IsStale ? "STALE · resynchronize before editing" : !_operator.IsConnected ? "DISCONNECTED" :
        !InspectorInputsValid || CaptureInspectorValues() is { IsValid: false } ? "INVALID INPUT · correct the typed attributes" :
        !string.IsNullOrWhiteSpace(_operator.LastError) ? "REJECTED · " + _operator.LastError :
        !string.IsNullOrWhiteSpace(_mediaDeck.LastError) ? "REJECTED · " + _mediaDeck.LastError :
        HasInspectorDraft ? "DRAFT · APPLY requires confirmation" : "CONFIRMED · local edits require APPLY";
    public string AttributeCompatibilityLabel => _attributeStatus.Length > 0 ? _attributeStatus :
        HasMultipleSelection ? "Batch editing deferred: no atomic multi-selection property contract." :
        _activeDraft is null ? "Select an editable layer, processing node, audio stream or cue." :
        _attributeClipboard is null ? "Copy typed attributes; paste changes a compatible draft only." :
        _attributeClipboard.RevisionToken != CurrentRevisionToken ? "STALE CLIPBOARD · copy again after authoritative rebase." :
        _attributeClipboard.Values.GetType() == _activeDraft.Current.GetType()
            ? "Copied " + _attributeClipboard.Values.Kind + "; paste changes the draft only. APPLY separately."
            : "Incompatible: copied " + _attributeClipboard.Values.Kind + "; selected " + _activeDraft.Current.Kind + ".";
    private bool CanCopyAttributes() => CanEditInspectorDraft && CaptureInspectorValues()?.IsValid == true;
    private bool CanPasteAttributes() => CanEditInspectorDraft && _attributeClipboard is { } payload &&
        CompatiblePayload(payload);
    private bool CompatiblePayload(InspectorAttributePayload payload) => payload.Version == 1 &&
        payload.RevisionToken == CurrentRevisionToken && payload.Values?.IsValid == true &&
        _activeDraft?.Current.GetType() == payload.Values.GetType();
    private Task CopyAttributesAsync()
    {
        if (CanCopyAttributes())
        {
            _attributeClipboard = new(1, CurrentRevisionToken!, CaptureInspectorValues()!);
            _attributeStatus = ""; NotifyInspectorHistory();
        }
        return Task.CompletedTask;
    }
    private Task PasteAttributesAsync()
    {
        if (_attributeClipboard is { } payload) TryPasteAttributeSet(payload);
        return Task.CompletedTask;
    }
    internal bool TryPasteAttributeSet(InspectorAttributePayload payload)
    {
        if (!CanEditInspectorDraft || !CompatiblePayload(payload))
        {
            _attributeStatus = "Paste rejected: incompatible, stale or invalid attributes. Draft unchanged.";
            NotifyInspectorHistory(); return false;
        }
        EndDraftTransaction();
        BeginDraftTransaction();
        RestoreInspectorValues(payload.Values);
        EndDraftTransaction();
        _attributeStatus = ""; NotifyInspectorHistory(); return true;
    }
}
