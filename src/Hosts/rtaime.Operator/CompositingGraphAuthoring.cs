// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

// Validate the observed topology before drawing live connections. This does not
// authorize rewiring: Runtime/Control remain the owners of the execution graph.
public sealed record CompositingGraphTopologyAssessment(bool IsValid, string Detail);

public static class CompositingGraphTopologyValidator
{
    public static CompositingGraphTopologyAssessment Validate(CompositingGraphProjection graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var nodes = new Dictionary<string, CompositingGraphNodeProjection>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !nodes.TryAdd(node.Id, node))
                return Invalid("Duplicate or missing node identity");
            if (node.Ports.GroupBy(port => (port.Direction, port.Id))
                .Any(group => string.IsNullOrWhiteSpace(group.Key.Id) || group.Count() != 1))
                return Invalid("Duplicate or missing node port");
        }

        var outgoing = nodes.Keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        var inbound = nodes.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        var edges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in graph.Connections)
        {
            if (string.IsNullOrWhiteSpace(connection.Id) || !edges.Add(connection.Id))
                return Invalid("Duplicate or missing connection identity");
            if (!nodes.TryGetValue(connection.FromNodeId, out var from) ||
                !nodes.TryGetValue(connection.ToNodeId, out var to))
                return Invalid("Connection references a missing endpoint");
            if (connection.FromNodeId == connection.ToNodeId)
                return Invalid("Self-referential connection");
            if (!from.Ports.Any(port => port.Id == connection.FromPortId &&
                    port.Direction == CompositingGraphPortDirection.Output) ||
                !to.Ports.Any(port => port.Id == connection.ToPortId &&
                    port.Direction == CompositingGraphPortDirection.Input))
                return Invalid("Connection port or direction is unsupported");
            outgoing[connection.FromNodeId].Add(connection.ToNodeId);
            inbound[connection.ToNodeId]++;
        }

        var ready = new Queue<string>(inbound.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            visited++;
            foreach (var next in outgoing[current])
                if (--inbound[next] == 0) ready.Enqueue(next);
        }
        return visited == nodes.Count
            ? new(true, "OBSERVED ACYCLIC TOPOLOGY · CONNECTIONS READ ONLY")
            : Invalid("Cyclic connection");
    }

    private static CompositingGraphTopologyAssessment Invalid(string reason) =>
        new(false, "INVALID OBSERVED GRAPH · " + reason + " · links withheld; no Program mutation");
}

public sealed partial class CompositingGraphNodeViewModel
{
    public string CapabilityLabel => _projection.Kind switch
    {
        CompositingGraphNodeKind.Layer => "LAYER STATE · LOCAL DRAFT / APPLY",
        CompositingGraphNodeKind.Transform => "STATIC TRANSFORM · INSPECTOR APPLY",
        CompositingGraphNodeKind.Processing => "STATIC FX · INSPECTOR APPLY",
        _ => "OBSERVE ONLY · CONNECTIONS LOCKED"
    };
}

public sealed partial class CompositingGraphViewModel
{
    private sealed record FxOrderDraft(
        string LayerId,
        string ContentIdentity,
        int LayerKind,
        ulong Revision,
        OperatorCompositingProcessingNodeDescriptor[] Baseline,
        string[] OrderedIds);

    private FxOrderDraft? _fxOrderDraft;
    private bool _fxOrderApplying;
    private string _fxOrderResult = "FX ORDER · CONFIRMED OBSERVATION";
    private string _topologyEvidence = "OBSERVED TOPOLOGY · CONNECTIONS READ ONLY";
    private string _layoutEvidence = "AUTO LAYOUT · PRESENTATION ONLY";
    private CompositingGraphConnectionViewModel? _selectedConnection;

    public ICommand SelectConnectionCommand { get; private set; } = null!;
    public ICommand MoveProcessingEarlierDraftCommand { get; private set; } = null!;
    public ICommand MoveProcessingLaterDraftCommand { get; private set; } = null!;
    public ICommand ApplyProcessingOrderDraftCommand { get; private set; } = null!;
    public ICommand DiscardProcessingOrderDraftCommand { get; private set; } = null!;

    public CompositingGraphConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        private set
        {
            if (!Set(ref _selectedConnection, value)) return;
            OnPropertyChanged(nameof(ConnectionEvidence));
        }
    }

    public string ConnectionEvidence => SelectedConnection is { } connection
        ? ("CONNECTION " + connection.FromNodeId + "/" + connection.FromPortId + " → " +
            connection.ToNodeId + "/" + connection.ToPortId + " · " +
            (connection.IsActive ? "OBSERVED ACTIVE" : "OBSERVED INACTIVE") +
            " · INSPECTION ONLY")
        : "Select a connection to inspect its confirmed endpoints · REWIRE UNAVAILABLE";
    public string TopologyEvidence
    {
        get => _topologyEvidence;
        private set => Set(ref _topologyEvidence, value);
    }
    public string LayoutEvidence
    {
        get => _layoutEvidence;
        private set => Set(ref _layoutEvidence, value);
    }
    public string RuntimeRevisionEvidence => _operator.Client?.Snapshot is { } snapshot
        ? ("CONTROL REVISION " + snapshot.Production.Revision.Value + " · " +
            (_operator.IsConnected && !_operator.IsStale ? "OBSERVED" : "STALE / DISCONNECTED"))
        : "NO AUTHORITATIVE REVISION · GRAPH UNVERIFIED";
    public string FxOrderEvidence => _fxOrderDraft is not { } draft
        ? _fxOrderResult
        : (!IsFxOrderBaselineCurrent(draft) ? "STALE FX ORDER DRAFT · DISCARD REQUIRED" :
            _fxOrderApplying ? "FX ORDER APPLYING · AWAITING RUNTIME OBSERVATION" :
            ("LOCAL FX ORDER DRAFT · NOT ON PROGRAM · " + draft.LayerId + " · REV " +
                draft.Revision + " · " + string.Join(" → ", draft.OrderedIds)));
    public bool HasFxOrderDraft => _fxOrderDraft is not null;

    private void InitializeGraphAuthoring()
    {
        SelectConnectionCommand = new CompositingGraphCommand(parameter =>
        {
            if (parameter is CompositingGraphConnectionViewModel connection &&
                ShowAdvancedGraph && Connections.Contains(connection))
                SelectConnection(connection);
        });
        MoveProcessingEarlierDraftCommand = new OperatorShellCommand(
            () => StageSelectedProcessingMove(-1), () => CanStageSelectedProcessingMove(-1));
        MoveProcessingLaterDraftCommand = new OperatorShellCommand(
            () => StageSelectedProcessingMove(1), () => CanStageSelectedProcessingMove(1));
        ApplyProcessingOrderDraftCommand = new AsyncRelayCommand(
            ApplyProcessingOrderDraftAsync, CanApplyProcessingOrderDraft);
        DiscardProcessingOrderDraftCommand = new OperatorShellCommand(
            DiscardProcessingOrderDraft, () => _fxOrderDraft is not null);
    }

    public void SelectConnection(CompositingGraphConnectionViewModel connection)
    {
        if (!ShowAdvancedGraph || !Connections.Contains(connection)) return;
        var target = Nodes.FirstOrDefault(node => node.Id == connection.ToNodeId);
        if (target is null) return;
        SelectNode(target); // The single Inspector keeps its existing selection owner.
        foreach (var candidate in Connections) candidate.IsSelected = ReferenceEquals(candidate, connection);
        SelectedConnection = connection;
    }

    private void RestoreConnectionSelection()
    {
        if (SelectedConnection is not { } previous) return;
        var current = Connections.FirstOrDefault(connection => connection.Id == previous.Id);
        if (current is null)
        {
            ClearConnectionSelection();
            return;
        }
        current.IsSelected = true;
        SelectedConnection = current;
        OnPropertyChanged(nameof(ConnectionEvidence));
    }

    private void ClearConnectionSelection()
    {
        foreach (var connection in Connections) connection.IsSelected = false;
        SelectedConnection = null;
    }

    private OperatorCompositingLayerDescriptor? FxDraftLayer(FxOrderDraft draft) =>
        _operator.CompositingLayers.FirstOrDefault(layer => layer.LayerId == draft.LayerId);

    private bool IsFxOrderBaselineCurrent(FxOrderDraft draft)
    {
        var layer = FxDraftLayer(draft);
        return _operator.IsConnected && !_operator.IsStale &&
            _operator.Client?.Snapshot?.Production.Revision.Value == draft.Revision &&
            layer is not null && layer.Kind == draft.LayerKind &&
            layer.ContentIdentity == draft.ContentIdentity &&
            layer.ProcessingStack.SequenceEqual(draft.Baseline) &&
            draft.OrderedIds.Length == draft.Baseline.Length &&
            draft.OrderedIds.ToHashSet(StringComparer.Ordinal).SetEquals(draft.Baseline.Select(node => node.NodeId));
    }

    private bool CanStageSelectedProcessingMove(int delta)
    {
        if (!ShowAdvancedGraph || _fxOrderApplying || !_operator.CanManageCompositingLayers() ||
            SelectedNode is not { } selected || !selected.Id.StartsWith("processing:", StringComparison.Ordinal) ||
            SelectedLayerCount > 1)
            return false;
        var layerId = ResolveLayerId(selected.Id);
        if (layerId is null) return false;
        var layer = _operator.CompositingLayers.FirstOrDefault(candidate => candidate.LayerId == layerId);
        if (layer is not { Kind: 2 or 3 } || layer.ProcessingStack.Count < 2) return false;
        if (_fxOrderDraft is { } draft && (draft.LayerId != layerId || !IsFxOrderBaselineCurrent(draft)))
            return false;
        var order = _fxOrderDraft?.OrderedIds ?? layer.ProcessingStack.Select(node => node.NodeId).ToArray();
        var selectedId = selected.Id[("processing:" + layerId + ":").Length..];
        var index = Array.IndexOf(order, selectedId);
        return index >= 0 && index + delta >= 0 && index + delta < order.Length;
    }

    public bool StageSelectedProcessingMove(int delta)
    {
        if (delta is not (-1 or 1) || !CanStageSelectedProcessingMove(delta)) return false;
        var selected = SelectedNode!;
        var layerId = ResolveLayerId(selected.Id)!;
        var layer = _operator.CompositingLayers.Single(candidate => candidate.LayerId == layerId);
        var revision = _operator.Client?.Snapshot?.Production.Revision.Value;
        if (revision is null) return false;
        var draft = _fxOrderDraft ?? new FxOrderDraft(layerId, layer.ContentIdentity, layer.Kind,
            revision.Value, layer.ProcessingStack.ToArray(),
            layer.ProcessingStack.Select(node => node.NodeId).ToArray());
        var order = draft.OrderedIds.ToArray();
        var selectedId = selected.Id[("processing:" + layerId + ":").Length..];
        var index = Array.IndexOf(order, selectedId);
        (order[index], order[index + delta]) = (order[index + delta], order[index]);
        _fxOrderDraft = draft with { OrderedIds = order };
        _fxOrderResult = "LOCAL FX ORDER DRAFT · APPLY REQUIRED";
        NotifyGraphAuthoringChanged();
        return true;
    }

    private bool CanApplyProcessingOrderDraft() =>
        !_fxOrderApplying && _operator.CanManageCompositingLayers() &&
        _fxOrderDraft is { } draft && IsFxOrderBaselineCurrent(draft) &&
        !draft.OrderedIds.SequenceEqual(draft.Baseline.Select(node => node.NodeId));

    private async Task ApplyProcessingOrderDraftAsync()
    {
        if (!CanApplyProcessingOrderDraft() || _fxOrderDraft is not { } draft) return;
        var stack = draft.OrderedIds.Select(id =>
            draft.Baseline.Single(node => node.NodeId == id)).ToArray();
        _fxOrderApplying = true;
        NotifyGraphAuthoringChanged();
        try
        {
            await _operator.SetCompositingLayerProcessingStackAsync(draft.LayerId, stack);
            var observed = FxDraftLayer(draft);
            if (_operator.LastError is null && observed is not null &&
                observed.Kind == draft.LayerKind && observed.ContentIdentity == draft.ContentIdentity &&
                observed.ProcessingStack.SequenceEqual(stack))
            {
                _fxOrderDraft = null;
                _fxOrderResult = "FX ORDER CONFIRMED · RUNTIME OBSERVED";
            }
            else
                _fxOrderResult = "FX ORDER REJECTED / UNCONFIRMED · LOCAL DRAFT RETAINED";
        }
        finally
        {
            _fxOrderApplying = false;
            NotifyGraphAuthoringChanged();
        }
    }

    public void DiscardProcessingOrderDraft()
    {
        _fxOrderDraft = null;
        _fxOrderResult = "LOCAL FX ORDER DISCARDED · RUNTIME UNCHANGED";
        NotifyGraphAuthoringChanged();
    }

    public bool TryMoveNode(CompositingGraphNodeViewModel node, double deltaViewportX, double deltaViewportY)
    {
        if (!ShowAdvancedGraph || !IsSelectMode || !Nodes.Contains(node) ||
            !double.IsFinite(deltaViewportX) || !double.IsFinite(deltaViewportY) ||
            !double.IsFinite(Zoom) || Zoom <= 0) return false;
        var x = Math.Clamp(node.X + deltaViewportX / Zoom, 0, Math.Max(0, CanvasWidth - CompositingGraphNodeViewModel.NodeWidth));
        var y = Math.Clamp(node.Y + deltaViewportY / Zoom, 0, Math.Max(0, CanvasHeight - CompositingGraphNodeViewModel.NodeHeight));
        if (Math.Abs(x - node.X) < .01 && Math.Abs(y - node.Y) < .01) return false;
        node.X = x;
        node.Y = y;
        LayoutEvidence = "LOCAL NODE ARRANGEMENT · NOT A RUNTIME TRANSFORM";
        return true;
    }

    private void NotifyGraphAuthoringSelectionChanged()
    {
        OnPropertyChanged(nameof(FxOrderEvidence));
        OnPropertyChanged(nameof(RuntimeRevisionEvidence));
        (MoveProcessingEarlierDraftCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
        (MoveProcessingLaterDraftCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
        (ApplyProcessingOrderDraftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DiscardProcessingOrderDraftCommand as OperatorShellCommand)?.RaiseCanExecuteChanged();
    }

    private void NotifyGraphAuthoringChanged()
    {
        OnPropertyChanged(nameof(HasFxOrderDraft));
        NotifyGraphAuthoringSelectionChanged();
    }
}
