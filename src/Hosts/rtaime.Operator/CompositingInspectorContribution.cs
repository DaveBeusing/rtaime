// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed partial class MediaPoolInspectorViewModel
{
    private sealed record CompositingIntent(string Production, string NodeId, InspectorAttributeValues Values);
    private readonly Dictionary<string, CompositingIntent> _compositingIntents = new(StringComparer.Ordinal);
    private int _compositingReviewCount;
    private ICommand? _restoreCompositingIntent, _discardCompositingIntent;
    private string? CompositingIntentKey => _operator.Client?.Snapshot is { } snapshot && _compositingNode is { } node
        ? snapshot.Production.ProductionId + ":" + node.Id : null;
    public ICommand RestoreCompositingIntentCommand => _restoreCompositingIntent ??= new OperatorShellCommand(() =>
    {
        if (!CanRestoreCompositingIntent() || CompositingIntentKey is not { } key) return;
        EndDraftTransaction(); BeginDraftTransaction();
        RestoreInspectorValues(_compositingIntents[key].Values);
        EndDraftTransaction();
        _compositingIntents.Remove(key);
        _attributeStatus = "Recovered local intent against current confirmation. Review values, then APPLY explicitly.";
        NotifyInspectorHistory();
    }, CanRestoreCompositingIntent);
    public ICommand DiscardCompositingIntentCommand => _discardCompositingIntent ??= new OperatorShellCommand(() =>
    {
        _compositingIntents.Clear();
        NotifyCompositingRecovery();
    }, () => _compositingIntents.Count > 0);
    public string CompositingRecoveryStatus => _compositingIntents.Count == 0 ? "" :
        $"RECOVERY · {_compositingIntents.Count} stored local intent(s). " +
        string.Join("; ", _compositingIntents.Values.Select(intent => intent.NodeId + ": " + intent.Values)) +
        " · Restore only in the matching current context; review and APPLY separately.";
    private bool CanRestoreCompositingIntent() => CanEditInspectorDraft &&
        CompositingIntentKey is { } key && _compositingIntents.TryGetValue(key, out var intent) &&
        _activeDraft?.Current.GetType() == intent.Values.GetType();
    private void RetainCompositingIntent()
    {
        if (_compositingNode is not { } node || _activeDraft is not { } draft || draft.Current == draft.Baseline ||
            _draftRevision is not { } revision || draft.Current is not (InspectorTransformAttributes or InspectorGradeAttributes or InspectorKeyAttributes))
            return;
        var separator = revision.LastIndexOf(':');
        if (separator < 0) return;
        var production = revision[..separator];
        var key = production + ":" + node.Id;
        if (_compositingIntents.Count == MaximumInspectorContexts && !_compositingIntents.ContainsKey(key))
        {
            var oldest = _compositingIntents.Keys.First();
            _attributeStatus = "Recovery capacity reached; oldest local intent discarded: " + oldest;
            _compositingIntents.Remove(oldest);
        }
        _compositingIntents[key] = new(production, node.Id, draft.Current);
    }
    private void NotifyCompositingRecovery()
    {
        if (CompositingIntentKey is { } key && _compositingIntents.TryGetValue(key, out var stored) &&
            _activeDraft?.Baseline == stored.Values && _activeDraft.Current == stored.Values) _compositingIntents.Remove(key);
        OnPropertyChanged(nameof(CompositingRecoveryStatus));
        (_restoreCompositingIntent as OperatorShellCommand)?.RaiseCanExecuteChanged();
        (_discardCompositingIntent as OperatorShellCommand)?.RaiseCanExecuteChanged();
    }
    // Refresh observes the same selected identity without performing another explicit selection.
    public void ObserveMissingCompositingNode(string id) => SelectCompositingNode(new("missing:" + id,
        CompositingGraphNodeKind.Composite, "Missing layer or processing node", "Runtime no longer reports " + id +
        ". Stored local intent remains available for review; edits require a valid current context.", "MISSING", CompositingGraphHealth.Error, []));
    public void ObserveCompositingNode(CompositingGraphNodeProjection node)
    {
        if (_compositingNode?.Id != node.Id) return;
        if (_compositingNode.Title == node.Title && _compositingNode.Detail == node.Detail &&
            _compositingNode.Status == node.Status && _compositingNode.Health == node.Health) return;
        _compositingNode = node;
        SynchronizeCompositingDraft();
        BuildInspector();
        RaiseSelectionState();
    }
    public void ReviewCompositingLayers(IReadOnlyList<OperatorCompositingLayerDescriptor> layers)
    {
        if (layers.Count < 2) return;
        static string Common<T>(IEnumerable<T> values)
        {
            var distinct = values.Distinct().Take(2).ToArray();
            return distinct.Length == 1 ? Convert.ToString(distinct[0], System.Globalization.CultureInfo.CurrentCulture) ?? "" : "MIXED";
        }
        var detail = $"Visibility {Common(layers.Select(layer => layer.Visible))}; opacity {Common(layers.Select(layer => layer.Opacity))}; " +
            $"scale {Common(layers.Select(layer => layer.Scale))}; processing count {Common(layers.Select(layer => layer.ProcessingStack.Count))}. " +
            "Read-only common-property review; select one layer for governed edits.";
        SelectCompositingNode(new("review:layers", CompositingGraphNodeKind.Composite,
            layers.Count + " selected layers", detail, "REVIEW ONLY", CompositingGraphHealth.Normal, []));
        _compositingReviewCount = layers.Count;
        foreach (var layer in layers.OrderBy(layer => layer.Order))
            Add("review." + layer.LayerId, layer.LayerId, layer.ContentIdentity + " · order " + layer.Order, "COMMITTED");
        RaiseSelectionState();
    }
}
