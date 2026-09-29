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
	private readonly Dictionary<string, BitmapSource> _sourceFrames = new(StringComparer.Ordinal);
	private Task? _reader;
	private Task? _watchdog;
	private DateTimeOffset _lastFrameAt;
	private ImageSource? _previewImage;
	private ImageSource? _programImage;
	private string _state = "WAITING";
	private string _detail = "Waiting for the independent RuntimeHost monitoring plane.";
	private string _previewFormat = "No Preview monitor frame received.";
	private string _programFormat = "No Program monitor frame received.";
	private string _sharedGpuMonitoringState = "Shared GPU monitoring capability has not been observed.";
	private FrameDiagnosticsSnapshot _previewDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
	private FrameDiagnosticsSnapshot _programDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
	private MediaScopeSnapshot? _programScopes;
	private DateTimeOffset _lastScopeAnalysisAt;
	private bool _scopesEnabled;
	private MediaCompareMode _compareMode;
	private ImageSource? _differenceImage;
	private string _comparisonDetail = "A/B comparison is off.";
	private MonitoringFrame? _latestPreviewFrame;

	public OperatorMonitoringViewModel(
		OperatorViewModel controlState,
		NamedPipeOperatorMonitoringTransport transport,
		SynchronizationContext? uiContext = null)
	{
		_controlState = controlState ?? throw new ArgumentNullException(nameof(controlState));
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();
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
			OnPropertyChanged(nameof(ProgramState));
		}
	}

	public string Detail { get => _detail; private set => Set(ref _detail, value); }
	public string PreviewFormat { get => _previewFormat; private set => Set(ref _previewFormat, value); }
	public string ProgramFormat { get => _programFormat; private set => Set(ref _programFormat, value); }
	public string SharedGpuMonitoringState { get => _sharedGpuMonitoringState; private set => Set(ref _sharedGpuMonitoringState, value); }
	public FrameDiagnosticsSnapshot PreviewDiagnostics { get => _previewDiagnostics; private set => Set(ref _previewDiagnostics, value); }
	public FrameDiagnosticsSnapshot ProgramDiagnostics { get => _programDiagnostics; private set => Set(ref _programDiagnostics, value); }
	public MediaScopeSnapshot? ProgramScopes { get => _programScopes; private set => Set(ref _programScopes, value); }
	public MediaCompareMode CompareMode { get => _compareMode; private set => Set(ref _compareMode, value); }
	public ImageSource? DifferenceImage { get => _differenceImage; private set => Set(ref _differenceImage, value); }
	public string ComparisonDetail { get => _comparisonDetail; private set => Set(ref _comparisonDetail, value); }
	public bool ScopesEnabled
	{
		get => _scopesEnabled;
		set
		{
			if (!Set(ref _scopesEnabled, value)) return;
			if (!value) ProgramScopes = null;
		}
	}
	public bool HasPreview => PreviewImage is not null;
	public bool HasProgram => ProgramImage is not null;
	public string PreviewState => ResolveViewerState(_controlState.PreviewViewerState, HasPreview);
	public string ProgramState => ResolveViewerState(_controlState.ProgramViewerState, HasProgram);

	public void Start()
	{
		if (_reader is not null) return;
		_reader = Task.Run(() => ReadAsync(_stop.Token));
		_watchdog = Task.Run(() => WatchdogAsync(_stop.Token));
	}

	public async ValueTask DisposeAsync()
	{
		_controlState.PropertyChanged -= ControlStatePropertyChanged;
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
			var descriptor = frame.Descriptor;
			var bitmap = frame.HasFallbackPayload ? CreateBitmap(frame) : null;
			if (frame.HasFallbackPayload &&
				descriptor.StreamKind == MonitoringStreamKind.Source &&
				string.Equals(descriptor.SourceId.ToString(), _controlState.PreviewSourceId, StringComparison.Ordinal))
			{
				_latestPreviewFrame = frame;
			}

			ImageSource? difference = null;
			string? comparisonDetail = null;
			if (frame.HasFallbackPayload &&
				CompareMode == MediaCompareMode.Difference &&
				descriptor.StreamKind == MonitoringStreamKind.Program)
			{
				var compatibility = MediaComparisonCompatibility.Evaluate(frame.Descriptor, _latestPreviewFrame?.Descriptor);
				comparisonDetail = compatibility.Detail;
				if (compatibility.IsCompatible && _latestPreviewFrame is { HasFallbackPayload: true })
				{
					var derived = new MonitoringFrame(frame.Descriptor, MediaDifference.CreateRgba(frame, _latestPreviewFrame));
					difference = CreateBitmap(derived);
				}
			}

			MediaScopeSnapshot? scopes = null;
			if (frame.HasFallbackPayload && ScopesEnabled && descriptor.StreamKind == MonitoringStreamKind.Program)
			{
				var now = DateTimeOffset.UtcNow;
				if (now - _lastScopeAnalysisAt >= TimeSpan.FromMilliseconds(200))
				{
					scopes = MediaScopeSnapshot.Analyze(frame, sampleStride: 2);
					_lastScopeAnalysisAt = now;
				}
			}

			lock (_gate) _lastFrameAt = DateTimeOffset.UtcNow;
			_uiContext.Post(_ =>
			{
				ApplySharedGpuMonitoringState(descriptor, frame.HasFallbackPayload);
				if (bitmap is not null)
					ApplyFrame(descriptor, bitmap, scopes);
				else if (descriptor.StreamKind == MonitoringStreamKind.Program)
				{
					State = "LIVE";
					ProgramFormat = $"{descriptor.Width}x{descriptor.Height} RGBA8 • GPU resource observation • sequence {descriptor.Timing.SequenceNumber}";
					ProgramDiagnostics = FrameDiagnosticsSnapshot.FromMonitoring(descriptor);
				}
				if (comparisonDetail is not null) ComparisonDetail = comparisonDetail;
				if (difference is not null) DifferenceImage = difference;
			}, null);
		}
	}

	private async Task WatchdogAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(500, cancellationToken).ConfigureAwait(false);
			DateTimeOffset last;
			lock (_gate) last = _lastFrameAt;
			if (last == default) continue;
			if (DateTimeOffset.UtcNow - last <= TimeSpan.FromSeconds(2)) continue;
			_uiContext.Post(_ =>
			{
				State = "STALE";
				Detail = "Monitoring frames are stale. Control and Program continuity remain independent.";
			}, null);
		}
	}

	private void ApplyFrame(MonitoringFrameDescriptor descriptor, BitmapSource bitmap, MediaScopeSnapshot? scopes)
	{
		State = "LIVE";
		Detail = $"Independent monitoring endpoint {_transport.Endpoint}; monitor loss does not block Program.";
		var format = $"{descriptor.Width}x{descriptor.Height} RGBA8 • {MonitoringDisplayTransform.Describe(descriptor.Color)} • sequence {descriptor.Timing.SequenceNumber}";
		if (descriptor.StreamKind == MonitoringStreamKind.Program)
		{
			ProgramImage = bitmap;
			ProgramFormat = format;
			ProgramDiagnostics = FrameDiagnosticsSnapshot.FromMonitoring(descriptor);
			if (scopes is not null) ProgramScopes = scopes;
			return;
		}

		var sourceId = descriptor.SourceId.ToString();
		_sourceFrames[sourceId] = bitmap;
		_controlState.ApplySourceThumbnail(sourceId, bitmap, format);
		if (string.Equals(sourceId, _controlState.PreviewSourceId, StringComparison.Ordinal))
		{
			PreviewImage = bitmap;
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
				"GPU resource available; CPU/WPF fallback remains active for the current presentation path.",
			MonitoringSharedResourceCapabilityState.Available when descriptor.HasSharedResource =>
				"GPU resource available; this observation is resource-only and awaits the provider-backed presentation adapter.",
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
		if (mode == MediaCompareMode.Off)
		{
			DifferenceImage = null;
			ComparisonDetail = "A/B comparison is off.";
		}
		else if (mode != MediaCompareMode.Difference)
		{
			DifferenceImage = null;
			ComparisonDetail = "A = Program, B = confirmed Preview monitoring frame.";
		}
		else
		{
			ComparisonDetail = "Waiting for semantically compatible Program and Preview frames.";
		}
	}

	private void ControlStatePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
	{
		if (string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.PreviewSourceId), StringComparison.Ordinal))
		{
			_uiContext.Post(_ => RefreshPreviewFromCache(), null);
			return;
		}

		if (string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.PreviewViewerState), StringComparison.Ordinal))
			_uiContext.Post(_ => OnPropertyChanged(nameof(PreviewState)), null);
		else if (string.Equals(eventArgs.PropertyName, nameof(OperatorViewModel.ProgramViewerState), StringComparison.Ordinal))
			_uiContext.Post(_ => OnPropertyChanged(nameof(ProgramState)), null);
	}

	private void RefreshPreviewFromCache()
	{
		if (_sourceFrames.TryGetValue(_controlState.PreviewSourceId, out var frame))
		{
			PreviewImage = frame;
			return;
		}

		PreviewImage = null;
		PreviewFormat = "No Preview monitor frame received for current source.";
		PreviewDiagnostics = FrameDiagnosticsSnapshot.Unavailable;
	}

	private string ResolveViewerState(string controlState, bool hasFrame)
	{
		if (controlState is "NO SIGNAL" or "SOURCE OFFLINE" or "DISCONNECTED")
			return controlState;
		if (string.Equals(State, "STALE", StringComparison.Ordinal) ||
			string.Equals(State, "DISCONNECTED", StringComparison.Ordinal))
			return "DISCONNECTED";
		if (string.Equals(controlState, "RECOVERING", StringComparison.Ordinal) || !hasFrame)
			return "RECOVERING";
		return "LIVE";
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
