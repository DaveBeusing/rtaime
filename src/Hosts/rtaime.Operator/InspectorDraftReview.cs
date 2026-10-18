// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

// Local intent and review evidence, never an alternative production selection or state store.
public sealed record InspectorEditTarget(string Kind, string Id, string? ChildId = null, string? AssetId = null)
{
    public string Key => Kind + ":" + Id + ":" + ChildId + ":" + AssetId;
    public string Label => Kind.ToUpperInvariant() + " · " + Id + (ChildId is null ? "" : " / " + ChildId);
}
public sealed record InspectorDraftReview(InspectorEditTarget Target, string Production, string Revision,
    InspectorAttributeValues Confirmed, InspectorAttributeValues Draft, string State)
{
    public string Summary => Target.Label + " · " + State + "\nCONFIRMED " + InspectorAttributePresentation.Format(Confirmed) + "\nLOCAL " + InspectorAttributePresentation.Format(Draft);
}

public sealed partial class MediaPoolInspectorViewModel
{
    private InspectorEditTarget? _activeInspectorTarget;
    private string? _activeInspectorRevision;
    private InspectorDraftReview? _selectedDraftReview;
    private string _draftReviewNotice = "";
    private bool _inspectorReviewBusy;
    private ICommand? _keepDraftReview, _discardDraftReview, _rebaseDraftReview, _restoreDraftReview, _applyDraftReview;
    public ObservableCollection<InspectorDraftReview> InspectorDraftReviews { get; } = [];
    public InspectorDraftReview? SelectedDraftReview
    {
        get => _selectedDraftReview;
        set { if (Set(ref _selectedDraftReview, value)) NotifyDraftReview(); }
    }
    public string DraftReviewNotice => _draftReviewNotice;
    public string DraftReviewGate => SelectedDraftReview is not { } review ? "" :
        review.Production != CurrentProduction ? "OTHER PRODUCTION · return to the original production; values cannot be reassigned." :
        ReadInspectorTarget(review.Target) is null ? "MISSING TARGET · intent retained; edits require the same available identity." :
        review.Revision != CurrentRevisionToken || ReadInspectorTarget(review.Target) != review.Confirmed
            ? "CONFLICT / REBASE REQUIRED · inspect current confirmation before APPLY. Current: " + InspectorAttributePresentation.Format(ReadInspectorTarget(review.Target)!)
            : "REVIEW READY · explicit APPLY uses individual existing commands.";
    public bool HasDraftReviews => InspectorDraftReviews.Count > 0;
    public string InspectorContextEvidence => EditingSelectionIdentity + "\n" +
        (HasMultipleSelection ? "MULTIPLE · review compatible attributes and individual targets" :
        _activeDraft is { } draft ? "CONFIRMED " + InspectorAttributePresentation.Format(draft.Baseline) + "\nLOCAL " + InspectorAttributePresentation.Format(draft.Current) :
        "READ ONLY · no typed property mutation for this context");
    public ICommand KeepDraftReviewCommand => _keepDraftReview ??= new OperatorShellCommand(() =>
    {
        if (SelectedDraftReview is { } review) ReplaceReview(review, review with { State = "KEPT · return to this context or review before APPLY" });
        _draftReviewNotice = "Draft kept locally. Selection and production are unchanged."; NotifyDraftReview();
    }, () => SelectedDraftReview is not null && !_inspectorReviewBusy);
    public ICommand DiscardDraftReviewCommand => _discardDraftReview ??= new OperatorShellCommand(() =>
    {
        if (SelectedDraftReview is not { } review) return;
        if (_activeInspectorTarget == review.Target && _activeDraft is { } active)
        {
            _transactionStart = null; active.History.Clear(); RestoreInspectorValues(active.Baseline);
        }
        else RemoveDraftContext(review.Target);
        InspectorDraftReviews.Remove(review); _selectedDraftReview = InspectorDraftReviews.LastOrDefault();
        _draftReviewNotice = "Discarded local intent only. Confirmed operations are excluded."; NotifyInspectorHistory();
    }, () => SelectedDraftReview is not null && !_inspectorReviewBusy);
    public ICommand RebaseDraftReviewCommand => _rebaseDraftReview ??= new OperatorShellCommand(() =>
    {
        if (SelectedDraftReview is not { } review || !CanReviewTarget(review.Target) ||
            ReadInspectorTarget(review.Target) is not { } baseline) return;
        ReplaceReview(review, review with { Production = CurrentProduction, Revision = CurrentRevisionToken!, Confirmed = baseline,
            State = "REVIEWED AGAINST CURRENT CONFIRMATION · APPLY remains explicit" });
        _draftReviewNotice = "Rebased comparison only; local intent was not applied."; NotifyDraftReview();
    }, () => SelectedDraftReview is { } review && review.Production == CurrentProduction && CanReviewTarget(review.Target));
    public ICommand RestoreDraftReviewCommand => _restoreDraftReview ??= new OperatorShellCommand(() =>
    {
        if (SelectedDraftReview is not { } review || !CanApplyReview(review) || _activeInspectorTarget != review.Target) return;
        EndDraftTransaction(); BeginDraftTransaction(); RestoreInspectorValues(review.Draft); EndDraftTransaction();
        _draftReviewNotice = "Restored as one local transaction. APPLY separately."; NotifyDraftReview();
    }, () => SelectedDraftReview is { } review && _activeInspectorTarget == review.Target && CanApplyReview(review) && CanEditInspectorDraft);
    public ICommand ApplyDraftReviewCommand => _applyDraftReview ??= new AsyncRelayCommand(async () =>
    {
        if (SelectedDraftReview is not { } review || !CanApplyReview(review)) return;
        _inspectorReviewBusy = true; NotifyDraftReview();
        try
        {
            await _operator.SynchronizeInspectorAsync();
            if (_operator.IsStale || !_operator.IsConnected || _operator.IsBusy || _operator.RuntimeStatus != "READY" ||
                review.Production != CurrentProduction || review.Revision != CurrentRevisionToken ||
                ReadInspectorTarget(review.Target) != review.Confirmed)
            {
                _draftReviewNotice = "CONFLICT / STALE · target was not sent. Rebase and review current evidence."; return;
            }
            var result = await ApplyInspectorTargetAsync(review.Target, review.Draft);
            if (result)
            {
                InspectorDraftReviews.Remove(review);
                RemoveDraftContext(review.Target);
                _selectedDraftReview = InspectorDraftReviews.LastOrDefault();
                _draftReviewNotice = "CONFIRMED · " + review.Target.Label + ". Local undo cannot reverse it.";
                RefreshInspectorDraftContext();
            }
            else
            {
                ReplaceReview(review, review with { State = "REJECTED / UNCONFIRMED · intent retained; review before retry" });
                _draftReviewNotice = _operator.LastError ?? _mediaDeck.LastError ?? "No matching confirmation. Review current evidence before retry.";
            }
        }
        finally { _inspectorReviewBusy = false; NotifyInspectorHistory(); }
    }, () => SelectedDraftReview is { } review && CanApplyReview(review));
    private string CurrentProduction => _operator.Client?.Snapshot?.Production.ProductionId.ToString() ?? "";
    private bool InspectorMutationReady => !_inspectorReviewBusy && !_operator.IsBusy && !_mediaDeck.IsBusy &&
        _operator.IsConnected && !_operator.IsStale && _operator.RuntimeStatus == "READY" &&
        _operator.ProgramSafety != OperatorProgramSafetyStates.Blocked;
    private bool CanReviewTarget(InspectorEditTarget target) => InspectorMutationReady && ReadInspectorTarget(target) is not null;
    private bool CanApplyReview(InspectorDraftReview review) => CanReviewTarget(review.Target) && review.Draft.IsValid &&
        review.Production == CurrentProduction && review.Revision == CurrentRevisionToken &&
        ReadInspectorTarget(review.Target) == review.Confirmed;
    private InspectorEditTarget? CurrentInspectorTarget()
    {
        if (HasMultipleSelection) return null;
        if (SelectedCompositingLayer is { Kind: 2 or 3 } layer)
        {
            if (SelectedProcessingNode is { } node) return new(node.Kind == 1 ? "grade" : "key", layer.LayerId, node.NodeId);
            if (ResolveSelectedProcessingNodeId() is not null) return null;
            return new("transform", layer.LayerId);
        }
        if (IsAudioSelection && SelectedItem is { } item && _operator.Client?.Snapshot?.AudioInputs.FirstOrDefault(
            input => item.Key == "audio:" + input.SourceId + ":" + input.StreamId) is { } input)
            return new("gain", input.SourceId, input.StreamId);
        if (_timelineCue is { } cue) return new("cue", cue.Id.ToString(), AssetId: _mediaDeck.AssetId);
        return null;
    }
    private InspectorAttributeValues? ReadInspectorTarget(InspectorEditTarget target)
    {
        if (target.Kind == "cue")
            return target.AssetId == _mediaDeck.AssetId && _mediaDeck.Cues.FirstOrDefault(cue => cue.Id.ToString() == target.Id) is { } cue
                ? new InspectorCueAttributes(cue.Name) : null;
        if (target.Kind == "gain")
            return _operator.Client?.Snapshot?.AudioInputs.FirstOrDefault(input => input.SourceId == target.Id && input.StreamId == target.ChildId) is { } input
                ? new InspectorGainAttributes(input.Gain) : null;
        var layer = _operator.CompositingLayers.FirstOrDefault(layer => layer.LayerId == target.Id && layer.Kind is 2 or 3);
        if (layer is null) return null;
        if (target.Kind == "transform") return new InspectorTransformAttributes(layer.PositionX, layer.PositionY, layer.Scale,
            layer.RotationDegrees, layer.AnchorX, layer.AnchorY, layer.CropLeft, layer.CropTop, layer.CropRight, layer.CropBottom);
        var node = layer.ProcessingStack.FirstOrDefault(node => node.NodeId == target.ChildId);
        return target.Kind switch
        {
            "grade" when node is { Kind: 1, ColorGrade: { } grade } => new InspectorGradeAttributes(node.Enabled, grade.Brightness, grade.Contrast, grade.Saturation),
            "key" when node is { Kind: 2, ChromaKey: { } key } => new InspectorKeyAttributes(node.Enabled, key.KeyRed, key.KeyGreen, key.KeyBlue, key.Tolerance, key.Softness, key.SpillSuppression),
            _ => null
        };
    }
    private void PreserveInspectorDraft()
    {
        if (_activeInspectorTarget is not { } target || _activeDraft is not { } draft || draft.Current == draft.Baseline ||
            _activeInspectorRevision is not { } revision) return;
        var production = revision[..revision.LastIndexOf(':')];
        if (production == CurrentProduction && ReadInspectorTarget(target) == draft.Current) { RemoveReview(target); return; }
        var existing = InspectorDraftReviews.FirstOrDefault(review => review.Target == target && review.Production == production);
        if (existing?.Draft == draft.Current && existing.Revision == revision) return;
        var next = new InspectorDraftReview(target, production, revision, draft.Baseline, draft.Current,
            "DIRTY · selection may change; APPLY / DISCARD / KEEP DRAFT");
        if (existing is not null) ReplaceReview(existing, next);
        else
        {
            if (InspectorDraftReviews.Count == MaximumInspectorContexts)
            {
                var displaced = InspectorDraftReviews[0];
                _draftReviewNotice = "Recovery capacity reached; displaced local copy: " + displaced.Summary;
                InspectorDraftReviews.RemoveAt(0);
            }
            InspectorDraftReviews.Add(next); _selectedDraftReview = next;
        }
    }
    private void RemoveReview(InspectorEditTarget target)
    {
        var review = InspectorDraftReviews.FirstOrDefault(review => review.Target == target && review.Production == CurrentProduction);
        if (review is null) return;
        InspectorDraftReviews.Remove(review);
        if (ReferenceEquals(_selectedDraftReview, review)) _selectedDraftReview = InspectorDraftReviews.LastOrDefault();
    }
    private void ReplaceReview(InspectorDraftReview old, InspectorDraftReview next)
    {
        var index = InspectorDraftReviews.IndexOf(old);
        if (index < 0) return;
        InspectorDraftReviews[index] = next;
        if (ReferenceEquals(_selectedDraftReview, old)) _selectedDraftReview = next;
    }
    private void RemoveDraftContext(InspectorEditTarget target)
    {
        var key = target.Kind switch { "transform" => target.Id + ":transform", "grade" or "key" => target.Id + ":" + target.ChildId + ":" + target.Kind,
            "gain" => CurrentProduction + ":audio:" + target.Id + ":" + target.ChildId + ":gain",
            "cue" => CurrentProduction + ":cue:" + target.AssetId + ":" + target.Id + ":cue", _ => null };
        if (key is not null) { _inspectorDrafts.Remove(key); _draftContextOrder.Remove(key); }
        if (_activeInspectorTarget == target && _activeDraft is { } active) active.History.Clear();
    }
    private async Task<bool> ApplyInspectorTargetAsync(InspectorEditTarget target, InspectorAttributeValues values)
    {
        switch (values)
        {
            case InspectorTransformAttributes t:
                await _operator.SetCompositingLayerTransformAsync(target.Id, t.X, t.Y, t.Scale, t.Rotation, t.AnchorX, t.AnchorY, t.Left, t.Top, t.Right, t.Bottom);
                break;
            case InspectorGainAttributes gain:
                await _operator.ApplyInspectorGainAsync(target.Id, target.ChildId!, gain.Gain);
                break;
            case InspectorGradeAttributes:
            case InspectorKeyAttributes:
                var layer = _operator.CompositingLayers.First(item => item.LayerId == target.Id);
                var stack = layer.ProcessingStack.Select(node => node.NodeId != target.ChildId ? node : values switch
                {
                    InspectorGradeAttributes g => new OperatorCompositingProcessingNodeDescriptor(node.NodeId, 1, g.Enabled, colorGrade: new(g.Brightness, g.Contrast, g.Saturation)),
                    InspectorKeyAttributes k => new OperatorCompositingProcessingNodeDescriptor(node.NodeId, 2, k.Enabled, chromaKey: new((byte)k.Red, (byte)k.Green, (byte)k.Blue, k.Tolerance, k.Softness, k.Spill)),
                    _ => node
                }).ToArray();
                await _operator.SetCompositingLayerProcessingStackAsync(target.Id, stack);
                break;
            case InspectorCueAttributes cue:
                var snapshot = await _operator.ApplyInspectorCueAsync(target.AssetId!, target.Id, cue.Name);
                if (snapshot is null) return false;
                _mediaDeck.ApplyConfirmedSnapshot(snapshot);
                break;
            default: return false;
        }
        return string.IsNullOrEmpty(_operator.LastError) && string.IsNullOrEmpty(_mediaDeck.LastError) && ReadInspectorTarget(target) == values;
    }
    private void NotifyDraftReview()
    {
        NotifyInspectorBatch();
        foreach (var property in new[] { nameof(SelectedDraftReview), nameof(DraftReviewNotice), nameof(HasDraftReviews), nameof(InspectorContextEvidence), nameof(DraftReviewGate) }) OnPropertyChanged(property);
        foreach (var command in new[] { _keepDraftReview, _discardDraftReview, _rebaseDraftReview, _restoreDraftReview, _applyDraftReview })
        {
            (command as OperatorShellCommand)?.RaiseCanExecuteChanged();
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

    }
}

public static class InspectorAttributePresentation
{
    private static string N(double value) => value.ToString("G17", System.Globalization.CultureInfo.CurrentCulture);
    public static string Format(InspectorAttributeValues values) => values switch
    {
        InspectorTransformAttributes t => $"Position {N(t.X)}, {N(t.Y)} norm; scale {N(t.Scale)}×; rotation {N(t.Rotation)}°; pivot {N(t.AnchorX)}, {N(t.AnchorY)}; crop L/T/R/B {N(t.Left)}/{N(t.Top)}/{N(t.Right)}/{N(t.Bottom)}",
        InspectorGradeAttributes g => $"{(g.Enabled ? "Enabled" : "Disabled")}; brightness {N(g.Brightness)}; contrast {N(g.Contrast)}×; saturation {N(g.Saturation)}×",
        InspectorKeyAttributes k => $"{(k.Enabled ? "Enabled" : "Disabled")}; key RGB {N(k.Red)}/{N(k.Green)}/{N(k.Blue)}; tolerance {N(k.Tolerance)}; softness {N(k.Softness)}; spill {N(k.Spill)}",
        InspectorGainAttributes g => $"Gain {N(g.Gain)}×",
        InspectorCueAttributes c => "Cue name “" + c.Name + "”",
        _ => "Unavailable typed attributes"
    };
}
