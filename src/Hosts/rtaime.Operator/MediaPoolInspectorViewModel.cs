// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum MediaPoolItemKind
{
	Source,
	Clip,
	Audio,
	Graphics,
	Composition
}

public sealed record InspectorPropertyViewModel(
	string PropertyId,
	string Label,
	string Value,
	string State,
	bool IsPinnable,
	bool IsMixed = false);

public sealed record MediaPoolItemViewModel(
	string Key,
	string Category,
	MediaPoolItemKind Kind,
	string Name,
	string Detail,
	string Format,
	string State,
	string? ReferenceId,
	ImageSource? Thumbnail,
	bool IsOnline,
	bool IsReady,
	string? LocalPath = null)
{
	public string DurationLabel =>
		Kind == MediaPoolItemKind.Clip
			? Detail.Split('·', 2, StringSplitOptions.TrimEntries)[0]
			: string.Empty;

	public string FileTypeLabel => Kind switch
	{
		MediaPoolItemKind.Clip => System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension : "VIDEO",
		MediaPoolItemKind.Source => "SOURCE",
		MediaPoolItemKind.Audio => "AUDIO",
		MediaPoolItemKind.Graphics => "GRAPHICS",
		MediaPoolItemKind.Composition => "COMPOSITION",
		_ => Kind.ToString().ToUpperInvariant()
	};

	public string AvailabilityLabel => IsOnline ? "ONLINE" : "OFFLINE";
	public bool CanRevealInExplorer => Kind == MediaPoolItemKind.Clip && !string.IsNullOrWhiteSpace(LocalPath);
}

public sealed record MediaAssetDragPayload(
	MediaPoolItemViewModel Primary,
	IReadOnlyList<MediaPoolItemViewModel> Items);

public sealed class MediaAssetCollection : ObservableCollection<MediaPoolItemViewModel>
{
	public void ReplaceWith(IEnumerable<MediaPoolItemViewModel> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		Items.Clear();
		foreach (var item in items)
			Items.Add(item);

		OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
		OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}
}

public sealed class MediaPoolInspectorViewModel : INotifyPropertyChanged, IDisposable
{
	private const int MaxProjectedItems = 4096;
	private static readonly string[] SupportedCategories = ["All", "Sources", "Clips", "Audio", "Graphics", "Compositions"];
	private static readonly string[] SupportedFilters = ["All", "Video", "Audio", "Graphics", "Online", "Offline"];

	private readonly OperatorViewModel _operator;
	private readonly MediaDeckViewModel _mediaDeck;
	private readonly List<OperatorSourceTileViewModel> _sourceSubscriptions = [];
	private readonly List<OperatorAudioInputViewModel> _audioSubscriptions = [];
	private readonly List<MediaPoolItemViewModel> _allItems = [];
	private readonly Dictionary<string, Dictionary<string, bool>> _groupExpansion = new(StringComparer.Ordinal);
	private string _searchText = string.Empty;
	private string _selectedCategory = "All";
	private string _selectedFilter = "All";
	private bool _isGridView = true;
	private MediaPoolItemViewModel? _selectedItem;
	private TimelineTrackItemViewModel? _timelineItem;
	private readonly List<TimelineTrackItemViewModel> _timelineItems = [];
	private TimelineCueViewModel? _timelineCue;
	private CompositingGraphNodeProjection? _compositingNode;
	private string _emptyState = "No assets are available.";

	public MediaPoolInspectorViewModel(OperatorViewModel @operator, MediaDeckViewModel mediaDeck)
	{
		_operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
		_mediaDeck = mediaDeck ?? throw new ArgumentNullException(nameof(mediaDeck));

		FilteredItems = [];
		SelectedItems = [];
		InspectorProperties = [];
		InspectorMetadataProperties = [];
		InspectorEffectProperties = [];
		ResetAutoPlayCommand = new AsyncRelayCommand(() =>
		{
			_mediaDeck.AutoPlayOnProgram = true;
			return Task.CompletedTask;
		});
		ResetEndBehaviorCommand = new AsyncRelayCommand(() =>
		{
			_mediaDeck.EndBehavior = MediaDeckEndBehavior.HoldLastFrame;
			return Task.CompletedTask;
		});
		ResetAudioGainCommand = new AsyncRelayCommand(() =>
		{
			if (_operator.SelectedAudioInput is { } input)
				input.Gain = 1.0;
			return Task.CompletedTask;
		});
		ResetGraphicsPositionXCommand = new AsyncRelayCommand(() =>
		{
			_operator.GraphicsPositionX = 72.0;
			return Task.CompletedTask;
		});
		ResetGraphicsPositionYCommand = new AsyncRelayCommand(() =>
		{
			_operator.GraphicsPositionY = 6.0;
			return Task.CompletedTask;
		});
		ResetGraphicsScaleCommand = new AsyncRelayCommand(() =>
		{
			_operator.GraphicsScale = 1.0;
			return Task.CompletedTask;
		});
		GridViewCommand = new AsyncRelayCommand(() =>
		{
			IsGridView = true;
			return Task.CompletedTask;
		});
		ListViewCommand = new AsyncRelayCommand(() =>
		{
			IsGridView = false;
			return Task.CompletedTask;
		});
		AddTestSignalCommand = new AsyncRelayCommand(() => ApplyTestSignalPresetAsync(OperatorTestSignalPreset.Static));
		SetStaticTestSignalCommand = new AsyncRelayCommand(() => ApplyTestSignalPresetAsync(OperatorTestSignalPreset.Static));
		SetMotionTestSignalCommand = new AsyncRelayCommand(() => ApplyTestSignalPresetAsync(OperatorTestSignalPreset.Motion));
		SetAvSyncTestSignalCommand = new AsyncRelayCommand(() => ApplyTestSignalPresetAsync(OperatorTestSignalPreset.AvSync));
		DisableTestSignalCommand = new AsyncRelayCommand(() => ApplyTestSignalPresetAsync(OperatorTestSignalPreset.Off));

		_operator.PropertyChanged += OnOperatorPropertyChanged;
		_mediaDeck.PropertyChanged += OnMediaDeckPropertyChanged;
		_operator.Sources.CollectionChanged += OnSourcesChanged;
		_operator.AudioInputs.CollectionChanged += OnAudioInputsChanged;
		RewireItemSubscriptions();
		Refresh();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public MediaAssetCollection FilteredItems { get; }
	public ObservableCollection<MediaPoolItemViewModel> SelectedItems { get; }
	public ObservableCollection<InspectorPropertyViewModel> InspectorProperties { get; }
	public ObservableCollection<InspectorPropertyViewModel> InspectorMetadataProperties { get; }
	public ObservableCollection<InspectorPropertyViewModel> InspectorEffectProperties { get; }
	public IReadOnlyList<string> Categories => SupportedCategories;
	public IReadOnlyList<string> Filters => SupportedFilters;
	public ICommand GridViewCommand { get; }
	public ICommand ListViewCommand { get; }
	public ICommand ResetAutoPlayCommand { get; }
	public ICommand ResetEndBehaviorCommand { get; }
	public ICommand ResetAudioGainCommand { get; }
	public ICommand ResetGraphicsPositionXCommand { get; }
	public ICommand ResetGraphicsPositionYCommand { get; }
	public ICommand ResetGraphicsScaleCommand { get; }
	public ICommand ImportCommand => _mediaDeck.OpenCommand;
	public ICommand AddTestSignalCommand { get; }
	public ICommand SetStaticTestSignalCommand { get; }
	public ICommand SetMotionTestSignalCommand { get; }
	public ICommand SetAvSyncTestSignalCommand { get; }
	public ICommand DisableTestSignalCommand { get; }

	public string SearchText
	{
		get => _searchText;
		set
		{
			if (!Set(ref _searchText, value ?? string.Empty))
				return;
			ApplyFilter();
		}
	}

	public string SelectedCategory
	{
		get => _selectedCategory;
		set
		{
			var normalized = SupportedCategories.Contains(value, StringComparer.Ordinal) ? value : "All";
			if (!Set(ref _selectedCategory, normalized))
				return;
			ApplyFilter();
		}
	}

	public string SelectedFilter
	{
		get => _selectedFilter;
		set
		{
			var normalized = SupportedFilters.Contains(value, StringComparer.Ordinal) ? value : "All";
			if (!Set(ref _selectedFilter, normalized))
				return;
			ApplyFilter();
		}
	}

	public bool IsGridView
	{
		get => _isGridView;
		private set
		{
			if (!Set(ref _isGridView, value))
				return;
			OnPropertyChanged(nameof(IsListView));
			OnPropertyChanged(nameof(ViewModeLabel));
		}
	}

	public bool IsListView => !IsGridView;
	public string ViewModeLabel => IsGridView ? "GRID" : "LIST";

	public MediaPoolItemViewModel? SelectedItem
	{
		get => _selectedItem;
		set
		{
			if (!Set(ref _selectedItem, value))
				return;
			_timelineItem = null;
			_timelineItems.Clear();
			_timelineCue = null;
			_compositingNode = null;
			ProjectSelectionToExistingOperatorContext(value);
			BuildInspector();
			RaiseSelectionState();
		}
	}

	public bool HasItems => FilteredItems.Count > 0;
	public bool HasSelection => _compositingNode is not null || SelectedItem is not null || _timelineItem is not null || _timelineItems.Count > 0 || _timelineCue is not null;
	public bool CanEditSelection => HasSelection && !HasMultipleSelection && _compositingNode is null;
	public bool HasMetadata => InspectorMetadataProperties.Count > 0;
	public bool HasEffects => InspectorEffectProperties.Count > 0;
	public bool SupportsTransformRotation => false;
	public bool SupportsTransformAnchor => false;
	public bool SupportsTransformCrop => false;
	public bool SupportsEffectOrdering => false;
	public string UnsupportedTransformCapabilityText => "Rotation, Anchor and Crop are not exposed by the current graphics capability.";
	public string UnsupportedEffectOrderingText => "Effect ordering is not exposed by the current processing capability.";
	public bool IsSourceSelection => _timelineItems.Count <= 1 && _timelineItem is null && _timelineCue is null && SelectedItem?.Kind == MediaPoolItemKind.Source;
	public bool IsTestSignalSelection => SelectedTestSignalSource?.IsTestPattern == true;
	public string TestSignalPresetLabel
	{
		get
		{
			var source = SelectedTestSignalSource;
			if (source?.IsTestPattern != true)
				return "OFF";
			var audio = ResolveAudioInput(source.Id);
			return string.Equals(source.MediaState, "MOTION", StringComparison.OrdinalIgnoreCase) &&
				audio?.TestSignalEnabled == true &&
				audio.TestSignalMode == 5
					? "A/V SYNC"
					: string.Equals(source.MediaState, "MOTION", StringComparison.OrdinalIgnoreCase)
						? "MOTION"
						: "STATIC";
		}
	}
	public string TestSignalResolutionLabel => ExtractResolution(SelectedTestSignalSource?.Format);
	public string TestSignalFrameRateLabel => SelectedTestSignalSource?.FrameRateLabel ?? "—";
	public string TestSignalAudioModeLabel
	{
		get
		{
			var source = SelectedTestSignalSource;
			if (source is null)
				return "OFF";
			var audio = ResolveAudioInput(source.Id);
			return audio?.TestSignalEnabled == true ? audio.TestSignalModeLabel : "OFF";
		}
	}
	public bool TestSignalMotionEnabled =>
		string.Equals(SelectedTestSignalSource?.MediaState, "MOTION", StringComparison.OrdinalIgnoreCase);
	public bool TestSignalTimecodeEnabled => TestSignalMotionEnabled;
	public bool TestSignalFrameCounterEnabled => TestSignalMotionEnabled;
	public bool IsClipSelection => _timelineItems.Count <= 1 && _timelineCue is null &&
		(_timelineItem?.Category == TimelineTrackCategory.Video ||
			(_timelineItem is null && SelectedItem?.Kind == MediaPoolItemKind.Clip));
	public bool IsAudioSelection => _timelineItems.Count <= 1 && _timelineCue is null &&
		(_timelineItem?.Category == TimelineTrackCategory.Audio ||
			(_timelineItem is null && SelectedItem?.Kind == MediaPoolItemKind.Audio));
	public bool IsGraphicsSelection => _timelineItems.Count <= 1 && _timelineCue is null &&
		(_timelineItem?.Category is TimelineTrackCategory.Graphics or TimelineTrackCategory.Overlay ||
			(_timelineItem is null && SelectedItem?.Kind == MediaPoolItemKind.Graphics));
	public bool IsCompositionSelection => _timelineItems.Count <= 1 && _timelineItem is null && _timelineCue is null && SelectedItem?.Kind == MediaPoolItemKind.Composition;
	public bool IsCueSelection => _timelineCue is not null;
	public string InspectorTitle => _compositingNode?.Title ?? (_timelineItems.Count > 1
		? $"{_timelineItems.Count} timeline items"
		: _timelineCue?.Name ?? _timelineItem?.Label ?? SelectedItem?.Name ?? "No selection");
	public string InspectorDetail => _compositingNode is { } graphNode
		? $"GRAPH · {graphNode.Kind.ToString().ToUpperInvariant()} · {graphNode.Status}"
		: _timelineItems.Count > 1
			? "TIMELINE · MULTI-SELECTION"
			: _timelineCue is { } cue
			? $"CUE · {cue.Type.ToString().ToUpperInvariant()} · {cue.Timecode}"
			: _timelineItem is { } timelineItem
				? $"TIMELINE · {timelineItem.Category.ToString().ToUpperInvariant()} · {timelineItem.Status}"
				: SelectedItem?.Detail ?? "Select a Media Pool resource, timeline item or cue.";
	public string EmptyState
	{
		get => _emptyState;
		private set => Set(ref _emptyState, value);
	}

	public bool IsLoading => _mediaDeck.IsBusy;
	public bool HasError => _mediaDeck.HasError;
	public string ErrorState => _mediaDeck.LastError ?? string.Empty;
	public int SelectionCount => _compositingNode is not null ? 1 : _timelineItems.Count > 0 ? _timelineItems.Count : SelectedItems.Count;
	public bool HasMultipleSelection => _compositingNode is null && (_timelineItems.Count > 1 ||
		(_timelineItem is null && _timelineCue is null && SelectedItems.Count > 1));
	public string SelectionSummary => _compositingNode is not null
		? "1 graph node selected"
		: _timelineItems.Count > 0
			? _timelineItems.Count == 1 ? "1 timeline item selected" : $"{_timelineItems.Count} timeline items selected"
			: SelectionCount switch
		{
			0 => "No selection",
			1 => "1 asset selected",
			_ => $"{SelectionCount} assets selected"
		};

	public bool IsOverviewExpanded
	{
		get => GetGroupExpansion("overview", true);
		set => SetGroupExpansion("overview", value);
	}
	public bool IsPlaybackExpanded
	{
		get => GetGroupExpansion("playback", true);
		set => SetGroupExpansion("playback", value);
	}
	public bool IsTestSignalExpanded
	{
		get => GetGroupExpansion("test-signal", true);
		set => SetGroupExpansion("test-signal", value);
	}
	public bool IsTransformExpanded
	{
		get => GetGroupExpansion("transform", true);
		set => SetGroupExpansion("transform", value);
	}
	public bool IsAudioExpanded
	{
		get => GetGroupExpansion("audio", true);
		set => SetGroupExpansion("audio", value);
	}
	public bool IsCueExpanded
	{
		get => GetGroupExpansion("cue", true);
		set => SetGroupExpansion("cue", value);
	}
	public bool IsEffectsExpanded
	{
		get => GetGroupExpansion("effects", true);
		set => SetGroupExpansion("effects", value);
	}
	public bool IsMetadataExpanded
	{
		get => GetGroupExpansion("metadata", true);
		set => SetGroupExpansion("metadata", value);
	}

	public void UpdateSelection(IEnumerable<MediaPoolItemViewModel> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		var selected = items
			.GroupBy(item => item.Key, StringComparer.Ordinal)
			.Select(group => group.First())
			.ToArray();

		SelectedItems.Clear();
		foreach (var item in selected)
			SelectedItems.Add(item);

		if (selected.Length == 0)
		{
			if (SelectedItem is not null)
				SelectedItem = null;
		}
		else if (SelectedItem is null || selected.All(item => !string.Equals(item.Key, SelectedItem.Key, StringComparison.Ordinal)))
		{
			SelectedItem = selected[0];
		}

		BuildInspector();
		RaiseSelectionState();
		OnPropertyChanged(nameof(SelectionCount));
		OnPropertyChanged(nameof(HasMultipleSelection));
		OnPropertyChanged(nameof(SelectionSummary));
		OnPropertyChanged(nameof(CanEditSelection));
	}

	public bool CanDropToPreview(MediaPoolItemViewModel? item)
	{
		if (item is null || item.Kind is not (MediaPoolItemKind.Source or MediaPoolItemKind.Clip))
			return false;
		if (string.IsNullOrWhiteSpace(item.ReferenceId))
			return false;

		var source = _operator.Sources.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, item.ReferenceId, StringComparison.Ordinal));
		return source is not null && _operator.SetPreviewCommand.CanExecute(null);
	}

	public Task DropToPreviewAsync(MediaPoolItemViewModel item)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (!CanDropToPreview(item))
			return Task.CompletedTask;

		var source = _operator.Sources.First(candidate =>
			string.Equals(candidate.Id, item.ReferenceId, StringComparison.Ordinal));
		SelectedItem = item;
		_operator.SelectedSource = source;
		if (_operator.SetPreviewCommand.CanExecute(null))
			_operator.SetPreviewCommand.Execute(null);
		return Task.CompletedTask;
	}

	public void SelectTimelineItem(TimelineTrackItemViewModel item) =>
		SelectTimelineItems([item], item);

	public void SelectTimelineItems(
		IEnumerable<TimelineTrackItemViewModel> items,
		TimelineTrackItemViewModel? primary = null)
	{
		ArgumentNullException.ThrowIfNull(items);
		var selected = items
			.GroupBy(item => item.Id, StringComparer.Ordinal)
			.Select(group => group.First())
			.ToArray();

		_compositingNode = null;
		_timelineItems.Clear();
		_timelineItems.AddRange(selected);
		_timelineItem = primary is not null && selected.Contains(primary)
			? primary
			: selected.LastOrDefault();
		_timelineCue = null;
		BuildInspector();
		RaiseSelectionState();
	}

	public void SelectTimelineCue(TimelineCueViewModel cue)
	{
		ArgumentNullException.ThrowIfNull(cue);
		_compositingNode = null;
		_timelineCue = cue;
		_timelineItem = null;
		_timelineItems.Clear();
		BuildInspector();
		RaiseSelectionState();
	}

	public void SelectCompositingNode(CompositingGraphNodeProjection node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_compositingNode = node;
		_selectedItem = null;
		SelectedItems.Clear();
		_timelineItem = null;
		_timelineItems.Clear();
		_timelineCue = null;

		if (node.Id.StartsWith("source:", StringComparison.Ordinal))
		{
			var sourceId = node.Id["source:".Length..];
			var source = _operator.Sources.FirstOrDefault(candidate =>
				string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
			if (source is not null)
				_operator.SelectedSource = source;
		}

		OnPropertyChanged(nameof(SelectedItem));
		BuildInspector();
		RaiseSelectionState();
	}

	public void ClearTimelineSelection()
	{
		if (_timelineItem is null && _timelineItems.Count == 0 && _timelineCue is null)
			return;
		_timelineItem = null;
		_timelineItems.Clear();
		_timelineCue = null;
		BuildInspector();
		RaiseSelectionState();
	}

	public void Refresh()
	{
		var selectedKey = SelectedItem?.Key;
		_allItems.Clear();

		foreach (var source in _operator.Sources.Take(MaxProjectedItems))
		{
			var online = IsOnlineState(source.Health);
			_allItems.Add(new MediaPoolItemViewModel(
				$"source:{source.Id}",
				"Sources",
				MediaPoolItemKind.Source,
				source.Name,
				source.Detail,
				source.Format,
				online ? source.StateDetail : $"OFFLINE · {source.StateDetail}",
				source.Id,
				source.Thumbnail,
				online,
				online));
		}

		if (_mediaDeck.IsLoaded)
		{
			var mediaOnline = !_mediaDeck.HasError;
			_allItems.Add(new MediaPoolItemViewModel(
				$"clip:{_mediaDeck.SourceId}",
				"Clips",
				MediaPoolItemKind.Clip,
				_mediaDeck.FileName,
				$"{_mediaDeck.Duration} · {_mediaDeck.VideoCodec}",
				$"{_mediaDeck.Resolution} · {_mediaDeck.FrameRate}",
				mediaOnline
					? _mediaDeck.State
					: $"OFFLINE · {(_mediaDeck.LastError ?? _mediaDeck.State)}",
				_mediaDeck.SourceId,
				_operator.Sources.FirstOrDefault(source => string.Equals(source.Id, _mediaDeck.SourceId, StringComparison.Ordinal))?.Thumbnail,
				mediaOnline,
				mediaOnline && !_mediaDeck.IsBusy,
				_mediaDeck.LocalPath));
		}

		foreach (var input in _operator.AudioInputs.Take(Math.Max(0, MaxProjectedItems - _allItems.Count)))
		{
			var online = IsOnlineState(input.Health);
			_allItems.Add(new MediaPoolItemViewModel(
				$"audio:{input.SourceId}:{input.StreamId}",
				"Audio",
				MediaPoolItemKind.Audio,
				input.SourceName,
				$"{input.SourceType} · {input.AfvLabel}",
				input.StreamId,
				online ? input.Health : $"OFFLINE · {input.Health}",
				input.SourceId,
				null,
				online,
				online));
		}

		if (!string.IsNullOrWhiteSpace(_operator.GraphicsAssetName) &&
			!string.Equals(_operator.GraphicsAssetName, "—", StringComparison.Ordinal))
		{
			_allItems.Add(new MediaPoolItemViewModel(
				$"graphics:{_operator.GraphicsAssetName}",
				"Graphics",
				MediaPoolItemKind.Graphics,
				_operator.GraphicsAssetName,
				_operator.GraphicsDimensions,
				"RGBA / PNG",
				_operator.GraphicsState,
				_operator.GraphicsAssetName,
				null,
				true,
				true));
		}

		if (!string.IsNullOrWhiteSpace(_operator.AIFeature) &&
			!string.Equals(_operator.AIFeature, "—", StringComparison.Ordinal))
		{
			var ready = !string.Equals(_operator.AIStatus, "UNAVAILABLE", StringComparison.OrdinalIgnoreCase);
			_allItems.Add(new MediaPoolItemViewModel(
				"composition:ai-showcase",
				"Compositions",
				MediaPoolItemKind.Composition,
				_operator.AIFeature,
				_operator.AIProvider,
				"AI effect",
				_operator.AIStatus,
				null,
				null,
				ready,
				ready));
		}

		if (_allItems.Count > MaxProjectedItems)
			_allItems.RemoveRange(MaxProjectedItems, _allItems.Count - MaxProjectedItems);

		ApplyFilter(selectedKey);
		RaiseTestSignalState();
	}

	public void Dispose()
	{
		_operator.PropertyChanged -= OnOperatorPropertyChanged;
		_mediaDeck.PropertyChanged -= OnMediaDeckPropertyChanged;
		_operator.Sources.CollectionChanged -= OnSourcesChanged;
		_operator.AudioInputs.CollectionChanged -= OnAudioInputsChanged;
		foreach (var source in _sourceSubscriptions)
			source.PropertyChanged -= OnSourceItemPropertyChanged;
		foreach (var input in _audioSubscriptions)
			input.PropertyChanged -= OnAudioItemPropertyChanged;
		_sourceSubscriptions.Clear();
		_audioSubscriptions.Clear();
	}

	private void ApplyFilter(string? preferredSelectionKey = null)
	{
		preferredSelectionKey ??= SelectedItem?.Key;
		var query = SearchText.Trim();

		var filtered = _allItems
			.Where(item => CategoryMatches(item) && FilterMatches(item) && SearchMatches(item, query))
			.OrderBy(item => item.Category, StringComparer.Ordinal)
			.ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(item => item.Key, StringComparer.Ordinal)
			.Take(MaxProjectedItems)
			.ToArray();

		FilteredItems.ReplaceWith(filtered);

		var preferred = preferredSelectionKey is null
			? null
			: filtered.FirstOrDefault(item => string.Equals(item.Key, preferredSelectionKey, StringComparison.Ordinal));
		if (preferred is not null)
		{
			if (SelectedItem is null || !string.Equals(SelectedItem.Key, preferred.Key, StringComparison.Ordinal))
				SelectedItem = preferred;
			else
			{
				_selectedItem = preferred;
				OnPropertyChanged(nameof(SelectedItem));
				if (_timelineItem is null && _timelineCue is null)
					BuildInspector();
			}
		}
		else if (SelectedItem is not null && filtered.All(item => !string.Equals(item.Key, SelectedItem.Key, StringComparison.Ordinal)))
			SelectedItem = null;

		EmptyState = filtered.Length > 0
			? string.Empty
			: _allItems.Count == 0
				? "No assets are available."
				: "No assets match the current search and filter.";
		OnPropertyChanged(nameof(HasItems));
	}

	private bool CategoryMatches(MediaPoolItemViewModel item) =>
		string.Equals(SelectedCategory, "All", StringComparison.Ordinal) ||
		string.Equals(item.Category, SelectedCategory, StringComparison.Ordinal);

	private bool FilterMatches(MediaPoolItemViewModel item) =>
		SelectedFilter switch
		{
			"All" => true,
			"Video" => item.Kind is MediaPoolItemKind.Source or MediaPoolItemKind.Clip,
			"Audio" => item.Kind == MediaPoolItemKind.Audio,
			"Graphics" => item.Kind == MediaPoolItemKind.Graphics,
			"Online" => item.IsOnline,
			"Offline" => !item.IsOnline,
			_ => true
		};

	private static bool SearchMatches(MediaPoolItemViewModel item, string query)
	{
		if (query.Length == 0)
			return true;

		return Contains(item.Name, query) ||
			Contains(item.Detail, query) ||
			Contains(item.Format, query) ||
			Contains(item.State, query) ||
			Contains(item.ReferenceId, query) ||
			Contains(item.LocalPath, query);
	}

	private static bool Contains(string? value, string query) =>
		value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

	private static bool IsOnlineState(string value) =>
		!value.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase) &&
		!value.Contains("FAIL", StringComparison.OrdinalIgnoreCase) &&
		!value.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase);

	private void ProjectSelectionToExistingOperatorContext(MediaPoolItemViewModel? item)
	{
		if (item is null)
			return;

		if (item.Kind is MediaPoolItemKind.Source or MediaPoolItemKind.Clip)
		{
			var source = _operator.Sources.FirstOrDefault(candidate =>
				string.Equals(candidate.Id, item.ReferenceId, StringComparison.Ordinal));
			if (source is not null)
				_operator.SelectedSource = source;
			return;
		}

		if (item.Kind == MediaPoolItemKind.Audio)
		{
			var input = _operator.AudioInputs.FirstOrDefault(candidate =>
				string.Equals(candidate.SourceId, item.ReferenceId, StringComparison.Ordinal));
			if (input is not null)
				_operator.SelectedAudioInput = input;
		}
	}

	private void BuildInspector()
	{
		InspectorProperties.Clear();
		InspectorMetadataProperties.Clear();
		InspectorEffectProperties.Clear();

		if (_compositingNode is { } graphNode)
		{
			Add("graph.node.kind", "Node", graphNode.Kind.ToString().ToUpperInvariant(), "METADATA");
			Add("graph.node.detail", "Detail", graphNode.Detail, "METADATA");
			Add("graph.node.status", "Status", graphNode.Status, "COMMITTED");
			Add("graph.node.health", "Health", graphNode.Health.ToString().ToUpperInvariant(), "COMMITTED");
			Add("graph.node.inputs", "Inputs", FormatGraphPorts(graphNode, CompositingGraphPortDirection.Input), "METADATA");
			Add("graph.node.outputs", "Outputs", FormatGraphPorts(graphNode, CompositingGraphPortDirection.Output), "METADATA");
			Add("graph.node.rewire", "Rewire", graphNode.CanRewire ? "AVAILABLE" : "READ ONLY", "CAPABILITY");
			RaiseInspectorProjectionState();
			return;
		}

		if (_timelineItems.Count > 1)
		{
			BuildTimelineMultiSelectionInspector();
			RaiseInspectorProjectionState();
			return;
		}

		if (HasMultipleSelection)
		{
			BuildMultiSelectionInspector();
			RaiseInspectorProjectionState();
			return;
		}

		if (_timelineCue is { } cue)
		{
			Add("timeline.cue.name", "Cue", cue.Name, "METADATA", true);
			Add("timeline.cue.type", "Type", cue.Type.ToString().ToUpperInvariant(), "METADATA");
			Add("timeline.cue.time", "Time", cue.Timecode, "COMMITTED");
			Add("timeline.cue.frame", "Frame", cue.Frame.ToString("N0"), "COMMITTED");
			Add("timeline.cue.target", "Target", cue.TargetReference ?? "—", "METADATA");
			RaiseInspectorProjectionState();
			return;
		}

		if (_timelineItem is { } timelineItem)
		{
			var state = timelineItem.Status.StartsWith("PROJECTED", StringComparison.Ordinal)
				? "METADATA"
				: "COMMITTED";
			Add("timeline.item.label", "Item", timelineItem.Label, "METADATA");
			Add("timeline.item.track", "Track", timelineItem.Category.ToString().ToUpperInvariant(), "METADATA");
			Add("timeline.item.start", "Start frame", timelineItem.StartFrame.ToString("N0"), state);
			Add("timeline.item.duration", "Duration frames", timelineItem.DurationFrames.ToString("N0"), state);
			Add("timeline.item.source", "Source", timelineItem.SourceReference ?? "—", "METADATA");
			Add("timeline.item.status", "Status", timelineItem.Status, state);
			Add("timeline.item.in", "IN", timelineItem.InFrame?.ToString("N0") ?? "—", state, timelineItem.CanTrim);
			Add("timeline.item.out", "OUT", timelineItem.OutFrame?.ToString("N0") ?? "—", state, timelineItem.CanTrim);
			RaiseInspectorProjectionState();
			return;
		}

		var item = SelectedItem;
		if (item is null)
		{
			RaiseInspectorProjectionState();
			return;
		}

		switch (item.Kind)
		{
			case MediaPoolItemKind.Source:
				Add("source.name", "Source", item.Name, "METADATA");
				Add("source.type", "Type", item.Detail, "METADATA");
				Add("source.format", "Format", item.Format, "METADATA");
				Add("source.state", "State", item.State, "COMMITTED");
				Add("source.readiness", "Readiness", item.IsReady ? "READY" : "NOT READY", "COMMITTED");
				if (IsTestSignalSelection)
				{
					Add("testsignal.pattern", "Pattern", "Broadcast Reference", "COMMITTED");
					Add("testsignal.preset", "Preset", TestSignalPresetLabel, "COMMITTED");
					Add("testsignal.resolution", "Resolution", TestSignalResolutionLabel, "COMMITTED");
					Add("testsignal.framerate", "Frame rate", TestSignalFrameRateLabel, "COMMITTED");
					Add("testsignal.audio", "Audio mode", TestSignalAudioModeLabel, "COMMITTED");
					Add("testsignal.motion", "Motion", TestSignalMotionEnabled ? "ON" : "OFF", "COMMITTED");
					Add("testsignal.timecode", "Timecode", TestSignalTimecodeEnabled ? "ON" : "OFF", "COMMITTED");
					Add("testsignal.framecounter", "Frame counter", TestSignalFrameCounterEnabled ? "ON" : "OFF", "COMMITTED");
				}
				Add("production.transition.frames", "Transition Duration", $"{_operator.TransitionFrames} frames", "DESIRED", true);
				break;

			case MediaPoolItemKind.Clip:
				Add("clip.source", "Source", _mediaDeck.SourceId, "METADATA");
				Add("clip.duration", "Duration", _mediaDeck.Duration, "METADATA");
				Add("clip.format", "Format", $"{_mediaDeck.Resolution} · {_mediaDeck.VideoCodec}", "METADATA");
				Add("clip.framerate", "Frame rate", _mediaDeck.FrameRate, "METADATA");
				Add("clip.audio", "Audio", _mediaDeck.AudioCodec, "METADATA");
				Add("clip.playback.state", "Playback", _mediaDeck.State, "COMMITTED");
				Add("clip.trim.range", "IN / OUT", _mediaDeck.EffectiveRange, "COMMITTED");
				Add("clip.playback.autoplay", "Auto Play on Program", _mediaDeck.AutoPlayOnProgram ? "ON" : "OFF", "DESIRED", true);
				Add("clip.playback.end", "End behavior", _mediaDeck.EndBehavior.ToString(), "DESIRED", true);
				break;

			case MediaPoolItemKind.Audio:
				var input = _operator.AudioInputs.FirstOrDefault(candidate =>
					string.Equals(candidate.SourceId, item.ReferenceId, StringComparison.Ordinal));
				if (input is null)
					break;
				Add("audio.source", "Source", input.SourceName, "METADATA");
				Add("audio.stream", "Stream", input.StreamId, "METADATA");
				Add("audio.health", "Health", input.Health, "COMMITTED");
				Add("audio.afv", "AFV", input.AfvLabel, "COMMITTED");
				Add("audio.gain", "Gain", $"{input.Gain:0.00}x", "DESIRED", true);
				Add("audio.mute", "Mute", input.Muted ? "MUTED" : "OPEN", "COMMITTED", true);
				break;

			case MediaPoolItemKind.Graphics:
				Add("graphics.asset.name", "Asset", _operator.GraphicsAssetName, "METADATA");
				Add("graphics.asset.dimensions", "Dimensions", _operator.GraphicsDimensions, "METADATA");
				Add("graphics.state", "State", _operator.GraphicsState, "COMMITTED");
				Add("graphics.visible", "Visible", _operator.GraphicsVisible ? "YES" : "NO", "COMMITTED", true);
				Add("graphics.position.x", "Position X", $"{_operator.GraphicsPositionX:0.##}%", "DESIRED", true);
				Add("graphics.position.y", "Position Y", $"{_operator.GraphicsPositionY:0.##}%", "DESIRED", true);
				Add("graphics.scale", "Scale", $"{_operator.GraphicsScale:0.##}x", "DESIRED", true);
				Add("graphics.rotation", "Rotation", "N/A", "CAPABILITY");
				Add("graphics.anchor", "Anchor", "N/A", "CAPABILITY");
				Add("graphics.crop", "Crop", "N/A", "CAPABILITY");
				break;

			case MediaPoolItemKind.Composition:
				Add("ai.feature", "Feature", _operator.AIFeature, "COMMITTED");
				Add("ai.enabled", "Enabled", _operator.AIEnabled ? "ON" : "OFF", "COMMITTED", true);
				Add("ai.provider", "Provider", _operator.AIProvider, "COMMITTED");
				Add("ai.confidence", "Confidence", _operator.AIConfidence, "COMMITTED");
				Add("ai.inference", "Inference", _operator.AIInferenceTime, "COMMITTED");
				Add("ai.fallback", "Fallback", string.IsNullOrWhiteSpace(_operator.AIError) ? "Clean Program" : _operator.AIError!, "COMMITTED");
				break;
		}

		RaiseInspectorProjectionState();
	}

	private void BuildTimelineMultiSelectionInspector()
	{
		var selected = _timelineItems.ToArray();
		if (selected.Length == 0)
			return;

		Add("timeline.selection.count", "Selection", $"{selected.Length} timeline items", "METADATA");
		AddMixed("timeline.selection.track", "Track", selected.Select(item => item.Category.ToString().ToUpperInvariant()));
		AddMixed("timeline.selection.status", "Status", selected.Select(item => item.Status), "COMMITTED");
		AddMixed("timeline.selection.start", "Start frame", selected.Select(item => item.StartFrame.ToString("N0")), "COMMITTED");
		AddMixed("timeline.selection.duration", "Duration frames", selected.Select(item => item.DurationFrames.ToString("N0")), "COMMITTED");
		AddMixed("timeline.selection.source", "Source", selected.Select(item => item.SourceReference ?? "—"));
		AddMixed("timeline.selection.trim", "Trim support", selected.Select(item => item.CanTrim ? "AVAILABLE" : "NOT AVAILABLE"), "CAPABILITY");
	}

	private void BuildMultiSelectionInspector()
	{
		var selected = SelectedItems.ToArray();
		if (selected.Length == 0)
			return;

		Add("selection.count", "Selection", $"{selected.Length} assets", "METADATA");
		AddMixed("selection.type", "Type", selected.Select(item => item.Kind.ToString().ToUpperInvariant()));
		AddMixed("selection.category", "Category", selected.Select(item => item.Category.ToUpperInvariant()));
		AddMixed("selection.state", "State", selected.Select(item => item.State), "COMMITTED");
		AddMixed("selection.availability", "Availability", selected.Select(item => item.AvailabilityLabel), "COMMITTED");
		AddMixed("selection.format", "Format", selected.Select(item => item.Format));
	}

	private static string FormatGraphPorts(
		CompositingGraphNodeProjection node,
		CompositingGraphPortDirection direction)
	{
		var ports = node.Ports
			.Where(port => port.Direction == direction)
			.Select(port => port.Label)
			.ToArray();
		return ports.Length == 0 ? "—" : string.Join(", ", ports);
	}

	private void AddMixed(string id, string label, IEnumerable<string> values, string state = "METADATA")
	{
		var distinct = values
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.Ordinal)
			.Take(2)
			.ToArray();
		var mixed = distinct.Length > 1;
		var value = distinct.Length == 0 ? "—" : mixed ? "MIXED" : distinct[0];
		Add(id, label, value, state, false, mixed);
	}

	private void Add(string id, string label, string value, string state, bool pinnable = false, bool mixed = false)
	{
		var property = new InspectorPropertyViewModel(id, label, value, state, pinnable, mixed);
		InspectorProperties.Add(property);
		if (string.Equals(state, "METADATA", StringComparison.Ordinal) || string.Equals(state, "CAPABILITY", StringComparison.Ordinal))
			InspectorMetadataProperties.Add(property);
		if (id.StartsWith("ai.", StringComparison.Ordinal))
			InspectorEffectProperties.Add(property);
	}

	private void RaiseInspectorProjectionState()
	{
		OnPropertyChanged(nameof(HasMetadata));
		OnPropertyChanged(nameof(HasEffects));
		OnPropertyChanged(nameof(CanEditSelection));
	}

	private string SelectionContextKey => _compositingNode is not null
		? $"graph:{_compositingNode.Id}"
		: _timelineCue is not null
			? "cue"
		: _timelineItems.Count > 1
			? "timeline:multi"
			: _timelineItem is not null
				? $"timeline:{_timelineItem.Category}"
				: HasMultipleSelection
					? "multi"
				: SelectedItem is null
					? "none"
					: $"asset:{SelectedItem.Kind}";

	private bool GetGroupExpansion(string group, bool defaultValue)
	{
		if (_groupExpansion.TryGetValue(SelectionContextKey, out var groups) &&
			groups.TryGetValue(group, out var expanded))
		{
			return expanded;
		}
		return defaultValue;
	}

	private void SetGroupExpansion(string group, bool value)
	{
		if (!_groupExpansion.TryGetValue(SelectionContextKey, out var groups))
		{
			groups = new Dictionary<string, bool>(StringComparer.Ordinal);
			_groupExpansion[SelectionContextKey] = groups;
		}
		if (groups.TryGetValue(group, out var current) && current == value)
			return;
		groups[group] = value;
		OnPropertyChanged(group switch
		{
			"overview" => nameof(IsOverviewExpanded),
			"playback" => nameof(IsPlaybackExpanded),
			"transform" => nameof(IsTransformExpanded),
			"audio" => nameof(IsAudioExpanded),
			"cue" => nameof(IsCueExpanded),
			"effects" => nameof(IsEffectsExpanded),
			"metadata" => nameof(IsMetadataExpanded),
			_ => null
		});
	}

	private void RaiseGroupExpansionState()
	{
		OnPropertyChanged(nameof(IsOverviewExpanded));
		OnPropertyChanged(nameof(IsPlaybackExpanded));
		OnPropertyChanged(nameof(IsTestSignalExpanded));
		OnPropertyChanged(nameof(IsTransformExpanded));
		OnPropertyChanged(nameof(IsAudioExpanded));
		OnPropertyChanged(nameof(IsCueExpanded));
		OnPropertyChanged(nameof(IsEffectsExpanded));
		OnPropertyChanged(nameof(IsMetadataExpanded));
	}

	private void RewireItemSubscriptions()
	{
		foreach (var source in _sourceSubscriptions)
			source.PropertyChanged -= OnSourceItemPropertyChanged;
		foreach (var input in _audioSubscriptions)
			input.PropertyChanged -= OnAudioItemPropertyChanged;
		_sourceSubscriptions.Clear();
		_audioSubscriptions.Clear();

		foreach (var source in _operator.Sources)
		{
			source.PropertyChanged += OnSourceItemPropertyChanged;
			_sourceSubscriptions.Add(source);
		}

		foreach (var input in _operator.AudioInputs)
		{
			input.PropertyChanged += OnAudioItemPropertyChanged;
			_audioSubscriptions.Add(input);
		}
	}

	private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		RewireItemSubscriptions();
		Refresh();
	}

	private void OnAudioInputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		RewireItemSubscriptions();
		Refresh();
	}

	private void OnOperatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(OperatorViewModel.GraphicsAssetName) or
			nameof(OperatorViewModel.GraphicsDimensions) or
			nameof(OperatorViewModel.GraphicsState) or
			nameof(OperatorViewModel.GraphicsVisible) or
			nameof(OperatorViewModel.GraphicsPositionX) or
			nameof(OperatorViewModel.GraphicsPositionY) or
			nameof(OperatorViewModel.GraphicsScale) or
			nameof(OperatorViewModel.AIEnabled) or
			nameof(OperatorViewModel.AIFeature) or
			nameof(OperatorViewModel.AIProvider) or
			nameof(OperatorViewModel.AIStatus) or
			nameof(OperatorViewModel.AIConfidence) or
			nameof(OperatorViewModel.AIInferenceTime) or
			nameof(OperatorViewModel.AIError) or
			nameof(OperatorViewModel.PreviewSourceId) or
			nameof(OperatorViewModel.ProgramSourceId))
		{
			Refresh();
		}
	}

	private void OnMediaDeckPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		Refresh();
		OnPropertyChanged(nameof(IsLoading));
		OnPropertyChanged(nameof(HasError));
		OnPropertyChanged(nameof(ErrorState));
	}
	private void OnSourceItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
	private void OnAudioItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

	private void RaiseSelectionState()
	{
		OnPropertyChanged(nameof(HasSelection));
		OnPropertyChanged(nameof(IsSourceSelection));
		RaiseTestSignalState();
		OnPropertyChanged(nameof(IsClipSelection));
		OnPropertyChanged(nameof(IsAudioSelection));
		OnPropertyChanged(nameof(IsGraphicsSelection));
		OnPropertyChanged(nameof(IsCompositionSelection));
		OnPropertyChanged(nameof(IsCueSelection));
		OnPropertyChanged(nameof(InspectorTitle));
		OnPropertyChanged(nameof(InspectorDetail));
		OnPropertyChanged(nameof(SelectionCount));
		OnPropertyChanged(nameof(HasMultipleSelection));
		OnPropertyChanged(nameof(SelectionSummary));
		OnPropertyChanged(nameof(CanEditSelection));
		OnPropertyChanged(nameof(HasMetadata));
		OnPropertyChanged(nameof(HasEffects));
		RaiseGroupExpansionState();
	}

	private OperatorSourceTileViewModel? SelectedTestSignalSource
	{
		get
		{
			if (SelectedItem?.Kind is not MediaPoolItemKind.Source || string.IsNullOrWhiteSpace(SelectedItem.ReferenceId))
				return null;
			return _operator.Sources.FirstOrDefault(source =>
				string.Equals(source.Id, SelectedItem.ReferenceId, StringComparison.Ordinal));
		}
	}

	private OperatorAudioInputViewModel? ResolveAudioInput(string sourceId) =>
		_operator.AudioInputs.FirstOrDefault(input =>
			string.Equals(input.SourceId, sourceId, StringComparison.Ordinal));

	private OperatorSourceTileViewModel? ResolveTestSignalTarget()
	{
		if (SelectedItem?.Kind is MediaPoolItemKind.Source or MediaPoolItemKind.Clip &&
			!string.IsNullOrWhiteSpace(SelectedItem.ReferenceId))
		{
			var selected = _operator.Sources.FirstOrDefault(source =>
				string.Equals(source.Id, SelectedItem.ReferenceId, StringComparison.Ordinal));
			if (selected is not null)
				return selected;
		}

		return _operator.SelectedSource ??
			_operator.Sources.FirstOrDefault(source => source.IsPreview) ??
			_operator.Sources.FirstOrDefault();
	}

	private async Task ApplyTestSignalPresetAsync(OperatorTestSignalPreset preset)
	{
		var source = ResolveTestSignalTarget();
		if (source is null)
			return;

		await _operator.ConfigureTestSignalAsync(source, preset);
		Refresh();

		var selected = FilteredItems.FirstOrDefault(item =>
			item.Kind == MediaPoolItemKind.Source &&
			string.Equals(item.ReferenceId, source.Id, StringComparison.Ordinal));
		if (selected is not null)
			UpdateSelection([selected]);
	}

	private static string ExtractResolution(string? format)
	{
		if (string.IsNullOrWhiteSpace(format))
			return "—";

		var marker = format.IndexOf('x');
		if (marker <= 0)
			return format.Trim();

		var start = marker;
		while (start > 0 && char.IsDigit(format[start - 1]))
			start--;

		var end = marker + 1;
		while (end < format.Length && char.IsDigit(format[end]))
			end++;

		return start < marker && end > marker + 1
			? format[start..end]
			: format.Trim();
	}

	private void RaiseTestSignalState()
	{
		OnPropertyChanged(nameof(IsTestSignalSelection));
		OnPropertyChanged(nameof(TestSignalPresetLabel));
		OnPropertyChanged(nameof(TestSignalResolutionLabel));
		OnPropertyChanged(nameof(TestSignalFrameRateLabel));
		OnPropertyChanged(nameof(TestSignalAudioModeLabel));
		OnPropertyChanged(nameof(TestSignalMotionEnabled));
		OnPropertyChanged(nameof(TestSignalTimecodeEnabled));
		OnPropertyChanged(nameof(TestSignalFrameCounterEnabled));
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
