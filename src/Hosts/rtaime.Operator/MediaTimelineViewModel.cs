// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;

namespace rtaime.Operator;

public sealed class MediaTimelineViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly MediaTimelineController _controller;
	private readonly SynchronizationContext? _synchronizationContext;
	private MediaTimelineState _state;

	public MediaTimelineViewModel(MediaTimelineController? controller = null)
	{
		_controller = controller ?? new MediaTimelineController();
		_synchronizationContext = SynchronizationContext.Current;
		_state = _controller.State;
		_controller.StateChanged += OnControllerStateChanged;
		StepBackwardCommand = new AsyncRelayCommand(() => _controller.SeekRelativeAsync(-1).AsTask(), () => CanSeek);
		StepForwardCommand = new AsyncRelayCommand(() => _controller.SeekRelativeAsync(1).AsTask(), () => CanSeek);
		JumpToStartCommand = new AsyncRelayCommand(() => _controller.SeekToStartAsync().AsTask(), () => CanSeek);
		JumpToEndCommand = new AsyncRelayCommand(() => _controller.SeekToEndAsync().AsTask(), () => CanSeek);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public MediaTimelineController Controller => _controller;
	public ICommand StepBackwardCommand { get; }
	public ICommand StepForwardCommand { get; }
	public ICommand JumpToStartCommand { get; }
	public ICommand JumpToEndCommand { get; }

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
	public string AccessibilityDescription => IsLoaded
		? $"Media timeline. Current {CurrentTimecode}, duration {DurationTimecode}, remaining {RemainingTimecode}."
		: "Media timeline. No local media transport is loaded.";

	public void BeginPointerSeek() => _controller.BeginPointerSeek();

	public void PreviewPointerSeek(double logicalX, double logicalWidth)
	{
		if (!CanSeek || TotalFrames <= 0 || logicalWidth <= 0)
			return;
		_controller.PreviewPointerSeek(MediaTimelineGeometry.FrameFromLogicalPosition(logicalX, logicalWidth, TotalFrames));
	}

	public ValueTask CompletePointerSeekAsync(double logicalX, double logicalWidth, CancellationToken cancellationToken = default)
	{
		if (!CanSeek || TotalFrames <= 0 || logicalWidth <= 0)
			return ValueTask.CompletedTask;
		var frame = MediaTimelineGeometry.FrameFromLogicalPosition(logicalX, logicalWidth, TotalFrames);
		return _controller.CompletePointerSeekAsync(frame, cancellationToken);
	}

	public async ValueTask DisposeAsync()
	{
		_controller.StateChanged -= OnControllerStateChanged;
		await _controller.DisposeAsync().ConfigureAwait(false);
	}

	private void OnControllerStateChanged(object? sender, EventArgs e)
	{
		if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
		{
			_synchronizationContext.Post(_ => RefreshState(), null);
			return;
		}
		RefreshState();
	}

	private void RefreshState()
	{
		_state = _controller.State;
		RaiseAll();
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
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
