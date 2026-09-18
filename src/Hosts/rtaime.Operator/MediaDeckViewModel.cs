// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record MediaDeckCueItem(
	MediaCuePointId Id,
	string Name,
	long Frame,
	string Timecode);

public sealed class MediaDeckViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly MediaDeckController _controller;
	private readonly Func<string?> _filePicker;
	private readonly Func<string?> _sourceIdSelector;
	private readonly SynchronizationContext? _synchronizationContext;
	private readonly CancellationTokenSource _dispose = new();
	private MediaDeckSnapshot _snapshot = MediaDeckSnapshot.Unloaded;
	private Task? _pollTask;
	private MediaDeckCueItem? _selectedCue;
	private string _cueName = "Cue";
	private string? _lastError;
	private bool _isBusy;
	private bool _autoPlayOnProgram = true;
	private MediaDeckEndBehavior _endBehavior = MediaDeckEndBehavior.HoldLastFrame;
	private bool _playbackPolicyDirty;

	public MediaDeckViewModel(
		MediaDeckController controller,
		Func<string?> filePicker,
		Func<string?> sourceIdSelector,
		SynchronizationContext? synchronizationContext = null)
	{
		_controller = controller ?? throw new ArgumentNullException(nameof(controller));
		_filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
		_sourceIdSelector = sourceIdSelector ?? throw new ArgumentNullException(nameof(sourceIdSelector));
		_synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
		Timeline = new MediaTimelineViewModel(_controller.Timeline);
		Cues = new ObservableCollection<MediaDeckCueItem>();
		_controller.StateChanged += OnControllerStateChanged;

		OpenCommand = new AsyncRelayCommand(OpenAsync, () => !IsBusy);
		RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
		PlayCommand = new AsyncRelayCommand(() => TransportAsync(_controller.PlayAsync), () => CanPlay);
		PauseCommand = new AsyncRelayCommand(() => TransportAsync(_controller.PauseAsync), () => CanPause);
		TogglePlayPauseCommand = new AsyncRelayCommand(TogglePlayPauseAsync, () => CanPlay || CanPause);
		StopCommand = new AsyncRelayCommand(() => TransportAsync(_controller.StopAsync), () => CanStop);
		CloseCommand = new AsyncRelayCommand(CloseAsync, () => IsLoaded && !IsBusy);
		SetInCommand = new AsyncRelayCommand(() => MarkerAsync(() => _controller.Markers.SetInAtCurrentFrameAsync()), () => CanMark);
		ClearInCommand = new AsyncRelayCommand(() => MarkerAsync(() => _controller.Markers.ClearInAsync()), () => HasIn && !IsBusy);
		SetOutCommand = new AsyncRelayCommand(() => MarkerAsync(() => _controller.Markers.SetOutAtCurrentFrameAsync()), () => CanMark);
		ClearOutCommand = new AsyncRelayCommand(() => MarkerAsync(() => _controller.Markers.ClearOutAsync()), () => HasOut && !IsBusy);
		JumpInCommand = new AsyncRelayCommand(() => _controller.Markers.JumpToInAsync().AsTask(), () => HasIn && CanSeek);
		JumpOutCommand = new AsyncRelayCommand(() => _controller.Markers.JumpToOutAsync().AsTask(), () => HasOut && CanSeek);
		AddCueCommand = new AsyncRelayCommand(AddCueAsync, () => CanMark && !string.IsNullOrWhiteSpace(CueName));
		RenameCueCommand = new AsyncRelayCommand(RenameCueAsync, () => SelectedCue is not null && !IsBusy && !string.IsNullOrWhiteSpace(CueName));
		DeleteCueCommand = new AsyncRelayCommand(DeleteCueAsync, () => SelectedCue is not null && !IsBusy);
		JumpCueCommand = new AsyncRelayCommand(JumpCueAsync, () => SelectedCue is not null && CanSeek);
		ApplyPlaybackPolicyCommand = new AsyncRelayCommand(ApplyPlaybackPolicyAsync, () => IsLoaded && !IsBusy);
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public event Action<MediaDeckSnapshot>? SnapshotChanged;

	public MediaTimelineViewModel Timeline { get; }
	public ObservableCollection<MediaDeckCueItem> Cues { get; }
	public IReadOnlyList<MediaDeckEndBehavior> EndBehaviors { get; } = Enum.GetValues<MediaDeckEndBehavior>();

	public ICommand OpenCommand { get; }
	public ICommand RefreshCommand { get; }
	public ICommand PlayCommand { get; }
	public ICommand PauseCommand { get; }
	public ICommand TogglePlayPauseCommand { get; }
	public ICommand StopCommand { get; }
	public ICommand CloseCommand { get; }
	public ICommand SetInCommand { get; }
	public ICommand ClearInCommand { get; }
	public ICommand SetOutCommand { get; }
	public ICommand ClearOutCommand { get; }
	public ICommand JumpInCommand { get; }
	public ICommand JumpOutCommand { get; }
	public ICommand AddCueCommand { get; }
	public ICommand RenameCueCommand { get; }
	public ICommand DeleteCueCommand { get; }
	public ICommand JumpCueCommand { get; }
	public ICommand ApplyPlaybackPolicyCommand { get; }

	public string State => _snapshot.State.ToString().ToUpperInvariant();
	public bool IsLoaded => _snapshot.IsLoaded;
	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (Set(ref _isBusy, value))
				RaiseCommands();
		}
	}
	public bool HasError => _snapshot.State == MediaDeckState.Error || !string.IsNullOrWhiteSpace(LastError);
	public bool CanSeek => Timeline.CanSeek;
	public bool CanMark => IsLoaded && CanSeek && !IsBusy;
	public bool CanPlay => IsLoaded && !IsBusy && _snapshot.Transport?.State is MediaTransportState.Ready or MediaTransportState.Paused or MediaTransportState.Ended;
	public bool CanPause => IsLoaded && !IsBusy && _snapshot.Transport?.State == MediaTransportState.Playing;
	public bool CanStop => IsLoaded && !IsBusy && _snapshot.Transport?.State is MediaTransportState.Playing or MediaTransportState.Paused or MediaTransportState.Ended;
	public bool HasIn => _snapshot.Markers?.InPointFrame is not null;
	public bool HasOut => _snapshot.Markers?.OutPointFrame is not null;

	public bool AutoPlayOnProgram
	{
		get => _autoPlayOnProgram;
		set
		{
			if (Set(ref _autoPlayOnProgram, value))
			{
				_playbackPolicyDirty = true;
				RaiseCommands();
			}
		}
	}

	public MediaDeckEndBehavior EndBehavior
	{
		get => _endBehavior;
		set
		{
			if (Set(ref _endBehavior, value))
			{
				_playbackPolicyDirty = true;
				RaiseCommands();
			}
		}
	}

	public string FileName => _snapshot.Probe?.FileName ?? "No local media loaded";
	public string SourceId => _snapshot.SourceId?.ToString() ?? "—";
	public string Resolution => _snapshot.Probe is null
		? "—"
		: $"{_snapshot.Probe.VideoFormat.Width}×{_snapshot.Probe.VideoFormat.Height}";
	public string FrameRate => _snapshot.Probe?.VideoFormat.FrameRate.ToString() ?? "—";
	public string VideoCodec => _snapshot.Probe?.VideoCodec.ToString().ToUpperInvariant() ?? "—";
	public string AudioCodec => _snapshot.Probe is null
		? "—"
		: $"{_snapshot.Probe.AudioCodec.ToString().ToUpperInvariant()} · {_snapshot.Probe.AudioFormat.ChannelCount}ch · {_snapshot.Probe.AudioFormat.SampleRate / 1000.0:0.#} kHz";
	public string Duration => Timeline.DurationTimecode;
	public string Current => Timeline.CurrentTimecode;
	public string Remaining => _snapshot.Transport is null
		? "—"
		: MediaTimelineTimecode.FormatFrame(
			_snapshot.Transport.EffectiveRemainingFrames,
			_snapshot.Transport.Position.FrameRate);
	public string Countdown => _snapshot.Transport is null ? "T-—" : $"T-{Remaining}";
	public string ProgramDeckState => _snapshot.Transport?.IsOnProgram == true ? "ON PROGRAM" : "OFF PROGRAM";
	public string EffectiveRange => _snapshot.Transport is null
		? "—"
		: $"{MediaTimelineTimecode.FormatFrame(_snapshot.Transport.EffectiveStartFrame, _snapshot.Transport.Position.FrameRate)} → {MediaTimelineTimecode.FormatFrame(_snapshot.Transport.EffectiveEndFrame, _snapshot.Transport.Position.FrameRate)}";
	public string InTimecode => FormatMarker(_snapshot.Markers?.InPointFrame);
	public string OutTimecode => FormatMarker(_snapshot.Markers?.OutPointFrame);
	public string StatusDetail => LastError ??
		(_snapshot.State == MediaDeckState.Unloaded
			? "Open an MP4 file to prepare the local media deck."
			: $"{State} · {Current} / {Duration}");
	public string? LastError
	{
		get => _lastError;
		private set
		{
			if (Set(ref _lastError, value))
			{
				OnPropertyChanged(nameof(HasError));
				OnPropertyChanged(nameof(StatusDetail));
			}
		}
	}

	public string CueName
	{
		get => _cueName;
		set
		{
			if (Set(ref _cueName, value))
				RaiseCommands();
		}
	}

	public MediaDeckCueItem? SelectedCue
	{
		get => _selectedCue;
		set
		{
			if (!Set(ref _selectedCue, value))
				return;
			if (value is not null)
				CueName = value.Name;
			RaiseCommands();
		}
	}

	public void Start()
	{
		if (_pollTask is null)
			_pollTask = PollAsync(_dispose.Token);
	}

	public async ValueTask DisposeAsync()
	{
		_controller.StateChanged -= OnControllerStateChanged;
		_dispose.Cancel();
		if (_pollTask is not null)
		{
			try { await _pollTask.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		await Timeline.DisposeAsync().ConfigureAwait(false);
		await _controller.DisposeAsync().ConfigureAwait(false);
		_dispose.Dispose();
	}

	private async Task OpenAsync()
	{
		var path = _filePicker();
		if (string.IsNullOrWhiteSpace(path))
			return;
		var sourceIdText = _sourceIdSelector();
		if (string.IsNullOrWhiteSpace(sourceIdText))
		{
			LastError = "Select a production source slot before opening local media.";
			return;
		}

		try
		{
			IsBusy = true;
			LastError = null;
			await _controller.OpenAsync(
				path,
				new MediaSourceId(Identity.Parse(sourceIdText)),
				_dispose.Token).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or FormatException)
		{
			Post(() => LastError = exception.Message);
		}
		finally
		{
			Post(() => IsBusy = false);
		}
	}

	private async Task RefreshAsync()
	{
		try
		{
			IsBusy = true;
			await _controller.RefreshAsync(_dispose.Token).ConfigureAwait(false);
			Post(() => LastError = null);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
		{
			Post(() => LastError = exception.Message);
		}
		finally
		{
			Post(() => IsBusy = false);
		}
	}

	private async Task CloseAsync()
	{
		try
		{
			IsBusy = true;
			await _controller.CloseAsync(_dispose.Token).ConfigureAwait(false);
			Post(() => LastError = null);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
			Post(() => LastError = exception.Message);
		}
		finally
		{
			Post(() => IsBusy = false);
		}
	}

	private async Task ApplyPlaybackPolicyAsync()
	{
		try
		{
			IsBusy = true;
			var result = await _controller
				.ConfigurePlaybackAsync(AutoPlayOnProgram, EndBehavior, _dispose.Token)
				.ConfigureAwait(false);
			Post(() =>
			{
				_playbackPolicyDirty = false;
				LastError = result.Failure?.Message;
				RefreshState();
			});
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
		{
			Post(() => LastError = exception.Message);
		}
		finally
		{
			Post(() => IsBusy = false);
		}
	}

	private Task TogglePlayPauseAsync() =>
		_snapshot.Transport?.State == MediaTransportState.Playing
			? TransportAsync(_controller.PauseAsync)
			: TransportAsync(_controller.PlayAsync);

	private async Task TransportAsync(
		Func<CancellationToken, ValueTask<MediaDeckSnapshot>> action)
	{
		try
		{
			IsBusy = true;
			var result = await action(_dispose.Token).ConfigureAwait(false);
			Post(() => LastError = result.Failure?.Message);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException)
		{
			Post(() => LastError = exception.Message);
		}
		finally
		{
			Post(() => IsBusy = false);
		}
	}

	private async Task MarkerAsync(Func<ValueTask<bool>> action)
	{
		try
		{
			IsBusy = true;
			var succeeded = await action().ConfigureAwait(false);
			if (!succeeded)
				Post(() => LastError = "Marker command was rejected.");
			else
				Post(() => LastError = null);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
		{
			Post(() => LastError = exception.Message);
		}
		finally
		{
			Post(() => IsBusy = false);
		}
	}

	private async Task AddCueAsync()
	{
		try
		{
			IsBusy = true;
			var id = await _controller.Markers
				.AddCueAtCurrentFrameAsync(CueName, _dispose.Token)
				.ConfigureAwait(false);
			Post(() => LastError = id.HasValue ? null : "Cue point could not be added.");
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
		{
			Post(() => LastError = exception.Message);
		}
		finally
		{
			Post(() => IsBusy = false);
		}
	}

	private Task RenameCueAsync() =>
		SelectedCue is null
			? Task.CompletedTask
			: MarkerAsync(() => _controller.Markers.RenameCueAsync(SelectedCue.Id, CueName, _dispose.Token));

	private Task DeleteCueAsync() =>
		SelectedCue is null
			? Task.CompletedTask
			: MarkerAsync(() => _controller.Markers.DeleteCueAsync(SelectedCue.Id, _dispose.Token));

	private async Task JumpCueAsync()
	{
		if (SelectedCue is not null)
			await _controller.Markers.JumpToCueAsync(SelectedCue.Id, _dispose.Token).ConfigureAwait(false);
	}

	private async Task PollAsync(CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
		while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
		{
			if (_snapshot.State != MediaDeckState.Playing || IsBusy)
				continue;
			try
			{
				await _controller.RefreshAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
			{
				Post(() => LastError = exception.Message);
			}
		}
	}

	private void OnControllerStateChanged(object? sender, EventArgs e) =>
		Post(RefreshState);

	private void RefreshState()
	{
		_snapshot = _controller.Snapshot;
		LastError = _snapshot.Failure?.Message;
		if (!_playbackPolicyDirty && _snapshot.Transport is { } playback)
		{
			_autoPlayOnProgram = playback.AutoPlayOnProgram;
			_endBehavior = playback.EndBehavior;
			OnPropertyChanged(nameof(AutoPlayOnProgram));
			OnPropertyChanged(nameof(EndBehavior));
		}

		Cues.Clear();
		if (_snapshot.Markers is not null && _snapshot.Probe is not null)
		{
			foreach (var cue in _snapshot.Markers.CuePoints)
			{
				Cues.Add(new MediaDeckCueItem(
					cue.Id,
					cue.Name,
					cue.PositionFrame,
					MediaTimelineTimecode.FormatFrame(cue.PositionFrame, _snapshot.Probe.VideoFormat.FrameRate)));
			}
		}

		OnPropertyChanged(nameof(State));
		OnPropertyChanged(nameof(IsLoaded));
		OnPropertyChanged(nameof(HasError));
		OnPropertyChanged(nameof(CanSeek));
		OnPropertyChanged(nameof(CanMark));
		OnPropertyChanged(nameof(CanPlay));
		OnPropertyChanged(nameof(CanPause));
		OnPropertyChanged(nameof(CanStop));
		OnPropertyChanged(nameof(HasIn));
		OnPropertyChanged(nameof(HasOut));
		OnPropertyChanged(nameof(FileName));
		OnPropertyChanged(nameof(SourceId));
		OnPropertyChanged(nameof(Resolution));
		OnPropertyChanged(nameof(FrameRate));
		OnPropertyChanged(nameof(VideoCodec));
		OnPropertyChanged(nameof(AudioCodec));
		OnPropertyChanged(nameof(Duration));
		OnPropertyChanged(nameof(Current));
		OnPropertyChanged(nameof(Remaining));
		OnPropertyChanged(nameof(Countdown));
		OnPropertyChanged(nameof(ProgramDeckState));
		OnPropertyChanged(nameof(EffectiveRange));
		OnPropertyChanged(nameof(InTimecode));
		OnPropertyChanged(nameof(OutTimecode));
		OnPropertyChanged(nameof(StatusDetail));
		SnapshotChanged?.Invoke(_snapshot);
		RaiseCommands();
	}

	private string FormatMarker(long? frame) =>
		frame.HasValue && _snapshot.Probe is not null
			? MediaTimelineTimecode.FormatFrame(frame.Value, _snapshot.Probe.VideoFormat.FrameRate)
			: "—";

	private void RaiseCommands()
	{
		foreach (var command in new[]
		{
			OpenCommand, RefreshCommand, PlayCommand, PauseCommand, TogglePlayPauseCommand, StopCommand, CloseCommand,
			SetInCommand, ClearInCommand, SetOutCommand, ClearOutCommand, JumpInCommand, JumpOutCommand,
			AddCueCommand, RenameCueCommand, DeleteCueCommand, JumpCueCommand, ApplyPlaybackPolicyCommand
		}.OfType<AsyncRelayCommand>())
		{
			command.RaiseCanExecuteChanged();
		}
	}

	private void Post(Action action)
	{
		if (_synchronizationContext is null || SynchronizationContext.Current == _synchronizationContext)
			action();
		else
			_synchronizationContext.Post(_ => action(), null);
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value))
			return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? name = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
