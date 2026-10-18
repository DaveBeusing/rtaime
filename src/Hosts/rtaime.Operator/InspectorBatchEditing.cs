// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed record InspectorBatchItem(InspectorEditTarget Target, InspectorAttributeValues? Confirmed,
    InspectorAttributeValues? Proposed, string Result)
{
    public string Summary => Target.Label + "\n" + Result +
        (Confirmed is null ? "" : "\nCONFIRMED " + InspectorAttributePresentation.Format(Confirmed)) +
        (Proposed is null ? "" : "\nPROPOSED " + InspectorAttributePresentation.Format(Proposed));
}
public sealed partial class MediaPoolInspectorViewModel
{
    public const int MaximumInspectorBatchTargets = 32;
    private string[] _inspectorReviewLayerIds = [];
    private bool _inspectorBatchRunning, _inspectorBatchStop;
    private string? _inspectorBatchRevision, _inspectorBatchProduction;
    private string _inspectorBatchState = "Review compatible copied attributes before applying individual targets.";
    private ICommand? _reviewInspectorBatch, _applyInspectorBatch, _stopInspectorBatch, _reviewRemainingInspectorBatch;
    public ObservableCollection<InspectorBatchItem> InspectorBatchItems { get; } = [];
    public bool HasInspectorBatch => HasMultipleSelection || InspectorBatchItems.Count > 0;
    public string InspectorBatchEvidence => "SEQUENTIAL · NOT ATOMIC · up to " + MaximumInspectorBatchTargets +
        " targets; stop after current request; confirmed results have no local batch undo.\n" + _inspectorBatchState;
    public string InspectorBatchSelectionEvidence
    {
        get
        {
            var targets = CurrentBatchTargets().Take(MaximumInspectorBatchTargets + 1).ToArray();
            if (targets.Length > MaximumInspectorBatchTargets) return "Selection exceeds the review limit; narrow it before applying.";
            var values = targets.Select(ReadInspectorTarget).ToArray();
            if (targets.Length == 0) return "No compatible multi-selection. Technical metadata and timeline topology are read-only.";
            if (values.Any(value => value is null) || values.Select(value => value!.GetType()).Distinct().Count() != 1)
                return "INCOMPATIBLE / MISSING · review each stable identity; unsupported targets are excluded.";
            static string Common(IEnumerable<double> numbers)
            {
                var items = numbers.Distinct().Take(2).ToArray();
                return items.Length == 1 ? items[0].ToString("G17", System.Globalization.CultureInfo.CurrentCulture) : "MIXED";
            }
            var common = values[0] switch
            {
                InspectorGainAttributes => "Gain " + Common(values.Cast<InspectorGainAttributes>().Select(value => value.Gain)) + "×",
                InspectorTransformAttributes => "Position X " + Common(values.Cast<InspectorTransformAttributes>().Select(value => value.X)) +
                    "; Y " + Common(values.Cast<InspectorTransformAttributes>().Select(value => value.Y)) +
                    "; scale " + Common(values.Cast<InspectorTransformAttributes>().Select(value => value.Scale)) +
                    "; rotation " + Common(values.Cast<InspectorTransformAttributes>().Select(value => value.Rotation)) + "°",
                _ => values.Distinct().Count() == 1 ? InspectorAttributePresentation.Format(values[0]!) : "MIXED compatible attributes"
            };
            return common + "\n" + string.Join("; ", targets.Select(target => target.Label));
        }
    }
    public ICommand ReviewInspectorBatchCommand => _reviewInspectorBatch ??= new OperatorShellCommand(() =>
    {
        var targets = CurrentBatchTargets().Take(MaximumInspectorBatchTargets + 1).ToArray();
        if (!CanReviewInspectorBatch() || targets.Length > MaximumInspectorBatchTargets) return;
        InspectorBatchItems.Clear();
        foreach (var target in targets)
        {
            var confirmed = ReadInspectorTarget(target);
            var proposed = confirmed?.GetType() == _attributeClipboard!.Values.GetType() ? _attributeClipboard.Values : null;
            InspectorBatchItems.Add(new(target, confirmed, proposed,
                proposed is null ? "EXCLUDED · missing identity, incompatible kind or unsupported property command" : "REVIEWED · awaiting explicit APPLY"));
        }
        _inspectorBatchRevision = CurrentRevisionToken; _inspectorBatchProduction = CurrentProduction;
        _inspectorBatchState = "REVIEWED · " + InspectorBatchItems.Count(row => row.Proposed is not null) + " compatible targets. Inspect every before/after value.";
        NotifyInspectorBatch();
    }, CanReviewInspectorBatch);
    public ICommand ReviewRemainingInspectorBatchCommand => _reviewRemainingInspectorBatch ??= new OperatorShellCommand(() =>
    {
        if (_inspectorBatchRunning || !InspectorMutationReady || _inspectorBatchProduction != CurrentProduction) return;
        for (var i = 0; i < InspectorBatchItems.Count; i++)
        {
            var row = InspectorBatchItems[i];
            if (row.Result == "CONFIRMED" || row.Proposed is null) continue;
            var confirmed = ReadInspectorTarget(row.Target);
            InspectorBatchItems[i] = row with { Confirmed = confirmed,
                Result = confirmed?.GetType() == row.Proposed.GetType() ? "REVIEWED · retry requires APPLY" : "MISSING / TYPE CHANGED · cannot submit" };
        }
        _inspectorBatchRevision = CurrentRevisionToken;
        _inspectorBatchState = "Remaining targets rebased for review only. Confirmed rows will not be sent again.";
        NotifyInspectorBatch();
    }, () => !_inspectorBatchRunning && InspectorMutationReady && _inspectorBatchProduction == CurrentProduction &&
        InspectorBatchItems.Any(row => row.Proposed is not null && row.Result != "CONFIRMED"));
    public ICommand StopInspectorBatchCommand => _stopInspectorBatch ??= new OperatorShellCommand(() =>
    {
        _inspectorBatchStop = true; _inspectorBatchState = "STOP REQUESTED · the current request must finish; later targets will not be sent.";
        NotifyInspectorBatch();
    }, () => _inspectorBatchRunning && !_inspectorBatchStop);
    public ICommand ApplyInspectorBatchCommand => _applyInspectorBatch ??= new AsyncRelayCommand(ApplyInspectorBatchAsync, CanApplyInspectorBatch);
    private bool CanReviewInspectorBatch() => HasMultipleSelection && !_inspectorBatchRunning && InspectorMutationReady &&
        _attributeClipboard is { Values.IsValid: true } payload && payload.RevisionToken == CurrentRevisionToken &&
        CurrentBatchTargets().Take(MaximumInspectorBatchTargets + 1).Count() is > 0 and <= MaximumInspectorBatchTargets;
    private bool CanApplyInspectorBatch() => !_inspectorBatchRunning && InspectorMutationReady &&
        _inspectorBatchProduction == CurrentProduction && _inspectorBatchRevision == CurrentRevisionToken &&
        InspectorBatchItems.Any(row => row.Proposed is not null && row.Result.StartsWith("REVIEWED", StringComparison.Ordinal)) &&
        InspectorBatchItems.Where(row => row.Proposed is not null && row.Result != "CONFIRMED").All(row =>
            row.Result.StartsWith("REVIEWED", StringComparison.Ordinal) && row.Proposed!.IsValid && row.Confirmed is not null &&
            ReadInspectorTarget(row.Target) == row.Confirmed);
    private IEnumerable<InspectorEditTarget> CurrentBatchTargets()
    {
        if (!HasMultipleSelection) yield break;
        if (_compositingNode?.Id == "review:layers")
        {
            foreach (var id in _inspectorReviewLayerIds)
            {
                var layer = _operator.CompositingLayers.FirstOrDefault(layer => layer.LayerId == id);
                if (_attributeClipboard?.Values is InspectorGradeAttributes or InspectorKeyAttributes)
                {
                    var kind = _attributeClipboard.Values is InspectorGradeAttributes ? 1 : 2;
                    var nodes = layer?.ProcessingStack.Where(node => node.Kind == kind).ToArray() ?? [];
                    // No guessed processing-node identity when a layer has several matching nodes.
                    yield return nodes.Length == 1 ? new(kind == 1 ? "grade" : "key", id, nodes[0].NodeId) : new("unsupported", id);
                }
                else yield return new("transform", id);
            }
            yield break;
        }
        if (_timelineItems.Count > 1)
        {
            foreach (var item in _timelineItems) yield return new("unsupported", item.Id);
            yield break;
        }
        foreach (var item in SelectedItems)
        {
            var input = _operator.Client?.Snapshot?.AudioInputs.FirstOrDefault(input => item.Key == "audio:" + input.SourceId + ":" + input.StreamId);
            yield return item.Kind == MediaPoolItemKind.Audio && input is not null ? new("gain", input.SourceId, input.StreamId) : new("unsupported", item.Key);
        }
    }
    internal async Task ApplyInspectorBatchAsync()
    {
        if (!CanApplyInspectorBatch()) return;
        _inspectorBatchRunning = true; _inspectorBatchStop = false; NotifyInspectorBatch();
        try
        {
            for (var i = 0; i < InspectorBatchItems.Count; i++)
            {
                var row = InspectorBatchItems[i];
                if (row.Proposed is null || row.Result == "CONFIRMED") continue;
                if (_inspectorBatchStop) { _inspectorBatchState = "STOPPED · confirmed rows retained; review remaining targets before retry."; break; }
                // Refresh through the existing owner before each individual request; never silently adopt an external revision.
                await _operator.SynchronizeInspectorAsync();
                if (_inspectorBatchStop) { _inspectorBatchState = "STOPPED before mutation; review remaining targets."; break; }
                if (!InspectorMutationReady || _inspectorBatchProduction != CurrentProduction ||
                    _inspectorBatchRevision != CurrentRevisionToken || ReadInspectorTarget(row.Target) != row.Confirmed)
                {
                    InspectorBatchItems[i] = row with { Result = "CONFLICT / STALE · review required; not sent" };
                    _inspectorBatchState = "STOPPED on revision, readiness or target conflict."; break;
                }
                var beforeRevision = _operator.Client!.Snapshot!.Production.Revision.Value;
                InspectorBatchItems[i] = row with { Result = "PENDING · awaiting this target's confirmation" };
                bool confirmed;
                try { confirmed = await ApplyInspectorTargetAsync(row.Target, row.Proposed); }
                catch (Exception error)
                {
                    InspectorBatchItems[i] = row with { Result = "REJECTED / UNKNOWN · " + error.Message };
                    _inspectorBatchState = "STOPPED · inspect individual results; no automatic retry."; break;
                }
                InspectorBatchItems[i] = row with { Result = confirmed ? "CONFIRMED" : "REJECTED / UNCONFIRMED · review before retry" };
                if (!confirmed) { _inspectorBatchState = "STOPPED on first failure. Earlier confirmations remain applied."; break; }
                RemoveDraftContext(row.Target);
                _inspectorBatchRevision = CurrentRevisionToken;
                var afterRevision = _operator.Client!.Snapshot!.Production.Revision.Value;
                if (afterRevision < beforeRevision || afterRevision - beforeRevision > (row.Target.Kind == "gain" ? 0UL : 1UL))
                { _inspectorBatchState = "STOPPED · concurrent revision advance detected after confirmation; review remaining targets."; break; }
                _inspectorBatchState = "CONFIRMED " + InspectorBatchItems.Count(item => item.Result == "CONFIRMED") + " individual targets.";
            }
            if (InspectorBatchItems.All(row => row.Proposed is null || row.Result == "CONFIRMED"))
                _inspectorBatchState = "COMPLETE · all compatible individual targets confirmed; excluded targets unchanged.";
        }
        finally
        {
            _inspectorBatchRunning = false;
            // A stopped batch cannot resume without a new explicit review.
            for (var i = 0; i < InspectorBatchItems.Count; i++)
                if (InspectorBatchItems[i].Proposed is not null && InspectorBatchItems[i].Result.StartsWith("REVIEWED", StringComparison.Ordinal))
                    InspectorBatchItems[i] = InspectorBatchItems[i] with { Result = "NOT ATTEMPTED · review remaining targets before retry" };
            NotifyInspectorBatch();
        }
    }
    private void NotifyInspectorBatch()
    {
        foreach (var property in new[] { nameof(HasInspectorBatch), nameof(InspectorBatchEvidence), nameof(InspectorBatchSelectionEvidence) }) OnPropertyChanged(property);
        foreach (var command in new[] { _reviewInspectorBatch, _applyInspectorBatch, _stopInspectorBatch, _reviewRemainingInspectorBatch })
        {
            (command as OperatorShellCommand)?.RaiseCanExecuteChanged();
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
