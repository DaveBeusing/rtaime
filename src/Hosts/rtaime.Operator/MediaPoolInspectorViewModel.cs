// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;

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
	bool IsPinnable);

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
	bool IsReady);

public sealed class MediaPoolInspectorViewModel : INotifyPropertyChanged, IDisposable
{
	private const int MaxVisibleItems = 256;
	private static readonly string[] SupportedCategories = ["All", "Sources", "Clips", "Audio", "Graphics", "Compositions"];
	private static readonly string[] SupportedFilters = ["All", "Video", "Audio", "Graphics", "Online", "Offline"];

	private readonly OperatorViewModel _operator;
	private readonly MediaDeckViewModel _mediaDeck;
	private readonly List<OperatorSourceTileViewModel> _sourceSubscriptions = [];
	private readonly List<OperatorAudioInputViewModel> _audioSubscriptions = [];
	private readonly List<MediaPoolItemViewModel> _allItems = [];
	private string _searchText = string.Empty;
	private string _selectedCategory = "All";
	private string _selectedFilter = "All";
	private bool _isGridView = true;
	private MediaPoolItemViewModel? _selectedItem;
	private TimelineTrackItemViewModel? _timelineItem;
	private TimelineCueViewModel? _timelineCue;
	private string _emptyState = "No assets are available.";

	public MediaPoolInspectorViewModel(OperatorViewModel @operator, MediaDeckViewModel mediaDeck)
	{
		_operator = @operator ?? throw new ArgumentNullException(nameof(@operator));
		_mediaDeck = mediaDeck ?? throw new ArgumentNullException(nameof(mediaDeck));

		FilteredItems = [];
		InspectorProperties = [];
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

		_operator.PropertyChanged += OnOperatorPropertyChanged;
		_mediaDeck.PropertyChanged += OnMediaDeckPropertyChanged;
		_operator.Sources.CollectionChanged += OnSourcesChanged;
		_operator.AudioInputs.CollectionChanged += OnAudioInputsChanged;
		RewireItemSubscriptions();
		Refresh();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<MediaPoolItemViewModel> FilteredItems { get; }
	public ObservableCollection<InspectorPropertyViewModel> InspectorProperties { get; }
	public IReadOnlyList<string> Categories => SupportedCategories;
	public IReadOnlyList<string> Filters => SupportedFilters;
	public ICommand GridViewCommand { get; }
	public ICommand ListViewCommand { get; }
	public ICommand ImportCommand => _mediaDeck.OpenCommand;

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
			_timelineCue = null;
			ProjectSelectionToExistingOperatorContext(value);
			BuildInspector();
			RaiseSelectionState();
		}
	}

	public bool HasItems => FilteredItems.Count > 0;
	public bool HasSelection => SelectedItem is not null || _timelineItem is not null || _timelineCue is not null;
	public bool IsSourceSelection => _timelineItem is null && _timelineCue is null && SelectedItem?.Kind == MediaPoolItemKind.Source;
	public bool IsClipSelection => _timelineCue is null && (_timelineItem?.Category == TimelineTrackCategory.Video || SelectedItem?.Kind == MediaPoolItemKind.Clip);
	public bool IsAudioSelection => SelectedItem?.Kind == MediaPoolItemKind.Audio;
	public bool IsGraphicsSelection => SelectedItem?.Kind == MediaPoolItemKind.Graphics;
	public bool IsCompositionSelection => SelectedItem?.Kind == MediaPoolItemKind.Composition;
	public string InspectorTitle => _timelineCue?.Name ?? _timelineItem?.Label ?? SelectedItem?.Name ?? "No selection";
	public string InspectorDetail => _timelineCue is { } cue
		? $"CUE · {cue.Type.ToString().ToUpperInvariant()} · {cue.Timecode}"
		: _timelineItem is { } timelineItem
			? $"TIMELINE · {timelineItem.Category.ToString().ToUpperInvariant()} · {timelineItem.Status}"
			: SelectedItem?.Detail ?? "Select a Media Pool resource, timeline item or cue.";
	public string EmptyState
	{
		get => _emptyState;
		private set => Set(ref _emptyState, value);
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

	public void SelectTimelineItem(TimelineTrackItemViewModel item)
	{
		ArgumentNullException.ThrowIfNull(item);
		_timelineItem = item;
		_timelineCue = null;
		BuildInspector();
		RaiseSelectionState();
	}

	public void SelectTimelineCue(TimelineCueViewModel cue)
	{
		ArgumentNullException.ThrowIfNull(cue);
		_timelineCue = cue;
		_timelineItem = null;
		BuildInspector();
		RaiseSelectionState();
	}

	public void ClearTimelineSelection()
	{
		if (_timelineItem is null && _timelineCue is null)
			return;
		_timelineItem = null;
		_timelineCue = null;
		BuildInspector();
		RaiseSelectionState();
	}

	public void Refresh()
	{
		var selectedKey = SelectedItem?.Key;
		_allItems.Clear();

		foreach (var source in _operator.Sources.Take(MaxVisibleItems))
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
				mediaOnline ? _mediaDeck.State : $"OFFLINE · {_mediaDeck.State}",
				_mediaDeck.SourceId,
				_operator.Sources.FirstOrDefault(source => string.Equals(source.Id, _mediaDeck.SourceId, StringComparison.Ordinal))?.Thumbnail,
				mediaOnline,
				mediaOnline && !_mediaDeck.IsBusy));
		}

		foreach (var input in _operator.AudioInputs.Take(Math.Max(0, MaxVisibleItems - _allItems.Count)))
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

		if (_allItems.Count > MaxVisibleItems)
			_allItems.RemoveRange(MaxVisibleItems, _allItems.Count - MaxVisibleItems);

		ApplyFilter(selectedKey);
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
			.Take(MaxVisibleItems)
			.ToArray();

		FilteredItems.Clear();
		foreach (var item in filtered)
			FilteredItems.Add(item);

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
			Contains(item.ReferenceId, query);
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

		if (_timelineCue is { } cue)
		{
			Add("timeline.cue.name", "Cue", cue.Name, "METADATA", true);
			Add("timeline.cue.type", "Type", cue.Type.ToString().ToUpperInvariant(), "METADATA");
			Add("timeline.cue.time", "Time", cue.Timecode, "COMMITTED");
			Add("timeline.cue.frame", "Frame", cue.Frame.ToString("N0"), "COMMITTED");
			Add("timeline.cue.target", "Target", cue.TargetReference ?? "—", "METADATA");
			return;
		}

		if (_timelineItem is { } timelineItem)
		{
			Add("timeline.item.label", "Item", timelineItem.Label, "METADATA");
			Add("timeline.item.track", "Track", timelineItem.Category.ToString().ToUpperInvariant(), "METADATA");
			Add("timeline.item.start", "Start frame", timelineItem.StartFrame.ToString("N0"), "COMMITTED");
			Add("timeline.item.duration", "Duration frames", timelineItem.DurationFrames.ToString("N0"), "COMMITTED");
			Add("timeline.item.source", "Source", timelineItem.SourceReference ?? "—", "METADATA");
			Add("timeline.item.status", "Status", timelineItem.Status, "COMMITTED");
			Add("timeline.item.in", "IN", timelineItem.InFrame?.ToString("N0") ?? "—", "COMMITTED", timelineItem.CanTrim);
			Add("timeline.item.out", "OUT", timelineItem.OutFrame?.ToString("N0") ?? "—", "COMMITTED", timelineItem.CanTrim);
			return;
		}

		var item = SelectedItem;
		if (item is null)
			return;

		switch (item.Kind)
		{
			case MediaPoolItemKind.Source:
				Add("source.name", "Source", item.Name, "METADATA");
				Add("source.type", "Type", item.Detail, "METADATA");
				Add("source.format", "Format", item.Format, "METADATA");
				Add("source.state", "State", item.State, "COMMITTED");
				Add("source.readiness", "Readiness", item.IsReady ? "READY" : "NOT READY", "COMMITTED");
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
				break;

			case MediaPoolItemKind.Composition:
				Add("ai.feature", "Feature", _operator.AIFeature, "COMMITTED");
				Add("ai.provider", "Provider", _operator.AIProvider, "COMMITTED");
				Add("ai.confidence", "Confidence", _operator.AIConfidence, "COMMITTED");
				Add("ai.inference", "Inference", _operator.AIInferenceTime, "COMMITTED");
				Add("ai.fallback", "Fallback", string.IsNullOrWhiteSpace(_operator.AIError) ? "Clean Program" : _operator.AIError!, "COMMITTED");
				break;
		}
	}

	private void Add(string id, string label, string value, string state, bool pinnable = false) =>
		InspectorProperties.Add(new InspectorPropertyViewModel(id, label, value, state, pinnable));

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

	private void OnMediaDeckPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
	private void OnSourceItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
	private void OnAudioItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

	private void RaiseSelectionState()
	{
		OnPropertyChanged(nameof(HasSelection));
		OnPropertyChanged(nameof(IsSourceSelection));
		OnPropertyChanged(nameof(IsClipSelection));
		OnPropertyChanged(nameof(IsAudioSelection));
		OnPropertyChanged(nameof(IsGraphicsSelection));
		OnPropertyChanged(nameof(IsCompositionSelection));
		OnPropertyChanged(nameof(InspectorTitle));
		OnPropertyChanged(nameof(InspectorDetail));
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
