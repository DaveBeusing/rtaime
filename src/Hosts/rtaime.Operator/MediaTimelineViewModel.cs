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
	bool CanTrim);

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
	}

	public TimelineTrackCategory Category { get; }
	public string Label { get; }
	public bool IsCueTrack => Category == TimelineTrackCategory.Cue;
	public ObservableCollection<TimelineTrackItemViewModel> Items { get; }
}

public sealed record TimelineSelection(
	TimelineTrackItemViewModel? Item,
	TimelineCueViewModel? Cue);

public sealed class MediaTimelineViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly MediaTimelineController _controller;
	private readonly MediaTimelineMarkerController? _markers;
	private readonly SynchronizationContext? _synchronizationContext;
	private readonly TimelineTrackViewModel _videoTrack;
	private readonly List<long> _snapFrames = [];
	private MediaTimelineState _state;
	private MediaTimelineVisibleRange _visibleRange;
	private FrameRate? _projectionFrameRate;
	private TimelineTrackItemViewModel? _selectedItem;
	private TimelineCueViewModel? _selectedCue;
	private double _zoom = MediaTimelineGeometry.MinimumZoom;
	private int _projectionHash;
	private bool _projectionInitialized;
	private long? _inPointFrame;
	private long? _outPointFrame;
	private bool _snapEnabled = true;

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
	public bool HasInPoint => InPointFrame.HasValue;
	public bool HasOutPoint => OutPointFrame.HasValue;
	public bool HasInvalidRange => InPointFrame.HasValue && OutPointFrame.HasValue && InPointFrame.Value > OutPointFrame.Value;
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
		_videoTrack.Items.Clear();
		Cues.Clear();
		_snapFrames.Clear();
		_selectedItem = null;
		_selectedCue = null;
		_inPointFrame = snapshot.Markers?.InPointFrame;
		_outPointFrame = snapshot.Markers?.OutPointFrame;
		_projectionFrameRate = snapshot.Transport?.Position.FrameRate;

		if (snapshot.IsLoaded && snapshot.Transport is { } transport && snapshot.Markers is { } markers)
		{
			var totalFrames = transport.Position.TotalFrames;
			var start = markers.InPointFrame ?? 0;
			var end = markers.OutPointFrame ?? totalFrames - 1;
			var duration = Math.Max(1, end - start + 1);
			var sourceReference = snapshot.SourceId?.ToString();
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

		RefreshVisibleRange(preserveStart: Zoom > MediaTimelineGeometry.MinimumZoom);
		OnPropertyChanged(nameof(InPointFrame));
		OnPropertyChanged(nameof(OutPointFrame));
		OnPropertyChanged(nameof(HasInPoint));
		OnPropertyChanged(nameof(HasOutPoint));
		OnPropertyChanged(nameof(HasInvalidRange));
		OnPropertyChanged(nameof(SelectedItem));
		OnPropertyChanged(nameof(SelectedCue));
		OnPropertyChanged(nameof(AccessibilityDescription));
		RaiseCueCommands();
		SelectionChanged?.Invoke(new TimelineSelection(null, null));
	}

	public void SelectItem(TimelineTrackItemViewModel? item)
	{
		if (EqualityComparer<TimelineTrackItemViewModel?>.Default.Equals(_selectedItem, item) && _selectedCue is null)
			return;
		_selectedItem = item;
		_selectedCue = null;
		OnPropertyChanged(nameof(SelectedItem));
		OnPropertyChanged(nameof(SelectedCue));
		RaiseCueCommands();
		SelectionChanged?.Invoke(new TimelineSelection(item, null));
	}

	public void SelectCue(TimelineCueViewModel? cue)
	{
		if (EqualityComparer<TimelineCueViewModel?>.Default.Equals(_selectedCue, cue) && _selectedItem is null)
			return;
		_selectedCue = cue;
		_selectedItem = null;
		OnPropertyChanged(nameof(SelectedCue));
		OnPropertyChanged(nameof(SelectedItem));
		RaiseCueCommands();
		SelectionChanged?.Invoke(new TimelineSelection(null, cue));
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

	private long ApplySnap(long frame)
	{
		if (!SnapEnabled || _snapFrames.Count == 0 || VisibleFrameCount <= 0)
			return frame;
		var threshold = Math.Clamp(VisibleFrameCount / 100, 1, 10);
		return MediaTimelineGeometry.SnapFrame(frame, _snapFrames, threshold);
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

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
