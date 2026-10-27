// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using rtaime.Client;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed class OperatorMonitoringViewModel : INotifyPropertyChanged, IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly OperatorViewModel _controlState;
	private readonly NamedPipeOperatorMonitoringTransport _transport;
	private readonly SynchronizationContext _uiContext;
	private readonly CancellationTokenSource _stop = new();
	private const int MaximumCachedSources = 16;
	private readonly Dictionary<string, CachedSourceFrame> _sourceFrames = new(StringComparer.Ordinal);
	private readonly LatestMonitoringFrameDelivery _delivery;
	private readonly MonitoringViewerFreshness _previewFreshness = new();
	private readonly MonitoringViewerFreshness _programFreshness = new();
	private sealed record CachedSourceFrame(BitmapSource Bitmap, MonitoringFrameDescriptor Descriptor, long ConnectionEpoch, DateTimeOffset ReceivedAt);
	private long _previewRouteVersion;
	private long _programRouteVersion;
	private long _readerConnectionEpoch;
	private long _presentedConnectionEpoch;
	private int _disposed;
	private int _watchdogQueued;
	private Task? _reader;
	private Task? _watchdog;
	private DateTimeOffset _lastFrameAt;
	private ImageSource? _previewImage;
	private ImageSource? _programImage;
	private OperatorGpuMonitoringFrame? _previewGpuFrame;
	private OperatorGpuMonitoringFrame? _programGpuFrame;
	private string _state = "WAITING";
	private string _detail = "Waiting for the independent RuntimeHost monitoring plane.";
	private string _previewFormat = "No Preview monitor frame received.";
	private string _programFormat = "No Program monitor frame received.";
	private string _sharedGpuMonitoringState = "Shared GPU monitoring capability has not been observed.";
	private FrameDiagnosticsSnapshot _previewDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
	private FrameDiagnosticsSnapshot _programDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
	private MediaScopeSnapshot? _programScopes;
	private MediaScopeSnapshot? _previewScopes;
	private MonitoringStreamKind _activeScopeStream = MonitoringStreamKind.Program;
	private DateTimeOffset _lastScopeAnalysisAt;
	private bool _scopesEnabled;
	private string _scopeProcessingDetail = "Scopes are disabled.";
	private MediaCompareMode _compareMode;
	private ImageSource? _differenceImage;
	private string _comparisonDetail = "A/B comparison is off.";
	private bool _isGpuComparisonActive;
	private MonitoringFrame? _latestPreviewFrame;
	private MonitoringFrame? _latestProgramFrame;
	private OperatorGpuMonitoringFrame? _latestPreviewGpuFrame;
	private OperatorGpuMonitoringFrame? _latestProgramGpuFrame;
	private bool _gpuScopeAnalysisUnavailable;
	private bool _gpuComparisonUnavailable;

	public OperatorMonitoringViewModel(
		OperatorViewModel controlState,
		NamedPipeOperatorMonitoringTransport transport,
		SynchronizationContext? uiContext = null)
	{
		_controlState = controlState ?? throw new ArgumentNullException(nameof(controlState));
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
		_delivery = new LatestMonitoringFrameDelivery(
			work => _uiContext.Post(_ => work(), null),
			ApplyObservation);
		_previewFreshness.SelectSource(_controlState.PreviewSourceId, 0, 0);
		_programFreshness.SelectSource(_controlState.ProgramSourceId, 0, 0);
		_controlState.PropertyChanged += ControlStatePropertyChanged;
		ToggleScopesCommand = new OperatorShellCommand(() => ScopesEnabled = !ScopesEnabled);
		SetCompareModeCommand = new OperatorShellCommand(parameter => SetCompareMode(parameter?.ToString()));
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public ICommand ToggleScopesCommand { get; }
	public ICommand SetCompareModeCommand { get; }

	public ImageSource? PreviewImage
	{
		get => _previewImage;
		private set
		{
			if (!Set(ref _previewImage, value))
				return;
			OnPropertyChanged(nameof(HasPreview));
			OnPropertyChanged(nameof(PreviewState));
			OnPropertyChanged(nameof(PreviewFrameAge));
		}
	}

	public ImageSource? ProgramImage
	{
		get => _programImage;
		private set
		{
			if (!Set(ref _programImage, value))
				return;
			OnPropertyChanged(nameof(HasProgram));
			OnPropertyChanged(nameof(ProgramState));
			OnPropertyChanged(nameof(ProgramFrameAge));
		}
	}

	public OperatorGpuMonitoringFrame? PreviewGpuFrame
	{
		get => _previewGpuFrame;
		private set
		{
			if (!Set(ref _previewGpuFrame, value)) return;
			OnPropertyChanged(nameof(HasPreview));
			OnPropertyChanged(nameof(PreviewState));
			OnPropertyChanged(nameof(PreviewFrameAge));
		}
	}

	public OperatorGpuMonitoringFrame? ProgramGpuFrame
	{
		get => _programGpuFrame;
		private set
		{
			if (!Set(ref _programGpuFrame, value)) return;
			OnPropertyChanged(nameof(HasProgram));
			OnPropertyChanged(nameof(ProgramState));
			OnPropertyChanged(nameof(ProgramFrameAge));
		}
	}

	public string State
	{
		get => _state;
		private set
		{
			if (!Set(ref _state, value))
				return;
			OnPropertyChanged(nameof(PreviewState));
			OnPropertyChanged(nameof(PreviewFrameAge));
			OnPropertyChanged(nameof(ProgramState));
			OnPropertyChanged(nameof(ProgramFrameAge));
		}
	}

	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public string PreviewFormat { get => _previewFormat; private set => Set(ref _previewFormat, value); }
	public string ProgramFormat { get => _programFormat; private set => Set(ref _programFormat, value); }
	public string SharedGpuMonitoringState { get => _sharedGpuMonitoringState; private set => Set(ref _sharedGpuMonitoringState, value); }
	public FrameDiagnosticsSnapshot PreviewDiagnostics { get => _previewDiagnostics; private set => Set(ref _previewDiagnostics, value); }
	public FrameDiagnosticsSnapshot ProgramDiagnostics { get => _programDiagnostics; private set => Set(ref _programDiagnostics, value); }
	public MediaScopeSnapshot? ProgramScopes { get => _programScopes; private set => Set(ref _programScopes, value); }
	public MediaScopeSnapshot? PreviewScopes { get => _previewScopes; private set => Set(ref _previewScopes, value); }
	/// <summary>Only one selected monitoring source is analyzed at a time.</summary>
	public MonitoringStreamKind ActiveScopeStream
	{
		get => _activeScopeStream;
		set
		{
			if (value is not (MonitoringStreamKind.Program or MonitoringStreamKind.Source) ||
			    !Set(ref _activeScopeStream, value)) return;
			_programScopes = null; _previewScopes = null;
			OnPropertyChanged(nameof(ProgramScopes)); OnPropertyChanged(nameof(PreviewScopes));
			_lastScopeAnalysisAt = default;
			Volatile.Write(ref _gpuScopeAnalysisUnavailable, false);
			ScopeProcessingDetail = "Waiting for current selected source and bounded scopes.";
		}
	}
	public string ScopeProcessingDetail { get => _scopeProcessingDetail; private set => Set(ref _scopeProcessingDetail, value); }
	public MediaCompareMode CompareMode { get => _compareMode; private set => Set(ref _compareMode, value); }
	public ImageSource? DifferenceImage { get => _differenceImage; private set => Set(ref _differenceImage, value); }
	public string ComparisonDetail { get => _comparisonDetail; private set => Set(ref _comparisonDetail, value); }
	public bool IsGpuComparisonActive { get => _isGpuComparisonActive; private set => Set(ref _isGpuComparisonActive, value); }
	public bool ScopesEnabled
	{
		get => _scopesEnabled;
		set
		{
			if (!Set(ref _scopesEnabled, value)) return;
			if (!value)
			{
				ProgramScopes = null;
				PreviewScopes = null;
				ScopeProcessingDetail = "Scopes are disabled.";
				return;
			}
			Volatile.Write(ref _gpuScopeAnalysisUnavailable, false);
			ScopeProcessingDetail = "Waiting for bounded GPU scope analysis or explicit CPU fallback.";
		}
	}
	public bool HasPreview => PreviewGpuFrame is not null || PreviewImage is not null;
	public bool HasProgram => ProgramGpuFrame is not null || ProgramImage is not null;
	public string PreviewState => ResolveViewerState(_controlState.PreviewViewerState, _previewFreshness.Status(HasPreview, DateTimeOffset.UtcNow));
	public string ProgramState => ResolveViewerState(_controlState.ProgramViewerState, _programFreshness.Status(HasProgram, DateTimeOffset.UtcNow));
	public string PreviewFrameAge => _previewFreshness.FrameAgeEvidence(HasPreview, DateTimeOffset.UtcNow);
	public string ProgramFrameAge => _programFreshness.FrameAgeEvidence(HasProgram, DateTimeOffset.UtcNow);

	public void Start()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (_reader is not null) return;
		_reader = Task.Run(() => ReadAsync(_stop.Token));
		_watchdog = Task.Run(() => WatchdogAsync(_stop.Token));
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_controlState.PropertyChanged -= ControlStatePropertyChanged;
		_delivery.Dispose();
		_stop.Cancel();
		if (_reader is not null)
		{
			try { await _reader.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		if (_watchdog is not null)
		{
			try { await _watchdog.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}
		_stop.Dispose();
	}

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		await foreach (var frame in _transport.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
		{
			if (Volatile.Read(ref _disposed) != 0) break;
			var receivedAt = DateTimeOffset.UtcNow;
			var connectionEpoch = _transport.ConnectionEpoch;
			var previousEpoch = Interlocked.Exchange(ref _readerConnectionEpoch, connectionEpoch);
			if (previousEpoch != 0 && previousEpoch != connectionEpoch)
				_delivery.Invalidate();
			lock (_gate) _lastFrameAt = receivedAt;
			_delivery.Enqueue(new MonitoringObservation(
				frame, connectionEpoch,
				Volatile.Read(ref _previewRouteVersion),
				Volatile.Read(ref _programRouteVersion),
				receivedAt));
		}
	}

	// All UI mutation and optional bitmap/scope/compare conversion happens after
	// coalescing, never on the transport reader or the Runtime Program path.
	private void ApplyObservation(MonitoringObservation observation)
	{
		if (Volatile.Read(ref _disposed) != 0 || observation.ConnectionEpoch < _presentedConnectionEpoch)
			return;

		if (observation.ConnectionEpoch != _presentedConnectionEpoch)
			ResetForConnectionEpoch(observation.ConnectionEpoch);

		var frame = observation.Frame;
		var descriptor = frame.Descriptor;
		var sourceId = descriptor.SourceId.ToString();
		var isProgram = descriptor.StreamKind == MonitoringStreamKind.Program;
		var confirmedPreview = !isProgram &&
			string.Equals(sourceId, _controlState.PreviewSourceId, StringComparison.Ordinal);
		var gpuFrame = descriptor.SharedResource is { Interop.IsPresentable: true }
			? new OperatorGpuMonitoringFrame(descriptor)
			: null;

		if (isProgram)
		{
			if (!string.Equals(sourceId, _controlState.ProgramSourceId, StringComparison.Ordinal) ||
				(gpuFrame is not null && !gpuFrame.IsNewerThan(ProgramGpuFrame)))
				return;
			if (!_programFreshness.Observe(sourceId, observation.ProgramRouteVersion,
					observation.ConnectionEpoch, observation.ReceivedAt, descriptor.Timing.SequenceNumber,
					descriptor.SharedResource?.ProviderInstanceId.ToString(),
					descriptor.SharedResource?.Lifetime.Generation.Value))
				return;

			if (_latestProgramGpuFrame?.Resource.ResourceId != gpuFrame?.Resource.ResourceId)
			{
				Volatile.Write(ref _gpuScopeAnalysisUnavailable, false);
				Volatile.Write(ref _gpuComparisonUnavailable, false);
				ProgramScopes = null;
				ProgramImage = null;
			}
			_latestProgramGpuFrame = gpuFrame;
			_latestProgramFrame = frame.HasFallbackPayload ? frame : null;
		}
		else if (confirmedPreview)
		{
			confirmedPreview = gpuFrame is null || gpuFrame.IsNewerThan(PreviewGpuFrame);
			confirmedPreview = confirmedPreview && _previewFreshness.Observe(
				sourceId, observation.PreviewRouteVersion, observation.ConnectionEpoch,
				observation.ReceivedAt, descriptor.Timing.SequenceNumber,
				descriptor.SharedResource?.ProviderInstanceId.ToString(),
					descriptor.SharedResource?.Lifetime.Generation.Value);
			if (confirmedPreview)
			{
				if (_latestPreviewGpuFrame?.Resource.ResourceId != gpuFrame?.Resource.ResourceId)
				{
					PreviewScopes = null;
					Volatile.Write(ref _gpuScopeAnalysisUnavailable, false);
					Volatile.Write(ref _gpuComparisonUnavailable, false);
					PreviewImage = null;
					DifferenceImage = null;
				}
				_latestPreviewGpuFrame = gpuFrame;
				_latestPreviewFrame = frame.HasFallbackPayload ? frame : null;
			}
		}

		// Source thumbnails can still be cached for future routing. They must not
		// confer LIVE status on the selected viewer until post-selection evidence.
		var bitmap = frame.HasFallbackPayload ? CreateBitmap(frame) : null;
		State = "LIVE";
		Detail = $"Independent monitoring endpoint {_transport.Endpoint}; monitor loss does not block Program.";
		ApplySharedGpuMonitoringState(descriptor, frame.HasFallbackPayload);
		if (isProgram || confirmedPreview)
		{
			ApplyGpuFrame(descriptor, gpuFrame);
			var format = $"{descriptor.Width}x{descriptor.Height} RGBA8 • {MonitoringDisplayTransform.Describe(descriptor.Color)} • sequence {descriptor.Timing.SequenceNumber}";
			if (isProgram)
			{
				if (bitmap is null) ProgramImage = null;
				ProgramFormat = format;
				ProgramDiagnostics = FrameDiagnosticsSnapshot.FromMonitoring(descriptor);
			}
			else
			{
				if (bitmap is null) PreviewImage = null;
				PreviewFormat = format;
				PreviewDiagnostics = FrameDiagnosticsSnapshot.FromMonitoring(descriptor);
			}
		}

		MediaScopeSnapshot? scopes = null;
		var cpuScopesRequired = !descriptor.HasSharedResource || Volatile.Read(ref _gpuScopeAnalysisUnavailable);
		if ((isProgram || confirmedPreview) && frame.HasFallbackPayload && ScopesEnabled &&
			descriptor.StreamKind == ActiveScopeStream && cpuScopesRequired &&
			observation.ReceivedAt - _lastScopeAnalysisAt >= GpuMonitoringAnalysisPolicy.ScopeUpdateInterval)
		{
			scopes = MediaScopeSnapshot.Analyze(frame, sampleStride: GpuMonitoringAnalysisPolicy.MinimumScopeSampleStride);
			_lastScopeAnalysisAt = observation.ReceivedAt;
		}

		if (bitmap is not null)
			ApplyFrame(descriptor, bitmap, scopes, confirmedPreview, observation.ConnectionEpoch, observation.ReceivedAt);

		if (isProgram && CompareMode == MediaCompareMode.Difference)
			UpdateDifferenceForProgram(frame, gpuFrame);
		OnPropertyChanged(nameof(PreviewState));
			OnPropertyChanged(nameof(PreviewFrameAge));
		OnPropertyChanged(nameof(ProgramState));
			OnPropertyChanged(nameof(ProgramFrameAge));
	}

	private void UpdateDifferenceForProgram(MonitoringFrame frame, OperatorGpuMonitoringFrame? gpuFrame)
	{
		if (ProgramState != "LIVE" || PreviewState != "LIVE")
		{
			DifferenceImage = null;
			IsGpuComparisonActive = false;
			ComparisonDetail = "Waiting for fresh Program and confirmed Preview observations.";
			return;
		}

		var gpuCompatibility = MediaComparisonCompatibility.Evaluate(gpuFrame, _latestPreviewGpuFrame);
		if (gpuCompatibility.IsCompatible && !Volatile.Read(ref _gpuComparisonUnavailable))
		{
			DifferenceImage = null;
			ComparisonDetail = "GPU Difference selected · Program=A · confirmed Preview=B.";
		}
		else if (frame.HasFallbackPayload && _latestPreviewFrame is { HasFallbackPayload: true } previewFallback)
		{
			var cpuCompatibility = MediaComparisonCompatibility.Evaluate(frame.Descriptor, previewFallback.Descriptor);
			ComparisonDetail = cpuCompatibility.IsCompatible
				? $"CPU fallback Difference · {gpuCompatibility.Detail}"
				: cpuCompatibility.Detail;
			DifferenceImage = cpuCompatibility.IsCompatible
				? CreateDisplayDifferenceBitmap(frame, previewFallback)
				: null;
		}
		else
		{
			DifferenceImage = null;
			ComparisonDetail = gpuCompatibility.Detail;
		}
	}

	private async Task WatchdogAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(500, cancellationToken).ConfigureAwait(false);
			// The watchdog also coalesces so blocked Dispatchers cannot accumulate ticks.
			if (Interlocked.CompareExchange(ref _watchdogQueued, 1, 0) != 0)
				continue;
			_uiContext.Post(_ =>
			{
				Interlocked.Exchange(ref _watchdogQueued, 0);
				if (Volatile.Read(ref _disposed) != 0) return;
				DateTimeOffset last;
				lock (_gate) last = _lastFrameAt;
				if (last != default && DateTimeOffset.UtcNow - last > MonitoringViewerFreshness.Budget)
				{
					State = "STALE";
					Detail = "Monitoring frames are stale. Control and Program continuity remain independent.";
				}
				if (CompareMode != MediaCompareMode.Off && (ProgramState != "LIVE" || PreviewState != "LIVE"))
				{
					DifferenceImage = null;
					IsGpuComparisonActive = false;
					ComparisonDetail = "Waiting for fresh Program and confirmed Preview observations.";
				}
				if (ProgramState != "LIVE")
					ProgramScopes = null;
				if (PreviewState != "LIVE")
					PreviewScopes = null;
				OnPropertyChanged(nameof(PreviewState));
			OnPropertyChanged(nameof(PreviewFrameAge));
				OnPropertyChanged(nameof(ProgramState));
			OnPropertyChanged(nameof(ProgramFrameAge));
			}, null);
		}
	}

	private void ResetForConnectionEpoch(long connectionEpoch)
	{
		_presentedConnectionEpoch = connectionEpoch;
		_sourceFrames.Clear();
		_previewFreshness.SelectSource(_controlState.PreviewSourceId, Volatile.Read(ref _previewRouteVersion), connectionEpoch);
		_programFreshness.SelectSource(_controlState.ProgramSourceId, Volatile.Read(ref _programRouteVersion), connectionEpoch);
		ClearPreviewPresentation();
		ClearProgramPresentation();
		State = "WAITING";
		Detail = "Monitoring transport reconnected; waiting for matching Preview and Program observations.";
	}

	private void ClearPreviewPresentation()
	{
		PreviewScopes = null;
		PreviewGpuFrame = null;
		PreviewImage = null;
		PreviewFormat = "Current Preview monitoring is unverified.";
		PreviewDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
		_latestPreviewFrame = null;
		_latestPreviewGpuFrame = null;
		DifferenceImage = null;
		IsGpuComparisonActive = false;
		ComparisonDetail = "Waiting for current Preview observations.";
		Volatile.Write(ref _gpuComparisonUnavailable, false);
		OnPropertyChanged(nameof(PreviewState));
			OnPropertyChanged(nameof(PreviewFrameAge));
	}

	private void ClearProgramPresentation()
	{
		ProgramGpuFrame = null;
		ProgramImage = null;
		ProgramFormat = "Current Program monitoring is unverified.";
		ProgramDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
		ProgramScopes = null;
		_latestProgramFrame = null;
		_latestProgramGpuFrame = null;
		DifferenceImage = null;
		IsGpuComparisonActive = false;
		Volatile.Write(ref _gpuScopeAnalysisUnavailable, false);
		Volatile.Write(ref _gpuComparisonUnavailable, false);
		OnPropertyChanged(nameof(ProgramState));
			OnPropertyChanged(nameof(ProgramFrameAge));
	}

	private void ApplyGpuFrame(MonitoringFrameDescriptor descriptor, OperatorGpuMonitoringFrame? gpuFrame)
	{
		if (descriptor.StreamKind == MonitoringStreamKind.Program)
		{
			if (gpuFrame is null || gpuFrame.IsNewerThan(ProgramGpuFrame))
				ProgramGpuFrame = gpuFrame;
			return;
		}

		if (!string.Equals(descriptor.SourceId.ToString(), _controlState.PreviewSourceId, StringComparison.Ordinal))
			return;
		if (gpuFrame is null || gpuFrame.IsNewerThan(PreviewGpuFrame))
			PreviewGpuFrame = gpuFrame;
	}

	private void ApplyFrame(
		MonitoringFrameDescriptor descriptor, BitmapSource bitmap, MediaScopeSnapshot? scopes,
		bool confirmedPreview, long connectionEpoch, DateTimeOffset receivedAt)
	{
		var format = $"{descriptor.Width}x{descriptor.Height} RGBA8 • {MonitoringDisplayTransform.Describe(descriptor.Color)} • sequence {descriptor.Timing.SequenceNumber}";
		if (descriptor.StreamKind == MonitoringStreamKind.Program)
		{
			ProgramImage = bitmap;
			ProgramFormat = format;
			ProgramDiagnostics = FrameDiagnosticsSnapshot.FromMonitoring(descriptor);
			if (scopes is not null)
			{
				ProgramScopes = scopes;
				ScopeProcessingDetail = FormatScopeProcessingDetail(scopes);
			}
			return;
		}

		var sourceId = descriptor.SourceId.ToString();
		if (!_sourceFrames.TryGetValue(sourceId, out var prior) ||
			prior.ConnectionEpoch != connectionEpoch ||
			descriptor.Timing.SequenceNumber > prior.Descriptor.Timing.SequenceNumber)
		{
			if (!_sourceFrames.ContainsKey(sourceId) && _sourceFrames.Count == MaximumCachedSources)
			{
				var oldest = _sourceFrames.MinBy(entry => entry.Value.ReceivedAt).Key;
				_sourceFrames.Remove(oldest);
			}
			_sourceFrames[sourceId] = new CachedSourceFrame(bitmap, descriptor, connectionEpoch, receivedAt);
			_controlState.ApplySourceThumbnail(sourceId, bitmap, format);
		}

		if (confirmedPreview)
		{
			PreviewImage = bitmap;
			if (scopes is not null)
			{
				PreviewScopes = scopes;
				ScopeProcessingDetail = FormatScopeProcessingDetail(scopes);
			}
			PreviewFormat = format;
			PreviewDiagnostics = FrameDiagnosticsSnapshot.FromMonitoring(descriptor);
		}
	}

	private void ApplySharedGpuMonitoringState(MonitoringFrameDescriptor descriptor, bool hasFallbackPayload)
	{
		if (descriptor.StreamKind != MonitoringStreamKind.Program)
			return;

		SharedGpuMonitoringState = descriptor.SharedResourceCapability switch
		{
			MonitoringSharedResourceCapabilityState.Available when descriptor.HasSharedResource && hasFallbackPayload =>
				"GPU resource available; D3D11 presentation is preferred with CPU/WPF fallback retained.",
			MonitoringSharedResourceCapabilityState.Available when descriptor.HasSharedResource =>
				"GPU resource available; provider-backed D3D11 presentation is active when adapter/resource validation succeeds.",
			MonitoringSharedResourceCapabilityState.Available =>
				"GPU resource capability is available, but the current sampled frame has no active shared resource; fallback remains deterministic.",
			MonitoringSharedResourceCapabilityState.Degraded =>
				"Shared GPU monitoring is degraded; CPU/WPF fallback remains active.",
			_ =>
				"Shared GPU monitoring is unavailable; CPU/WPF fallback remains active."
		};
	}

	private void SetCompareMode(string? value)
	{
		if (!Enum.TryParse<MediaCompareMode>(value, ignoreCase: true, out var mode))
			mode = MediaCompareMode.Off;
		CompareMode = mode;
		Volatile.Write(ref _gpuComparisonUnavailable, false);
		IsGpuComparisonActive = false;
		if (mode == MediaCompareMode.Off)
		{
			DifferenceImage = null;
			ComparisonDetail = "A/B comparison is off.";
		}
		else if (mode != MediaCompareMode.Difference)
		{
			DifferenceImage = null;
			ComparisonDetail = "A = Program, B = confirmed Preview monitoring frame; GPU presentation is preferred when compatible.";
		}
		else
		{
			ComparisonDetail = "Waiting for semantically compatible Program and Preview frames.";
		}
	}

	private void ControlStatePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		if (string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.PreviewSourceId), StringComparison.Ordinal))
		{
			var version = Interlocked.Increment(ref _previewRouteVersion);
			var sourceId = _controlState.PreviewSourceId;
			DispatchRouteChange(() =>
			{
				if (version != Volatile.Read(ref _previewRouteVersion)) return;
				_previewFreshness.SelectSource(sourceId, version, _presentedConnectionEpoch);
				ClearPreviewPresentation();
				RefreshPreviewFromCache();
			});
			return;
		}
		if (string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.ProgramSourceId), StringComparison.Ordinal))
		{
			var version = Interlocked.Increment(ref _programRouteVersion);
			var sourceId = _controlState.ProgramSourceId;
			DispatchRouteChange(() =>
			{
				if (version != Volatile.Read(ref _programRouteVersion)) return;
				_programFreshness.SelectSource(sourceId, version, _presentedConnectionEpoch);
				ClearProgramPresentation();
			});
			return;
		}

		if (string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.PreviewViewerState), StringComparison.Ordinal))
			DispatchRouteChange(() => OnPropertyChanged(nameof(PreviewState)));
		else if (string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.ProgramViewerState), StringComparison.Ordinal))
			DispatchRouteChange(() => OnPropertyChanged(nameof(ProgramState)));
	}

	private void DispatchRouteChange(Action change)
	{
		void Apply()
		{
			if (Volatile.Read(ref _disposed) == 0)
				change();
		}
		if (ReferenceEquals(SynchronizationContext.Current, _uiContext))
			Apply();
		else
			_uiContext.Post(_ => Apply(), null);
	}

	internal void ApplyGpuScopes(MediaScopeSnapshot snapshot,
		OperatorGpuMonitoringFrame origin, bool preview = false)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(origin);
		var stream = preview ? MonitoringStreamKind.Source : MonitoringStreamKind.Program;
		var current = preview ? PreviewGpuFrame : ProgramGpuFrame;
		if (!ScopesEnabled || ActiveScopeStream != stream || current is null ||
			(preview ? PreviewState : ProgramState) != "LIVE" ||
			origin.Descriptor.StreamKind != stream ||
			origin.Descriptor.SourceId != current.Descriptor.SourceId ||
			origin.Resource.ResourceId != current.Resource.ResourceId ||
			origin.Resource.ProviderInstanceId != current.Resource.ProviderInstanceId ||
			origin.Resource.Lifetime.Generation != current.Resource.Lifetime.Generation ||
			origin.SequenceNumber != current.SequenceNumber ||
			snapshot.ProcessingPath != MediaScopeProcessingPath.GpuSharedResource ||
			snapshot.SequenceNumber != origin.SequenceNumber ||
			snapshot.SourceWidth != origin.Resource.Format.Width ||
			snapshot.SourceHeight != origin.Resource.Format.Height ||
			snapshot.Color != origin.Resource.Format.Color)
			return;

		Volatile.Write(ref _gpuScopeAnalysisUnavailable, false);
		if (preview) PreviewScopes = snapshot;
		else ProgramScopes = snapshot;
		ScopeProcessingDetail = FormatScopeProcessingDetail(snapshot);
	}

	internal void SetGpuScopesUnavailable(string detail, bool preview = false)
	{
		if (!ScopesEnabled || ActiveScopeStream !=
			(preview ? MonitoringStreamKind.Source : MonitoringStreamKind.Program))
			return;
		Volatile.Write(ref _gpuScopeAnalysisUnavailable, true);
		ScopeProcessingDetail = $"CPU fallback eligible · {detail}";
	}

	internal void SetGpuComparisonState(bool active, string detail)
	{
		if (CompareMode == MediaCompareMode.Off || ProgramState != "LIVE" || PreviewState != "LIVE")
		{
			IsGpuComparisonActive = false;
			return;
		}

		var wasUnavailable = Volatile.Read(ref _gpuComparisonUnavailable);
		IsGpuComparisonActive = active;
		Volatile.Write(ref _gpuComparisonUnavailable, !active);
		ComparisonDetail = detail;
		if (active)
		{
			DifferenceImage = null;
			return;
		}

		if (CompareMode == MediaCompareMode.Difference && (!wasUnavailable || DifferenceImage is null))
			RefreshCpuDifferenceFallback();
	}

	private void RefreshCpuDifferenceFallback()
	{
		if (ProgramState != "LIVE" || PreviewState != "LIVE")
		{
			DifferenceImage = null;
			return;
		}
		if (_latestProgramFrame is not { HasFallbackPayload: true } program ||
			_latestPreviewFrame is not { HasFallbackPayload: true } preview)
			return;

		var compatibility = MediaComparisonCompatibility.Evaluate(program.Descriptor, preview.Descriptor);
		if (!compatibility.IsCompatible)
		{
			DifferenceImage = null;
			ComparisonDetail = compatibility.Detail;
			return;
		}

		DifferenceImage = CreateDisplayDifferenceBitmap(program, preview);
		ComparisonDetail = "CPU fallback Difference · GPU compare path unavailable.";
	}

	private static string FormatScopeProcessingDetail(MediaScopeSnapshot snapshot)
	{
		var duration = snapshot.AnalysisDuration.TotalMilliseconds;
		return $"{snapshot.ProcessingPath} · ≤5 Hz · stride {snapshot.SampleStride} · {snapshot.SampleCount:N0} samples · {duration:0.###} ms · {snapshot.ResultTransferBytes:N0} B transfer · {snapshot.ManagedAllocationBytes:N0} B managed alloc · {snapshot.Detail}";
	}

	private void RefreshPreviewFromCache()
	{
		// Always invalidate the old GPU surface and associated metadata first.
		// Cached source B is retained evidence, never proof that B is now LIVE.
		PreviewGpuFrame = null;
		PreviewImage = null;
		PreviewDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
		PreviewFormat = "No Preview monitor frame received for current source.";
		if (_sourceFrames.TryGetValue(_controlState.PreviewSourceId, out var cached) &&
			cached.ConnectionEpoch == _presentedConnectionEpoch)
		{
			PreviewImage = cached.Bitmap;
			PreviewFormat = $"{cached.Descriptor.Width}x{cached.Descriptor.Height} RGBA8 • retained source frame; waiting for current routing evidence.";
			PreviewDiagnostics = FrameDiagnosticsSnapshot.FromMonitoring(cached.Descriptor);
		}
		OnPropertyChanged(nameof(PreviewState));
			OnPropertyChanged(nameof(PreviewFrameAge));
	}

	private static string ResolveViewerState(string controlState, string monitoringState)
	{
		if (controlState is "NO SIGNAL" or "SOURCE OFFLINE" or "DISCONNECTED")
			return controlState;
		if (string.Equals(controlState, "RECOVERING", StringComparison.Ordinal))
			return "RECOVERING";
		return monitoringState;
	}

	private static BitmapSource CreateDisplayDifferenceBitmap(MonitoringFrame program, MonitoringFrame preview)
	{
		var rgba = MediaDifference.CreateDisplayRgba(program, preview);
		var bgra = new byte[rgba.Length];
		for (var offset = 0; offset < rgba.Length; offset += 4)
		{
			bgra[offset] = rgba[offset + 2];
			bgra[offset + 1] = rgba[offset + 1];
			bgra[offset + 2] = rgba[offset];
			bgra[offset + 3] = rgba[offset + 3];
		}

		var width = checked((int)program.Descriptor.Width);
		var height = checked((int)program.Descriptor.Height);
		var bitmap = BitmapSource.Create(
			width,
			height,
			96,
			96,
			PixelFormats.Bgra32,
			null,
			bgra,
			checked(width * 4));
		bitmap.Freeze();
		return bitmap;
	}

	private static BitmapSource CreateBitmap(MonitoringFrame frame)
	{
		var descriptor = frame.Descriptor;
		var rgba = frame.Pixels.Span;
		var bgra = new byte[rgba.Length];
		MonitoringDisplayTransform.ConvertRgbaToBgra(rgba, bgra, descriptor.Color);

		var width = checked((int)descriptor.Width);
		var height = checked((int)descriptor.Height);
		var bitmap = BitmapSource.Create(
			width,
			height,
			96,
			96,
			PixelFormats.Bgra32,
			null,
			bgra,
			checked(width * 4));
		bitmap.Freeze();
		return bitmap;
	}

	private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return false;
		field = value;
		OnPropertyChanged(propertyName);
		return true;
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
