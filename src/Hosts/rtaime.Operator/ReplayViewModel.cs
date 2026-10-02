// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Core;
using rtaime.Control.Contracts;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed class ReplayViewModel : INotifyPropertyChanged
{
	private readonly OperatorControlClient _client;
	private readonly MediaDeckViewModel _mediaDeck;
	private ReplayControlSnapshot _snapshot = ReplayControlSnapshot.Unavailable;
	private ReplayClipAssetResult? _lastClip;
	private string _clipName = "Replay Clip";
	private double _lookbackSeconds = 10;
	private bool _isBusy;
	private string? _lastError;

	public ReplayViewModel(OperatorControlClient client, MediaDeckViewModel mediaDeck)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_mediaDeck = mediaDeck ?? throw new ArgumentNullException(nameof(mediaDeck));

		RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
		MarkInCommand = new AsyncRelayCommand(MarkInAsync, () => CanMark);
		MarkOutCommand = new AsyncRelayCommand(MarkOutAsync, () => CanMark);
		CreateClipCommand = new AsyncRelayCommand(CreateClipAsync, () => CanCreateClip);
		OpenInMediaDeckCommand = new AsyncRelayCommand(OpenInMediaDeckAsync, () => CanOpenClip);
		SendToPreviewCommand = new AsyncRelayCommand(SendToPreviewAsync, () => CanOpenClip);
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public ICommand RefreshCommand { get; }
	public ICommand MarkInCommand { get; }
	public ICommand MarkOutCommand { get; }
	public ICommand CreateClipCommand { get; }
	public ICommand OpenInMediaDeckCommand { get; }
	public ICommand SendToPreviewCommand { get; }

	public string ClipName
	{
		get => _clipName;
		set
		{
			if (Set(ref _clipName, value ?? string.Empty))
				RaiseCommands();
		}
	}

	public double LookbackSeconds
	{
		get => _lookbackSeconds;
		set
		{
			if (!double.IsFinite(value))
				return;
			if (Set(ref _lookbackSeconds, Math.Clamp(value, 0.25, 1800)))
				RaiseCommands();
		}
	}

	public bool IsBusy
	{
		get => _isBusy;
		private set
		{
			if (Set(ref _isBusy, value))
				RaiseCommands();
		}
	}

	public string? LastError
	{
		get => _lastError;
		private set
		{
			if (Set(ref _lastError, value))
			{
				OnPropertyChanged(nameof(HasFailure));
				OnPropertyChanged(nameof(StatusDetail));
			}
		}
	}

	public bool HasFailure => !string.IsNullOrWhiteSpace(LastError) || _snapshot.Failure is not null;
	public bool CanMark => !IsBusy && _snapshot.CaptureState is ReplayControlCaptureState.Capturing or ReplayControlCaptureState.Degraded;
	public bool CanCreateClip => !IsBusy && _snapshot.HasSelection && !string.IsNullOrWhiteSpace(ClipName);
	public bool CanOpenClip =>
		!IsBusy &&
		_lastClip is { Succeeded: true, AssetId: not null } &&
		!string.IsNullOrWhiteSpace(_lastClip.SourceLocation);

	public string CaptureState => _snapshot.CaptureState.ToString().ToUpperInvariant();
	public string ClipState => _snapshot.ClipState.ToString().ToUpperInvariant();
	public string RetainedDuration => FormatTime(_snapshot.RetainedDuration);
	public string RetentionLimit => FormatTime(_snapshot.Retention);
	public string StorageUsage => _snapshot.MaximumStorageBytes <= 0
		? "UNAVAILABLE"
		: $"{FormatBytes(_snapshot.RetainedBytes)} / {FormatBytes(_snapshot.MaximumStorageBytes)}";
	public double StoragePercent => _snapshot.MaximumStorageBytes <= 0
		? 0
		: Math.Clamp(_snapshot.RetainedBytes * 100.0 / _snapshot.MaximumStorageBytes, 0, 100);
	public string SegmentStatus => $"{_snapshot.RetainedSegmentCount} SEGMENTS · {_snapshot.FinalizedSegments} FINALIZED · {_snapshot.EvictedSegments} EVICTED";
	public string BackpressureStatus => _snapshot.DroppedSamples == 0 && _snapshot.Discontinuities == 0
		? "CONTINUOUS"
		: $"{_snapshot.DroppedSamples} DROPPED · {_snapshot.Discontinuities} DISCONTINUITIES";
	public string MarkIn => _snapshot.MarkIn is { } value ? FormatTime(value) : "—";
	public string MarkOut => _snapshot.MarkOut is { } value ? FormatTime(value) : "—";
	public string SelectedDuration => _snapshot.SelectedDuration is { } value ? FormatTime(value) : "—";
	public string ClipResult => _lastClip is null
		? "NO MATERIALIZED CLIP"
		: _lastClip.Succeeded
			? $"READY · {_lastClip.AssetId} · {FormatTime(_lastClip.Duration)}"
			: $"FAILED · {_lastClip.Failure?.Code ?? "UNKNOWN"}";
	public string StatusDetail => LastError ?? _snapshot.Failure?.Message ??
		(_snapshot.CaptureState == ReplayControlCaptureState.Capturing
			? "Runtime-owned encoded replay capture is active."
			: $"Replay capture state: {CaptureState}.");

	internal void ApplyConfirmedSnapshot(ReplayControlSnapshot snapshot)
	{
		_snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
		if (_snapshot.Failure is not null)
			LastError = _snapshot.Failure.Value.Message;
		else if (LastError == _snapshot.Failure?.Message)
			LastError = null;
		RaiseSnapshotProperties();
	}

	private async Task RefreshAsync() =>
		await RunAsync(async () => ApplyConfirmedSnapshot(await _client.GetReplaySnapshotAsync().ConfigureAwait(false)));

	private async Task MarkInAsync() =>
		await RunAsync(async () =>
		{
			_lastClip = null;
			var snapshot = await _client.MarkReplayInAsync(TimeSpan.FromSeconds(LookbackSeconds)).ConfigureAwait(false);
			ApplyConfirmedSnapshot(snapshot);
			OnPropertyChanged(nameof(ClipResult));
		});

	private async Task MarkOutAsync() =>
		await RunAsync(async () =>
		{
			_lastClip = null;
			var snapshot = await _client.MarkReplayOutAsync().ConfigureAwait(false);
			ApplyConfirmedSnapshot(snapshot);
			OnPropertyChanged(nameof(ClipResult));
		});

	private async Task CreateClipAsync() =>
		await RunAsync(async () =>
		{
			var result = await _client.CreateReplayClipAsync(ClipName).ConfigureAwait(false);
			_lastClip = result;
			LastError = result.Failure?.Message;
			ApplyConfirmedSnapshot(await _client.GetReplaySnapshotAsync().ConfigureAwait(false));
			OnPropertyChanged(nameof(ClipResult));
			OnPropertyChanged(nameof(CanOpenClip));
		});

	private async Task OpenInMediaDeckAsync() =>
		await RunAsync(async () =>
		{
			var clip = RequireReadyClip();
			var opened = await _mediaDeck.OpenCatalogAssetAsync(clip.SourceLocation!, new MediaAssetId(Identity.Parse(clip.AssetId!))).ConfigureAwait(false);
			if (!opened)
				throw new InvalidOperationException("Replay clip could not be opened through the existing Media Deck.");
		});

	private async Task SendToPreviewAsync() =>
		await RunAsync(async () =>
		{
			var clip = RequireReadyClip();
			var opened = await _mediaDeck.OpenCatalogAssetAsync(clip.SourceLocation!, clip.AssetId!.Value).ConfigureAwait(false);
			if (!opened)
				throw new InvalidOperationException("Replay clip could not be opened through the existing Media Deck.");
			if (string.IsNullOrWhiteSpace(_mediaDeck.SourceId) || _mediaDeck.SourceId == "—")
				throw new InvalidOperationException("Media Deck did not confirm a production source slot for the replay clip.");
			var response = await _client.SelectPreviewAsync(_mediaDeck.SourceId).ConfigureAwait(false);
			if (!response.Accepted)
				throw new InvalidOperationException(response.Failure?.Message ?? "Replay clip Preview selection was rejected.");
		});

	private ReplayClipAssetResult RequireReadyClip() =>
		_lastClip is { Succeeded: true, AssetId: not null } && !string.IsNullOrWhiteSpace(_lastClip.SourceLocation)
			? _lastClip
			: throw new InvalidOperationException("Create and confirm a Media Library replay clip first.");

	private async Task RunAsync(Func<Task> operation)
	{
		if (IsBusy)
			return;
		IsBusy = true;
		LastError = null;
		try
		{
			await operation().ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or FormatException or TimeoutException)
		{
			LastError = exception.Message;
		}
		finally
		{
			IsBusy = false;
			RaiseSnapshotProperties();
		}
	}

	private void RaiseSnapshotProperties()
	{
		foreach (var name in new[]
		{
			nameof(CaptureState), nameof(ClipState), nameof(RetainedDuration), nameof(RetentionLimit),
			nameof(StorageUsage), nameof(StoragePercent), nameof(SegmentStatus), nameof(BackpressureStatus),
			nameof(MarkIn), nameof(MarkOut), nameof(SelectedDuration), nameof(ClipResult), nameof(StatusDetail),
			nameof(HasFailure), nameof(CanMark), nameof(CanCreateClip), nameof(CanOpenClip)
		})
			OnPropertyChanged(name);
		RaiseCommands();
	}

	private void RaiseCommands()
	{
		foreach (var command in new[] { RefreshCommand, MarkInCommand, MarkOutCommand, CreateClipCommand, OpenInMediaDeckCommand, SendToPreviewCommand }.OfType<AsyncRelayCommand>())
			command.RaiseCanExecuteChanged();
	}

	private static string FormatTime(TimeSpan value) =>
		value.TotalHours >= 1
			? value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
			: value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

	private static string FormatBytes(long bytes)
	{
		if (bytes < 1024) return $"{bytes} B";
		if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KiB";
		if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MiB";
		return $"{bytes / (1024.0 * 1024 * 1024):0.00} GiB";
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
