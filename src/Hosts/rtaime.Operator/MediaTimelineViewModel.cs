// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public enum TimelineTrackCategory
{
	Video,
	Graphics,
	Overlay,
	Audio,
	AI,
	Control,
	Cue
}

public enum TimelineCueType
{
	Media
}

public sealed record TimelineTrackItemViewModel(
	string Id,
	TimelineTrackCategory Category,
	string Label,
	long StartFrame,
	long DurationFrames,
	long? InFrame,
	long? OutFrame,
	string? SourceReference,
	string Status,
	bool CanTrim) : INotifyPropertyChanged
{
	private bool _isSelected;

	public event PropertyChangedEventHandler? PropertyChanged;

	public bool IsSelected
	{
		get => _isSelected;
		set
		{
			if (_isSelected == value)
				return;
			_isSelected = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
		}
	}
}

public sealed record TimelineCueViewModel(
	MediaCuePointId Id,
	string Name,
	long Frame,
	string Timecode,
	TimelineCueType Type,
	string? TargetReference);

public sealed record TimelineRulerTickViewModel(long Frame, string Timecode);

public sealed class TimelineTrackViewModel
{
	public TimelineTrackViewModel(TimelineTrackCategory category, string label)
	{
		Category = category;
		Label = label;
		Items = [];
		VisibleItems = [];
	}

	public TimelineTrackCategory Category { get; }
	public string Label { get; }
	public bool IsCueTrack => Category == TimelineTrackCategory.Cue;
	public ObservableCollection<TimelineTrackItemViewModel> Items { get; }
	public ObservableCollection<TimelineTrackItemViewModel> VisibleItems { get; }

	public void RefreshVisibleItems(long visibleStartFrame, long visibleEndFrame)
	{
		var visible = Items
			.Where(item =>
			{
				var itemEnd = item.StartFrame > long.MaxValue - item.DurationFrames + 1
					? long.MaxValue
					: item.StartFrame + item.DurationFrames - 1;
				return itemEnd >= visibleStartFrame && item.StartFrame <= visibleEndFrame;
			})
			.ToArray();

		if (VisibleItems.SequenceEqual(visible))
			return;

		VisibleItems.Clear();
		foreach (var item in visible)
			VisibleItems.Add(item);
	}
}

public sealed record TimelineSelection(
	TimelineTrackItemViewModel? Item,
	TimelineCueViewModel? Cue,
	IReadOnlyList<TimelineTrackItemViewModel> Items);

public sealed class MediaTimelineViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly MediaTimelineController _controller;
	private readonly MediaTimelineMarkerController? _markers;
	private readonly SynchronizationContext? _synchronizationContext;
	private readonly TimelineTrackViewModel _videoTrack;
	private readonly List<long> _snapFrames = [];
	private readonly List<TimelineResourceProjection> _resourceProjections = [];
	private MediaTimelineState _state;
	private MediaTimelineVisibleRange _visibleRange;
	private FrameRate? _projectionFrameRate;
	private TimelineTrackItemViewModel? _selectedItem;
	private TimelineCueViewModel? _selectedCue;
	private long? _previewInPointFrame;
	private long? _previewOutPointFrame;
	private double _zoom = MediaTimelineGeometry.MinimumZoom;
	private int _projectionHash;
	private bool _projectionInitialized;
	private long? _inPointFrame;
	private long? _outPointFrame;
	private bool _snapEnabled = true;
	private string? _activeTimelineContextReference;

	public MediaTimelineViewModel(
		MediaTimelineController? controller = null,
		MediaTimelineMarkerController? markers = null)
	{
		_controller = controller ?? new MediaTimelineController();
		_markers = markers;
		_synchronizationContext = SynchronizationContext.Current;
		_state = _controller.State;
		_visibleRange = new MediaTimelineVisibleRange(0, 0);

		Tracks =
		[
			_videoTrack = new TimelineTrackViewModel(TimelineTrackCategory.Video, "V1  PROGRAM"),
			new TimelineTrackViewModel(TimelineTrackCategory.Graphics, "V2  GRAPHICS"),
			new TimelineTrackViewModel(TimelineTrackCategory.Overlay, "V3  OVERLAY"),
			new TimelineTrackViewModel(TimelineTrackCategory.Audio, "A1  AUDIO"),
			new TimelineTrackViewModel(TimelineTrackCategory.AI, "AI  AUTOMATION"),
			new TimelineTrackViewModel(TimelineTrackCategory.Control, "CTRL"),
			new TimelineTrackViewModel(TimelineTrackCategory.Cue, "CUE")
		];
		Cues = [];
		VisibleCues = [];
		SelectedItems = [];
		RulerTicks = [];

		_controller.StateChanged += OnControllerStateChanged;
		if (_markers is not null)
			_markers.StateChanged += OnMarkerStateChanged;

		StepBackwardCommand = new AsyncRelayCommand(() => _controller.SeekRelativeAsync(-1).AsTask(), () => CanSeek);
		StepForwardCommand = new AsyncRelayCommand(() => _controller.SeekRelativeAsync(1).AsTask(), () => CanSeek);
		JumpToStartCommand = new AsyncRelayCommand(() => _controller.SeekToStartAsync().AsTask(), () => CanSeek);
		JumpToEndCommand = new AsyncRelayCommand(() => _controller.SeekToEndAsync().AsTask(), () => CanSeek);
		PreviousCueCommand = new AsyncRelayCommand(JumpToPreviousCueAsync, () => CanSeek && _markers is not null && Cues.Count > 0);
		NextCueCommand = new AsyncRelayCommand(JumpToNextCueAsync, () => CanSeek && _markers is not null && Cues.Count > 0);
		JumpSelectedCueCommand = new AsyncRelayCommand(JumpSelectedCueAsync, () => CanSeek && _markers is not null && SelectedCue is not null);
		ZoomInCommand = new AsyncRelayCommand(() =>
		{
			SetZoom(Math.Min(MediaTimelineGeometry.MaximumZoom, Zoom * 2.0));
			return Task.CompletedTask;
		}, () => IsLoaded && Zoom < MediaTimelineGeometry.MaximumZoom);
		ZoomOutCommand = new AsyncRelayCommand(() =>
		{
			SetZoom(Math.Max(MediaTimelineGeometry.MinimumZoom, Zoom / 2.0));
			return Task.CompletedTask;
		}, () => IsLoaded && Zoom > MediaTimelineGeometry.MinimumZoom);
		FitCommand = new AsyncRelayCommand(() =>
		{
			SetZoom(MediaTimelineGeometry.MinimumZoom);
			return Task.CompletedTask;
		}, () => IsLoaded && Zoom > MediaTimelineGeometry.MinimumZoom);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public event Action<TimelineSelection>? SelectionChanged;

	public MediaTimelineController Controller => _controller;
	public ObservableCollection<TimelineTrackViewModel> Tracks { get; }
	public ObservableCollection<TimelineCueViewModel> Cues { get; }
	public ObservableCollection<TimelineCueViewModel> VisibleCues { get; }
	public ObservableCollection<TimelineTrackItemViewModel> SelectedItems { get; }
	public ObservableCollection<TimelineRulerTickViewModel> RulerTicks { get; }

	public ICommand StepBackwardCommand { get; }
	public ICommand StepForwardCommand { get; }
	public ICommand JumpToStartCommand { get; }
	public ICommand JumpToEndCommand { get; }
	public ICommand PreviousCueCommand { get; }
	public ICommand NextCueCommand { get; }
	public ICommand JumpSelectedCueCommand { get; }
	public ICommand ZoomInCommand { get; }
	public ICommand ZoomOutCommand { get; }
	public ICommand FitCommand { get; }

	public bool IsLoaded => _state.IsLoaded;
	public bool CanSeek => _state.CanSeek;
	public bool HasPendingSeek => _state.HasPendingSeek;
	public string TransportState => _state.TransportState;
	public long ConfirmedFrame => _state.ConfirmedFrame;
	public long DisplayFrame => _state.DisplayFrame;
	public long TotalFrames => _state.TotalFrames;
	public double SliderMaximum => Math.Max(0, _state.TotalFrames - 1);
	public double SliderValue => _state.DisplayFrame;
	public double ProgressPercent => _state.Progress * 100.0;
	public string CurrentTimecode => _state.CurrentTimecode;
	public string DurationTimecode => _state.DurationTimecode;
	public string RemainingTimecode => _state.RemainingTimecode;
	public string FrameRate => _state.FrameRate;
	public string? Failure => _state.Failure;
	public long VisibleStartFrame => IsLoaded ? _visibleRange.StartFrame : 0;
	public long VisibleEndFrame => IsLoaded ? _visibleRange.EndFrame : 0;
	public long VisibleFrameCount => IsLoaded ? _visibleRange.FrameCount : 0;
	public double ScrollMaximum => Math.Max(0, TotalFrames - VisibleFrameCount);
	public double ScrollLargeChange => Math.Max(1, VisibleFrameCount * 0.8);
	public double ScrollValue
	{
		get => VisibleStartFrame;
		set
		{
			if (!IsLoaded || TotalFrames <= 0 || !double.IsFinite(value))
				return;
			var requested = checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
			var range = MediaTimelineGeometry.CalculateVisibleRangeFromStart(TotalFrames, Zoom, requested);
			if (range == _visibleRange)
				return;
			_visibleRange = range;
			RaiseViewport();
		}
	}

	public double Zoom => _zoom;
	public string ZoomLabel => $"{Zoom:0.#}×";
	public string VisibleRangeLabel => IsLoaded
		? $"{FormatFrame(VisibleStartFrame)} → {FormatFrame(VisibleEndFrame)}"
		: "—";
	public long? InPointFrame => _inPointFrame;
	public long? OutPointFrame => _outPointFrame;
	public long? PreviewInPointFrame => _previewInPointFrame;
	public long? PreviewOutPointFrame => _previewOutPointFrame;
	public long? EffectiveInPointFrame => PreviewInPointFrame ?? InPointFrame;
	public long? EffectiveOutPointFrame => PreviewOutPointFrame ?? OutPointFrame;
	public bool HasTrimPreview => PreviewInPointFrame.HasValue || PreviewOutPointFrame.HasValue;
	public bool HasInPoint => EffectiveInPointFrame.HasValue;
	public bool HasOutPoint => EffectiveOutPointFrame.HasValue;
	public bool HasInvalidRange => EffectiveInPointFrame.HasValue && EffectiveOutPointFrame.HasValue && EffectiveInPointFrame.Value > EffectiveOutPointFrame.Value;
	public bool SnapEnabled
	{
		get => _snapEnabled;
		set
		{
			if (_snapEnabled == value)
				return;
			_snapEnabled = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(SnapStatus));
		}
	}
	public string SnapStatus => SnapEnabled ? "SNAP ON" : "SNAP OFF";
	public TimelineTrackItemViewModel? SelectedItem => _selectedItem;
	public TimelineCueViewModel? SelectedCue => _selectedCue;
	public int SelectedItemCount => SelectedItems.Count;
	public bool HasMultipleItemSelection => SelectedItemCount > 1;
	public string SelectionStatus => SelectedItemCount switch
	{
		0 => "NO CLIP SELECTION",
		1 => "1 CLIP SELECTED",
		_ => $"{SelectedItemCount} CLIPS SELECTED"
	};
	public string AccessibilityDescription => IsLoaded
		? $"Layered media timeline. Current {CurrentTimecode}, visible range {VisibleRangeLabel}, duration {DurationTimecode}."
		: "Layered media timeline. No local media transport is loaded.";

	public void ApplyMediaDeckSnapshot(MediaDeckSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var hash = ProjectionHash(snapshot);
		if (_projectionInitialized && hash == _projectionHash)
			return;

		_projectionInitialized = true;
		_projectionHash = hash;
		var sourceReference = snapshot.SourceId?.ToString();
		var timelineContextReference = snapshot.IsLoaded
			? $"{snapshot.Probe?.AssetId}|{sourceReference}"
			: null;
		if (!string.Equals(_activeTimelineContextReference, timelineContextReference, StringComparison.Ordinal))
		{
			_resourceProjections.Clear();
			_activeTimelineContextReference = timelineContextReference;
		}
		foreach (var track in Tracks)
			track.Items.Clear();
		Cues.Clear();
		_snapFrames.Clear();
		ClearSelectedItems();
		_selectedItem = null;
		_selectedCue = null;
		_previewInPointFrame = null;
		_previewOutPointFrame = null;
		_inPointFrame = snapshot.Markers?.InPointFrame;
		_outPointFrame = snapshot.Markers?.OutPointFrame;
		_projectionFrameRate = snapshot.Transport?.Position.FrameRate;

		if (snapshot.IsLoaded && snapshot.Transport is { } transport && snapshot.Markers is { } markers)
		{
			var totalFrames = transport.Position.TotalFrames;
			var start = markers.InPointFrame ?? 0;
			var end = markers.OutPointFrame ?? totalFrames - 1;
			var duration = Math.Max(1, end - start + 1);
			var item = new TimelineTrackItemViewModel(
				$"media:{markers.AssetId}",
				TimelineTrackCategory.Video,
				snapshot.Probe?.FileName ?? "Loaded media",
				start,
				duration,
				markers.InPointFrame,
				markers.OutPointFrame,
				sourceReference,
				snapshot.State.ToString().ToUpperInvariant(),
				true);
			_videoTrack.Items.Add(item);

			foreach (var cue in markers.CuePoints)
			{
				Cues.Add(new TimelineCueViewModel(
					cue.Id,
					cue.Name,
					cue.PositionFrame,
					MediaTimelineTimecode.FormatFrame(cue.PositionFrame, transport.Position.FrameRate),
					TimelineCueType.Media,
					sourceReference));
				_snapFrames.Add(cue.PositionFrame);
			}
			if (markers.InPointFrame.HasValue)
				_snapFrames.Add(markers.InPointFrame.Value);
			if (markers.OutPointFrame.HasValue)
				_snapFrames.Add(markers.OutPointFrame.Value);
		}

		RebuildResourceProjections();
		RefreshVisibleRange(preserveStart: Zoom > MediaTimelineGeometry.MinimumZoom);
		OnPropertyChanged(nameof(InPointFrame));
		OnPropertyChanged(nameof(OutPointFrame));
		OnPropertyChanged(nameof(PreviewInPointFrame));
		OnPropertyChanged(nameof(PreviewOutPointFrame));
		OnPropertyChanged(nameof(EffectiveInPointFrame));
		OnPropertyChanged(nameof(EffectiveOutPointFrame));
		OnPropertyChanged(nameof(HasTrimPreview));
		OnPropertyChanged(nameof(HasInPoint));
		OnPropertyChanged(nameof(HasOutPoint));
		OnPropertyChanged(nameof(HasInvalidRange));
		OnPropertyChanged(nameof(SelectedItem));
		OnPropertyChanged(nameof(SelectedCue));
		RaiseSelectionProperties();
		OnPropertyChanged(nameof(AccessibilityDescription));
		RaiseCueCommands();
		SelectionChanged?.Invoke(new TimelineSelection(null, null, []));
	}

	public bool CanAcceptMediaPoolDrop(
		MediaPoolItemViewModel? item,
		TimelineTrackCategory? category)
	{
		if (item is null || category is null || !item.IsReady || !IsLoaded || TotalFrames <= 0)
			return false;

		return item.Kind switch
		{
			MediaPoolItemKind.Clip =>
				category == TimelineTrackCategory.Video &&
				_videoTrack.Items.Any(candidate =>
					string.Equals(candidate.SourceReference, item.ReferenceId, StringComparison.Ordinal)),
			MediaPoolItemKind.Audio => category == TimelineTrackCategory.Audio,
			MediaPoolItemKind.Graphics => category is TimelineTrackCategory.Graphics or TimelineTrackCategory.Overlay,
			_ => false
		};
	}

	public bool ProjectMediaPoolDrop(
		MediaPoolItemViewModel item,
		TimelineTrackCategory category)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (!CanAcceptMediaPoolDrop(item, category))
			return false;

		if (item.Kind == MediaPoolItemKind.Clip)
		{
			var authoritativeItem = _videoTrack.Items.First(candidate =>
				string.Equals(candidate.SourceReference, item.ReferenceId, StringComparison.Ordinal));
			SelectItem(authoritativeItem);
			return true;
		}

		_resourceProjections.RemoveAll(candidate =>
			string.Equals(candidate.Key, item.Key, StringComparison.Ordinal) &&
			candidate.Category == category);
		_resourceProjections.Add(new TimelineResourceProjection(
			item.Key,
			category,
			item.Name,
			item.ReferenceId,
			item.State));
		RebuildResourceTrack(category);

		var track = Tracks.Single(candidate => candidate.Category == category);
		var projectedItem = track.Items.LastOrDefault(candidate =>
			string.Equals(candidate.Id, $"projection:{item.Key}:{category}", StringComparison.Ordinal));
		if (projectedItem is not null)
			SelectItem(projectedItem);
		return projectedItem is not null;
	}

	public void SelectItem(
		TimelineTrackItemViewModel? item,
		bool extendSelection = false,
		bool toggleSelection = false)
	{
		if (item is null)
		{
			ClearSelectedItems();
			_selectedItem = null;
			_selectedCue = null;
			RaiseSelectionProperties();
			RaiseCueCommands();
			SelectionChanged?.Invoke(new TimelineSelection(null, null, []));
			return;
		}

		if (!extendSelection)
			ClearSelectedItems();

		if (toggleSelection && item.IsSelected)
		{
			item.IsSelected = false;
			SelectedItems.Remove(item);
			_selectedItem = SelectedItems.LastOrDefault();
		}
		else
		{
			if (!item.IsSelected)
			{
				item.IsSelected = true;
				SelectedItems.Add(item);
			}
			_selectedItem = item;
		}

		_selectedCue = null;
		RaiseSelectionProperties();
		RaiseCueCommands();
		SelectionChanged?.Invoke(new TimelineSelection(_selectedItem, null, SelectedItems.ToArray()));
	}

	public void SelectCue(TimelineCueViewModel? cue)
	{
		if (EqualityComparer<TimelineCueViewModel?>.Default.Equals(_selectedCue, cue) && _selectedItem is null)
			return;
		ClearSelectedItems();
		_selectedCue = cue;
		_selectedItem = null;
		RaiseSelectionProperties();
		RaiseCueCommands();
		SelectionChanged?.Invoke(new TimelineSelection(null, cue, []));
	}

	public void BeginTrimPreview(string trimKind)
	{
		if (trimKind is not ("IN" or "OUT"))
			throw new ArgumentOutOfRangeException(nameof(trimKind));

		if (string.Equals(trimKind, "IN", StringComparison.Ordinal))
			_previewInPointFrame = InPointFrame;
		else
			_previewOutPointFrame = OutPointFrame;
		RaiseTrimPreview();
	}

	public void PreviewTrim(string trimKind, double logicalX, double logicalWidth)
	{
		if (!CanSeek || TotalFrames <= 0 || logicalWidth <= 0 || trimKind is not ("IN" or "OUT"))
			return;

		var frame = MediaTimelineGeometry.FrameFromVisiblePosition(logicalX, logicalWidth, _visibleRange);
		frame = ApplySnap(frame);
		if (string.Equals(trimKind, "IN", StringComparison.Ordinal))
			_previewInPointFrame = frame;
		else
			_previewOutPointFrame = frame;
		RaiseTrimPreview();
	}

	public void CancelTrimPreview()
	{
		if (!HasTrimPreview)
			return;
		_previewInPointFrame = null;
		_previewOutPointFrame = null;
		RaiseTrimPreview();
	}

	public void BeginPointerSeek() => _controller.BeginPointerSeek();

	public void PreviewPointerSeek(double logicalX, double logicalWidth)
	{
		if (!CanSeek || TotalFrames <= 0 || logicalWidth <= 0)
			return;
		var frame = MediaTimelineGeometry.FrameFromVisiblePosition(logicalX, logicalWidth, _visibleRange);
		_controller.PreviewPointerSeek(ApplySnap(frame));
	}

	public ValueTask CompletePointerSeekAsync(
		double logicalX,
		double logicalWidth,
		CancellationToken cancellationToken = default)
	{
		if (!CanSeek || TotalFrames <= 0 || logicalWidth <= 0)
			return ValueTask.CompletedTask;
		var frame = MediaTimelineGeometry.FrameFromVisiblePosition(logicalX, logicalWidth, _visibleRange);
		return _controller.CompletePointerSeekAsync(ApplySnap(frame), cancellationToken);
	}

	public ValueTask<bool> TrimInAsync(
		double logicalX,
		double logicalWidth,
		CancellationToken cancellationToken = default)
	{
		if (_markers is null || !CanSeek || TotalFrames <= 0 || logicalWidth <= 0)
			return ValueTask.FromResult(false);
		var frame = MediaTimelineGeometry.FrameFromVisiblePosition(logicalX, logicalWidth, _visibleRange);
		return _markers.SetInAtFrameAsync(ApplySnap(frame), cancellationToken);
	}

	public ValueTask<bool> TrimOutAsync(
		double logicalX,
		double logicalWidth,
		CancellationToken cancellationToken = default)
	{
		if (_markers is null || !CanSeek || TotalFrames <= 0 || logicalWidth <= 0)
			return ValueTask.FromResult(false);
		var frame = MediaTimelineGeometry.FrameFromVisiblePosition(logicalX, logicalWidth, _visibleRange);
		return _markers.SetOutAtFrameAsync(ApplySnap(frame), cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		_controller.StateChanged -= OnControllerStateChanged;
		if (_markers is not null)
			_markers.StateChanged -= OnMarkerStateChanged;
		await _controller.DisposeAsync().ConfigureAwait(false);
	}

	private async Task JumpToPreviousCueAsync()
	{
		if (_markers is null || !await _markers.JumpToPreviousCueAsync().ConfigureAwait(false))
			return;
		Post(SelectCueAtConfirmedFrame);
	}

	private async Task JumpToNextCueAsync()
	{
		if (_markers is null || !await _markers.JumpToNextCueAsync().ConfigureAwait(false))
			return;
		Post(SelectCueAtConfirmedFrame);
	}

	private async Task JumpSelectedCueAsync()
	{
		var cue = SelectedCue;
		if (_markers is null || cue is null)
			return;
		await _markers.JumpToCueAsync(cue.Id).ConfigureAwait(false);
	}

	private void SelectCueAtConfirmedFrame()
	{
		var cue = Cues.FirstOrDefault(candidate => candidate.Frame == _controller.State.ConfirmedFrame);
		if (cue is not null)
			SelectCue(cue);
	}

	private void SetZoom(double zoom)
	{
		if (!IsLoaded || TotalFrames <= 0)
			return;
		var normalized = Math.Clamp(zoom, MediaTimelineGeometry.MinimumZoom, MediaTimelineGeometry.MaximumZoom);
		if (Math.Abs(normalized - _zoom) < 0.001)
			return;

		_zoom = normalized;
		_visibleRange = MediaTimelineGeometry.CalculateVisibleRange(TotalFrames, _zoom, DisplayFrame);
		RaiseViewport();
	}

	private void RefreshVisibleRange(bool preserveStart)
	{
		if (!IsLoaded || TotalFrames <= 0)
		{
			_visibleRange = new MediaTimelineVisibleRange(0, 0);
			RulerTicks.Clear();
			RaiseViewport();
			return;
		}

		_visibleRange = preserveStart
			? MediaTimelineGeometry.CalculateVisibleRangeFromStart(TotalFrames, Zoom, _visibleRange.StartFrame)
			: MediaTimelineGeometry.CalculateVisibleRange(TotalFrames, Zoom, DisplayFrame);
		RaiseViewport();
	}

	private void EnsurePlayheadVisible()
	{
		if (!IsLoaded || Zoom <= MediaTimelineGeometry.MinimumZoom || _visibleRange.FrameCount <= 0)
			return;
		if (DisplayFrame >= _visibleRange.StartFrame && DisplayFrame <= _visibleRange.EndFrame)
			return;

		_visibleRange = MediaTimelineGeometry.CalculateVisibleRange(TotalFrames, Zoom, DisplayFrame);
		RaiseViewport();
	}

	private void RebuildResourceProjections()
	{
		foreach (var category in new[] { TimelineTrackCategory.Audio, TimelineTrackCategory.Graphics, TimelineTrackCategory.Overlay })
			RebuildResourceTrack(category);
	}

	private void RebuildResourceTrack(TimelineTrackCategory category)
	{
		var track = Tracks.Single(candidate => candidate.Category == category);
		track.Items.Clear();
		if (!IsLoaded || TotalFrames <= 0)
			return;

		foreach (var projection in _resourceProjections.Where(candidate => candidate.Category == category))
		{
			track.Items.Add(new TimelineTrackItemViewModel(
				$"projection:{projection.Key}:{projection.Category}",
				projection.Category,
				projection.Label,
				0,
				TotalFrames,
				null,
				null,
				projection.ReferenceId,
				$"PROJECTED · {projection.SourceState}",
				false));
		}
		track.RefreshVisibleItems(VisibleStartFrame, VisibleEndFrame);
	}

	private long ApplySnap(long frame)
	{
		if (!SnapEnabled || _snapFrames.Count == 0 || VisibleFrameCount <= 0)
			return frame;
		var threshold = Math.Clamp(VisibleFrameCount / 100, 1, 10);
		return MediaTimelineGeometry.SnapFrame(frame, _snapFrames, threshold);
	}

	private void RefreshViewportProjection()
	{
		foreach (var track in Tracks)
			track.RefreshVisibleItems(VisibleStartFrame, VisibleEndFrame);

		var visibleCues = Cues
			.Where(cue => cue.Frame >= VisibleStartFrame && cue.Frame <= VisibleEndFrame)
			.ToArray();
		if (!VisibleCues.SequenceEqual(visibleCues))
		{
			VisibleCues.Clear();
			foreach (var cue in visibleCues)
				VisibleCues.Add(cue);
		}
	}

	private void RefreshRulerTicks()
	{
		RulerTicks.Clear();
		if (!IsLoaded || _projectionFrameRate is null || _visibleRange.FrameCount <= 0)
			return;

		const int targetTickCount = 9;
		var lastFrame = -1L;
		for (var index = 0; index < targetTickCount; index++)
		{
			var fraction = index / (double)(targetTickCount - 1);
			var frame = _visibleRange.StartFrame + checked((long)Math.Round(
				(_visibleRange.FrameCount - 1) * fraction,
				MidpointRounding.AwayFromZero));
			if (frame == lastFrame)
				continue;
			RulerTicks.Add(new TimelineRulerTickViewModel(frame, FormatFrame(frame)));
			lastFrame = frame;
		}
	}

	private string FormatFrame(long frame) =>
		_projectionFrameRate is { } frameRate
			? MediaTimelineTimecode.FormatFrame(Math.Max(0, frame), frameRate)
			: frame.ToString("N0");

	private void OnControllerStateChanged(object? sender, EventArgs e) => Post(RefreshState);

	private void OnMarkerStateChanged(object? sender, EventArgs e) => Post(RaiseCueCommands);

	private void RefreshState()
	{
		_state = _controller.State;
		EnsurePlayheadVisible();
		RaiseAll();
	}

	private void RaiseViewport()
	{
		RefreshViewportProjection();
		RefreshRulerTicks();
		OnPropertyChanged(nameof(VisibleStartFrame));
		OnPropertyChanged(nameof(VisibleEndFrame));
		OnPropertyChanged(nameof(VisibleFrameCount));
		OnPropertyChanged(nameof(ScrollMaximum));
		OnPropertyChanged(nameof(ScrollLargeChange));
		OnPropertyChanged(nameof(ScrollValue));
		OnPropertyChanged(nameof(Zoom));
		OnPropertyChanged(nameof(ZoomLabel));
		OnPropertyChanged(nameof(VisibleRangeLabel));
		OnPropertyChanged(nameof(AccessibilityDescription));
		(ZoomInCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ZoomOutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(FitCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private void RaiseAll()
	{
		OnPropertyChanged(nameof(IsLoaded));
		OnPropertyChanged(nameof(CanSeek));
		OnPropertyChanged(nameof(HasPendingSeek));
		OnPropertyChanged(nameof(TransportState));
		OnPropertyChanged(nameof(ConfirmedFrame));
		OnPropertyChanged(nameof(DisplayFrame));
		OnPropertyChanged(nameof(TotalFrames));
		OnPropertyChanged(nameof(SliderMaximum));
		OnPropertyChanged(nameof(SliderValue));
		OnPropertyChanged(nameof(ProgressPercent));
		OnPropertyChanged(nameof(CurrentTimecode));
		OnPropertyChanged(nameof(DurationTimecode));
		OnPropertyChanged(nameof(RemainingTimecode));
		OnPropertyChanged(nameof(FrameRate));
		OnPropertyChanged(nameof(Failure));
		OnPropertyChanged(nameof(AccessibilityDescription));
		(StepBackwardCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(StepForwardCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(JumpToStartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(JumpToEndCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		RaiseCueCommands();
		(ZoomInCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ZoomOutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(FitCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private void RaiseSelectionProperties()
	{
		OnPropertyChanged(nameof(SelectedItem));
		OnPropertyChanged(nameof(SelectedCue));
		OnPropertyChanged(nameof(SelectedItemCount));
		OnPropertyChanged(nameof(HasMultipleItemSelection));
		OnPropertyChanged(nameof(SelectionStatus));
	}

	private void ClearSelectedItems()
	{
		foreach (var selected in SelectedItems)
			selected.IsSelected = false;
		SelectedItems.Clear();
	}

	private void RaiseTrimPreview()
	{
		OnPropertyChanged(nameof(PreviewInPointFrame));
		OnPropertyChanged(nameof(PreviewOutPointFrame));
		OnPropertyChanged(nameof(EffectiveInPointFrame));
		OnPropertyChanged(nameof(EffectiveOutPointFrame));
		OnPropertyChanged(nameof(HasTrimPreview));
		OnPropertyChanged(nameof(HasInPoint));
		OnPropertyChanged(nameof(HasOutPoint));
		OnPropertyChanged(nameof(HasInvalidRange));
	}

	private void RaiseCueCommands()
	{
		(PreviousCueCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(NextCueCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(JumpSelectedCueCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private static int ProjectionHash(MediaDeckSnapshot snapshot)
	{
		var hash = new HashCode();
		hash.Add(snapshot.IsLoaded);
		hash.Add(snapshot.State);
		hash.Add(snapshot.SourceId);
		hash.Add(snapshot.Probe?.AssetId);
		hash.Add(snapshot.Probe?.FileName, StringComparer.Ordinal);
		hash.Add(snapshot.Transport?.Position.TotalFrames);
		hash.Add(snapshot.Transport?.Position.FrameRate);
		hash.Add(snapshot.Markers?.InPointFrame);
		hash.Add(snapshot.Markers?.OutPointFrame);
		if (snapshot.Markers is not null)
		{
			foreach (var cue in snapshot.Markers.CuePoints)
			{
				hash.Add(cue.Id);
				hash.Add(cue.Name, StringComparer.Ordinal);
				hash.Add(cue.PositionFrame);
			}
		}
		return hash.ToHashCode();
	}

	private void Post(Action action)
	{
		if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
			_synchronizationContext.Post(_ => action(), null);
		else
			action();
	}

	private sealed record TimelineResourceProjection(
		string Key,
		TimelineTrackCategory Category,
		string Label,
		string? ReferenceId,
		string SourceState);

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
