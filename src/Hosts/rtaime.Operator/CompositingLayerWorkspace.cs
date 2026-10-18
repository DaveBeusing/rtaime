// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed partial class CompositingGraphNodeViewModel
{
    private OperatorCompositingLayerDescriptor? _layer;
    private bool _collapsed, _matchesSearch = true, _visibleDraft;
    private double _opacityDraft = 255;
    private bool _stateDirty, _stateConflict;
    public bool IsCollapsed { get => _collapsed; set => Set(ref _collapsed, value); }
    public bool MatchesSearch { get => _matchesSearch; internal set => Set(ref _matchesSearch, value); }
    public int ConfirmedOrder => _layer?.Order ?? 0;
    public string LayerEvidence => _layer is null ? "" :
        $"RUNTIME #{_layer.Order + 1} · {(_layer.Visible ? "VISIBLE" : "HIDDEN")} · {_layer.Opacity}/255 · {_layer.ContentIdentity}";
    public bool HasStateDraft => _stateDirty;
    public bool StateConflict => _stateConflict;
    public bool VisibleDraft { get => _visibleDraft; set { if (Set(ref _visibleDraft, value)) ChangedStateDraft(); } }
    public double OpacityDraft
    {
        get => _opacityDraft;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 255 || value != Math.Truncate(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (Set(ref _opacityDraft, value)) ChangedStateDraft();
        }
    }
    private void ChangedStateDraft()
    {
        _stateDirty = _layer is not null && (_layer.Visible != _visibleDraft || _layer.Opacity != _opacityDraft);
        OnPropertyChanged(nameof(HasStateDraft));
    }
    internal void ObserveLayer(OperatorCompositingLayerDescriptor layer)
    {
        var stateChanged = _layer is not null && (_layer.Visible != layer.Visible || _layer.Opacity != layer.Opacity);
        _layer = layer;
        if (!_stateDirty || _visibleDraft == layer.Visible && _opacityDraft == layer.Opacity) ResetStateDraft();
        else if (stateChanged) { _stateConflict = true; OnPropertyChanged(nameof(StateConflict)); }
        OnPropertyChanged(nameof(LayerEvidence));
        OnPropertyChanged(nameof(ConfirmedOrder));
    }
    internal void ResetStateDraft()
    {
        if (_layer is null) return;
        _visibleDraft = _layer.Visible; _opacityDraft = _layer.Opacity;
        _stateDirty = false; _stateConflict = false;
        foreach (var name in new[] { nameof(VisibleDraft), nameof(OpacityDraft), nameof(HasStateDraft), nameof(StateConflict) })
            OnPropertyChanged(name);
    }
    internal void ReviewStateDraft() { _stateConflict = false; OnPropertyChanged(nameof(StateConflict)); }
}

public sealed partial class CompositingGraphViewModel
{
    private string _searchText = "";
    private bool _showLayers = true, _selectingLayers, _reviewingLayers;
    private string? _missingSelectedNodeId;
    private List<string>? _orderDraft;
    private readonly Dictionary<string, string> _removedDrafts = new(StringComparer.Ordinal);
    private string _workspaceNotice = "";
    private double _workspaceWidth = 700;
    private bool _layerDraftInputsValid = true;
    public double LayerPanelWidth => Math.Min(220, Math.Max(0, _workspaceWidth * 0.38));
    public void UpdateWorkspaceWidth(double width) { if (double.IsFinite(width) && width > 0) { _workspaceWidth = width; OnPropertyChanged(nameof(LayerPanelWidth)); } }
    public bool LayerDraftInputsValid { get => _layerDraftInputsValid; set { _layerDraftInputsValid = value; SynchronizeSelectedLayerDraft(); } }
    public ICommand RecallSceneCommand => _operator.ActivateSceneCommand;
    public ObservableCollection<CompositingGraphNodeViewModel> Layers { get; } = [];
    public ObservableCollection<CompositingGraphNodeViewModel> VisibleNodes { get; } = [];
    public ObservableCollection<CompositingGraphConnectionViewModel> VisibleConnections { get; } = [];
    public ICommand ToggleLayerListCommand { get; private set; } = null!;
    public ICommand CollapseLayerCommand { get; private set; } = null!;
    public ICommand ApplyLayerDraftCommand { get; private set; } = null!;
    public ICommand DiscardLayerDraftCommand { get; private set; } = null!;
    public ICommand ReviewLayerDraftCommand { get; private set; } = null!;
    public ICommand ApplyOrderDraftCommand { get; private set; } = null!;
    public ICommand DiscardOrderDraftCommand { get; private set; } = null!;
    public bool ExtendSelection { get; set; }
    public bool ShowLayers { get => _showLayers; set => Set(ref _showLayers, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value ?? "")) RefreshViewportProjection(); }
    }
    public int SelectedLayerCount => Layers.Count(layer => layer.IsSelected);
    public CompositingGraphNodeViewModel? SelectedLayerRow => SelectedLayerCount == 1 ? Layers.First(layer => layer.IsSelected) : null;
    public string WorkspaceNotice { get => _workspaceNotice; private set => Set(ref _workspaceNotice, value); }
    public string SceneDeclaration => _operator.SelectedScene is { } scene
        ? $"SCENE DESIRED · {scene.Name} · {scene.DesiredLayerCount} declared layers" : "SCENE DESIRED · none selected";
    public string RuntimeEvidence => $"{(_operator.IsStale || !_operator.IsConnected ? "STALE / DISCONNECTED" : "RUNTIME OBSERVED")} · {_operator.CompositingLayers.Count} layers · active scene: {_operator.ActiveSceneName}";
    public bool CanStageLayerDraft => SelectedLayerCount == 1 && SelectedLayer() is { Kind: 2 or 3 };
    public string SelectionEvidence => SelectedLayerCount == 1 && !CanStageLayerDraft ? "READ ONLY · layer state capability is unavailable" : SelectedLayerCount > 1
        ? $"{SelectedLayerCount} layers · common-property review only; no atomic batch edit"
        : SelectedLayerRow is { StateConflict: true } ? "DRAFT CONFLICT · Runtime changed; REVIEW before APPLY"
        : SelectedLayerRow is { HasStateDraft: true } ? "LOCAL DRAFT · APPLY confirms; graph remains Runtime evidence"
        : "Select a Layer / Transform / Processing node; detailed edits use the shared Inspector.";
    public string OrderEvidence => _orderDraft is null ? "RUNTIME ORDER" :
        (_orderDraft.ToHashSet(StringComparer.Ordinal).SetEquals(_operator.CompositingLayers.Select(layer => layer.LayerId))
            ? "LOCAL ORDER DRAFT · " : "STALE ORDER DRAFT · layer set changed; discard and restage · ") + string.Join(" → ", _orderDraft);
    public InspectorPropertyDefinition OpacityDefinition { get; } = new("compositing.opacity", "Opacity", 0, 255, 255, 1, "8-bit", true);
    private void InitializeLayerWorkspace()
    {
        ToggleLayerListCommand = new CompositingGraphCommand(_ => ShowLayers = !ShowLayers);
        CollapseLayerCommand = new CompositingGraphCommand(value =>
        {
            if (value is CompositingGraphNodeViewModel row) { row.IsCollapsed = !row.IsCollapsed; RefreshViewportProjection(); }
        });
        ApplyLayerDraftCommand = new AsyncRelayCommand(async () =>
        {
            var row = SelectedLayerRow;
            var layer = SelectedLayer();
            if (row is null || layer is null || !CanApplyLayerDraft()) return;
            LayerCommandStatus = "APPLYING";
            await _operator.SetCompositingLayerStateAsync(layer.LayerId, row.VisibleDraft, checked((byte)row.OpacityDraft));
            LayerCommandStatus = _operator.LastError is null ? "CONFIRMED" : "REJECTED · draft retained";
            SynchronizeSelectedLayerDraft();
        }, CanApplyLayerDraft);
        DiscardLayerDraftCommand = new CompositingGraphCommand(_ => { SelectedLayerRow?.ResetStateDraft(); SynchronizeSelectedLayerDraft(); });
        ReviewLayerDraftCommand = new CompositingGraphCommand(_ => { SelectedLayerRow?.ReviewStateDraft(); SynchronizeSelectedLayerDraft(); });
        ApplyOrderDraftCommand = new AsyncRelayCommand(async () =>
        {
            if (!CanApplyOrderDraft()) return;
            var submitted = _orderDraft!.ToArray();
            LayerCommandStatus = "APPLYING";
            await _operator.ReorderCompositingLayersAsync(submitted);
            if (_operator.LastError is null) _orderDraft = null;
            LayerCommandStatus = _operator.LastError is null ? "CONFIRMED" : "REJECTED · order draft retained";
            SynchronizeLayers();
        }, CanApplyOrderDraft);
        DiscardOrderDraftCommand = new CompositingGraphCommand(_ => { _orderDraft = null; SynchronizeLayers(); });
    }
    private bool CanApplyLayerDraft() => LayerDraftInputsValid && CanEditSelectedLayer() &&
        SelectedLayerRow is { HasStateDraft: true, StateConflict: false };
    private bool CanApplyOrderDraft() => _operator.CanManageCompositingLayers() && _orderDraft is not null &&
        _orderDraft.Count == _operator.CompositingLayers.Count &&
        _orderDraft.ToHashSet(StringComparer.Ordinal).SetEquals(_operator.CompositingLayers.Select(layer => layer.LayerId));
    public void StageLayerOrder(string sourceId, string targetId)
    {
        var ids = (_orderDraft ?? _operator.CompositingLayers.OrderBy(layer => layer.Order).Select(layer => layer.LayerId).ToList()).ToList();
        if (sourceId == targetId || !ids.Contains(sourceId) || !ids.Contains(targetId)) return;
        ids.Remove(sourceId);
        ids.Insert(ids.IndexOf(targetId), sourceId);
        _orderDraft = ids;
        SynchronizeLayers();
    }
    public void SelectLayers(IEnumerable<CompositingGraphNodeViewModel> selection)
    {
        if (_selectingLayers) return;
        _selectingLayers = true;
        try
        {
            var selected = selection.Where(Layers.Contains).Distinct().ToArray();
            _missingSelectedNodeId = null;
            _reviewingLayers = selected.Length > 1;
            foreach (var node in Nodes) node.IsSelected = selected.Contains(node);
            SelectedNode = selected.LastOrDefault();
            if (selected.Length == 1) _inspector.SelectCompositingNode(selected[0].Projection);
            else if (selected.Length > 1) _inspector.ReviewCompositingLayers(
                _operator.CompositingLayers.Where(layer => selected.Any(node => ResolveLayerId(node.Id) == layer.LayerId)).ToArray());
            SynchronizeSelectedLayerDraft();
            RefreshLayerCommandState();
        }
        finally { _selectingLayers = false; }
    }
    private void SynchronizeLayers()
    {
        var ordered = _operator.CompositingLayers.OrderBy(layer =>
            _orderDraft is not null && _orderDraft.Contains(layer.LayerId) ? _orderDraft.IndexOf(layer.LayerId) : layer.Order).ToArray();
        foreach (var layer in ordered)
            Nodes.FirstOrDefault(node => node.Id == "layer:" + layer.LayerId)?.ObserveLayer(layer);
        Reconcile(Layers, ordered.Select(layer => Nodes.First(node => node.Id == "layer:" + layer.LayerId)).ToArray());
        if (SelectedLayerCount > 1)
        {
            _reviewingLayers = true;
            _inspector.ReviewCompositingLayers(ordered.Where(layer => Layers.Any(row => row.IsSelected && ResolveLayerId(row.Id) == layer.LayerId)).ToArray());
        }
        else if (_reviewingLayers && SelectedLayerRow is { } remaining)
        {
            _reviewingLayers = false;
            SelectedNode = remaining;
            _inspector.SelectCompositingNode(remaining.Projection);
        }
        SynchronizeSelectedLayerDraft();
        foreach (var property in new[] { nameof(SceneDeclaration), nameof(RuntimeEvidence), nameof(OrderEvidence) }) OnPropertyChanged(property);
        (ApplyOrderDraftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    private void SynchronizeSelectedLayerDraft()
    {
        OnPropertyChanged(nameof(CanStageLayerDraft));
        OnPropertyChanged(nameof(SelectedLayerRow));
        OnPropertyChanged(nameof(SelectedLayerCount));
        OnPropertyChanged(nameof(SelectionEvidence));
        (ApplyLayerDraftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    private void RetainMissingLayerDraft(CompositingGraphNodeViewModel node)
    {
        if (!node.HasStateDraft) return;
        var detail = $"{node.Title}: visibility={node.VisibleDraft}, opacity={node.OpacityDraft}/255";
        if (_removedDrafts.Count == 8 && !_removedDrafts.ContainsKey(node.Id))
        {
            WorkspaceNotice = "Draft recovery is full. Review or discard stored intent before changing the composition.";
            return;
        }
        _removedDrafts[node.Id] = detail;
        WorkspaceNotice = "REMOVED LAYER · local intent retained for review: " + string.Join("; ", _removedDrafts.Values);
    }
    private bool Matches(CompositingGraphNodeViewModel node)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var owner = ResolveLayerId(node.Id);
        return (node.Title + " " + node.Id + " " + node.Detail).Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            owner is not null && Nodes.Any(layer => layer.Id == "layer:" + owner &&
                (layer.Title + " " + layer.Id + " " + layer.Detail).Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }
    private void RefreshViewportProjection()
    {
        if (Nodes is null) return;
        var left = -PanX / Zoom - 32; var top = -PanY / Zoom - 32;
        var right = left + _viewportWidth / Zoom + 64; var bottom = top + _viewportHeight / Zoom + 64;
        foreach (var layer in Layers) layer.MatchesSearch = Matches(layer);
        var visible = Nodes.Where(node => Matches(node) && node.X + CompositingGraphNodeViewModel.NodeWidth >= left &&
            node.X <= right && node.Y + CompositingGraphNodeViewModel.NodeHeight >= top && node.Y <= bottom &&
            !(node.Projection.Kind is CompositingGraphNodeKind.Transform or CompositingGraphNodeKind.Processing &&
              Layers.Any(layer => layer.IsCollapsed && ResolveLayerId(layer.Id) == ResolveLayerId(node.Id)))).ToArray();
        Reconcile(VisibleNodes, visible);
        // A connection is shown only with both of its real semantic endpoints present.
        var ids = visible.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Reconcile(VisibleConnections, Connections.Where(connection => GraphConnectionVisible(connection, ids)).ToArray());
    }
    private bool GraphConnectionVisible(CompositingGraphConnectionViewModel connection, HashSet<string> ids) =>
        connection.HasVisibleEndpoints(ids);
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> incoming)
    {
        for (var i = target.Count - 1; i >= 0; i--) if (!incoming.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < incoming.Count; i++)
        {
            var existing = target.IndexOf(incoming[i]);
            if (existing < 0) target.Insert(i, incoming[i]);
            else if (existing != i) target.Move(existing, i);
        }
    }
}
