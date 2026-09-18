// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed class OperatorViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly OperatorControlClient? _client;
	private SynchronizationContext? _synchronizationContext;
	private readonly CancellationTokenSource _audioPollingStop = new();
	private Func<OperatorGraphicsAsset?>? _graphicsAssetPicker;
	private Task? _audioPollingTask;
	private OperatorSourceTileViewModel? _selectedSource;
	private OperatorAudioInputViewModel? _selectedAudioInput;
	private string? _mediaDeckSourceId;
	private string _previewSourceName = "—";
	private string _previewSourceId = "—";
	private string _programSourceName = "—";
	private string _programSourceId = "—";
	private string _runtimeStatus = "DISCONNECTED";
	private string _timingStatus = "UNKNOWN";
	private string _inputStatus = "UNKNOWN";
	private string _aiStatus = "UNKNOWN";
	private string _recordingStatus = "UNKNOWN";
	private string _recordingDestination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "rtaime");
	private string _recordingFileName = CreateRecordingFileName();
	private string _recordingElapsed = "00:00:00";
	private string _recordingFinalPath = "—";
	private string _recordingStatistics = "0 written · 0 dropped";
	private string? _recordingError;
	private string _engineHealth = "UNVERIFIED";
	private string _engineHealthDetail = "Health snapshot unavailable.";
	private string _controlHealth = "UNVERIFIED";
	private string _runtimeHealth = "UNVERIFIED";
	private string _mediaHealth = "UNVERIFIED";
	private string _providerHealth = "UNVERIFIED";
	private string _gpuProviderHealth = "UNVERIFIED";
	private string _currentFormat = "UNVERIFIED";
	private string _frameTime = "UNVERIFIED";
	private string _droppedFrames = "0";
	private string _uptime = "00:00:00";
	private string _gpuUtilization = "UNVERIFIED";
	private string _vram = "UNVERIFIED";
	private string _healthObserved = "—";
	private string _visualLayerStatus = "UNKNOWN";
	private string _graphicsAssetName = "No graphics asset loaded";
	private string _graphicsDimensions = "—";
	private string _graphicsState = "EMPTY";
	private bool _graphicsVisible;
	private double _graphicsPositionX = 72.0;
	private double _graphicsPositionY = 6.0;
	private double _graphicsScale = 1.0;
	private string _audioPeak = "0.000";
	private string _audioPeakPercent = "0%";
	private string _audioAfvSourceName = "—";
	private string _audioAfvSourceId = "—";
	private string _audioHealth = "UNKNOWN";
	private string _audioMeterStatus = "IDLE";
	private double _audioLeftPeak;
	private double _audioRightPeak;
	private double _audioMasterPeak;
	private bool _audioClipping;
	private bool _audioMuted;
	private double _audioProgramGain = 1.0;
	private string _clipAudioStatus = "NO CLIP AUDIO";
	private string _connectionState = "DISCONNECTED";
	private string _connectionDetail = "Synchronize to load authoritative state.";
	private string _commandStatus = "IDLE";
	private string _commitStatus = "UNCONFIRMED";
	private string _transitionStatus = "IDLE";
	private string _lastEvent = "Operator started. Synchronization pending.";
	private string _revisionLabel = "REV —";
	private uint _transitionFrames = 12;
	private string? _lastError;
	private bool _isBusy;
	private bool _isConnected;
	private bool _isStale;

	public OperatorViewModel(
		OperatorControlClient? client = null,
		Func<OperatorGraphicsAsset?>? graphicsAssetPicker = null,
		SynchronizationContext? synchronizationContext = null)
	{
		_client = client;
		_graphicsAssetPicker = graphicsAssetPicker;
		_synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
		Sources = new ObservableCollection<OperatorSourceTileViewModel>();
		AudioInputs = new ObservableCollection<OperatorAudioInputViewModel>();
		SynchronizeCommand = new AsyncRelayCommand(SynchronizeAsync, () => _client is not null && !IsBusy);
		SetPreviewCommand = new AsyncRelayCommand(SetPreviewAsync, CanSetPreview);
		CutCommand = new AsyncRelayCommand(CutAsync, CanTakePreview);
		DissolveCommand = new AsyncRelayCommand(DissolveAsync, () => CanTakePreview() && TransitionFrames >= 2);
		LoadGraphicsCommand = new AsyncRelayCommand(LoadGraphicsAsync, () => CanControl() && _graphicsAssetPicker is not null);
		ApplyGraphicsCommand = new AsyncRelayCommand(ApplyGraphicsAsync, CanApplyGraphics);
		ToggleGraphicsCommand = new AsyncRelayCommand(ToggleGraphicsAsync, CanApplyGraphics);
		ClearGraphicsCommand = new AsyncRelayCommand(ClearGraphicsAsync, CanApplyGraphics);
		ApplyAudioGainCommand = new AsyncRelayCommand(ApplyAudioGainAsync, CanApplyAudio);
		ToggleAudioMuteCommand = new AsyncRelayCommand(ToggleAudioMuteAsync, CanApplyAudio);
		StartRecordingCommand = new AsyncRelayCommand(StartRecordingAsync, CanStartRecording);
		StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, CanStopRecording);
	}

	internal OperatorControlClient? Client => _client;

	public event PropertyChangedEventHandler? PropertyChanged;

	public ObservableCollection<OperatorSourceTileViewModel> Sources { get; }
	public ObservableCollection<OperatorAudioInputViewModel> AudioInputs { get; }
	public ICommand SynchronizeCommand { get; }
	public ICommand SetPreviewCommand { get; }
	public ICommand CutCommand { get; }
	public ICommand DissolveCommand { get; }
	public ICommand LoadGraphicsCommand { get; }
	public ICommand ApplyGraphicsCommand { get; }
	public ICommand ToggleGraphicsCommand { get; }
	public ICommand ClearGraphicsCommand { get; }
	public ICommand ApplyAudioGainCommand { get; }
	public ICommand ToggleAudioMuteCommand { get; }
	public ICommand StartRecordingCommand { get; }
	public ICommand StopRecordingCommand { get; }

	public string MonitoringStatus => "Independent Runtime monitoring active when connected.";
	public string FormatStatus => CurrentFormat;

	public OperatorSourceTileViewModel? SelectedSource
	{
		get => _selectedSource;
		set
		{
			if (Set(ref _selectedSource, value))
				RaiseCommandState();
		}
	}

	public OperatorAudioInputViewModel? SelectedAudioInput
	{
		get => _selectedAudioInput;
		set
		{
			if (Set(ref _selectedAudioInput, value))
				RaiseCommandState();
		}
	}

	public string PreviewSourceName { get => _previewSourceName; private set => Set(ref _previewSourceName, value); }
	public string PreviewSourceId { get => _previewSourceId; private set => Set(ref _previewSourceId, value); }
	public string ProgramSourceName { get => _programSourceName; private set => Set(ref _programSourceName, value); }
	public string ProgramSourceId { get => _programSourceId; private set => Set(ref _programSourceId, value); }
	public string RuntimeStatus { get => _runtimeStatus; private set => Set(ref _runtimeStatus, value); }
	public string TimingStatus { get => _timingStatus; private set => Set(ref _timingStatus, value); }
	public string InputStatus { get => _inputStatus; private set => Set(ref _inputStatus, value); }
	public string AIStatus { get => _aiStatus; private set => Set(ref _aiStatus, value); }
	public string RecordingStatus { get => _recordingStatus; private set => Set(ref _recordingStatus, value); }
	public string RecordingDestination
	{
		get => _recordingDestination;
		set
		{
			if (Set(ref _recordingDestination, value))
				RaiseCommandState();
		}
	}
	public string RecordingFileName
	{
		get => _recordingFileName;
		set
		{
			if (Set(ref _recordingFileName, value))
				RaiseCommandState();
		}
	}
	public string RecordingElapsed { get => _recordingElapsed; private set => Set(ref _recordingElapsed, value); }
	public string RecordingFinalPath { get => _recordingFinalPath; private set => Set(ref _recordingFinalPath, value); }
	public string RecordingStatistics { get => _recordingStatistics; private set => Set(ref _recordingStatistics, value); }
	public string? RecordingError { get => _recordingError; private set => Set(ref _recordingError, value); }
	public string EngineHealth { get => _engineHealth; private set => Set(ref _engineHealth, value); }
	public string EngineHealthDetail { get => _engineHealthDetail; private set => Set(ref _engineHealthDetail, value); }
	public string ControlHealth { get => _controlHealth; private set => Set(ref _controlHealth, value); }
	public string RuntimeHealth { get => _runtimeHealth; private set => Set(ref _runtimeHealth, value); }
	public string MediaHealth { get => _mediaHealth; private set => Set(ref _mediaHealth, value); }
	public string ProviderHealth { get => _providerHealth; private set => Set(ref _providerHealth, value); }
	public string GpuProviderHealth { get => _gpuProviderHealth; private set => Set(ref _gpuProviderHealth, value); }
	public string CurrentFormat { get => _currentFormat; private set { if (Set(ref _currentFormat, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormatStatus))); } }
	public string FrameTime { get => _frameTime; private set => Set(ref _frameTime, value); }
	public string DroppedFrames { get => _droppedFrames; private set => Set(ref _droppedFrames, value); }
	public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }
	public string GpuUtilization { get => _gpuUtilization; private set => Set(ref _gpuUtilization, value); }
	public string Vram { get => _vram; private set => Set(ref _vram, value); }
	public string HealthObserved { get => _healthObserved; private set => Set(ref _healthObserved, value); }
	public string VisualLayerStatus { get => _visualLayerStatus; private set => Set(ref _visualLayerStatus, value); }
	public string GraphicsAssetName { get => _graphicsAssetName; private set => Set(ref _graphicsAssetName, value); }
	public string GraphicsDimensions { get => _graphicsDimensions; private set => Set(ref _graphicsDimensions, value); }
	public string GraphicsState { get => _graphicsState; private set => Set(ref _graphicsState, value); }
	public bool GraphicsVisible { get => _graphicsVisible; private set => Set(ref _graphicsVisible, value); }
	public double GraphicsPositionX
	{
		get => _graphicsPositionX;
		set
		{
			if (Set(ref _graphicsPositionX, Math.Clamp(value, 0.0, 100.0)))
				RaiseCommandState();
		}
	}
	public double GraphicsPositionY
	{
		get => _graphicsPositionY;
		set
		{
			if (Set(ref _graphicsPositionY, Math.Clamp(value, 0.0, 100.0)))
				RaiseCommandState();
		}
	}
	public double GraphicsScale
	{
		get => _graphicsScale;
		set
		{
			if (Set(ref _graphicsScale, Math.Clamp(value, 0.05, 4.0)))
				RaiseCommandState();
		}
	}
	public string AudioPeak { get => _audioPeak; private set => Set(ref _audioPeak, value); }
	public string AudioPeakPercent { get => _audioPeakPercent; private set => Set(ref _audioPeakPercent, value); }
	public string AudioAfvSourceName { get => _audioAfvSourceName; private set => Set(ref _audioAfvSourceName, value); }
	public string AudioAfvSourceId { get => _audioAfvSourceId; private set => Set(ref _audioAfvSourceId, value); }
	public string AudioHealth { get => _audioHealth; private set => Set(ref _audioHealth, value); }
	public string AudioMeterStatus { get => _audioMeterStatus; private set => Set(ref _audioMeterStatus, value); }
	public double AudioLeftPeak { get => _audioLeftPeak; private set => Set(ref _audioLeftPeak, value); }
	public double AudioRightPeak { get => _audioRightPeak; private set => Set(ref _audioRightPeak, value); }
	public double AudioMasterPeak { get => _audioMasterPeak; private set => Set(ref _audioMasterPeak, value); }
	public bool AudioClipping { get => _audioClipping; private set => Set(ref _audioClipping, value); }
	public bool AudioMuted { get => _audioMuted; private set => Set(ref _audioMuted, value); }
	public double AudioProgramGain { get => _audioProgramGain; private set => Set(ref _audioProgramGain, value); }
	public string ClipAudioStatus { get => _clipAudioStatus; private set => Set(ref _clipAudioStatus, value); }
	public string ConnectionState { get => _connectionState; private set => Set(ref _connectionState, value); }
	public string ConnectionDetail { get => _connectionDetail; private set => Set(ref _connectionDetail, value); }
	public string CommandStatus { get => _commandStatus; private set => Set(ref _commandStatus, value); }
	public string CommitStatus { get => _commitStatus; private set => Set(ref _commitStatus, value); }
	public string TransitionStatus { get => _transitionStatus; private set => Set(ref _transitionStatus, value); }
	public string LastEvent { get => _lastEvent; private set => Set(ref _lastEvent, value); }
	public string RevisionLabel { get => _revisionLabel; private set => Set(ref _revisionLabel, value); }
	public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }
	public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
	public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }
	public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }

	public uint TransitionFrames
	{
		get => _transitionFrames;
		set
		{
			if (Set(ref _transitionFrames, value))
				RaiseCommandState();
		}
	}

	private bool CanControl() =>
		_client is not null &&
		IsConnected &&
		!IsStale &&
		!IsBusy &&
		string.Equals(RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase);

	private bool CanSetPreview() => CanControl() && SelectedSource is not null;

	private bool CanTakePreview() => CanControl() && _client?.Snapshot is not null;

	private bool CanApplyGraphics() =>
		CanControl() &&
		_client?.Snapshot?.GraphicsOverlay.AssetLoaded == true;

	private bool CanApplyAudio() => CanControl() && SelectedAudioInput is not null;

	private bool CanStartRecording() =>
		CanControl() &&
		!string.IsNullOrWhiteSpace(RecordingDestination) &&
		!string.IsNullOrWhiteSpace(RecordingFileName) &&
		RecordingStatus is not ("RECORDING" or "FINALIZING");

	private bool CanStopRecording() =>
		CanControl() &&
		string.Equals(RecordingStatus, "RECORDING", StringComparison.Ordinal);


	public void StartAudioMetering()
	{
		if (_audioPollingTask is null && _client is not null)
			_audioPollingTask = PollAudioAsync(_audioPollingStop.Token);
	}

	public async ValueTask DisposeAsync()
	{
		_audioPollingStop.Cancel();
		if (_audioPollingTask is not null)
		{
			try { await _audioPollingTask.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_audioPollingStop.Dispose();
	}

	private async Task PollAudioAsync(CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
		while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
		{
			if (_client is null || !IsConnected || IsStale || IsBusy)
				continue;
			try
			{
				var snapshot = await _client.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
				Post(() =>
				{
					ApplyAudio(snapshot, preserveSelectedGainEdit: true);
					ApplyRecording(snapshot.Recording, preserveTargetEdit: true);
					ApplyHealth(snapshot.Health);
					AudioMeterStatus = "LIVE";
				});
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
			{
				Post(() => AudioMeterStatus = "STALE");
			}
		}
	}

	private async Task SynchronizeAsync()
	{
		if (_client is null)
		{
			LastError = "Remote control transport is not configured.";
			ConnectionState = "DISCONNECTED";
			CommandStatus = "UNAVAILABLE";
			return;
		}

		await ExecuteAsync("SYNCHRONIZE", async () =>
		{
			Apply(await _client.SynchronizeAsync());
			CommandStatus = "SYNCHRONIZED";
			TransitionStatus = "READY";
			LastEvent = "Authoritative state synchronized from ControlHost.";
		});
	}

	private async Task SetPreviewAsync()
	{
		if (_client is null || SelectedSource is null) return;
		var source = SelectedSource;
		await ExecuteAsync("SET PREVIEW", async () =>
		{
			var response = await _client.SelectPreviewAsync(source.Id);
			if (!Accept(response, "Set Preview")) return;
			Apply(_client.Snapshot!);
			CommandStatus = "APPLIED";
			TransitionStatus = "PREVIEW CONFIRMED";
			LastEvent = $"Preview source changed to {source.Name} and confirmed by authoritative control.";
		});
	}

	private async Task CutAsync()
	{
		if (_client is null || _client.Snapshot is null) return;
		var previewName = PreviewSourceName;
		await ExecuteAsync("CUT", async () =>
		{
			TransitionStatus = "CUT IN FLIGHT";
			var response = await _client.CutPreviewAsync();
			if (!Accept(response, "CUT")) return;
			Apply(_client.Snapshot!);
			CommandStatus = "APPLIED";
			TransitionStatus = "CUT CONFIRMED";
			LastEvent = $"CUT committed confirmed Preview source {previewName} to Program.";
		});
	}

	private async Task DissolveAsync()
	{
		if (_client is null || _client.Snapshot is null) return;
		var previewName = PreviewSourceName;
		var durationFrames = TransitionFrames;
		await ExecuteAsync("DISSOLVE", async () =>
		{
			TransitionStatus = $"DISSOLVE {durationFrames}F IN FLIGHT";
			var response = await _client.DissolvePreviewAsync(durationFrames);
			if (!Accept(response, "DISSOLVE")) return;
			Apply(_client.Snapshot!);
			CommandStatus = "APPLIED";
			TransitionStatus = $"DISSOLVE {durationFrames}F CONFIRMED";
			LastEvent = $"DISSOLVE committed confirmed Preview source {previewName} to Program over {durationFrames} frames.";
		});
	}

	private async Task StartRecordingAsync()
	{
		if (_client is null) return;
		var destination = RecordingDestination;
		var fileName = RecordingFileName;

		await ExecuteAsync("START RECORDING", async () =>
		{
			var result = await _client.StartRecordingAsync(destination, fileName);
			Apply(_client.Snapshot!);
			if (!result.Succeeded)
			{
				RecordingError = result.Failure?.Message ?? "Recording start was rejected.";
				LastError = RecordingError;
				CommandStatus = "RECORDING FAILED";
				LastEvent = "Recording start was rejected; Program execution remains unchanged.";
				return;
			}

			RecordingError = null;
			CommandStatus = "RECORDING";
			LastEvent = $"Program recording started: {fileName}.";
		});
	}

	private async Task StopRecordingAsync()
	{
		if (_client is null) return;
		await ExecuteAsync("STOP RECORDING", async () =>
		{
			var result = await _client.StopRecordingAsync();
			Apply(_client.Snapshot!);
			if (!result.Succeeded)
			{
				RecordingError = result.Failure?.Message ?? "Recording stop failed.";
				LastError = RecordingError;
				CommandStatus = "RECORDING FAILED";
				LastEvent = "Recording stop/finalization failed; RuntimeHost reported the failure.";
				return;
			}

			RecordingError = null;
			CommandStatus = "RECORDING SAVED";
			LastEvent = string.IsNullOrWhiteSpace(RecordingFinalPath)
				? "Program recording finalized."
				: $"Program recording finalized: {RecordingFinalPath}.";
			RecordingFileName = CreateRecordingFileName();
		});
	}

	private async Task LoadGraphicsAsync()
	{
		if (_client is null || _graphicsAssetPicker is null) return;
		OperatorGraphicsAsset? asset;
		try
		{
			asset = _graphicsAssetPicker();
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or ArgumentException)
		{
			LastError = exception.Message;
			CommandStatus = "GRAPHICS LOAD REJECTED";
			LastEvent = "Graphics asset validation failed before upload.";
			return;
		}
		if (asset is null) return;

		await ExecuteAsync("LOAD GRAPHICS", async () =>
		{
			await _client.LoadGraphicsOverlayAsync(asset);
			Apply(_client.Snapshot!);
			CommandStatus = "GRAPHICS LOADED";
			LastEvent = $"Graphics asset {asset.Name} loaded and confirmed by RuntimeHost.";
		});
	}

	private async Task ApplyGraphicsAsync()
	{
		if (_client is null || _client.Snapshot?.GraphicsOverlay.AssetLoaded != true) return;
		await ExecuteAsync("APPLY GRAPHICS", async () =>
		{
			await _client.SetGraphicsOverlayAsync(
				GraphicsVisible,
				GraphicsPositionX / 100.0,
				GraphicsPositionY / 100.0,
				GraphicsScale);
			Apply(_client.Snapshot!);
			CommandStatus = "GRAPHICS APPLIED";
			LastEvent = $"Graphics placement confirmed at X {GraphicsPositionX:0.#}%, Y {GraphicsPositionY:0.#}%, scale {GraphicsScale:0.##}.";
		});
	}

	private async Task ToggleGraphicsAsync()
	{
		if (_client is null || _client.Snapshot?.GraphicsOverlay.AssetLoaded != true) return;
		var show = !GraphicsVisible;
		await ExecuteAsync(show ? "SHOW GRAPHICS" : "HIDE GRAPHICS", async () =>
		{
			await _client.SetGraphicsOverlayAsync(
				show,
				GraphicsPositionX / 100.0,
				GraphicsPositionY / 100.0,
				GraphicsScale);
			Apply(_client.Snapshot!);
			CommandStatus = show ? "GRAPHICS ON AIR" : "GRAPHICS HIDDEN";
			LastEvent = show
				? "Graphics overlay is visible in the confirmed Runtime Program path."
				: "Graphics overlay is hidden in the confirmed Runtime Program path.";
		});
	}

	private async Task ClearGraphicsAsync()
	{
		if (_client is null || _client.Snapshot?.GraphicsOverlay.AssetLoaded != true) return;
		await ExecuteAsync("CLEAR GRAPHICS", async () =>
		{
			await _client.ClearGraphicsOverlayAsync();
			Apply(_client.Snapshot!);
			CommandStatus = "GRAPHICS CLEARED";
			LastEvent = "Graphics overlay asset was cleared from RuntimeHost.";
		});
	}

	private async Task ApplyAudioGainAsync()
	{
		if (_client is null || SelectedAudioInput is null) return;
		var input = SelectedAudioInput;
		await ExecuteAsync("AUDIO GAIN", async () =>
		{
			await _client.SetAudioInputStateAsync(input.SourceId, input.Gain, input.Muted);
			ApplyAudio(_client.Snapshot!, preserveSelectedGainEdit: false);
			CommandStatus = "AUDIO CONFIRMED";
			LastEvent = $"Audio gain {input.Gain:0.##}x confirmed for {input.SourceName}.";
		});
	}

	private async Task ToggleAudioMuteAsync()
	{
		if (_client is null || SelectedAudioInput is null) return;
		var input = SelectedAudioInput;
		var muted = !input.Muted;
		await ExecuteAsync(muted ? "AUDIO MUTE" : "AUDIO UNMUTE", async () =>
		{
			await _client.SetAudioInputStateAsync(input.SourceId, input.Gain, muted);
			ApplyAudio(_client.Snapshot!, preserveSelectedGainEdit: false);
			CommandStatus = muted ? "AUDIO MUTED" : "AUDIO LIVE";
			LastEvent = $"{input.SourceName} audio {(muted ? "muted" : "unmuted")} and confirmed by RuntimeHost.";
		});
	}

	private bool Accept(OperatorMutationResponse response, string operation)
	{
		if (response.Accepted) return true;

		var message = response.Failure?.Message ?? $"{operation} command rejected.";
		CommandStatus = "REJECTED";
		CommitStatus = $"REJECTED · {RevisionLabel} UNCHANGED";
		TransitionStatus = $"{operation} REJECTED";
		LastError = message;
		LastEvent = $"{operation} rejected by authoritative control; Program remains at the last confirmed revision.";
		return false;
	}

	private async Task ExecuteAsync(string operation, Func<Task> action)
	{
		if (IsBusy) return;

		IsBusy = true;
		CommandStatus = $"{operation} IN FLIGHT";
		CommitStatus = string.Equals(operation, "SYNCHRONIZE", StringComparison.Ordinal)
			? "SYNCHRONIZING"
			: operation.StartsWith("AUDIO ", StringComparison.Ordinal)
				? "RUNTIME CONFIRM PENDING"
				: "COMMIT PENDING";
		LastError = null;
		RaiseCommandState();
		try
		{
			await action();
		}
		catch (RemoteHostSessionChangedException) when (_client is not null)
		{
			IsStale = true;
			ConnectionState = "RESYNCING";
			ConnectionDetail = "ControlHost session changed. Full authoritative resynchronization is required.";
			CommandStatus = "RESYNC REQUIRED";
			CommitStatus = "UNCONFIRMED · RESYNC REQUIRED";
			TransitionStatus = "BLOCKED";
			RaiseCommandState();
			try
			{
				Apply(await _client.SynchronizeAsync());
				CommandStatus = "RESYNCHRONIZED";
				LastError = "ControlHost restarted. Authoritative state was resynchronized; repeat the requested operation.";
				LastEvent = "ControlHost session changed and a full snapshot was restored.";
			}
			catch (Exception recoveryException)
			{
				MarkStale($"ControlHost session changed and resynchronization failed: {recoveryException.Message}");
				CommandStatus = "RESYNC FAILED";
			}
		}
		catch (Exception exception)
		{
			if (exception is IOException or TimeoutException or OperationCanceledException)
				MarkStale(exception.Message);
			else
				LastError = exception.Message;

			CommandStatus = "FAILED";
			CommitStatus = IsStale ? "UNCONFIRMED" : $"FAILED · {RevisionLabel} UNCHANGED";
			TransitionStatus = $"{operation} FAILED";
			LastEvent = $"{operation} failed; Program was not advanced locally.";
		}
		finally
		{
			IsBusy = false;
			RaiseCommandState();
		}
	}

	private void Apply(OperatorStatusSnapshot snapshot)
	{
		var previousSelectionId = SelectedSource?.Id;
		var existing = Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
		Sources.Clear();
		foreach (var descriptor in snapshot.Sources)
		{
			if (!existing.TryGetValue(descriptor.Id, out var source))
				source = new OperatorSourceTileViewModel(descriptor);
			else
				source.ApplyDescriptor(descriptor);
			Sources.Add(source);
		}

		var previewId = snapshot.Production.Routing.PreviewSourceId.ToString();
		var programId = snapshot.Production.Routing.ProgramSourceId.ToString();
		foreach (var source in Sources)
			source.ApplyRouting(previewId, programId);
		SelectedSource = Sources.FirstOrDefault(source => string.Equals(source.Id, previousSelectionId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault(source => string.Equals(source.Id, previewId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault();

		PreviewSourceId = previewId;
		ProgramSourceId = programId;
		PreviewSourceName = ResolveSourceName(snapshot, previewId);
		ProgramSourceName = ResolveSourceName(snapshot, programId);
		RuntimeStatus = snapshot.RuntimeStatus;
		TimingStatus = snapshot.TimingStatus;
		InputStatus = snapshot.InputStatus;
		AIStatus = snapshot.AIStatus;
		ApplyRecording(snapshot.Recording, preserveTargetEdit: false);
		ApplyHealth(snapshot.Health);
		var graphics = snapshot.GraphicsOverlay;
		GraphicsAssetName = graphics.AssetLoaded ? graphics.AssetName ?? "Unnamed graphics asset" : "No graphics asset loaded";
		GraphicsDimensions = graphics.AssetLoaded ? $"{graphics.AssetWidth}×{graphics.AssetHeight}" : "—";
		GraphicsVisible = graphics.Visible;
		GraphicsPositionX = graphics.PositionX * 100.0;
		GraphicsPositionY = graphics.PositionY * 100.0;
		GraphicsScale = graphics.Scale;
		GraphicsState = graphics.Visible ? "ON AIR" : graphics.AssetLoaded ? "READY" : "EMPTY";
		VisualLayerStatus = graphics.Visible ? "GRAPHICS ON" : snapshot.VisualLayerEnabled ? "ENABLED" : "DISABLED";
		ApplyAudio(snapshot, preserveSelectedGainEdit: false);
		RevisionLabel = $"REV {snapshot.Production.Revision.Value}";
		CommitStatus = string.Equals(snapshot.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase)
			? $"CONFIRMED · REV {snapshot.Production.Revision.Value}"
			: $"RUNTIME {snapshot.RuntimeStatus} · REV {snapshot.Production.Revision.Value}";
		IsConnected = true;
		IsStale = false;
		ConnectionState = string.Equals(snapshot.RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase)
			? "CONNECTED"
			: "DEGRADED";
		ConnectionDetail = $"Authoritative snapshot loaded. Runtime status: {snapshot.RuntimeStatus}.";
		if (!IsBusy)
			TransitionStatus = "READY";
		RaiseCommandState();
	}


	private void ApplyAudio(OperatorStatusSnapshot snapshot, bool preserveSelectedGainEdit)
	{
		var previousSelectedId = SelectedAudioInput?.SourceId;
		var existing = AudioInputs.ToDictionary(input => input.SourceId, StringComparer.Ordinal);
		AudioInputs.Clear();
		foreach (var descriptor in snapshot.AudioInputs)
		{
			var source = snapshot.Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, descriptor.SourceId, StringComparison.Ordinal));
			var isAfv = string.Equals(descriptor.SourceId, snapshot.AudioProgram.ActiveVideoSourceId, StringComparison.Ordinal);
			if (!existing.TryGetValue(descriptor.SourceId, out var input))
			{
				input = new OperatorAudioInputViewModel(
					descriptor,
					source?.Name ?? descriptor.SourceId,
					source?.Type ?? "LIVE",
					isAfv);
			}
			else
			{
				input.Apply(
					descriptor,
					source?.Name ?? descriptor.SourceId,
					source?.Type ?? "LIVE",
					isAfv,
					preserveSelectedGainEdit && string.Equals(previousSelectedId, descriptor.SourceId, StringComparison.Ordinal));
			}
			AudioInputs.Add(input);
		}

		SelectedAudioInput = AudioInputs.FirstOrDefault(input => string.Equals(input.SourceId, previousSelectedId, StringComparison.Ordinal))
			?? AudioInputs.FirstOrDefault(input => input.IsAfv)
			?? AudioInputs.FirstOrDefault();

		var program = snapshot.AudioProgram;
		AudioAfvSourceId = program.ActiveVideoSourceId;
		AudioAfvSourceName = ResolveSourceName(snapshot, program.ActiveVideoSourceId);
		AudioHealth = program.Health;
		AudioLeftPeak = program.LeftPeak;
		AudioRightPeak = program.RightPeak;
		AudioMasterPeak = program.MasterPeak;
		AudioClipping = program.Clipping;
		AudioMuted = program.Muted;
		AudioProgramGain = program.Gain;
		AudioPeak = program.MasterPeak.ToString("0.000", CultureInfo.InvariantCulture);
		AudioPeakPercent = program.MasterPeak.ToString("P0", CultureInfo.InvariantCulture);
		if (!string.Equals(AudioMeterStatus, "STALE", StringComparison.Ordinal))
			AudioMeterStatus = "LIVE";
		RaiseCommandState();
	}

	private void ApplyRecording(OperatorRecordingDescriptor recording, bool preserveTargetEdit)
	{
		RecordingStatus = recording.State;
		RecordingElapsed = FormatElapsed(recording.Elapsed);
		RecordingFinalPath = string.IsNullOrWhiteSpace(recording.FinalPath) ? "—" : recording.FinalPath;
		RecordingStatistics = $"{recording.Written} written · {recording.Dropped} dropped · {recording.WriterFailures} writer failures";
		RecordingError = recording.Failure?.Message;

		if (!preserveTargetEdit || recording.State is "RECORDING" or "FINALIZING")
		{
			if (!string.IsNullOrWhiteSpace(recording.Destination))
				RecordingDestination = recording.Destination;
			if (!string.IsNullOrWhiteSpace(recording.FileName))
				RecordingFileName = recording.FileName;
		}

		RaiseCommandState();
	}

	private void ApplyHealth(OperatorHealthDescriptor health)
	{
		EngineHealth = health.Engine.State;
		EngineHealthDetail = health.Engine.Detail;
		ControlHealth = health.Control.State;
		RuntimeHealth = health.Runtime.State;
		MediaHealth = health.Media.State;
		ProviderHealth = health.Provider.State;
		GpuProviderHealth = health.GpuProvider.State;
		CurrentFormat = health.CurrentFormat;
		FrameTime = health.FrameBudget > TimeSpan.Zero
			? $"{health.FrameTime.TotalMilliseconds:0.00} ms / {health.FrameBudget.TotalMilliseconds:0.00} ms"
			: "UNVERIFIED";
		DroppedFrames = health.DroppedFrames.ToString(CultureInfo.InvariantCulture);
		Uptime = FormatElapsed(health.Uptime);
		GpuUtilization = health.GpuUtilization;
		Vram = health.Vram;
		HealthObserved = health.ObservedAtUtc == DateTimeOffset.MinValue
			? "—"
			: health.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
	}

	private static string FormatElapsed(TimeSpan elapsed) =>
		$"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";

	private static string CreateRecordingFileName() =>
		$"rtaime-{DateTime.Now:yyyyMMdd-HHmmss}";

	private void Post(Action action)
	{
		if (_synchronizationContext is null)
		{
			action();
			return;
		}
		_synchronizationContext.Post(static state => ((Action)state!).Invoke(), action);
	}

	internal void SetSynchronizationContext(SynchronizationContext synchronizationContext) =>
		_synchronizationContext = synchronizationContext ?? throw new ArgumentNullException(nameof(synchronizationContext));

	internal void SetGraphicsAssetPicker(Func<OperatorGraphicsAsset?> graphicsAssetPicker)
	{
		_graphicsAssetPicker = graphicsAssetPicker ?? throw new ArgumentNullException(nameof(graphicsAssetPicker));
		RaiseCommandState();
	}

	internal void ApplySourceThumbnail(string sourceId, ImageSource thumbnail, string format)
	{
		var source = Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
		source?.ApplyThumbnail(thumbnail, format);
	}

	internal void ApplyMediaDeckSnapshot(MediaDeckSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var sourceId = snapshot.SourceId?.ToString();
		if (_mediaDeckSourceId is not null && !string.Equals(_mediaDeckSourceId, sourceId, StringComparison.Ordinal))
		{
			Sources.FirstOrDefault(source => string.Equals(source.Id, _mediaDeckSourceId, StringComparison.Ordinal))
				?.ClearMediaDeck();
		}

		_mediaDeckSourceId = sourceId;
		ClipAudioStatus = snapshot.Probe is null
			? "NO CLIP AUDIO"
			: $"{snapshot.Probe.AudioCodec.ToString().ToUpperInvariant()} · {snapshot.Probe.AudioFormat.ChannelCount}ch · {snapshot.Probe.AudioFormat.SampleRate / 1000.0:0.#} kHz · {snapshot.State.ToString().ToUpperInvariant()}";
		if (sourceId is null)
			return;

		var source = Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, sourceId, StringComparison.Ordinal));
		if (source is null)
			return;

		var format = snapshot.Probe is null
			? source.Format
			: $"{snapshot.Probe.VideoFormat.Width}×{snapshot.Probe.VideoFormat.Height} {snapshot.Probe.VideoFormat.FrameRate}";
		var remaining = snapshot.Transport is null
			? "—"
			: MediaTimelineTimecode.FormatFrame(
				snapshot.Transport.EffectiveRemainingFrames,
				snapshot.Transport.Position.FrameRate);
		source.ApplyMediaDeck(
			snapshot.State.ToString().ToUpperInvariant(),
			format,
			remaining,
			snapshot.Probe?.FileName);
	}


	private void MarkStale(string detail)
	{
		IsConnected = false;
		IsStale = true;
		ConnectionState = "STALE";
		ConnectionDetail = detail;
		CommitStatus = "UNCONFIRMED";
		TransitionStatus = "BLOCKED";
		EngineHealth = "FAIL";
		EngineHealthDetail = $"Control connection is stale: {detail}";
		ControlHealth = "FAIL";
		RuntimeHealth = "UNVERIFIED";
		MediaHealth = "UNVERIFIED";
		ProviderHealth = "UNVERIFIED";
		GpuProviderHealth = "UNVERIFIED";
		HealthObserved = "STALE";
		LastError = detail;
		RaiseCommandState();
	}

	private static string ResolveSourceName(OperatorStatusSnapshot snapshot, string sourceId) =>
		snapshot.Sources.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal))?.Name ?? sourceId;

	private void RaiseCommandState()
	{
		(SynchronizeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(SetPreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(CutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(DissolveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(LoadGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ApplyGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ClearGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ApplyAudioGainCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleAudioMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(StartRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(StopRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		return true;
	}
}

internal sealed class AsyncRelayCommand : ICommand
{
	private readonly Func<Task> _execute;
	private readonly Func<bool> _canExecute;
	private bool _executing;

	public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
	{
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_canExecute = canExecute ?? (() => true);
	}

	public event EventHandler? CanExecuteChanged;

	public bool CanExecute(object? parameter) => !_executing && _canExecute();

	public async void Execute(object? parameter)
	{
		if (!CanExecute(parameter)) return;
		_executing = true;
		RaiseCanExecuteChanged();
		try
		{
			await _execute();
		}
		finally
		{
			_executing = false;
			RaiseCanExecuteChanged();
		}
	}

	public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
