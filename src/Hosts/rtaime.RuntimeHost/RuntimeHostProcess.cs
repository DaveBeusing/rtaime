// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

public enum RuntimeHostProcessState
{
	Created = 1,
	Starting = 2,
	Ready = 3,
	Degraded = 4,
	Draining = 5,
	Stopped = 6,
	Failed = 7
}

public enum RuntimeHostHealthState
{
	Unknown = 1,
	Healthy = 2,
	Degraded = 3,
	Unhealthy = 4,
	Stopped = 5
}

public enum RuntimeHostExitCode
{
	Success = 0,
	ConfigurationError = 2,
	StartupFailure = 3,
	ShutdownFailure = 4,
	UnexpectedFailure = 10
}

public enum RuntimeMediaIoMode
{
	Virtual = 1,
	Native = 2
}

public sealed record RuntimeHostLifecycleSnapshot(
	RuntimeHostProcessState State,
	RuntimeHostHealthState Health,
	string Detail,
	DateTimeOffset UpdatedAt);

public sealed record RuntimeHostProcessOptions(
	MediaSourceId SourceAId,
	MediaSourceId SourceBId,
	VideoFormat Format,
	string ListenEndpoint,
	TimeSpan ShutdownTimeout,
	RuntimeMediaIoMode MediaIoMode = RuntimeMediaIoMode.Virtual,
	bool RequireExternalReference = false)
{
	public string AIEndpoint { get; init; } = "rtaime.v1.ai.default";

	public static RuntimeHostProcessOptions Default => new(
		new MediaSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000a")),
		new MediaSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000b")),
		VideoFormat.Hd1080p50Rgba8,
		"rtaime.v1.runtime.default",
		TimeSpan.FromSeconds(10));

	public static RuntimeHostProcessOptions Load(
		IReadOnlyList<string> args,
		Func<string, string?>? environment = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		environment ??= Environment.GetEnvironmentVariable;
		var defaults = Default;

		return new RuntimeHostProcessOptions(
			new MediaSourceId(ParseIdentity(Get(args, environment, "source-a-id", "RTAIME_RUNTIME_SOURCE_A_ID", defaults.SourceAId.ToString()), "source-a-id")),
			new MediaSourceId(ParseIdentity(Get(args, environment, "source-b-id", "RTAIME_RUNTIME_SOURCE_B_ID", defaults.SourceBId.ToString()), "source-b-id")),
			ParseFormat(Get(args, environment, "format", "RTAIME_RUNTIME_FORMAT", "1080p50")),
			Get(args, environment, "listen-endpoint", "RTAIME_RUNTIME_ENDPOINT", defaults.ListenEndpoint),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "shutdown-timeout-ms", "RTAIME_RUNTIME_SHUTDOWN_TIMEOUT_MS", ((int)defaults.ShutdownTimeout.TotalMilliseconds).ToString()), "shutdown-timeout-ms")),
			ParseMediaIoMode(Get(args, environment, "media-io", "RTAIME_RUNTIME_MEDIA_IO", "virtual")),
			ParseBoolean(Get(args, environment, "require-external-reference", "RTAIME_RUNTIME_REQUIRE_EXTERNAL_REFERENCE", "false"), "require-external-reference"))
		{
			AIEndpoint = Get(args, environment, "ai-endpoint", "RTAIME_AI_ENDPOINT", defaults.AIEndpoint)
		};
	}

	public void Validate()
	{
		if (SourceAId.Value.IsEmpty || SourceBId.Value.IsEmpty)
			throw new ArgumentException("Runtime source identities must not be empty.");
		if (SourceAId == SourceBId)
			throw new ArgumentException("Runtime source identities must be distinct.");
		if (Format != VideoFormat.Hd1080p50Rgba8 && Format != VideoFormat.Hd1080p59_94Rgba8)
			throw new ArgumentException("RuntimeHost V1 supports only 1080p50 RGBA8 and 1080p59.94 RGBA8.", nameof(Format));
		if (string.IsNullOrWhiteSpace(ListenEndpoint))
			throw new ArgumentException("RuntimeHost listen endpoint is required.", nameof(ListenEndpoint));
		if (string.IsNullOrWhiteSpace(AIEndpoint))
			throw new ArgumentException("AIHost endpoint is required.", nameof(AIEndpoint));
		if (ShutdownTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
		if (!Enum.IsDefined(typeof(RuntimeMediaIoMode), MediaIoMode))
			throw new ArgumentOutOfRangeException(nameof(MediaIoMode));
		if (RequireExternalReference && MediaIoMode != RuntimeMediaIoMode.Native)
			throw new ArgumentException("External reference may be required only when native Media I/O is selected.", nameof(RequireExternalReference));
	}

	private static string Get(
		IReadOnlyList<string> args,
		Func<string, string?> environment,
		string key,
		string environmentName,
		string defaultValue)
	{
		var prefix = $"--{key}=";
		var commandLine = args.LastOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		if (commandLine is not null)
			return commandLine[prefix.Length..];

		var environmentValue = environment(environmentName);
		return string.IsNullOrWhiteSpace(environmentValue) ? defaultValue : environmentValue.Trim();
	}

	private static Identity ParseIdentity(string value, string key)
	{
		try
		{
			return Identity.Parse(value);
		}
		catch (Exception exception) when (exception is FormatException or ArgumentException)
		{
			throw new ArgumentException($"Configuration '{key}' must be a valid identity.", key, exception);
		}
	}

	private static VideoFormat ParseFormat(string value) => value.Trim().ToLowerInvariant() switch
	{
		"1080p50" or "hd1080p50" => VideoFormat.Hd1080p50Rgba8,
		"1080p59.94" or "1080p59_94" or "hd1080p59.94" => VideoFormat.Hd1080p59_94Rgba8,
		_ => throw new ArgumentException("Configuration 'format' must be '1080p50' or '1080p59.94'.", "format")
	};

	private static RuntimeMediaIoMode ParseMediaIoMode(string value) => value.Trim().ToLowerInvariant() switch
	{
		"virtual" => RuntimeMediaIoMode.Virtual,
		"native" => RuntimeMediaIoMode.Native,
		_ => throw new ArgumentException("Configuration 'media-io' must be 'virtual' or 'native'.", "media-io")
	};

	private static bool ParseBoolean(string value, string key)
	{
		if (!bool.TryParse(value, out var parsed))
			throw new ArgumentException($"Configuration '{key}' must be 'true' or 'false'.", key);
		return parsed;
	}

	private static int ParsePositiveInt(string value, string key)
	{
		if (!int.TryParse(value, out var parsed) || parsed <= 0)
			throw new ArgumentException($"Configuration '{key}' must be a positive integer.", key);
		return parsed;
	}
}

/// <summary>
/// Executable RuntimeHost composition root. It owns lifecycle only; committed runtime execution,
/// media processing, GPU processing, monitoring and recording remain implemented by dedicated runtime services.
/// </summary>
public sealed class RuntimeHostProcess
{
	private readonly object _gate = new();
	private readonly RuntimeHostProcessOptions _options;
	private readonly Func<IProgramRecordingWriter> _recordingWriterFactory;
	private readonly Func<RuntimeHostProcessOptions, IProgramRecordingWriter, V1RuntimeHostService> _runtimeFactory;
	private readonly Func<V1RuntimeHostService, RuntimeHostProcessOptions, RuntimeAIShowcaseService> _aiShowcaseFactory;
	private readonly Stopwatch _timingClock = Stopwatch.StartNew();
	private readonly RuntimeTimingQualificationProbe _timingProbe;
	private readonly RuntimeFrameDropCounter _frameDropCounter = new();
	private RuntimeHostLifecycleSnapshot _lifecycle = new(
		RuntimeHostProcessState.Created,
		RuntimeHostHealthState.Unknown,
		"Process has not started.",
		DateTimeOffset.UtcNow);
	private int _runStarted;
	private V1RuntimeHostService? _runtime;
	private LocalMediaDeckRuntimeService? _mediaDeck;
	private RuntimeMediaIoVerticalSlice? _mediaIo;
	private RuntimeAIShowcaseService? _aiShowcase;
	private RuntimeHostIpcServer? _ipcServer;
	private RuntimeHostMonitoringServer? _monitoringServer;
	private Task? _mediaLoop;
	private Task? _mediaDeckLoop;
	private bool _runtimeDisposed;
	private V1RuntimeHostSnapshot? _finalRuntimeSnapshot;

	public RuntimeHostProcess(
		RuntimeHostProcessOptions options,
		Func<IProgramRecordingWriter>? recordingWriterFactory = null,
		Func<RuntimeHostProcessOptions, IProgramRecordingWriter, V1RuntimeHostService>? runtimeFactory = null,
		Func<V1RuntimeHostService, RuntimeHostProcessOptions, RuntimeAIShowcaseService>? aiShowcaseFactory = null)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_recordingWriterFactory = recordingWriterFactory ?? (() => new ReferenceRecordingPayloadWriter(
			Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"rtaime",
				"recordings")));
		_runtimeFactory = runtimeFactory ?? ((processOptions, writer) => new V1RuntimeHostService(
			processOptions.SourceAId,
			processOptions.SourceBId,
			processOptions.Format,
			writer));
		_aiShowcaseFactory = aiShowcaseFactory ?? ((runtime, processOptions) => new RuntimeAIShowcaseService(
			runtime,
			new NamedPipeRuntimeAIHostTransport(processOptions.AIEndpoint)));

		var framePeriod = TimeSpan.FromSeconds(options.Format.FrameRate.Denominator / (double)options.Format.FrameRate.Numerator);
		_timingProbe = new RuntimeTimingQualificationProbe(new TimingQualificationThresholds(
			framePeriod,
			TimeSpan.FromTicks(Math.Max(1, framePeriod.Ticks / 4)),
			framePeriod,
			2048));
	}

	public RuntimeHostLifecycleSnapshot Lifecycle
	{
		get
		{
			lock (_gate)
				return _lifecycle;
		}
	}

	public V1RuntimeHostService? Runtime => _runtime;
	public LocalMediaDeckRuntimeService? MediaDeck => _mediaDeck;
	public RuntimeHostIpcServer? IpcServer => _ipcServer;
	public RuntimeHostMonitoringServer? MonitoringServer => _monitoringServer;
	public RuntimeAIShowcaseService? AIShowcase => _aiShowcase;
	public string MonitoringEndpoint => $"{_options.ListenEndpoint}.monitor";
	public bool RuntimeDisposed => _runtimeDisposed;
	public V1RuntimeHostSnapshot? FinalRuntimeSnapshot => _finalRuntimeSnapshot;
	public MediaIoVerticalSliceStatistics? MediaIoStatistics => _mediaIo?.Statistics;
	public MediaIoPortStatus? MediaIoInputAStatus => _mediaIo?.InputAStatus;
	public MediaIoPortStatus? MediaIoInputBStatus => _mediaIo?.InputBStatus;
	public MediaIoPortStatus? MediaIoProgramOutputStatus => _mediaIo?.ProgramOutputStatus;
	public TimingQualificationSnapshot TimingQualification => _timingProbe.Snapshot(_timingClock.Elapsed);

	public async Task<RuntimeHostExitCode> RunAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _runStarted, 1) != 0)
			throw new InvalidOperationException("A RuntimeHostProcess instance can be run only once.");

		Update(RuntimeHostProcessState.Starting, RuntimeHostHealthState.Unknown, "Composing RuntimeHost dependencies.");
		try
		{
			_options.Validate();
			var writer = _recordingWriterFactory()
				?? throw new InvalidOperationException("Recording writer factory returned null.");
			_runtime = _runtimeFactory(_options, writer)
				?? throw new InvalidOperationException("Runtime factory returned null.");
			_mediaDeck = new LocalMediaDeckRuntimeService(_options.Format);
			_aiShowcase = _aiShowcaseFactory(_runtime, _options)
				?? throw new InvalidOperationException("AI showcase factory returned null.");

			if (_options.MediaIoMode == RuntimeMediaIoMode.Native)
			{
				var adapter = new NativeMediaIoProviderAdapter();
				try
				{
					_mediaIo = new RuntimeMediaIoVerticalSlice(
						_runtime,
						adapter,
						_options.SourceAId,
						_options.SourceBId,
						_options.RequireExternalReference);
				}
				catch
				{
					await adapter.DisposeAsync().ConfigureAwait(false);
					throw;
				}
			}

			_ipcServer = new RuntimeHostIpcServer(_options.ListenEndpoint, () => _runtime, () => _mediaDeck, () => _aiShowcase);
			_monitoringServer = new RuntimeHostMonitoringServer(MonitoringEndpoint, _runtime.MonitoringHub);
			await _ipcServer.StartAsync(cancellationToken).ConfigureAwait(false);
			await _monitoringServer.StartAsync(cancellationToken).ConfigureAwait(false);
			_mediaLoop = RunMediaLoopAsync(_runtime, _mediaIo, _aiShowcase, cancellationToken);
			_mediaDeckLoop = RunMediaDeckLoopAsync(_runtime, _mediaDeck, cancellationToken);
		}
		catch (ArgumentException exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(RuntimeHostProcessState.Failed, RuntimeHostHealthState.Unhealthy, $"Configuration rejected: {exception.Message}");
			return RuntimeHostExitCode.ConfigurationError;
		}
		catch (Exception exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(RuntimeHostProcessState.Failed, RuntimeHostHealthState.Unhealthy, $"Startup failed: {exception.Message}");
			return RuntimeHostExitCode.StartupFailure;
		}

		Update(
			RuntimeHostProcessState.Ready,
			RuntimeHostHealthState.Healthy,
			$"RuntimeHost is ready on '{_options.ListenEndpoint}' with monitoring on '{MonitoringEndpoint}' and Media I/O mode '{_options.MediaIoMode}'.");

		try
		{
			await Task.WhenAll(
				_mediaLoop,
				_mediaDeckLoop ?? Task.CompletedTask).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Expected process stop signal.
		}
		catch (Exception exception)
		{
			Update(RuntimeHostProcessState.Failed, RuntimeHostHealthState.Unhealthy, $"Run loop failed: {exception.Message}");
			return RuntimeHostExitCode.UnexpectedFailure;
		}

		return await StopAsync().ConfigureAwait(false);
	}

	private async Task RunMediaLoopAsync(
		V1RuntimeHostService runtime,
		RuntimeMediaIoVerticalSlice? mediaIo,
		RuntimeAIShowcaseService aiShowcase,
		CancellationToken cancellationToken)
	{
		var framePeriod = TimeSpan.FromSeconds(runtime.Format.FrameRate.Denominator / (double)runtime.Format.FrameRate.Numerator);
		using var timer = new PeriodicTimer(framePeriod);
		while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
		{
			var boundaryObservedAt = _timingClock.Elapsed;
			mediaIo?.PumpInputs();
			if (!runtime.HasCommittedExecution) continue;

			var processingStartedAt = _timingClock.Elapsed;
			var boundary = runtime.ProcessNextBoundary();
			mediaIo?.SubmitProgram(boundary);
			aiShowcase.ObserveProgramBoundary(boundary.ProgramFrame);
			var processingDuration = _timingClock.Elapsed - processingStartedAt;
			var timing = _timingProbe.RecordBoundary(boundary.SequenceNumber, boundaryObservedAt, processingDuration);
			var mediaIoStatistics = mediaIo?.Statistics;
			var droppedFrames = _frameDropCounter.Observe(
				boundaryObservedAt,
				framePeriod,
				mediaIoStatistics?.OutputBackpressure ?? 0,
				mediaIoStatistics?.OutputRejected ?? 0);
			runtime.SetPerformanceObservations(processingDuration, droppedFrames);
			runtime.SetTimingHealth(MapTimingHealth(timing.State));
		}
	}

	private async Task RunMediaDeckLoopAsync(
		V1RuntimeHostService runtime,
		LocalMediaDeckRuntimeService mediaDeck,
		CancellationToken cancellationToken)
	{
		var framePeriod = TimeSpan.FromSeconds(
			_options.Format.FrameRate.Denominator /
			(double)_options.Format.FrameRate.Numerator);
		MediaSourceId? activeDeckSource = null;
		using var timer = new PeriodicTimer(framePeriod);
		while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
		{
			mediaDeck.ObserveProgramSource(runtime.CommittedProgramSourceId);
			var snapshot = mediaDeck.ProcessBoundary();
			if (snapshot.SourceId is not { } sourceId)
			{
				if (activeDeckSource is { } previous)
					runtime.ClearExternalAudioMeter(previous);
				activeDeckSource = null;
				continue;
			}

			if (activeDeckSource is { } previousSource && previousSource != sourceId)
				runtime.ClearExternalAudioMeter(previousSource);
			activeDeckSource = sourceId;

			var boundary = mediaDeck.LatestBoundary;
			if (boundary is { Succeeded: true, Video: not null } && !boundary.RgbaPixels.IsEmpty)
			{
				if (boundary.Video.Surface.Format == runtime.Format)
				{
					runtime.SetExternalInputContent(
						sourceId,
						new RgbaFrameBuffer(runtime.Format, boundary.RgbaPixels.Span),
						V1InputSignalState.Valid);
				}
				else
				{
					runtime.SetInputSignalState(sourceId, V1InputSignalState.Unstable);
				}
			}

			if (boundary is { Succeeded: true } && !boundary.AudioPayload.IsEmpty)
			{
				runtime.SetExternalAudioInput(sourceId, boundary.AudioPayload.Span, available: true);
			}
			else
			{
				var available = snapshot.State != MediaDeckState.Error;
				runtime.SetExternalAudioMeter(sourceId, 0, 0, available);
			}
		}
	}

	private async Task<RuntimeHostExitCode> StopAsync()
	{
		Update(RuntimeHostProcessState.Draining, RuntimeHostHealthState.Degraded, "Draining RuntimeHost IPC, monitoring and runtime resources.");
		using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
		try
		{
			if (_monitoringServer is not null)
				await _monitoringServer.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);

			if (_ipcServer is not null)
				await _ipcServer.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);

			if (_aiShowcase is not null)
				await _aiShowcase.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
			_aiShowcase = null;

			_mediaIo?.Dispose();
			_mediaIo = null;
			_mediaDeck?.Dispose();
			_mediaDeck = null;

			if (_runtime is not null)
			{
				await _runtime.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
				_runtimeDisposed = true;
				_finalRuntimeSnapshot = _runtime.Snapshot;
				if (_finalRuntimeSnapshot.ActiveGpuSurfaces != 0)
					throw new InvalidOperationException("RuntimeHost retained GPU surfaces after shutdown.");
			}

			Update(RuntimeHostProcessState.Stopped, RuntimeHostHealthState.Stopped, "RuntimeHost stopped cleanly and released IPC/monitoring/media/GPU/recording resources.");
			return RuntimeHostExitCode.Success;
		}
		catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
		{
			Update(RuntimeHostProcessState.Failed, RuntimeHostHealthState.Unhealthy, "RuntimeHost shutdown exceeded the configured timeout.");
			return RuntimeHostExitCode.ShutdownFailure;
		}
		catch (Exception exception)
		{
			Update(RuntimeHostProcessState.Failed, RuntimeHostHealthState.Unhealthy, $"RuntimeHost shutdown failed: {exception.Message}");
			return RuntimeHostExitCode.ShutdownFailure;
		}
	}

	private async Task CleanupStartupFailureAsync()
	{
		try
		{
			if (_monitoringServer is not null)
				await _monitoringServer.DisposeAsync().ConfigureAwait(false);
		}
		catch { }

		try
		{
			if (_ipcServer is not null)
				await _ipcServer.DisposeAsync().ConfigureAwait(false);
		}
		catch { }

		try
		{
			if (_aiShowcase is not null)
				await _aiShowcase.DisposeAsync().ConfigureAwait(false);
			_aiShowcase = null;
		}
		catch { }

		try
		{
			_mediaIo?.Dispose();
			_mediaIo = null;
		}
		catch { }

		try
		{
			_mediaDeck?.Dispose();
			_mediaDeck = null;
		}
		catch { }

		if (_runtime is null)
			return;

		try
		{
			await _runtime.DisposeAsync().ConfigureAwait(false);
			_runtimeDisposed = true;
			_finalRuntimeSnapshot = _runtime.Snapshot;
		}
		catch
		{
			// Preserve the original startup failure as the process outcome.
		}
	}

	private void Update(RuntimeHostProcessState state, RuntimeHostHealthState health, string detail)
	{
		lock (_gate)
			_lifecycle = new RuntimeHostLifecycleSnapshot(state, health, detail, DateTimeOffset.UtcNow);
	}

	private static V1TimingHealthState MapTimingHealth(TimingQualificationState state) => state switch
	{
		TimingQualificationState.Recovering => V1TimingHealthState.Recovering,
		TimingQualificationState.Healthy => V1TimingHealthState.Healthy,
		TimingQualificationState.Degraded => V1TimingHealthState.Degraded,
		TimingQualificationState.Unstable => V1TimingHealthState.Unstable,
		TimingQualificationState.Lost => V1TimingHealthState.Lost,
		_ => throw new InvalidOperationException($"Unsupported timing qualification state '{state}'.")
	};

	private sealed class NullProgramRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.CompletedTask;
		}

		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
