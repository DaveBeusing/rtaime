// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using rtaime.Client;

namespace rtaime.Operator;

public enum CompositingGraphInteractionMode
{
	Select,
	Pan
}

public sealed class CompositingGraphNodeViewModel : INotifyPropertyChanged
{
	public const double NodeWidth = 214;
	public const double NodeHeight = 116;

	private CompositingGraphNodeProjection _projection;
	private double _x;
	private double _y;
	private bool _isSelected;

	public CompositingGraphNodeViewModel(CompositingGraphNodeProjection projection)
	{
		_projection = projection ?? throw new ArgumentNullException(nameof(projection));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public CompositingGraphNodeProjection Projection => _projection;
	public string Id => _projection.Id;
	public string Title => _projection.Title;
	public string Detail => _projection.Detail;
	public string Status => _projection.Status;
	public string KindLabel => _projection.Kind.ToString().ToUpperInvariant();
	public string RoleLabel => ResolveRoleLabel(_projection);
	public string HealthLabel => _projection.Health.ToString().ToUpperInvariant();
	public bool IsError => _projection.Health == CompositingGraphHealth.Error;
	public bool IsDegraded => _projection.Health == CompositingGraphHealth.Degraded;
	public bool CanRewire => _projection.CanRewire;
	public string InputPorts => FormatPorts(CompositingGraphPortDirection.Input);
	public string OutputPorts => FormatPorts(CompositingGraphPortDirection.Output);

	public double X
	{
		get => _x;
		set => Set(ref _x, double.IsFinite(value) ? value : 0);
	}

	public double Y
	{
		get => _y;
		set => Set(ref _y, double.IsFinite(value) ? value : 0);
	}

	public bool IsSelected
	{
		get => _isSelected;
		set => Set(ref _isSelected, value);
	}

	public void Apply(CompositingGraphNodeProjection projection)
	{
		ArgumentNullException.ThrowIfNull(projection);
		if (!string.Equals(Id, projection.Id, StringComparison.Ordinal))
			throw new InvalidOperationException("Graph node identity cannot change.");

		_projection = projection;
		OnPropertyChanged(nameof(Projection));
		OnPropertyChanged(nameof(Title));
		OnPropertyChanged(nameof(Detail));
		OnPropertyChanged(nameof(Status));
		OnPropertyChanged(nameof(KindLabel));
		OnPropertyChanged(nameof(RoleLabel));
		OnPropertyChanged(nameof(HealthLabel));
		OnPropertyChanged(nameof(IsError));
		OnPropertyChanged(nameof(IsDegraded));
		OnPropertyChanged(nameof(CanRewire));
		OnPropertyChanged(nameof(InputPorts));
		OnPropertyChanged(nameof(OutputPorts));
	}

	private static string ResolveRoleLabel(CompositingGraphNodeProjection projection) =>
		projection.Id switch
		{
			"routing" => "OUTPUT ROUTER",
			"graphics-transform" => "TRANSFORM",
			"composite" => "MERGE",
			"recorder" => "RECORDER",
			"preview-output" or "program-output" => "OUTPUT",
			_ when projection.Kind == CompositingGraphNodeKind.Layer => "COMPOSITING LAYER",
			_ when projection.Kind == CompositingGraphNodeKind.Source => "MEDIA INPUT",
			_ => projection.Kind.ToString().ToUpperInvariant()
		};

	private string FormatPorts(CompositingGraphPortDirection direction)
	{
		var ports = _projection.Ports
			.Where(port => port.Direction == direction)
			.Select(port => port.Label)
			.ToArray();
		return ports.Length == 0 ? "—" : string.Join(" · ", ports);
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CompositingGraphConnectionViewModel : INotifyPropertyChanged
{
	private CompositingGraphConnectionProjection _projection;
	private readonly CompositingGraphNodeViewModel _from;
	private readonly CompositingGraphNodeViewModel _to;
	private Geometry _geometry = Geometry.Empty;
	private PointCollection _arrowPoints = [];

	public CompositingGraphConnectionViewModel(
		CompositingGraphConnectionProjection projection,
		CompositingGraphNodeViewModel from,
		CompositingGraphNodeViewModel to)
	{
		_projection = projection ?? throw new ArgumentNullException(nameof(projection));
		_from = from ?? throw new ArgumentNullException(nameof(from));
		_to = to ?? throw new ArgumentNullException(nameof(to));
		UpdateGeometry();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Id => _projection.Id;
	public bool IsActive => _projection.IsActive;
	public double Opacity => IsActive ? 0.9 : 0.35;
	public Geometry Geometry => _geometry;
	public PointCollection ArrowPoints => _arrowPoints;

	public void Apply(CompositingGraphConnectionProjection projection)
	{
		ArgumentNullException.ThrowIfNull(projection);
		if (!string.Equals(Id, projection.Id, StringComparison.Ordinal))
			throw new InvalidOperationException("Graph connection identity cannot change.");
		_projection = projection;
		OnPropertyChanged(nameof(IsActive));
		OnPropertyChanged(nameof(Opacity));
		UpdateGeometry();
	}

	public void UpdateGeometry()
	{
		var start = new System.Windows.Point(
			_from.X + CompositingGraphNodeViewModel.NodeWidth,
			_from.Y + (CompositingGraphNodeViewModel.NodeHeight / 2));
		var end = new System.Windows.Point(
			_to.X,
			_to.Y + (CompositingGraphNodeViewModel.NodeHeight / 2));
		var controlDistance = Math.Max(70, Math.Abs(end.X - start.X) * 0.45);
		var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
		figure.Segments.Add(new BezierSegment(
			new System.Windows.Point(start.X + controlDistance, start.Y),
			new System.Windows.Point(end.X - controlDistance, end.Y),
			end,
			true));
		var geometry = new PathGeometry();
		geometry.Figures.Add(figure);
		_geometry = geometry;
		_arrowPoints =
		[
			end,
			new System.Windows.Point(end.X - 9, end.Y - 5),
			new System.Windows.Point(end.X - 9, end.Y + 5)
		];
		OnPropertyChanged(nameof(Geometry));
		OnPropertyChanged(nameof(ArrowPoints));
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CompositingGraphViewModel : INotifyPropertyChanged, IDisposable
{
	private const double MinimumZoom = 0.35;
	private const double MaximumZoom = 2.5;
	private readonly OperatorViewModel _operator;
	private readonly MediaPoolInspectorViewModel _inspector;
	private readonly List<OperatorSourceTileViewModel> _sourceSubscriptions = [];
	private CompositingGraphNodeViewModel? _selectedNode;
	private CompositingGraphInteractionMode _interactionMode = CompositingGraphInteractionMode.Select;
	private double _zoom = 1;
	private double _panX;
	private double _panY;
	private double _canvasWidth = 1520;
	private double _canvasHeight = 760;
	private double _viewportWidth = 1000;
	private double _viewportHeight = 650;
	private int _healthFingerprint;
	private bool _hasHealthFingerprint;
	private long _healthRevision;
	private string _layerCommandStatus = "CONFIRMED";

	public CompositingGraphViewModel(
		OperatorViewModel @operator,
		MediaPoolInspectorViewModel inspector)
	{
		_operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
		_inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));

		Nodes = [];
		Connections = [];
		SelectNodeCommand = new CompositingGraphCommand(parameter =>
		{
			if (parameter is CompositingGraphNodeViewModel node)
				SelectNode(node);
		});
		SelectModeCommand = new CompositingGraphCommand(_ => InteractionMode = CompositingGraphInteractionMode.Select);
		PanModeCommand = new CompositingGraphCommand(_ => InteractionMode = CompositingGraphInteractionMode.Pan);
		ResetViewCommand = new CompositingGraphCommand(_ => ResetView());
		FitCommand = new CompositingGraphCommand(_ => Fit());
		AutoLayoutCommand = new CompositingGraphCommand(_ => AutoLayout());
		ToggleSelectedLayerCommand = new AsyncRelayCommand(ToggleSelectedLayerAsync, CanEditSelectedLayer);
		MoveSelectedLayerUpCommand = new AsyncRelayCommand(() => MoveSelectedLayerAsync(1), () => CanMoveSelectedLayer(1));
		MoveSelectedLayerDownCommand = new AsyncRelayCommand(() => MoveSelectedLayerAsync(-1), () => CanMoveSelectedLayer(-1));
		DecreaseSelectedLayerOpacityCommand = new AsyncRelayCommand(() => AdjustSelectedLayerOpacityAsync(-16), CanEditSelectedLayer);
		IncreaseSelectedLayerOpacityCommand = new AsyncRelayCommand(() => AdjustSelectedLayerOpacityAsync(16), CanEditSelectedLayer);

		_operator.PropertyChanged += OnOperatorPropertyChanged;
		_operator.Sources.CollectionChanged += OnSourcesChanged;
		RewireSourceSubscriptions();
		Refresh();
		AutoLayout();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<CompositingGraphNodeViewModel> Nodes { get; }
	public ObservableCollection<CompositingGraphConnectionViewModel> Connections { get; }
	public long HealthRevision => _healthRevision;
	public ICommand SelectNodeCommand { get; }
	public ICommand SelectModeCommand { get; }
	public ICommand PanModeCommand { get; }
	public ICommand ResetViewCommand { get; }
	public ICommand FitCommand { get; }
	public ICommand AutoLayoutCommand { get; }
	public ICommand ToggleSelectedLayerCommand { get; }
	public ICommand MoveSelectedLayerUpCommand { get; }
	public ICommand MoveSelectedLayerDownCommand { get; }
	public ICommand DecreaseSelectedLayerOpacityCommand { get; }
	public ICommand IncreaseSelectedLayerOpacityCommand { get; }
	public string LayerCommandStatus
	{
		get => _layerCommandStatus;
		private set => Set(ref _layerCommandStatus, value);
	}

	public CompositingGraphNodeViewModel? SelectedNode
	{
		get => _selectedNode;
		private set => Set(ref _selectedNode, value);
	}

	public CompositingGraphInteractionMode InteractionMode
	{
		get => _interactionMode;
		set
		{
			if (!Set(ref _interactionMode, value))
				return;
			OnPropertyChanged(nameof(InteractionModeLabel));
			OnPropertyChanged(nameof(IsSelectMode));
			OnPropertyChanged(nameof(IsPanMode));
		}
	}

	public string InteractionModeLabel => InteractionMode.ToString().ToUpperInvariant();
	public bool IsSelectMode => InteractionMode == CompositingGraphInteractionMode.Select;
	public bool IsPanMode => InteractionMode == CompositingGraphInteractionMode.Pan;

	public double Zoom
	{
		get => _zoom;
		private set
		{
			var normalized = Math.Clamp(double.IsFinite(value) ? value : 1, MinimumZoom, MaximumZoom);
			if (!Set(ref _zoom, normalized))
				return;
			OnPropertyChanged(nameof(ZoomLabel));
		}
	}

	public string ZoomLabel => $"{Zoom * 100:0}%";

	public double PanX
	{
		get => _panX;
		private set => Set(ref _panX, double.IsFinite(value) ? value : 0);
	}

	public double PanY
	{
		get => _panY;
		private set => Set(ref _panY, double.IsFinite(value) ? value : 0);
	}

	public double CanvasWidth
	{
		get => _canvasWidth;
		private set => Set(ref _canvasWidth, Math.Max(800, value));
	}

	public double CanvasHeight
	{
		get => _canvasHeight;
		private set => Set(ref _canvasHeight, Math.Max(560, value));
	}

	public void UpdateViewport(double width, double height)
	{
		if (double.IsFinite(width) && width > 0)
			_viewportWidth = width;
		if (double.IsFinite(height) && height > 0)
			_viewportHeight = height;
	}

	public void PanBy(double deltaX, double deltaY)
	{
		PanX += double.IsFinite(deltaX) ? deltaX : 0;
		PanY += double.IsFinite(deltaY) ? deltaY : 0;
	}

	public void ZoomAt(double factor, double viewportX, double viewportY)
	{
		if (!double.IsFinite(factor) || factor <= 0)
			return;
		var previous = Zoom;
		var next = Math.Clamp(previous * factor, MinimumZoom, MaximumZoom);
		if (Math.Abs(next - previous) < 0.0001)
			return;

		var worldX = (viewportX - PanX) / previous;
		var worldY = (viewportY - PanY) / previous;
		Zoom = next;
		PanX = viewportX - (worldX * next);
		PanY = viewportY - (worldY * next);
	}

	public void ResetView()
	{
		Zoom = 1;
		PanX = 18;
		PanY = 18;
	}

	public void Fit()
	{
		var scaleX = (_viewportWidth - 48) / Math.Max(1, CanvasWidth);
		var scaleY = (_viewportHeight - 48) / Math.Max(1, CanvasHeight);
		Zoom = Math.Clamp(Math.Min(scaleX, scaleY), MinimumZoom, 1);
		PanX = Math.Max(18, (_viewportWidth - (CanvasWidth * Zoom)) / 2);
		PanY = Math.Max(18, (_viewportHeight - (CanvasHeight * Zoom)) / 2);
	}

	public void AutoLayout()
	{
		var sources = Nodes
			.Where(node => node.Projection.Kind == CompositingGraphNodeKind.Source && node.Id.StartsWith("source:", StringComparison.Ordinal))
			.OrderBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
			.ThenBy(node => node.Id, StringComparer.Ordinal)
			.ToArray();

		for (var index = 0; index < sources.Length; index++)
		{
			sources[index].X = 40;
			sources[index].Y = 60 + (index * 132);
		}

		var graphicsY = Math.Max(60, 60 + (sources.Length * 132));
		Position("graphics", 40, graphicsY);
		Position("routing", 330, 130);
		Position("graphics-transform", 330, graphicsY);
		var layerNodes = Nodes
			.Where(node => node.Projection.Kind == CompositingGraphNodeKind.Layer)
			.OrderBy(node => node.Id, StringComparer.Ordinal)
			.ToArray();
		for (var index = 0; index < layerNodes.Length; index++)
		{
			layerNodes[index].X = 330;
			layerNodes[index].Y = Math.Max(280, graphicsY) + (index * 132);
		}
		Position("preview-output", 635, 60);
		var compositeY = layerNodes.Length == 0
			? Math.Max(250, graphicsY - 40)
			: Math.Max(250, Math.Max(280, graphicsY) + ((layerNodes.Length - 1) * 66));
		Position("composite", 635, compositeY);
		Position("program-output", 940, compositeY);
		Position("recorder", 1230, compositeY);

		CanvasWidth = 1490;
		CanvasHeight = Math.Max(650, graphicsY + 190 + Math.Max(0, layerNodes.Length - 1) * 132);
		UpdateConnections();
	}

	public void Refresh()
	{
		var graph = CompositingGraphProjector.Project(CreateProjectionInput());
		var incomingIds = graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

		for (var index = Nodes.Count - 1; index >= 0; index--)
		{
			if (!incomingIds.Contains(Nodes[index].Id))
			{
				if (ReferenceEquals(SelectedNode, Nodes[index]))
					SelectedNode = null;
				Nodes[index].PropertyChanged -= OnNodePropertyChanged;
				Nodes.RemoveAt(index);
			}
		}

		foreach (var projection in graph.Nodes)
		{
			var node = Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, projection.Id, StringComparison.Ordinal));
			if (node is null)
			{
				node = new CompositingGraphNodeViewModel(projection);
				node.PropertyChanged += OnNodePropertyChanged;
				Nodes.Add(node);
			}
			else
			{
				node.Apply(projection);
			}
		}

		var incomingConnections = graph.Connections.Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
		for (var index = Connections.Count - 1; index >= 0; index--)
		{
			if (!incomingConnections.Contains(Connections[index].Id))
				Connections.RemoveAt(index);
		}

		foreach (var projection in graph.Connections)
		{
			var from = Nodes.First(node => string.Equals(node.Id, projection.FromNodeId, StringComparison.Ordinal));
			var to = Nodes.First(node => string.Equals(node.Id, projection.ToNodeId, StringComparison.Ordinal));
			var connection = Connections.FirstOrDefault(candidate => string.Equals(candidate.Id, projection.Id, StringComparison.Ordinal));
			if (connection is null)
				Connections.Add(new CompositingGraphConnectionViewModel(projection, from, to));
			else
				connection.Apply(projection);
		}

		if (SelectedNode is { } selected)
		{
			var current = Nodes.FirstOrDefault(node => string.Equals(node.Id, selected.Id, StringComparison.Ordinal));
			if (current is not null)
			{
				current.IsSelected = true;
				SelectedNode = current;
				_inspector.SelectCompositingNode(current.Projection);
			}
		}

		UpdateConnections();
		PublishHealthRevision(graph.Nodes);
		RefreshLayerCommandState();
	}

	private OperatorCompositingLayerDescriptor? SelectedLayer()
	{
		if (SelectedNode is null || !SelectedNode.Id.StartsWith("layer:", StringComparison.Ordinal))
			return null;
		var layerId = SelectedNode.Id["layer:".Length..];
		return _operator.CompositingLayers.FirstOrDefault(layer =>
			string.Equals(layer.LayerId, layerId, StringComparison.Ordinal));
	}

	private bool CanEditSelectedLayer()
	{
		var layer = SelectedLayer();
		return layer is not null &&
			layer.Kind is 2 or 3 &&
			_operator.CanManageCompositingLayers();
	}

	private bool CanMoveSelectedLayer(int delta)
	{
		if (!_operator.CanManageCompositingLayers())
			return false;
		var selected = SelectedLayer();
		if (selected is null)
			return false;
		var ordered = _operator.CompositingLayers
			.OrderBy(layer => layer.Order)
			.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
			.ToArray();
		var index = Array.FindIndex(ordered, layer => string.Equals(layer.LayerId, selected.LayerId, StringComparison.Ordinal));
		var target = index + delta;
		return index >= 0 && target >= 0 && target < ordered.Length;
	}

	private async Task ToggleSelectedLayerAsync()
	{
		var layer = SelectedLayer();
		if (layer is null || !CanEditSelectedLayer())
			return;
		LayerCommandStatus = "APPLYING";
		await _operator.SetCompositingLayerStateAsync(layer.LayerId, !layer.Visible, layer.Opacity);
		LayerCommandStatus = _operator.LastError is null ? "CONFIRMED" : "REJECTED";
		RefreshLayerCommandState();
	}

	private async Task AdjustSelectedLayerOpacityAsync(int delta)
	{
		var layer = SelectedLayer();
		if (layer is null || !CanEditSelectedLayer())
			return;
		var opacity = checked((byte)Math.Clamp(layer.Opacity + delta, byte.MinValue, byte.MaxValue));
		if (opacity == layer.Opacity)
			return;
		LayerCommandStatus = "APPLYING";
		await _operator.SetCompositingLayerStateAsync(layer.LayerId, layer.Visible, opacity);
		LayerCommandStatus = _operator.LastError is null ? "CONFIRMED" : "REJECTED";
		RefreshLayerCommandState();
	}

	private async Task MoveSelectedLayerAsync(int delta)
	{
		var selected = SelectedLayer();
		if (selected is null || !CanMoveSelectedLayer(delta))
			return;
		var ordered = _operator.CompositingLayers
			.OrderBy(layer => layer.Order)
			.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
			.Select(layer => layer.LayerId)
			.ToList();
		var index = ordered.FindIndex(layerId => string.Equals(layerId, selected.LayerId, StringComparison.Ordinal));
		var target = index + delta;
		(ordered[index], ordered[target]) = (ordered[target], ordered[index]);
		LayerCommandStatus = "APPLYING";
		await _operator.ReorderCompositingLayersAsync(ordered);
		LayerCommandStatus = _operator.LastError is null ? "CONFIRMED" : "REJECTED";
		RefreshLayerCommandState();
	}

	private void RefreshLayerCommandState()
	{
		(ToggleSelectedLayerCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(MoveSelectedLayerUpCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(MoveSelectedLayerDownCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(DecreaseSelectedLayerOpacityCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(IncreaseSelectedLayerOpacityCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	public void Dispose()
	{
		_operator.PropertyChanged -= OnOperatorPropertyChanged;
		_operator.Sources.CollectionChanged -= OnSourcesChanged;
		foreach (var source in _sourceSubscriptions)
			source.PropertyChanged -= OnSourcePropertyChanged;
		_sourceSubscriptions.Clear();
		foreach (var node in Nodes)
			node.PropertyChanged -= OnNodePropertyChanged;
	}

	private CompositingGraphProjectionInput CreateProjectionInput() =>
		new(
			_operator.Sources.Select(source => new CompositingGraphSourceProjection(
				source.Id,
				source.Name,
				source.Type,
				source.Format,
				source.Health,
				source.IsPreview,
				source.IsProgram)).ToArray(),
			_operator.PreviewSourceId,
			_operator.ProgramSourceId,
			_operator.RuntimeStatus,
			_operator.RuntimeHealth,
			_operator.GpuProviderHealth,
			_operator.CommitStatus,
			_operator.TransitionStatus,
			_operator.CurrentFormat,
			_operator.VisualLayerStatus,
			_operator.GraphicsAssetName,
			_operator.GraphicsDimensions,
			_operator.GraphicsState,
			_operator.GraphicsVisible,
			_operator.GraphicsPositionX,
			_operator.GraphicsPositionY,
			_operator.GraphicsScale,
			_operator.RecordingStatus,
			_operator.RecordingError,
			_operator.CompositingLayers);

	private void SelectNode(CompositingGraphNodeViewModel node)
	{
		if (SelectedNode is { } previous && !ReferenceEquals(previous, node))
			previous.IsSelected = false;
		node.IsSelected = true;
		SelectedNode = node;
		_inspector.SelectCompositingNode(node.Projection);
		RefreshLayerCommandState();
	}

	private void Position(string id, double x, double y)
	{
		var node = Nodes.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
		if (node is null)
			return;
		node.X = x;
		node.Y = y;
	}

	private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		RewireSourceSubscriptions();
		Refresh();
		AutoLayout();
	}

	private void RewireSourceSubscriptions()
	{
		foreach (var source in _sourceSubscriptions)
			source.PropertyChanged -= OnSourcePropertyChanged;
		_sourceSubscriptions.Clear();
		foreach (var source in _operator.Sources)
		{
			source.PropertyChanged += OnSourcePropertyChanged;
			_sourceSubscriptions.Add(source);
		}
	}

	private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

	private void OnOperatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(OperatorViewModel.PreviewSourceId) or
			nameof(OperatorViewModel.ProgramSourceId) or
			nameof(OperatorViewModel.RuntimeStatus) or
			nameof(OperatorViewModel.RuntimeHealth) or
			nameof(OperatorViewModel.GpuProviderHealth) or
			nameof(OperatorViewModel.CommitStatus) or
			nameof(OperatorViewModel.TransitionStatus) or
			nameof(OperatorViewModel.CurrentFormat) or
			nameof(OperatorViewModel.VisualLayerStatus) or
			nameof(OperatorViewModel.GraphicsAssetName) or
			nameof(OperatorViewModel.GraphicsDimensions) or
			nameof(OperatorViewModel.GraphicsState) or
			nameof(OperatorViewModel.GraphicsVisible) or
			nameof(OperatorViewModel.GraphicsPositionX) or
			nameof(OperatorViewModel.GraphicsPositionY) or
			nameof(OperatorViewModel.GraphicsScale) or
			nameof(OperatorViewModel.CompositingLayers) or
			nameof(OperatorViewModel.IsConnected) or
			nameof(OperatorViewModel.IsStale) or
			nameof(OperatorViewModel.IsBusy) or
			nameof(OperatorViewModel.RecordingStatus) or
			nameof(OperatorViewModel.RecordingError))
		{
			Refresh();
		}
	}

	private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(CompositingGraphNodeViewModel.X) or nameof(CompositingGraphNodeViewModel.Y))
			UpdateConnections();
	}

	private void PublishHealthRevision(IReadOnlyList<CompositingGraphNodeProjection> nodes)
	{
		var fingerprint = new HashCode();
		foreach (var node in nodes)
		{
			fingerprint.Add(node.Id, StringComparer.Ordinal);
			fingerprint.Add(node.Health);
			fingerprint.Add(node.Status, StringComparer.Ordinal);
			fingerprint.Add(node.Detail, StringComparer.Ordinal);
		}

		var current = fingerprint.ToHashCode();
		if (_hasHealthFingerprint && current == _healthFingerprint)
			return;

		_healthFingerprint = current;
		_hasHealthFingerprint = true;
		_healthRevision++;
		OnPropertyChanged(nameof(HealthRevision));
	}

	private void UpdateConnections()
	{
		foreach (var connection in Connections)
			connection.UpdateGeometry();
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		return true;
	}
}

internal sealed class CompositingGraphCommand : ICommand
{
	private readonly Action<object?> _execute;

	public CompositingGraphCommand(Action<object?> execute)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
	}

	public event EventHandler? CanExecuteChanged
	{
		add { }
		remove { }
	}

	public bool CanExecute(object? parameter) => true;

	public void Execute(object? parameter) => _execute(parameter);
}
