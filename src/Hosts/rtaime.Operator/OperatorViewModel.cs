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

internal enum OperatorTestSignalPreset
{
	Off,
	Static,
	Motion,
	AvSync
}

public sealed class OperatorViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly OperatorControlClient? _client;
	private readonly IRuntimeReadinessService _runtimeReadiness;
	private readonly bool _ownsRuntimeReadiness;
	private SynchronizationContext? _synchronizationContext;
	private readonly CancellationTokenSource _audioPollingStop = new();
	private Func<OperatorGraphicsAsset?>? _graphicsAssetPicker;
	private Task? _audioPollingTask;
	private OperatorSourceTileViewModel? _selectedSource;
	private OperatorSceneViewModel? _selectedScene;
	private OperatorAudioInputViewModel? _selectedAudioInput;
	private string? _mediaDeckSourceId;
	private string _previewSourceName = "—";
	private string _previewSourceId = "—";
	private string _programSourceName = "—";
	private string _programSourceId = "—";
	private string _activeSceneName = "NO CONFIRMED SCENE";
	private string _activeSceneId = "—";
	private string _sceneFailureReason = "No confirmed scene is active.";
	private string _runtimeStatus = "DISCONNECTED";
	private string _timingStatus = "UNKNOWN";
	private string _inputStatus = "UNKNOWN";
	private string _aiStatus = "UNKNOWN";
	private bool _aiEnabled;
	private string _aiFeature = "Person Segmentation Highlight";
	private string _aiProvider = "UNVERIFIED";
	private string _aiInferenceTime = "—";
	private string _aiPersonRegions = "0";
	private string _aiSynchronization = "—";
	private string _aiConfidence = "—";
	private string? _aiError;
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
	private string _outputFps = "UNVERIFIED";
	private string _droppedFrames = "0";
	private string _uptime = "00:00:00";
	private string _cpuDeviceName = "UNVERIFIED";
	private string _cpuUtilization = "UNVERIFIED";
	private string _systemMemory = "UNVERIFIED";
	private string _gpuDeviceName = "UNVERIFIED";
	private string _gpuUtilization = "UNVERIFIED";
	private string _vram = "UNVERIFIED";
	private string _healthObserved = "—";
	private string _avSyncState = "UNAVAILABLE";
	private string _avSyncEvent = "UNAVAILABLE";
	private string _avSyncScheduledOffset = "UNAVAILABLE";
	private string _avSyncSubmitOffset = "UNAVAILABLE";
	private string _avSyncDrift = "UNAVAILABLE";
	private string _avSyncDetail = "A/V sync diagnostics are unavailable.";
	private string _visualLayerStatus = "UNKNOWN";
	private string _graphicsAssetName = "No graphics asset loaded";
	private string _graphicsDimensions = "—";
	private string _graphicsState = "EMPTY";
	private bool _graphicsVisible;
	private double _graphicsPositionX = 72.0;
	private double _graphicsPositionY = 6.0;
	private double _graphicsScale = 1.0;
	private string _graphicsText = "LIVE FROM RTAIME";
	private string _graphicsTypeface = "Segoe UI";
	private string _graphicsFallbackTypeface = "Arial";
	private double _graphicsFontSize = 54.0;
	private string _graphicsCgStatus = "BITMAP";
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
	private string _engineLifecycleState = OperatorLifecycleStates.Starting;
	private string _engineLifecycleDetail = "Waiting for the first authoritative Control snapshot.";
	private string _programSafety = OperatorProgramSafetyStates.Unknown;
	private string _recoveryAction = "Startup is automatic; no operator action is required.";
	private string _affectedComponent = "Control";
	private bool _startupComplete;
	private bool _hasSynchronized;
	private string _connectionState = "DISCONNECTED";
	private string _connectionDetail = "Synchronize to load authoritative state.";
	private string _commandStatus = "IDLE";
	private string _commitStatus = "UNCONFIRMED";
	private string _transitionStatus = "IDLE";
	private string _lastEvent = "Operator started. Synchronization pending.";
	private string _revisionLabel = "REV —";
	private uint _transitionFrames = 12;
	private string _previewViewerState = "DISCONNECTED";
	private string _programViewerState = "DISCONNECTED";
	private IReadOnlyList<OperatorOutputRoleDescriptor> _outputRoles = Array.Empty<OperatorOutputRoleDescriptor>();
	private IReadOnlyList<OperatorCompositingLayerDescriptor> _compositingLayers = Array.Empty<OperatorCompositingLayerDescriptor>();
	private string? _lastError;
	private bool _isBusy;
	private bool _isConnected;
	private bool _isStale;

	public OperatorViewModel(
		OperatorControlClient? client = null,
		Func<OperatorGraphicsAsset?>? graphicsAssetPicker = null,
		SynchronizationContext? synchronizationContext = null,
		IRuntimeReadinessService? runtimeReadiness = null)
	{
		_client = client;
		_graphicsAssetPicker = graphicsAssetPicker;
		_synchronizationContext = synchronizationContext ?? SynchronizationContext.Current;
		_runtimeReadiness = runtimeReadiness ?? new RuntimeReadinessService();
		_ownsRuntimeReadiness = runtimeReadiness is null;
		_runtimeReadiness.Changed += OnRuntimeReadinessChanged;
		Sources = new ObservableCollection<OperatorSourceTileViewModel>();
		Scenes = new ObservableCollection<OperatorSceneViewModel>();
		AudioInputs = new ObservableCollection<OperatorAudioInputViewModel>();
		SynchronizeCommand = new AsyncRelayCommand(SynchronizeAsync, () => _client is not null && !IsBusy);
		SetPreviewCommand = new AsyncRelayCommand(SetPreviewAsync, CanSetPreview);
		ActivateSceneCommand = new AsyncRelayCommand(ActivateSceneAsync, CanActivateScene);
		ToggleTestPatternCommand = new AsyncRelayCommand(ToggleTestPatternAsync, CanToggleTestPattern);
		CutCommand = new AsyncRelayCommand(CutAsync, CanTakePreview);
		DissolveCommand = new AsyncRelayCommand(DissolveAsync, () => CanTakePreview() && TransitionFrames >= 2);
		LoadGraphicsCommand = new AsyncRelayCommand(LoadGraphicsAsync, () => CanControl() && _graphicsAssetPicker is not null);
		ApplyGraphicsCommand = new AsyncRelayCommand(ApplyGraphicsAsync, CanApplyGraphicsPlacement);
		ApplyTextGraphicsCommand = new AsyncRelayCommand(ApplyTextGraphicsAsync, CanApplyTextGraphics);
		ToggleGraphicsCommand = new AsyncRelayCommand(ToggleGraphicsAsync, CanApplyGraphics);
		ClearGraphicsCommand = new AsyncRelayCommand(ClearGraphicsAsync, CanApplyGraphics);
		ApplyAudioGainCommand = new AsyncRelayCommand(ApplyAudioGainAsync, CanApplyAudio);
		ToggleAudioMuteCommand = new AsyncRelayCommand(ToggleAudioMuteAsync, CanApplyAudio);
		CycleAudioTestSignalCommand = new AsyncRelayCommand(CycleAudioTestSignalAsync, CanApplyAudio);
		StartRecordingCommand = new AsyncRelayCommand(StartRecordingAsync, CanStartRecording);
		StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, CanStopRecording);
		EnableAIShowcaseCommand = new AsyncRelayCommand(() => SetAIShowcaseAsync(true), () => CanControl() && !AIEnabled);
		DisableAIShowcaseCommand = new AsyncRelayCommand(() => SetAIShowcaseAsync(false), () => CanControl() && AIEnabled);
		ApplyRuntimeReadiness(_runtimeReadiness.Current);
	}

	internal OperatorControlClient? Client => _client;
	public IRuntimeReadinessService RuntimeReadiness => _runtimeReadiness;

	public event PropertyChangedEventHandler? PropertyChanged;
	internal event Action<MediaDeckSnapshot>? ConfirmedMediaDeckSnapshot;
	internal event Action<ShowControlWorkspaceSnapshot>? ConfirmedShowControlSnapshot;

	public ObservableCollection<OperatorSourceTileViewModel> Sources { get; }
	public ObservableCollection<OperatorSceneViewModel> Scenes { get; }
	public ObservableCollection<OperatorAudioInputViewModel> AudioInputs { get; }
	public ICommand SynchronizeCommand { get; }
	public ICommand SetPreviewCommand { get; }
	public ICommand ActivateSceneCommand { get; }
	public ICommand ToggleTestPatternCommand { get; }
	public ICommand CutCommand { get; }
	public ICommand DissolveCommand { get; }
	public ICommand LoadGraphicsCommand { get; }
	public ICommand ApplyGraphicsCommand { get; }
	public ICommand ApplyTextGraphicsCommand { get; }
	public ICommand ToggleGraphicsCommand { get; }
	public ICommand ClearGraphicsCommand { get; }
	public ICommand ApplyAudioGainCommand { get; }
	public ICommand ToggleAudioMuteCommand { get; }
	public ICommand CycleAudioTestSignalCommand { get; }
	public ICommand StartRecordingCommand { get; }
	public ICommand StopRecordingCommand { get; }
	public ICommand EnableAIShowcaseCommand { get; }
	public ICommand DisableAIShowcaseCommand { get; }

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

	public OperatorSceneViewModel? SelectedScene
	{
		get => _selectedScene;
		set
		{
			if (Set(ref _selectedScene, value))
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
	public string ActiveSceneName { get => _activeSceneName; private set => Set(ref _activeSceneName, value); }
	public string ActiveSceneId { get => _activeSceneId; private set => Set(ref _activeSceneId, value); }
	public string SceneFailureReason { get => _sceneFailureReason; private set => Set(ref _sceneFailureReason, value); }
	public string RuntimeStatus { get => _runtimeStatus; private set => Set(ref _runtimeStatus, value); }
	public string TimingStatus { get => _timingStatus; private set => Set(ref _timingStatus, value); }
	public string InputStatus { get => _inputStatus; private set => Set(ref _inputStatus, value); }
	public string AIStatus { get => _aiStatus; private set => Set(ref _aiStatus, value); }
	public bool AIEnabled { get => _aiEnabled; private set => Set(ref _aiEnabled, value); }
	public string AIFeature { get => _aiFeature; private set => Set(ref _aiFeature, value); }
	public string AIProvider { get => _aiProvider; private set => Set(ref _aiProvider, value); }
	public string AIInferenceTime { get => _aiInferenceTime; private set => Set(ref _aiInferenceTime, value); }
	public string AIPersonRegions { get => _aiPersonRegions; private set => Set(ref _aiPersonRegions, value); }
	public string AISynchronization { get => _aiSynchronization; private set => Set(ref _aiSynchronization, value); }
	public string AIConfidence { get => _aiConfidence; private set => Set(ref _aiConfidence, value); }
	public string? AIError { get => _aiError; private set => Set(ref _aiError, value); }
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
	public string OutputFps { get => _outputFps; private set => Set(ref _outputFps, value); }
	public string DroppedFrames { get => _droppedFrames; private set => Set(ref _droppedFrames, value); }
	public string Uptime { get => _uptime; private set => Set(ref _uptime, value); }
	public string CpuDeviceName { get => _cpuDeviceName; private set => Set(ref _cpuDeviceName, value); }
	public string CpuUtilization { get => _cpuUtilization; private set => Set(ref _cpuUtilization, value); }
	public string SystemMemory { get => _systemMemory; private set => Set(ref _systemMemory, value); }
	public string GpuDeviceName { get => _gpuDeviceName; private set => Set(ref _gpuDeviceName, value); }
	public string GpuUtilization { get => _gpuUtilization; private set => Set(ref _gpuUtilization, value); }
	public string Vram { get => _vram; private set => Set(ref _vram, value); }
	public string HealthObserved { get => _healthObserved; private set => Set(ref _healthObserved, value); }
	public string AvSyncState { get => _avSyncState; private set => Set(ref _avSyncState, value); }
	public string AvSyncEvent { get => _avSyncEvent; private set => Set(ref _avSyncEvent, value); }
	public string AvSyncScheduledOffset { get => _avSyncScheduledOffset; private set => Set(ref _avSyncScheduledOffset, value); }
	public string AvSyncSubmitOffset { get => _avSyncSubmitOffset; private set => Set(ref _avSyncSubmitOffset, value); }
	public string AvSyncDrift { get => _avSyncDrift; private set => Set(ref _avSyncDrift, value); }
	public string AvSyncDetail { get => _avSyncDetail; private set => Set(ref _avSyncDetail, value); }
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
	public string GraphicsText
	{
		get => _graphicsText;
		set
		{
			if (Set(ref _graphicsText, value ?? string.Empty))
				RaiseCommandState();
		}
	}
	public string GraphicsTypeface
	{
		get => _graphicsTypeface;
		set
		{
			if (Set(ref _graphicsTypeface, value ?? string.Empty))
				RaiseCommandState();
		}
	}
	public string GraphicsFallbackTypeface
	{
		get => _graphicsFallbackTypeface;
		set
		{
			if (Set(ref _graphicsFallbackTypeface, value ?? string.Empty))
				RaiseCommandState();
		}
	}
	public double GraphicsFontSize
	{
		get => _graphicsFontSize;
		set
		{
			if (Set(ref _graphicsFontSize, Math.Clamp(value, 8.0, 256.0)))
				RaiseCommandState();
		}
	}
	public string GraphicsCgStatus { get => _graphicsCgStatus; private set => Set(ref _graphicsCgStatus, value); }
	public string AudioPeak { get => _audioPeak; private set => Set(ref _audioPeak, value); }
	public string AudioPeakPercent { get => _audioPeakPercent; private set => Set(ref _audioPeakPercent, value); }
	public string AudioAfvSourceName { get => _audioAfvSourceName; private set => Set(ref _audioAfvSourceName, value); }
	public string AudioAfvSourceId { get => _audioAfvSourceId; private set => Set(ref _audioAfvSourceId, value); }
	public string AudioHealth { get => _audioHealth; private set => Set(ref _audioHealth, value); }
	public string AudioMeterStatus { get => _audioMeterStatus; private set => Set(ref _audioMeterStatus, value); }
	public double AudioLeftPeak
	{
		get => _audioLeftPeak;
		private set
		{
			if (Set(ref _audioLeftPeak, value))
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioLeftDb)));
		}
	}

	public double AudioRightPeak
	{
		get => _audioRightPeak;
		private set
		{
			if (Set(ref _audioRightPeak, value))
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AudioRightDb)));
		}
	}

	public string AudioLeftDb => FormatDb(AudioLeftPeak);
	public string AudioRightDb => FormatDb(AudioRightPeak);
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
	public string EngineLifecycleState { get => _engineLifecycleState; private set => Set(ref _engineLifecycleState, value); }
	public string EngineLifecycleDetail { get => _engineLifecycleDetail; private set => Set(ref _engineLifecycleDetail, value); }
	public string ProgramSafety { get => _programSafety; private set => Set(ref _programSafety, value); }
	public string RecoveryAction { get => _recoveryAction; private set => Set(ref _recoveryAction, value); }
	public string AffectedComponent { get => _affectedComponent; private set => Set(ref _affectedComponent, value); }
	public bool StartupComplete { get => _startupComplete; private set => Set(ref _startupComplete, value); }
	public string GlobalReadinessState => _runtimeReadiness.Current.State.ToString().ToUpperInvariant();
	public string GlobalReadinessLabel => FormatGlobalReadinessLabel(_runtimeReadiness.Current);
	public string GlobalReadinessSummary => ResolveGlobalReadinessSummary(_runtimeReadiness.Current);
	public string GlobalReadinessTooltip => FormatGlobalReadinessTooltip(_runtimeReadiness.Current);
	public bool IsProductionReady => _runtimeReadiness.Current.IsProductionReady;
	public string PerformanceVerificationState => _runtimeReadiness.Current.Performance.State.ToString().ToUpperInvariant();
	public string PerformanceVerificationDetail => _runtimeReadiness.Current.Performance.Detail;
	public string PreviewViewerState { get => _previewViewerState; private set => Set(ref _previewViewerState, value); }
	public string ProgramViewerState { get => _programViewerState; private set => Set(ref _programViewerState, value); }
	public IReadOnlyList<OperatorOutputRoleDescriptor> OutputRoles
	{
		get => _outputRoles;
		private set
		{
			_outputRoles = value ?? Array.Empty<OperatorOutputRoleDescriptor>();
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputRoles)));
		}
	}

	public IReadOnlyList<OperatorCompositingLayerDescriptor> CompositingLayers
	{
		get => _compositingLayers;
		private set
		{
			_compositingLayers = value ?? Array.Empty<OperatorCompositingLayerDescriptor>();
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CompositingLayers)));
		}
	}

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
		ProgramSafety != OperatorProgramSafetyStates.Blocked &&
		string.Equals(RuntimeStatus, "READY", StringComparison.OrdinalIgnoreCase);

	private bool CanSetPreview() => CanControl() && SelectedSource is not null;

	private bool CanActivateScene() => CanControl() && SelectedScene is not null;

	private bool CanToggleTestPattern() => CanControl() && SelectedSource is not null;

	private bool CanTakePreview() => CanControl() && _client?.Snapshot is not null;

	private bool CanApplyGraphics() =>
		CanControl() &&
		_client?.Snapshot?.GraphicsOverlay.AssetLoaded == true;

	private bool CanApplyGraphicsPlacement() =>
		CanApplyGraphics() &&
		(_client?.Snapshot?.ProductionCgText.Active != true ||
			CompositingLayers.Any(layer => string.Equals(layer.LayerId, "bitmap-graphics", StringComparison.Ordinal)));

	internal bool CanManageCompositingLayers() => CanControl() && CompositingLayers.Count > 0;

	private bool CanApplyTextGraphics() =>
		CanControl() &&
		!string.IsNullOrWhiteSpace(GraphicsText) &&
		GraphicsText.Length <= 512 &&
		!string.IsNullOrWhiteSpace(GraphicsTypeface) &&
		GraphicsTypeface.Length <= 128 &&
		GraphicsFontSize is >= 8 and <= 256;

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
		_runtimeReadiness.Changed -= OnRuntimeReadinessChanged;
		if (_ownsRuntimeReadiness && _runtimeReadiness is IDisposable disposableReadiness)
			disposableReadiness.Dispose();
	}

	private async Task PollAudioAsync(CancellationToken cancellationToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
		while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
		{
			if (_client is null || IsBusy)
				continue;
			try
			{
				var snapshot = await _client.SynchronizeAsync(cancellationToken).ConfigureAwait(false);
				Post(() =>
				{
					var recovered = _hasSynchronized && (IsStale || !IsConnected);
					if (!_hasSynchronized || recovered)
					{
						Apply(snapshot);
						if (recovered)
						{
							CommandStatus = "RESYNCHRONIZED";
							LastError = null;
							LastEvent = "Automatic recovery restored a full authoritative Control snapshot.";
						}
					}
					else
					{
						ApplyAudio(snapshot, preserveSelectedGainEdit: true);
						ApplyRecording(snapshot.Recording, preserveTargetEdit: true);
						ApplyHealth(snapshot.Health);
						ApplyOutputRoles(snapshot.OutputRoles);
						CompositingLayers = snapshot.CompositingLayers;
						ApplyAI(snapshot.AIShowcase);
						ApplyEmbeddedMediaDeckSnapshot(snapshot.MediaDeck);
						ApplyLifecycle(snapshot);
						UpdateViewerStates(snapshot);
					}
					AudioMeterStatus = "LIVE";
				});
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
			{
				Post(() =>
				{
					AudioMeterStatus = "STALE";
					if (_hasSynchronized)
					{
						if (!IsStale)
						{
							MarkStale("Control synchronization is stale. Automatic full-snapshot recovery is active.");
							CommandStatus = "RECOVERING";
							LastEvent = "Control connection was lost; automatic recovery is running.";
						}
						else
						{
							ApplyLifecycle(_client?.Snapshot);
						}
					}
					else
					{
						MarkStartupWaiting(exception.Message);
					}
				});
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

	private async Task ActivateSceneAsync()
	{
		if (_client is null || SelectedScene is null)
			return;

		var scene = SelectedScene;
		await ExecuteAsync("TAKE SCENE", async () =>
		{
			var response = await _client.ActivateSceneAsync(scene.Id);
			if (!Accept(response, "Scene Activation"))
			{
				SceneFailureReason = response.Failure?.Message ?? "Scene activation was rejected.";
				return;
			}

			Apply(_client.Snapshot!);
			SceneFailureReason = "NONE";
			CommandStatus = "SCENE APPLIED";
			TransitionStatus = "SCENE CONFIRMED";
			LastEvent = $"Scene {scene.Name} committed as the authoritative production state.";
		});
	}

	private Task ToggleTestPatternAsync()
	{
		if (SelectedSource is null)
			return Task.CompletedTask;

		var preset = !SelectedSource.IsTestPattern
			? OperatorTestSignalPreset.Static
			: string.Equals(SelectedSource.MediaState, "STATIC", StringComparison.OrdinalIgnoreCase)
				? OperatorTestSignalPreset.Motion
				: OperatorTestSignalPreset.Off;
		return ConfigureTestSignalAsync(SelectedSource, preset);
	}

	internal async Task ConfigureTestSignalAsync(
		OperatorSourceTileViewModel source,
		OperatorTestSignalPreset preset)
	{
		ArgumentNullException.ThrowIfNull(source);
		if (_client is null)
			return;

		var audioInput = AudioInputs.FirstOrDefault(input =>
			string.Equals(input.SourceId, source.Id, StringComparison.Ordinal));
		var videoEnabled = preset != OperatorTestSignalPreset.Off;
		var motionTiming = preset is OperatorTestSignalPreset.Motion or OperatorTestSignalPreset.AvSync;
		var audioPulse = preset == OperatorTestSignalPreset.AvSync;
		var operation = preset switch
		{
			OperatorTestSignalPreset.Static => "ENABLE STATIC TEST SIGNAL",
			OperatorTestSignalPreset.Motion => "ENABLE MOTION TEST SIGNAL",
			OperatorTestSignalPreset.AvSync => "ENABLE A/V SYNC TEST SIGNAL",
			_ => "DISABLE TEST SIGNAL"
		};

		SelectedSource = source;
		await ExecuteAsync(operation, async () =>
		{
			if (audioPulse && audioInput is null)
				throw new InvalidOperationException(
					$"A/V sync test signal requires an audio input associated with {source.Name}.");

			await _client.SetBroadcastTestPatternAsync(source.Id, videoEnabled, motionTiming);

			if (audioInput is not null)
			{
				await _client.SetAudioTestSignalAsync(
					source.Id,
					audioPulse,
					5,
					1_000,
					0.25);
			}

			Apply(_client.Snapshot!);
			CommandStatus = preset switch
			{
				OperatorTestSignalPreset.Static => "STATIC TEST ACTIVE",
				OperatorTestSignalPreset.Motion => "MOTION TEST ACTIVE",
				OperatorTestSignalPreset.AvSync => "A/V SYNC TEST ACTIVE",
				_ => "TEST SIGNAL OFF"
			};
			LastEvent = preset switch
			{
				OperatorTestSignalPreset.Static =>
					$"Static broadcast test signal activated on {source.Name}; generated audio is off.",
				OperatorTestSignalPreset.Motion =>
					$"Motion/timing test signal activated on {source.Name}; generated audio is off.",
				OperatorTestSignalPreset.AvSync =>
					$"A/V sync diagnostics activated on {source.Name} with motion/timing video and pulse audio.",
				_ => $"Internal video and generated audio test signals deactivated on {source.Name}."
			};
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

	private async Task SetAIShowcaseAsync(bool enabled)
	{
		if (_client is null) return;
		await ExecuteAsync(enabled ? "ENABLE AI SHOWCASE" : "DISABLE AI SHOWCASE", async () =>
		{
			await _client.SetAIShowcaseEnabledAsync(enabled);
			Apply(_client.Snapshot!);
			CommandStatus = enabled ? "AI ENABLED" : "AI DISABLED";
			LastEvent = enabled
				? "Person Segmentation Highlight enabled through ControlHost and RuntimeHost; AIHost inference remains failure-isolated."
				: "Person Segmentation Highlight disabled and Runtime visual fallback restored.";
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

	private async Task ApplyTextGraphicsAsync()
	{
		if (_client is null || !CanApplyTextGraphics()) return;
		await ExecuteAsync("APPLY LOWER THIRD", async () =>
		{
			var definition = new OperatorProductionCgText(
				GraphicsText,
				GraphicsTypeface,
				string.IsNullOrWhiteSpace(GraphicsFallbackTypeface) ? null : GraphicsFallbackTypeface,
				(float)GraphicsFontSize,
				new OperatorCgColor(255, 255, 255, 255),
				0.05,
				0.91,
				900,
				144,
				OperatorCgTextAlignment.Left,
				OperatorCgAnchor.BottomLeft,
				new OperatorCgPanelStyle(
					true,
					new OperatorCgColor(18, 23, 32, 224),
					14,
					28),
				true);
			await _client.ApplyProductionCgTextAsync(definition);
			Apply(_client.Snapshot!);
			CommandStatus = "LOWER THIRD ON AIR";
			LastEvent = $"Production CG text was rendered by RuntimeHost using {GraphicsCgStatus}.";
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

	internal async Task SetCompositingLayerStateAsync(
		string layerId,
		bool visible,
		byte opacity)
	{
		if (_client is null || !CanManageCompositingLayers()) return;
		if (string.IsNullOrWhiteSpace(layerId)) throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));

		await ExecuteAsync("LAYER STATE", async () =>
		{
			await _client.SetCompositingLayerStateAsync(layerId.Trim(), visible, opacity);
			Apply(_client.Snapshot!);
			CommandStatus = "LAYER CONFIRMED";
			LastEvent = $"Compositing layer {layerId.Trim()} state was confirmed by RuntimeHost.";
		});
	}

	internal async Task ReorderCompositingLayersAsync(IReadOnlyList<string> orderedLayerIds)
	{
		if (_client is null || !CanManageCompositingLayers()) return;
		ArgumentNullException.ThrowIfNull(orderedLayerIds);

		await ExecuteAsync("LAYER ORDER", async () =>
		{
			await _client.ReorderCompositingLayersAsync(orderedLayerIds);
			Apply(_client.Snapshot!);
			CommandStatus = "LAYER ORDER CONFIRMED";
			LastEvent = "Compositing layer order was confirmed by RuntimeHost.";
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

	private async Task CycleAudioTestSignalAsync()
	{
		if (_client is null || SelectedAudioInput is null) return;
		var input = SelectedAudioInput;
		var nextMode = !input.TestSignalEnabled
			? 1
			: input.TestSignalMode is >= 1 and < 5
				? input.TestSignalMode.Value + 1
				: 0;
		var enabled = nextMode != 0;
		var requestMode = enabled ? nextMode : 2;
		await ExecuteAsync(enabled ? "AUDIO TEST SIGNAL" : "AUDIO TEST SIGNAL OFF", async () =>
		{
			await _client.SetAudioTestSignalAsync(input.SourceId, enabled, requestMode, 1_000, 0.25);
			ApplyAudio(_client.Snapshot!, preserveSelectedGainEdit: false);
			CommandStatus = enabled ? "AUDIO TEST ACTIVE" : "AUDIO TEST OFF";
			LastEvent = enabled
				? $"{input.SourceName} generated audio test signal advanced to mode {requestMode}."
				: $"{input.SourceName} generated audio test signal disabled.";
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
			: operation.StartsWith("AUDIO ", StringComparison.Ordinal) ||
				operation.EndsWith("TEST SIGNAL", StringComparison.Ordinal)
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
			var connectivityFailure = exception is IOException or TimeoutException or OperationCanceledException;
			var startupWaiting = connectivityFailure && !_hasSynchronized;
			if (connectivityFailure)
			{
				if (startupWaiting)
					MarkStartupWaiting(exception.Message);
				else
					MarkStale(exception.Message);
			}
			else
			{
				LastError = exception.Message;
			}

			CommandStatus = startupWaiting ? "WAITING" : "FAILED";
			CommitStatus = startupWaiting ? "UNCONFIRMED" : IsStale ? "UNCONFIRMED" : $"FAILED · {RevisionLabel} UNCHANGED";
			TransitionStatus = startupWaiting ? "STARTING" : $"{operation} FAILED";
			LastEvent = startupWaiting
				? "Waiting for authoritative Control readiness."
				: $"{operation} failed; Program was not advanced locally.";
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
		var previousSceneSelectionId = SelectedScene?.Id;
		var existing = Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
		Sources.Clear();
		var displayIndex = 1;
		foreach (var descriptor in snapshot.Sources)
		{
			if (!existing.TryGetValue(descriptor.Id, out var source))
				source = new OperatorSourceTileViewModel(descriptor);
			else
				source.ApplyDescriptor(descriptor);
			source.ApplyDisplayIndex(displayIndex++);
			Sources.Add(source);
		}

		var previewId = snapshot.Production.Routing.PreviewSourceId.ToString();
		var programId = snapshot.Production.Routing.ProgramSourceId.ToString();
		foreach (var source in Sources)
			source.ApplyRouting(previewId, programId);
		SelectedSource = Sources.FirstOrDefault(source => string.Equals(source.Id, previousSelectionId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault(source => string.Equals(source.Id, previewId, StringComparison.Ordinal))
			?? Sources.FirstOrDefault();

		var existingScenes = Scenes.ToDictionary(scene => scene.Id, StringComparer.Ordinal);
		Scenes.Clear();
		var sceneDisplayIndex = 1;
		foreach (var descriptor in snapshot.Scenes)
		{
			var programSource = Sources.FirstOrDefault(source =>
				string.Equals(source.Id, descriptor.ProgramSourceId, StringComparison.Ordinal));
			if (programSource is null)
				continue;

			if (!existingScenes.TryGetValue(descriptor.Id, out var scene))
				scene = new OperatorSceneViewModel(descriptor, programSource, sceneDisplayIndex);
			else
				scene.Apply(descriptor, programSource, sceneDisplayIndex);
			scene.ApplyEvidence(previewId, snapshot.Production.ActiveSceneId?.ToString());
			Scenes.Add(scene);
			sceneDisplayIndex++;
		}

		SelectedScene = Scenes.FirstOrDefault(scene => string.Equals(scene.Id, previousSceneSelectionId, StringComparison.Ordinal))
			?? Scenes.FirstOrDefault(scene => scene.IsPreview)
			?? Scenes.FirstOrDefault();

		var activeSceneId = snapshot.Production.ActiveSceneId?.ToString();
		var activeScene = string.IsNullOrWhiteSpace(activeSceneId)
			? null
			: Scenes.FirstOrDefault(scene => string.Equals(scene.Id, activeSceneId, StringComparison.Ordinal));
		ActiveSceneId = activeSceneId ?? "—";
		ActiveSceneName = activeScene?.Name ?? "NO CONFIRMED SCENE";
		SceneFailureReason = activeScene is null
			? "Current Program state was not committed by a Scene activation."
			: "NONE";

		PreviewSourceId = previewId;
		ProgramSourceId = programId;
		PreviewSourceName = ResolveSourceName(snapshot, previewId);
		ProgramSourceName = ResolveSourceName(snapshot, programId);
		RuntimeStatus = snapshot.RuntimeStatus;
		TimingStatus = snapshot.TimingStatus;
		InputStatus = snapshot.InputStatus;
		AIStatus = snapshot.AIStatus;
		ApplyAI(snapshot.AIShowcase);
		ApplyRecording(snapshot.Recording, preserveTargetEdit: false);
		ApplyHealth(snapshot.Health);
		ApplyOutputRoles(snapshot.OutputRoles);
		CompositingLayers = snapshot.CompositingLayers;
		var graphics = snapshot.GraphicsOverlay;
		GraphicsAssetName = graphics.AssetLoaded ? graphics.AssetName ?? "Unnamed graphics asset" : "No graphics asset loaded";
		GraphicsDimensions = graphics.AssetLoaded ? $"{graphics.AssetWidth}×{graphics.AssetHeight}" : "—";
		GraphicsVisible = graphics.Visible;
		GraphicsPositionX = graphics.PositionX * 100.0;
		GraphicsPositionY = graphics.PositionY * 100.0;
		GraphicsScale = graphics.Scale;
		GraphicsState = graphics.Visible ? "ON AIR" : graphics.AssetLoaded ? "READY" : "EMPTY";
		VisualLayerStatus = graphics.Visible ? "GRAPHICS ON" : snapshot.VisualLayerEnabled ? "ENABLED" : "DISABLED";
		if (snapshot.ProductionCgText.Active)
		{
			GraphicsText = snapshot.ProductionCgText.Text ?? GraphicsText;
			GraphicsTypeface = snapshot.ProductionCgText.Typeface ?? GraphicsTypeface;
			GraphicsFontSize = snapshot.ProductionCgText.FontSizePixels;
			var resolvedTypeface = snapshot.ProductionCgText.ResolvedTypeface ?? "UNRESOLVED";
			var cacheState = snapshot.ProductionCgText.CacheHit ? "CACHE" : "RENDER";
			GraphicsCgStatus = $"CG · {resolvedTypeface} · {cacheState} · {snapshot.ProductionCgText.RenderDuration.TotalMilliseconds:0.0} ms";
		}
		else
		{
			GraphicsCgStatus = graphics.AssetLoaded ? "BITMAP" : "EMPTY";
		}
		ApplyAudio(snapshot, preserveSelectedGainEdit: false);
		ApplyEmbeddedMediaDeckSnapshot(snapshot.MediaDeck);
		ConfirmedShowControlSnapshot?.Invoke(snapshot.ShowControl);
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
		_hasSynchronized = true;
		ApplyLifecycle(snapshot);
		UpdateViewerStates(snapshot);
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

		foreach (var source in Sources)
		{
			var input = AudioInputs.FirstOrDefault(candidate => string.Equals(candidate.SourceId, source.Id, StringComparison.Ordinal));
			if (input is null)
				source.ClearAudioMeter();
			else
				source.ApplyAudioMeter(input.LeftPeak, input.RightPeak, input.Clipping);
		}

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

	private void ApplyOutputRoles(IReadOnlyList<OperatorOutputRoleDescriptor> outputRoles)
	{
		OutputRoles = Array.AsReadOnly((outputRoles ?? Array.Empty<OperatorOutputRoleDescriptor>()).ToArray());
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

	private void ApplyAI(OperatorAIShowcaseDescriptor showcase)
	{
		AIEnabled = showcase.Enabled;
		AIFeature = showcase.Feature;
		AIStatus = showcase.Status;
		AIProvider = showcase.Provider;
		AIInferenceTime = showcase.InferenceTime > TimeSpan.Zero
			? $"{showcase.InferenceTime.TotalMilliseconds:0.0} ms"
			: "—";
		AIPersonRegions = showcase.PersonRegionCount.ToString(CultureInfo.InvariantCulture);
		AISynchronization = showcase.SourceSequence is { } source
			? showcase.AppliedSequence is { } applied
				? $"source {source} → Program {applied}"
				: $"source {source} → not applied"
			: "—";
		AIConfidence = showcase.Confidence is { } confidence
			? confidence.ToString("P0", CultureInfo.InvariantCulture)
			: "—";
		AIError = showcase.Failure?.Message;
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
		FrameTime = RuntimePerformanceDisplayFormatter.FormatFrameTime(health.FrameTime, health.FrameBudget);
		OutputFps = RuntimePerformanceDisplayFormatter.FormatFramesPerSecond(health.OutputFramesPerSecond);
		DroppedFrames = RuntimePerformanceDisplayFormatter.FormatDroppedFrames(health.DroppedFrames);
		Uptime = FormatElapsed(health.Uptime);
		CpuDeviceName = health.CpuDeviceName;
		CpuUtilization = health.CpuUtilization;
		SystemMemory = health.SystemMemory;
		GpuDeviceName = health.GpuDeviceName;
		GpuUtilization = health.GpuUtilization;
		Vram = health.Vram;
		AvSyncState = health.AvSyncState;
		AvSyncEvent = health.AvSyncEvent;
		AvSyncScheduledOffset = health.AvSyncScheduledOffset;
		AvSyncSubmitOffset = health.AvSyncSubmitOffset;
		AvSyncDrift = health.AvSyncDrift;
		AvSyncDetail = health.AvSyncDetail;
		HealthObserved = health.ObservedAtUtc == DateTimeOffset.MinValue
			? "—"
			: health.ObservedAtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
	}

	private void UpdateViewerStates(OperatorStatusSnapshot? snapshot = null)
	{
		if (IsStale)
		{
			PreviewViewerState = "DISCONNECTED";
			ProgramViewerState = "DISCONNECTED";
			return;
		}

		if (!IsConnected)
		{
			PreviewViewerState = "RECOVERING";
			ProgramViewerState = "RECOVERING";
			return;
		}

		var previewHealth = Sources.FirstOrDefault(source => source.IsPreview)?.Health;
		var programHealth = Sources.FirstOrDefault(source => source.IsProgram)?.Health;
		var runtimeStatus = RuntimeStatus;
		if (snapshot is not null)
		{
			var previewId = snapshot.Production.Routing.PreviewSourceId.ToString();
			var programId = snapshot.Production.Routing.ProgramSourceId.ToString();
			previewHealth = snapshot.Sources.FirstOrDefault(source =>
				string.Equals(source.Id, previewId, StringComparison.Ordinal))?.Health ?? previewHealth;
			programHealth = snapshot.Sources.FirstOrDefault(source =>
				string.Equals(source.Id, programId, StringComparison.Ordinal))?.Health ?? programHealth;
			runtimeStatus = snapshot.RuntimeStatus;
		}

		PreviewViewerState = MapViewerSourceState(previewHealth, runtimeStatus);
		ProgramViewerState = MapViewerSourceState(programHealth, runtimeStatus);
	}

	private static string MapViewerSourceState(string? sourceHealth, string runtimeStatus)
	{
		var normalizedRuntime = runtimeStatus?.Trim() ?? string.Empty;
		if (normalizedRuntime.Contains("RECOVER", StringComparison.OrdinalIgnoreCase))
			return "RECOVERING";
		if (!string.Equals(normalizedRuntime, "READY", StringComparison.OrdinalIgnoreCase))
		{
			if (normalizedRuntime.Contains("DISCONNECT", StringComparison.OrdinalIgnoreCase) ||
				normalizedRuntime.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase) ||
				normalizedRuntime.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
				normalizedRuntime.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
				normalizedRuntime.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
				return "DISCONNECTED";
			return "RECOVERING";
		}

		return sourceHealth?.Trim().ToUpperInvariant() switch
		{
			"LOST" => "NO SIGNAL",
			"ERROR" or "FAILED" or "OFFLINE" => "SOURCE OFFLINE",
			"RECOVERING" or "UNSTABLE" or "UNKNOWN" => "RECOVERING",
			_ => "LIVE"
		};
	}

	private static string FormatDb(double peak)
	{
		if (!double.IsFinite(peak) || peak <= 0)
			return "−∞";
		return $"{20.0 * Math.Log10(Math.Clamp(peak, double.Epsilon, 1.0)):0.0}";
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

	internal void ApplyConfirmedSnapshot(OperatorStatusSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Apply(snapshot);
	}

	private void ApplyEmbeddedMediaDeckSnapshot(MediaDeckSnapshot snapshot)
	{
		ApplyMediaDeckSnapshot(snapshot);
		ConfirmedMediaDeckSnapshot?.Invoke(snapshot);
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
		var timecode = snapshot.Transport is null
			? "—"
			: MediaTimelineTimecode.FormatFrame(
				snapshot.Transport.Position.CurrentFrame,
				snapshot.Transport.Position.FrameRate);
		source.ApplyMediaDeck(
			snapshot.State.ToString().ToUpperInvariant(),
			format,
			remaining,
			timecode,
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
		AvSyncState = "UNAVAILABLE";
		AvSyncEvent = "UNAVAILABLE";
		AvSyncScheduledOffset = "UNAVAILABLE";
		AvSyncSubmitOffset = "UNAVAILABLE";
		AvSyncDrift = "UNAVAILABLE";
		AvSyncDetail = "A/V sync diagnostics are unavailable while Control is stale.";
		HealthObserved = "STALE";
		LastError = detail;
		ApplyLifecycle(_client?.Snapshot);
		UpdateViewerStates();
		RaiseCommandState();
	}

	private void MarkStartupWaiting(string detail)
	{
		IsConnected = false;
		IsStale = false;
		ConnectionState = "STARTING";
		ConnectionDetail = $"Waiting for authoritative startup readiness. {detail}";
		CommandStatus = "WAITING";
		CommitStatus = "UNCONFIRMED";
		TransitionStatus = "STARTING";
		LastError = null;
		ApplyLifecycle(null);
		UpdateViewerStates();
		RaiseCommandState();
	}

	private void ApplyLifecycle(OperatorStatusSnapshot? snapshot)
	{
		_runtimeReadiness.Observe(new RuntimeReadinessObservation(
			snapshot?.Health ?? OperatorHealthDescriptor.Unavailable,
			snapshot?.RuntimeStatus ?? RuntimeStatus,
			snapshot?.AIShowcase ?? OperatorAIShowcaseDescriptor.Unavailable,
			IsConnected,
			IsStale));
		ApplyRuntimeReadiness(_runtimeReadiness.Current);
	}

	private void OnRuntimeReadinessChanged(object? sender, RuntimeReadinessChangedEventArgs e) =>
		Post(() => ApplyRuntimeReadiness(_runtimeReadiness.Current));

	private void ApplyRuntimeReadiness(RuntimeReadinessSnapshot snapshot)
	{
		EngineLifecycleState = snapshot.State switch
		{
			RuntimeReadinessState.Initializing => OperatorLifecycleStates.Starting,
			RuntimeReadinessState.Ready => OperatorLifecycleStates.Healthy,
			RuntimeReadinessState.Degraded => OperatorLifecycleStates.Degraded,
			RuntimeReadinessState.NotReady => OperatorLifecycleStates.Degraded,
			RuntimeReadinessState.Recovering => OperatorLifecycleStates.Recovering,
			RuntimeReadinessState.Failed => OperatorLifecycleStates.Failed,
			_ => OperatorLifecycleStates.Degraded
		};
		EngineLifecycleDetail = ResolveGlobalReadinessSummary(snapshot);
		ProgramSafety = snapshot.State switch
		{
			RuntimeReadinessState.Ready => OperatorProgramSafetyStates.Safe,
			RuntimeReadinessState.Degraded => OperatorProgramSafetyStates.Caution,
			RuntimeReadinessState.Initializing => OperatorProgramSafetyStates.Unknown,
			_ => OperatorProgramSafetyStates.Blocked
		};
		RecoveryAction = snapshot.State switch
		{
			RuntimeReadinessState.Initializing => "Startup is automatic; no operator action is required.",
			RuntimeReadinessState.Ready => "No operator action is required.",
			RuntimeReadinessState.Degraded => "Review the named degradation while production remains available.",
			RuntimeReadinessState.NotReady => "Keep production mutations blocked until required components recover.",
			RuntimeReadinessState.Recovering => "Automatic recovery is active; wait for fresh authoritative state.",
			RuntimeReadinessState.Failed => "Review diagnostics before attempting another production session.",
			_ => "Review diagnostics."
		};
		AffectedComponent = snapshot.Reasons.FirstOrDefault()?.Component ?? "—";
		if (!StartupComplete && snapshot.IsProductionReady)
			StartupComplete = true;

		foreach (var propertyName in new[]
		{
			nameof(GlobalReadinessState),
			nameof(GlobalReadinessLabel),
			nameof(GlobalReadinessSummary),
			nameof(GlobalReadinessTooltip),
			nameof(IsProductionReady),
			nameof(PerformanceVerificationState),
			nameof(PerformanceVerificationDetail)
		})
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
		RaiseCommandState();
	}

	private static string FormatGlobalReadinessLabel(RuntimeReadinessSnapshot snapshot)
	{
		var state = snapshot.State switch
		{
			RuntimeReadinessState.Initializing => "INITIALIZING",
			RuntimeReadinessState.Ready => "PRODUCTION READY",
			RuntimeReadinessState.Degraded => "DEGRADED",
			RuntimeReadinessState.NotReady => "NOT READY",
			RuntimeReadinessState.Recovering => "RECOVERING",
			RuntimeReadinessState.Failed => "FAILED",
			_ => "UNKNOWN"
		};
		var reason = snapshot.Reasons.FirstOrDefault();
		return reason is null || snapshot.State == RuntimeReadinessState.Ready
			? state
			: $"{state} · {reason.Component}";
	}

	private static string ResolveGlobalReadinessSummary(RuntimeReadinessSnapshot snapshot) =>
		snapshot.Reasons.FirstOrDefault()?.Detail ??
		"All required Runtime readiness, health and performance evidence is qualified.";

	private static string FormatGlobalReadinessTooltip(RuntimeReadinessSnapshot snapshot)
	{
		var reasons = snapshot.Reasons.Count == 0
			? "No active readiness reasons."
			: string.Join(Environment.NewLine, snapshot.Reasons.Select(reason => $"{reason.Component}: {reason.Detail}"));
		return $"{reasons}{Environment.NewLine}Performance: {snapshot.Performance.State.ToString().ToUpperInvariant()} · {snapshot.Performance.Detail}";
	}

	private static string ResolveSourceName(OperatorStatusSnapshot snapshot, string sourceId) =>
		snapshot.Sources.FirstOrDefault(source => string.Equals(source.Id, sourceId, StringComparison.Ordinal))?.Name ?? sourceId;

	private void RaiseCommandState()
	{
		(SynchronizeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(SetPreviewCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ActivateSceneCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleTestPatternCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(CutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(DissolveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(LoadGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ApplyGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ApplyTextGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ClearGraphicsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ApplyAudioGainCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(ToggleAudioMuteCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(CycleAudioTestSignalCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(StartRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(StopRecordingCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(EnableAIShowcaseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
		(DisableAIShowcaseCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
		catch (OperationCanceledException)
		{
			// Cancellation is an expected UI/lifecycle outcome and must not escape async-void ICommand execution.
		}
		finally
		{
			_executing = false;
			RaiseCanExecuteChanged();
		}
	}

	public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
