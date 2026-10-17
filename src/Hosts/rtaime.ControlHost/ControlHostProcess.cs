// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Globalization;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public enum ControlHostProcessState
{
	Created = 1,
	Starting = 2,
	Ready = 3,
	Degraded = 4,
	Draining = 5,
	Stopped = 6,
	Failed = 7
}

public enum ControlHostHealthState
{
	Unknown = 1,
	Healthy = 2,
	Degraded = 3,
	Unhealthy = 4,
	Stopped = 5
}

public enum ControlHostExitCode
{
	Success = 0,
	ConfigurationError = 2,
	StartupFailure = 3,
	ShutdownFailure = 4,
	UnexpectedFailure = 10
}

public sealed record ControlHostLifecycleSnapshot(
	ControlHostProcessState State,
	ControlHostHealthState Health,
	string Detail,
	DateTimeOffset UpdatedAt);

/// <summary>
/// Production IPC seam between authoritative ControlHost and execution-owning RuntimeHost.
/// The boundary carries contracts/descriptors only and never introduces a host-to-host project reference.
/// </summary>
public interface IControlRuntimeTransportSeam
{
	bool IsConnected { get; }
	string? HostInstanceId { get; }
	IReadOnlyList<ProviderDescriptor> ProviderDescriptors { get; }

	ValueTask ConnectAsync(CancellationToken cancellationToken = default);
	ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default);
	ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
	ValueTask<MediaSourceDiscoverySnapshot> GetMediaSourceDiscoveryAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaSourceDiscoverySnapshot>(
			new NotSupportedException("Runtime transport does not expose media-source discovery."));
	ValueTask<IReadOnlyList<MediaInputHealthSnapshot>> GetMediaInputHealthAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<MediaInputHealthSnapshot>>(
			new NotSupportedException("Runtime transport does not expose media-input health."));
	ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition,
		CancellationToken cancellationToken = default);
	ValueTask<RuntimeAudioInputSnapshot> SetAudioInputStateAsync(
		MediaSourceId sourceId,
		double gain,
		bool muted,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeAudioInputSnapshot>(
			new NotSupportedException("Runtime transport does not expose audio input control."));

	ValueTask<RuntimeAudioProgramSnapshot> SetAudioRoutingAsync(
		int mode,
		MediaSourceId? breakawaySourceId,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeAudioProgramSnapshot>(
			new NotSupportedException("Runtime transport does not expose audio routing control."));

	ValueTask<RuntimeAudioProductionSnapshot> SetAudioProductionAsync(
		AudioProductionConfiguration configuration,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeAudioProductionSnapshot>(
			new NotSupportedException("Runtime transport does not expose advanced audio production control."));

	ValueTask<RuntimeAudioInputSnapshot> SetAudioTestSignalAsync(
		MediaSourceId sourceId,
		bool enabled,
		int mode,
		double frequencyHz,
		double peakLevel,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeAudioInputSnapshot>(
			new NotSupportedException("Runtime transport does not expose generated audio test signal control."));

	ValueTask<bool> SetBroadcastTestPatternAsync(
		MediaSourceId sourceId,
		bool enabled,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<bool>(
			new NotSupportedException("Runtime transport does not expose broadcast test pattern control."));

	ValueTask<bool> SetBroadcastTestPatternAsync(
		MediaSourceId sourceId,
		bool enabled,
		bool motionTiming,
		CancellationToken cancellationToken = default) =>
		motionTiming
			? ValueTask.FromException<bool>(
				new NotSupportedException("Runtime transport does not expose motion/timing test pattern control."))
			: SetBroadcastTestPatternAsync(sourceId, enabled, cancellationToken);

	ValueTask<RuntimeGraphicsOverlaySnapshot> LoadGraphicsOverlayAsync(
		string assetName,
		uint width,
		uint height,
		byte[] rgbaPixels,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeGraphicsOverlaySnapshot>(
			new NotSupportedException("Runtime transport does not expose graphics overlay control."));

	ValueTask<RuntimeGraphicsOverlaySnapshot> ApplyProductionCgTextAsync(
		RuntimeProductionCgTextDefinition definition,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeGraphicsOverlaySnapshot>(
			new NotSupportedException("Runtime transport does not expose Production CG text control."));

	ValueTask<RuntimeGraphicsOverlaySnapshot> SetGraphicsOverlayAsync(
		bool visible,
		double positionX,
		double positionY,
		double scale,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeGraphicsOverlaySnapshot>(
			new NotSupportedException("Runtime transport does not expose graphics overlay control."));

	ValueTask<RuntimeGraphicsOverlaySnapshot> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeGraphicsOverlaySnapshot>(
			new NotSupportedException("Runtime transport does not expose graphics overlay control."));

	ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerStateAsync(
		string layerId,
		bool visible,
		byte opacity,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<RuntimeCompositingLayerSnapshot>>(
			new NotSupportedException("Runtime transport does not expose compositing layer control."));

	ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerTransformAsync(
		string layerId,
		double positionX,
		double positionY,
		double scale,
		double rotationDegrees,
		double anchorX,
		double anchorY,
		double cropLeft,
		double cropTop,
		double cropRight,
		double cropBottom,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<RuntimeCompositingLayerSnapshot>>(
			new NotSupportedException("Runtime transport does not expose compositing layer transform control."));

	ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerProcessingNodeAsync(
		string layerId,
		PreparedCompositingProcessingNodeState? processingNode,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<RuntimeCompositingLayerSnapshot>>(
			new NotSupportedException("Runtime transport does not expose compositing layer processing control."));

	ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerProcessingStackAsync(
		string layerId,
		IReadOnlyList<PreparedCompositingProcessingNodeState> processingStack,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<RuntimeCompositingLayerSnapshot>>(
			new NotSupportedException("Runtime transport does not expose compositing layer processing-stack control."));

	ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> ReorderCompositingLayersAsync(
		IReadOnlyList<string> orderedLayerIds,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<RuntimeCompositingLayerSnapshot>>(
			new NotSupportedException("Runtime transport does not expose compositing layer reorder control."));

	ValueTask<RuntimeRecordingCommandResult> StartRecordingAsync(
		string destinationDirectory,
		string fileName,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeRecordingCommandResult>(
			new NotSupportedException("Runtime transport does not expose recording control."));

	ValueTask<RuntimeRecordingCommandResult> StartRecordingAsync(
		string destinationDirectory,
		string fileName,
		string? profileId,
		CancellationToken cancellationToken = default) =>
		StartRecordingAsync(destinationDirectory, fileName, cancellationToken);

	ValueTask<RuntimeRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeRecordingCommandResult>(
			new NotSupportedException("Runtime transport does not expose recording control."));

	ValueTask<RuntimeReplaySnapshot> GetReplaySnapshotAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeReplaySnapshot>(
			new NotSupportedException("Runtime transport does not expose replay control."));

	ValueTask<RuntimeReplaySnapshot> MarkReplayInAsync(
		TimeSpan? lookback = null,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeReplaySnapshot>(
			new NotSupportedException("Runtime transport does not expose replay control."));

	ValueTask<RuntimeReplaySnapshot> MarkReplayOutAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeReplaySnapshot>(
			new NotSupportedException("Runtime transport does not expose replay control."));

	ValueTask<RuntimeReplaySnapshot> SetReplayRangeAsync(
		TimeSpan @in,
		TimeSpan @out,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeReplaySnapshot>(
			new NotSupportedException("Runtime transport does not expose replay control."));

	ValueTask<RuntimeReplayClipResult> CreateReplayClipAsync(
		string name,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeReplayClipResult>(
			new NotSupportedException("Runtime transport does not expose replay control."));

	ValueTask<string> GetAIEffectCatalogJsonAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<string>(new NotSupportedException("Runtime transport does not expose AI capability evidence."));
	ValueTask<RuntimeAIShowcaseRemoteSnapshot> TestAIEffectAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeAIShowcaseRemoteSnapshot>(new NotSupportedException("Runtime transport does not expose descriptor-only AI testing."));

	ValueTask<RuntimeAIShowcaseRemoteSnapshot> SetAIShowcaseEnabledAsync(
		bool enabled,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeAIShowcaseRemoteSnapshot>(
			new NotSupportedException("Runtime transport does not expose AI showcase control."));


    ValueTask<MediaDeckRuntimeSnapshot> PreloadMediaDeckAsync(MediaDeckOpenRequest request, PreparedExecutionContract execution, string fingerprint, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckRuntimeSnapshot>(new NotSupportedException("Runtime does not expose resident preload."));
    ValueTask<MediaDeckPreparation> PrepareLoadedNextAsync(MediaSourceId source, MediaAssetId asset, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaDeckPreparation>(new NotSupportedException("Runtime does not expose prepared-next evidence."));
    ValueTask CancelMediaPreparationAsync(string ticket, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Runtime does not expose prepared-next cancellation."));
    ValueTask<RuntimeRemoteApplyResult> ApplyPreparedMediaExecutionAsync(PreparedExecutionContract execution, MediaSinkId sink, RuntimeProgramTransitionIntent? transition, MediaDeckPreparation preparation, bool consume, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RuntimeRemoteApplyResult>(new NotSupportedException("Runtime does not expose guarded prepared-media commit."));

    ValueTask<MediaAssetPreflightObservation> BeginMediaAssetPreflightAsync(string path, MediaAssetId assetId, string fingerprint, MediaAssetPreflightOptions options, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetPreflightObservation>(new NotSupportedException("Runtime preflight is unavailable."));
    ValueTask<MediaAssetPreflightObservation> GetMediaAssetPreflightAsync(string jobId, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetPreflightObservation>(new NotSupportedException("Runtime preflight is unavailable."));
    ValueTask<MediaAssetPreflightObservation> CancelMediaAssetPreflightAsync(string jobId, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<MediaAssetPreflightObservation>(new NotSupportedException("Runtime preflight is unavailable."));

	ValueTask<MediaAssetProbeResult> ProbeMediaAssetAsync(
		string path,
		MediaAssetId assetId,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaAssetProbeResult>(
			new NotSupportedException("Runtime transport does not expose media-asset probing."));

    async ValueTask<MediaDeckRuntimeSnapshot> GetMediaDeckSnapshotAsync(MediaSourceId sourceId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetMediaDeckSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.SourceId is not null && snapshot.SourceId != sourceId)
            throw new NotSupportedException("Transport does not support explicit deck selection.");
        return snapshot;
    }
    async ValueTask<MediaDeckRuntimeSnapshot> CloseMediaDeckAsync(MediaSourceId sourceId, CancellationToken cancellationToken = default)
    {
        await GetMediaDeckSnapshotAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return await CloseMediaDeckAsync(cancellationToken).ConfigureAwait(false);
    }

	ValueTask<MediaDeckRuntimeSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaDeckRuntimeSnapshot>(
			new NotSupportedException("Runtime transport does not expose media-deck control."));

	ValueTask<MediaDeckRuntimeSnapshot> OpenMediaDeckAsync(
		MediaDeckOpenRequest request,
		PreparedExecutionContract preparedExecution,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaDeckRuntimeSnapshot>(
			new NotSupportedException("Runtime transport does not expose media-deck control."));

	ValueTask<MediaTransportCommandResult> ApplyMediaDeckTransportAsync(
		MediaTransportCommand command,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaTransportCommandResult>(
			new NotSupportedException("Runtime transport does not expose media-deck control."));

	ValueTask<MediaDeckRuntimeSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaDeckRuntimeSnapshot>(
			new NotSupportedException("Runtime transport does not expose media-deck control."));
	ValueTask DisconnectAsync();
}

public sealed class UnboundControlRuntimeTransportSeam : IControlRuntimeTransportSeam
{
	private static readonly IReadOnlyList<ProviderDescriptor> EmptyProviders = Array.Empty<ProviderDescriptor>();

	public bool IsConnected => false;
	public string? HostInstanceId => null;
	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => EmptyProviders;

	public ValueTask ConnectAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<ProviderDescriptor>>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeRemoteSnapshot>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeRemoteApplyResult>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<MediaDeckRuntimeSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaDeckRuntimeSnapshot>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<MediaDeckRuntimeSnapshot> OpenMediaDeckAsync(
		MediaDeckOpenRequest request,
		PreparedExecutionContract preparedExecution,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaDeckRuntimeSnapshot>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<MediaTransportCommandResult> ApplyMediaDeckTransportAsync(
		MediaTransportCommand command,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaTransportCommandResult>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<MediaDeckRuntimeSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<MediaDeckRuntimeSnapshot>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
}

public sealed record ControlHostProcessOptions(
	ProductionId ProductionId,
	ProductionSourceId SourceAId,
	ProductionSourceId SourceBId,
	string ProductionName,
	int JournalCapacity,
	string ListenEndpoint,
	string RuntimeEndpoint,
	TimeSpan ConnectTimeout,
	TimeSpan RequestTimeout,
	TimeSpan RuntimeRetryInterval,
	TimeSpan ShutdownTimeout)
{
	public string DurabilityRoot { get; init; } = DefaultDurabilityRoot();
	public int JournalRetainedCapacity { get; init; } = 256;
	public int CheckpointQueueCapacity { get; init; } = 64;
	public ExternalControlServerOptions ExternalControl { get; init; } = new();

	public static ControlHostProcessOptions Default => new(
		new ProductionId(Identity.Parse("70000000-0000-0000-0000-000000000001")),
		new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000a")),
		new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000b")),
		"rtaime V1 Production",
		1024,
		"rtaime.v1.control.default",
		"rtaime.v1.runtime.default",
		TimeSpan.FromSeconds(1),
		TimeSpan.FromSeconds(5),
		TimeSpan.FromMilliseconds(250),
		TimeSpan.FromSeconds(10));

	public static ControlHostProcessOptions Load(
		IReadOnlyList<string> args,
		Func<string, string?>? environment = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		environment ??= Environment.GetEnvironmentVariable;
		var defaults = Default;

		return new ControlHostProcessOptions(
			new ProductionId(ParseIdentity(Get(args, environment, "production-id", "RTAIME_CONTROL_PRODUCTION_ID", defaults.ProductionId.ToString()), "production-id")),
			new ProductionSourceId(ParseIdentity(Get(args, environment, "source-a-id", "RTAIME_CONTROL_SOURCE_A_ID", defaults.SourceAId.ToString()), "source-a-id")),
			new ProductionSourceId(ParseIdentity(Get(args, environment, "source-b-id", "RTAIME_CONTROL_SOURCE_B_ID", defaults.SourceBId.ToString()), "source-b-id")),
			Get(args, environment, "production-name", "RTAIME_CONTROL_PRODUCTION_NAME", defaults.ProductionName),
			ParsePositiveInt(Get(args, environment, "journal-capacity", "RTAIME_CONTROL_JOURNAL_CAPACITY", defaults.JournalCapacity.ToString()), "journal-capacity"),
			Get(args, environment, "listen-endpoint", "RTAIME_CONTROL_ENDPOINT", defaults.ListenEndpoint),
			Get(args, environment, "runtime-endpoint", "RTAIME_RUNTIME_ENDPOINT", defaults.RuntimeEndpoint),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "connect-timeout-ms", "RTAIME_CONTROL_CONNECT_TIMEOUT_MS", ((int)defaults.ConnectTimeout.TotalMilliseconds).ToString()), "connect-timeout-ms")),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "request-timeout-ms", "RTAIME_CONTROL_REQUEST_TIMEOUT_MS", ((int)defaults.RequestTimeout.TotalMilliseconds).ToString()), "request-timeout-ms")),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "runtime-retry-ms", "RTAIME_CONTROL_RUNTIME_RETRY_MS", ((int)defaults.RuntimeRetryInterval.TotalMilliseconds).ToString()), "runtime-retry-ms")),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "shutdown-timeout-ms", "RTAIME_CONTROL_SHUTDOWN_TIMEOUT_MS", ((int)defaults.ShutdownTimeout.TotalMilliseconds).ToString()), "shutdown-timeout-ms")))
		{
			DurabilityRoot = Get(args, environment, "durability-root", "RTAIME_CONTROL_DURABILITY_ROOT", defaults.DurabilityRoot),
			JournalRetainedCapacity = ParsePositiveInt(Get(args, environment, "journal-retained-capacity", "RTAIME_CONTROL_JOURNAL_RETAINED_CAPACITY", defaults.JournalRetainedCapacity.ToString()), "journal-retained-capacity"),
			CheckpointQueueCapacity = ParsePositiveInt(Get(args, environment, "checkpoint-capacity", "RTAIME_CONTROL_CHECKPOINT_CAPACITY", defaults.CheckpointQueueCapacity.ToString()), "checkpoint-capacity"),
			ExternalControl = ExternalControlServerOptions.Load(args, environment)
		};
	}

	public void Validate()
	{
		if (ProductionId.Value.IsEmpty) throw new ArgumentException("Production identity must not be empty.", nameof(ProductionId));
		if (SourceAId.Value.IsEmpty || SourceBId.Value.IsEmpty) throw new ArgumentException("Production source identities must not be empty.");
		if (SourceAId == SourceBId) throw new ArgumentException("Production source identities must be distinct.");
		if (string.IsNullOrWhiteSpace(ProductionName)) throw new ArgumentException("Production name is required.", nameof(ProductionName));
		if (JournalCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(JournalCapacity));
		if (JournalRetainedCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(JournalRetainedCapacity));
		if (CheckpointQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(CheckpointQueueCapacity));
		if (string.IsNullOrWhiteSpace(DurabilityRoot)) throw new ArgumentException("ControlHost durability root is required.", nameof(DurabilityRoot));
		if (string.IsNullOrWhiteSpace(ListenEndpoint)) throw new ArgumentException("ControlHost listen endpoint is required.", nameof(ListenEndpoint));
		if (string.IsNullOrWhiteSpace(RuntimeEndpoint)) throw new ArgumentException("RuntimeHost endpoint is required.", nameof(RuntimeEndpoint));
		if (ConnectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
		if (RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
		if (RuntimeRetryInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RuntimeRetryInterval));
		if (ShutdownTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
		ArgumentNullException.ThrowIfNull(ExternalControl);
		if (ExternalControl.Required)
			ExternalControl.Validate();
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
		if (commandLine is not null) return commandLine[prefix.Length..];
		var environmentValue = environment(environmentName);
		return string.IsNullOrWhiteSpace(environmentValue) ? defaultValue : environmentValue.Trim();
	}

	private static string DefaultDurabilityRoot()
	{
		var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(root)) root = AppContext.BaseDirectory;
		return Path.Combine(root, "rtaime", "data");
	}

	private static Identity ParseIdentity(string value, string key)
	{
		try { return Identity.Parse(value); }
		catch (Exception exception) when (exception is FormatException or ArgumentException)
		{
			throw new ArgumentException($"Configuration '{key}' must be a valid identity.", key, exception);
		}
	}

	private static int ParsePositiveInt(string value, string key)
	{
		if (!int.TryParse(value, out var parsed) || parsed <= 0)
			throw new ArgumentException($"Configuration '{key}' must be a positive integer.", key);
		return parsed;
	}
}

/// <summary>
/// Executable ControlHost composition root. Production authority remains in <see cref="ControlHostService"/>;
/// this class owns process startup, durable recovery, production IPC, runtime reconciliation and orderly shutdown.
/// </summary>
public sealed record ControlHostShutdownPhaseObservation(
    string Phase, double StartedMilliseconds, double DurationMilliseconds, string? FailureType,
    ProductionCheckpointWriterStatistics? CheckpointsBefore, ProductionCheckpointWriterStatistics? CheckpointsAfter,
    ProductionJournalStatistics? JournalBefore, ProductionJournalStatistics? JournalAfter);

public sealed class ControlHostProcess
{
	private readonly object _gate = new();
	private readonly ControlHostProcessOptions _options;
	private readonly Func<IControlRuntimeTransportSeam> _transportFactory;
	private ControlHostLifecycleSnapshot _lifecycle = new(ControlHostProcessState.Created, ControlHostHealthState.Unknown, "Process has not started.", DateTimeOffset.UtcNow);
	private ControlHostRecoverySnapshot _recovery = new(ControlHostRecoveryState.Fresh, null, "Durable recovery has not been evaluated yet.");
	private int _runStarted;
    private readonly List<ControlHostShutdownPhaseObservation> _shutdownPhases = new(9);
    public IReadOnlyList<ControlHostShutdownPhaseObservation> ShutdownPhases
    {
        get { lock (_gate) return _shutdownPhases.ToArray(); }
    }
	private BoundedProductionJournal? _journal;
	private SqliteManagementStore? _managementStore;
	private BoundedProductionCheckpointWriter? _checkpointWriter;
	private Revision? _lastCheckpointRevision;
	private ControlHostService? _control;
	private IControlRuntimeTransportSeam? _runtimeTransport;
	private MediaDeckControlService? _mediaDeckControl;
	private MediaAssetCatalogService? _mediaAssetCatalog;
	private ControlHostIpcServer? _ipcServer;
	private ExternalControlServer? _externalControlServer;
	private Task? _runtimeBindingTask;
	private string? _boundRuntimeHostInstanceId;
	private readonly HostLog? _log;
	private string? _interruptedInitialHostId;
	private AuthoritativeProductionState? _interruptedInitialAuthority;
	private long _bindingAttemptStarted = Stopwatch.GetTimestamp();
	private CancellationToken _bindingLifetimeToken;
	private string _bindingAttemptId = Guid.NewGuid().ToString("N");

	public ControlHostProcess(ControlHostProcessOptions options, Func<IControlRuntimeTransportSeam>? transportFactory = null, HostLog? log = null)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_log = log;
		_transportFactory = transportFactory ?? (() => new NamedPipeRuntimeHostTransport(_options.RuntimeEndpoint, _options.ConnectTimeout, _options.RequestTimeout));
	}

	public ControlHostLifecycleSnapshot Lifecycle { get { lock (_gate) return _lifecycle; } }
	public ControlHostRecoverySnapshot Recovery { get { lock (_gate) return _recovery; } }
	public ControlHostService? Control => _control;
	public BoundedProductionJournal? Journal => _journal;
	public SqliteManagementStore? ManagementStore => _managementStore;
	public BoundedProductionCheckpointWriter? CheckpointWriter => _checkpointWriter;
	public IControlRuntimeTransportSeam? RuntimeTransport => _runtimeTransport;
	public MediaDeckControlService? MediaDeckControl => _mediaDeckControl;
	public MediaAssetCatalogService? MediaAssetCatalog => _mediaAssetCatalog;
	public ControlHostIpcServer? IpcServer => _ipcServer;
	public ExternalControlServer? ExternalControlServer => _externalControlServer;

	public async Task<ControlHostExitCode> RunAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _runStarted, 1) != 0) throw new InvalidOperationException("A ControlHostProcess instance can be run only once.");
		_bindingLifetimeToken = cancellationToken;
		Update(ControlHostProcessState.Starting, ControlHostHealthState.Unknown, "Composing ControlHost dependencies and evaluating durable recovery state.");
		try
		{
			_options.Validate();
			await BindingPhaseAsync("authority-load", () => new ValueTask(ComposeAsync(cancellationToken)), cancellationToken).ConfigureAwait(false);
			await _ipcServer!.StartAsync(cancellationToken).ConfigureAwait(false);
			if (_externalControlServer is not null)
				await _externalControlServer.StartAsync(cancellationToken).ConfigureAwait(false);
			SetOperationalState(ControlHostProcessState.Degraded, ControlHostHealthState.Degraded, $"ControlHost is listening on '{_options.ListenEndpoint}' while RuntimeHost reconciliation is pending.");
			_runtimeBindingTask = RuntimeBindingLoopAsync(cancellationToken);
		}
		catch (ArgumentException exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, $"Configuration rejected: {exception.Message}");
			return ControlHostExitCode.ConfigurationError;
		}
		catch (Exception exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, $"Startup failed: {exception.Message}");
			return ControlHostExitCode.StartupFailure;
		}

		try
		{
			var bindingTask = _runtimeBindingTask ?? throw new InvalidOperationException("Runtime binding worker was not started.");
			var stopSignal = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			var completed = await Task.WhenAny(stopSignal, bindingTask).ConfigureAwait(false);
			if (completed == bindingTask && !cancellationToken.IsCancellationRequested)
			{
				await bindingTask.ConfigureAwait(false);
				Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, "Runtime binding worker stopped unexpectedly.");
				return ControlHostExitCode.UnexpectedFailure;
			}
			await stopSignal.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception exception)
		{
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, $"Run loop failed: {exception.Message}");
			return ControlHostExitCode.UnexpectedFailure;
		}

		return await StopAsync().ConfigureAwait(false);
	}

	private async Task ComposeAsync(CancellationToken cancellationToken)
	{
		_runtimeTransport = _transportFactory() ?? throw new InvalidOperationException("Runtime transport factory returned null.");
		if (_runtimeTransport.ProviderDescriptors is null) throw new InvalidOperationException("Runtime transport provider snapshot must not be null.");

		var baselineSpecification = new ProductionSpecification(
			ControlContractVersion.Current,
			_options.ProductionId,
			_options.ProductionName,
			new[]
			{
				new ProductionSourceSpecification(_options.SourceAId, "Input A"),
				new ProductionSourceSpecification(_options.SourceBId, "Input B")
			},
			new ProductionRoutingState(_options.SourceAId, _options.SourceAId),
			null,
			new[]
			{
				ProductionOutputRoleState.Program(_options.SourceAId),
				ProductionOutputRoleState.Aux(_options.SourceBId)
			});

		var durabilityDirectory = ResolveDurabilityDirectory();
		Directory.CreateDirectory(durabilityDirectory);
		_managementStore = new SqliteManagementStore(Path.Combine(durabilityDirectory, "management.db"));
		var managementIntegrity = await _managementStore.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
		if (!managementIntegrity.Healthy) throw new InvalidDataException($"Management persistence integrity failed: {managementIntegrity.Detail}");

		var showProjectStore = new ShowProjectPersistenceStore(_managementStore);
		var showProject = await showProjectStore
			.LoadOrCreateAsync(baselineSpecification, cancellationToken)
			.ConfigureAwait(false);
		var specification = showProject.ApplyTo(baselineSpecification);

		var recoveredState = await ControlHostRecovery.LoadAsync(_managementStore, specification, cancellationToken).ConfigureAwait(false);
		var journalStore = new SqliteProductionJournalStore(Path.Combine(durabilityDirectory, "production-journal.db"));
		try
		{
			var journalIntegrity = await journalStore.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
			if (!journalIntegrity.Healthy) throw new InvalidDataException($"Production Journal integrity failed: {journalIntegrity.Detail}");

			_journal = new BoundedProductionJournal(_options.JournalCapacity, journalStore, _options.JournalRetainedCapacity);
			_checkpointWriter = new BoundedProductionCheckpointWriter(_managementStore, _options.CheckpointQueueCapacity);
			_control = new ControlHostService(
				specification,
				_runtimeTransport.ProviderDescriptors,
				_journal,
				authoritativeCommitted: TryQueueConfirmedCheckpoint);

			if (recoveredState is not null)
			{
				_control.RestoreAuthoritativeState(recoveredState);
				_lastCheckpointRevision = recoveredState.Revision;
				SetRecovery(ControlHostRecoveryState.Recovered, recoveredState.Revision, $"Durable authoritative revision {recoveredState.Revision} was restored and requires Runtime reconciliation.");
			}
			else
			{
				SetRecovery(ControlHostRecoveryState.Fresh, null, "No durable authority checkpoint exists; a fresh Runtime-backed initialization is required.");
			}

			_mediaDeckControl = new MediaDeckControlService(
				() => _control,
				_runtimeTransport,
				new MediaMarkerPersistenceStore(_managementStore));
			_mediaAssetCatalog = new MediaAssetCatalogService(
				_runtimeTransport,
				new MediaAssetCatalogPersistenceStore(_managementStore));
			_ipcServer = new ControlHostIpcServer(
				_options.ListenEndpoint,
				() => _control,
				_runtimeTransport,
				_mediaDeckControl,
				new ShowControlPersistenceStore(_managementStore, showProjectStore, specification),
				_mediaAssetCatalog,
				showProjectStore,
				showProject,
				HostIpcSessionTracker.DrainIntervalForHost(_options.ShutdownTimeout),
				baselineSpecification);
			_externalControlServer = new ExternalControlServer(_options.ExternalControl, _ipcServer);
		}
		catch
		{
			if (_journal is null) await journalStore.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private async Task RuntimeBindingLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				_bindingAttemptId = Guid.NewGuid().ToString("N");
				_bindingAttemptStarted = Stopwatch.GetTimestamp();
				var transport = _runtimeTransport ?? throw new InvalidOperationException("Runtime transport was not composed.");
				var control = _control ?? throw new InvalidOperationException("Control service was not composed.");
				using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				operationTimeout.CancelAfter(_options.ConnectTimeout + _options.RequestTimeout);
				var operationToken = operationTimeout.Token;

				await BindingPhaseAsync("ipc-connect", () => transport.ConnectAsync(operationToken), cancellationToken).ConfigureAwait(false);
				if (!transport.IsConnected) throw new IOException("Runtime transport did not report a connected state after a successful probe.");
				if (control.HasPendingExecution)
				{
					WriteBindingPhase("pending-execution", "waiting", Stopwatch.GetTimestamp(), "mutation-in-flight");
					await Task.Delay(_options.RuntimeRetryInterval, cancellationToken).ConfigureAwait(false);
					continue;
				}

				var runtimeHostInstanceId = transport.HostInstanceId ?? throw new InvalidDataException("Connected RuntimeHost did not expose a host instance identity.");
				if (!control.HasAuthoritativeState)
				{
					await RunBindingMutationAsync(
						 token => new ValueTask(InitializeFreshAuthorityAsync(control, transport, runtimeHostInstanceId, token)),
						 operationToken).ConfigureAwait(false);
				}
				else
				{
					var runtimeSnapshot = await BindingPhaseAsync("runtime-snapshot", () => transport.GetSnapshotAsync(operationToken), cancellationToken).ConfigureAwait(false);
					var hostChanged = !string.Equals(_boundRuntimeHostInstanceId, runtimeHostInstanceId, StringComparison.Ordinal);
					var aligned = RuntimeMatchesAuthority(runtimeSnapshot, control.State);
					if (hostChanged || !aligned)
						await BindingPhaseAsync("reconcile", () => new ValueTask(ReconcileRuntimeAsync(control, transport, runtimeHostInstanceId, runtimeSnapshot, operationToken)), cancellationToken).ConfigureAwait(false);
					else
						SetOperationalState(ControlHostProcessState.Ready, ControlHostHealthState.Healthy, $"ControlHost is connected to RuntimeHost instance '{runtimeHostInstanceId}'.");
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (RuntimeRecoveryConflictException exception)
			{
				WriteBindingPhase("binding-attempt", "failed", _bindingAttemptStarted, "authority-conflict", exception);
				SetRecovery(ControlHostRecoveryState.Conflict, exception.RuntimeAuthorityRevision, exception.Message);
				SetOperationalState(ControlHostProcessState.Degraded, ControlHostHealthState.Degraded, "Runtime recovery conflict requires operator intervention; authoritative mutation transport is paused.");
				if (_runtimeTransport is not null)
				{
					try { await _runtimeTransport.DisconnectAsync().ConfigureAwait(false); } catch { }
				}
				try { _control?.RecordObservation("recovery", "recovery.runtime.conflict", exception.Message, new Failure("recovery.runtime.conflict", exception.Message)); } catch { }
			}
			catch (Exception exception)
			{
				WriteBindingPhase("binding-attempt", "failed", Stopwatch.GetTimestamp(), exception is OperationCanceledException ? "timeout" : "rejected", exception);
				SetOperationalState(ControlHostProcessState.Degraded, ControlHostHealthState.Degraded, "RuntimeHost is unavailable; authoritative mutation transport is paused.");
				if (_runtimeTransport is not null)
				{
					try { await _runtimeTransport.DisconnectAsync().ConfigureAwait(false); } catch { }
				}
				if (_control is { } control)
				{
					try { control.RecordObservation("runtime", "runtime.connection.degraded", $"RuntimeHost binding failed: {exception.GetType().Name}.", new Failure("runtime.connection.failed", exception.Message)); } catch { }
				}
			}

			await Task.Delay(_options.RuntimeRetryInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	private async ValueTask<T> BindingPhaseAsync<T>(string phase, Func<ValueTask<T>> operation, CancellationToken lifetimeToken)
	{
		var started = Stopwatch.GetTimestamp();
		WriteBindingPhase(phase, "started", started);
		try
		{
			lifetimeToken.ThrowIfCancellationRequested();
			var result = await operation().ConfigureAwait(false);
			WriteBindingPhase(phase, "completed", started);
			return result;
		}
		catch (Exception exception)
		{
			var reason = exception is OperationCanceledException
				? _bindingLifetimeToken.IsCancellationRequested ? "cancelled" : "timeout"
				: exception is RuntimeRecoveryConflictException ? "authority-conflict"
				: exception is IOException ? "transport-failure" : "rejected";
			WriteBindingPhase(phase, "failed", started, reason, exception);
			throw;
		}
	}

	private async ValueTask BindingPhaseAsync(string phase, Func<ValueTask> operation, CancellationToken lifetimeToken)
	{
		await BindingPhaseAsync(phase, async () =>
		{
			await operation().ConfigureAwait(false);
			return true;
		}, lifetimeToken).ConfigureAwait(false);
	}

	private void WriteBindingPhase(string phase, string outcome, long started, string reason = "none", Exception? exception = null)
	{
		_log?.Write(
			exception is not null ? DiagnosticSeverity.Warning : Lifecycle.State == ControlHostProcessState.Ready && phase != "qualified-ready" ? DiagnosticSeverity.Trace : DiagnosticSeverity.Information,
			"runtime-binding",
			$"controlhost.binding.{phase}.{outcome}",
			$"ControlHost runtime binding phase {phase} {outcome}.",
			exception,
			new Dictionary<string, string>
			{
				["attemptId"] = _bindingAttemptId,
				["phase"] = phase,
				["outcome"] = outcome,
				["reason"] = reason,
				["elapsedMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
				["runtimeHostInstanceId"] = _runtimeTransport?.HostInstanceId ?? "unbound",
				["hasPendingExecution"] = (_control?.HasPendingExecution ?? false).ToString()
			});
	}

	private async ValueTask RunBindingMutationAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken)
	{
		if (_ipcServer is null)
		{
			await operation(cancellationToken).ConfigureAwait(false);
			return;
		}
		var started = Stopwatch.GetTimestamp();
		var acquired = false;
		WriteBindingPhase("mutation-gate", "started", started);
		try
		{
			await _ipcServer.RunSerializedMutationAsync(async token =>
			{
				acquired = true;
				WriteBindingPhase("mutation-gate", "completed", started);
				await operation(token).ConfigureAwait(false);
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (!acquired)
		{
			WriteBindingPhase("mutation-gate", "failed", started,
				exception is OperationCanceledException ? _bindingLifetimeToken.IsCancellationRequested ? "cancelled" : "timeout" : "rejected", exception);
			throw;
		}
	}

	private async ValueTask RestoreBindingStateAsync(CancellationToken cancellationToken)
	{
		if (_ipcServer is null) return;
		await BindingPhaseAsync("restore-graphics", () => _ipcServer.RestoreGraphicsStateWithinMutationAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
		await BindingPhaseAsync("restore-audio", () => _ipcServer.RestoreAudioRoutingStateWithinMutationAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask AcceptBindingAsync(ControlHostService control, IControlRuntimeTransportSeam transport, CancellationToken cancellationToken)
	{
		await BindingPhaseAsync("runtime-acceptance", async () =>
		{
			var snapshot = await transport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
			ValidateRuntimeAuthority(snapshot, control.State);
			if (!RuntimeMatchesAuthority(snapshot, control.State))
				throw new InvalidDataException("Runtime execution does not match current authoritative Control state.");
		}, cancellationToken).ConfigureAwait(false);
	}

	private void PublishBinding(string runtimeHostInstanceId, string detail)
	{
		var started = Stopwatch.GetTimestamp();
		WriteBindingPhase("host-binding", "started", started);
		_boundRuntimeHostInstanceId = runtimeHostInstanceId;
		WriteBindingPhase("host-binding", "completed", started);
		WriteBindingPhase("qualified-ready", "started", started);
		SetOperationalState(ControlHostProcessState.Ready, ControlHostHealthState.Healthy, detail);
		WriteBindingPhase("qualified-ready", "completed", started);
	}

	private async Task InitializeFreshAuthorityAsync(
		ControlHostService control,
		IControlRuntimeTransportSeam transport,
		string runtimeHostInstanceId,
		CancellationToken cancellationToken)
	{
		var providers = await BindingPhaseAsync("provider-snapshot", () => transport.GetProviderDescriptorsAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
		control.RefreshProviderSnapshot(providers);
		var staged = await BindingPhaseAsync("authority-prepare", () => ValueTask.FromResult(control.Initialize()), cancellationToken).ConfigureAwait(false);
		if (staged.Execution is null) throw new InvalidOperationException("Control initialization did not produce a prepared execution.");

		try
		{
			var initialSnapshot = await BindingPhaseAsync("initial-runtime-acceptance", async () =>
			{
				var snapshot = await transport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
				ValidateRuntimeAuthority(snapshot, staged.Execution.AuthoritativeState);
				return snapshot;
			}, cancellationToken).ConfigureAwait(false);

			if (RuntimeMatchesAuthority(initialSnapshot, staged.Execution.AuthoritativeState) &&
				_interruptedInitialHostId == runtimeHostInstanceId &&
				_interruptedInitialAuthority == staged.Execution.AuthoritativeState)
			{
				await BindingPhaseAsync("authority-snapshot-confirm", () =>
				{
					var confirmation = control.ConfirmInitialRuntimeSnapshot(staged.Execution.PreparedExecution.PreparedExecutionId, initialSnapshot);
					if (!confirmation.Committed)
						throw new InvalidDataException(confirmation.Failure?.Message ?? "Runtime snapshot did not confirm interrupted initialization.");
					return ValueTask.CompletedTask;
				}, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				RuntimeRemoteApplyResult remote;
				try
				{
					remote = await BindingPhaseAsync("runtime-apply",
						() => transport.ApplyExecutionAsync(staged.Execution.PreparedExecution, staged.Execution.ProgramSinkId, staged.Execution.ProgramTransition, cancellationToken),
						cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
				{
					_interruptedInitialHostId = runtimeHostInstanceId;
					_interruptedInitialAuthority = staged.Execution.AuthoritativeState;
					throw;
				}
				if (remote.Commit is null)
					throw new InvalidOperationException(remote.Prepare.Failure?.Message ?? "Runtime rejected initial prepare without a commit result.");

				await BindingPhaseAsync("authority-confirm", () =>
				{
					var confirmation = control.ConfirmRuntimeCommit(staged.Execution.PreparedExecution.PreparedExecutionId, remote.Commit);
					if (!confirmation.Committed || confirmation.State is null)
						throw new InvalidOperationException(confirmation.Failure?.Message ?? "Initial Runtime commit was not confirmed.");
					return ValueTask.CompletedTask;
				}, cancellationToken).ConfigureAwait(false);
			}
			_interruptedInitialHostId = null;
			_interruptedInitialAuthority = null;
		}
		catch (Exception exception)
		{
			// Only this staged execution is owned by initialization. A response loss may
			// leave Runtime committed; the next attempt must probe it before qualifying.
			control.RejectRuntimeCommit(staged.Execution.PreparedExecution.PreparedExecutionId,
				new Failure("runtime.initialization.interrupted", exception.Message));
			throw;
		}

		await RestoreBindingStateAsync(cancellationToken).ConfigureAwait(false);
		await AcceptBindingAsync(control, transport, cancellationToken).ConfigureAwait(false);
		SetRecovery(ControlHostRecoveryState.Fresh, control.State.Revision, "Fresh authority was initialized and committed by RuntimeHost.");
		cancellationToken.ThrowIfCancellationRequested();
		PublishBinding(runtimeHostInstanceId, $"ControlHost is bound to RuntimeHost instance '{runtimeHostInstanceId}'.");
	}

	private async Task ReconcileRuntimeAsync(
		ControlHostService control,
		IControlRuntimeTransportSeam transport,
		string runtimeHostInstanceId,
		RuntimeRemoteSnapshot runtimeSnapshot,
		CancellationToken cancellationToken)
	{
		if (_ipcServer is null)
		{
			await ReconcileRuntimeCoreAsync(
				control,
				transport,
				runtimeHostInstanceId,
				runtimeSnapshot,
				restoreWithinMutationGate: false,
				cancellationToken).ConfigureAwait(false);
			return;
		}

		await RunBindingMutationAsync(
			async token =>
			{
				var currentSnapshot = await BindingPhaseAsync("runtime-snapshot", () => transport.GetSnapshotAsync(token), token).ConfigureAwait(false);
				await ReconcileRuntimeCoreAsync(
					control,
					transport,
					runtimeHostInstanceId,
					currentSnapshot,
					restoreWithinMutationGate: true,
					token).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask ReconcileRuntimeCoreAsync(
		ControlHostService control,
		IControlRuntimeTransportSeam transport,
		string runtimeHostInstanceId,
		RuntimeRemoteSnapshot runtimeSnapshot,
		bool restoreWithinMutationGate,
		CancellationToken cancellationToken)
	{
		var authority = control.State;
		ValidateRuntimeAuthority(runtimeSnapshot, authority);

		if (RuntimeMatchesAuthority(runtimeSnapshot, authority))
		{
			// A probe taken before a concurrent operator mutation may look stale.
			// Once serialized, a still-qualified binding needs no restoration.
			if (!string.Equals(_boundRuntimeHostInstanceId, runtimeHostInstanceId, StringComparison.Ordinal))
				await RestoreBindingStateAsync(cancellationToken).ConfigureAwait(false);
			await AcceptBindingAsync(control, transport, cancellationToken).ConfigureAwait(false);
			control.RecordObservation("recovery", "recovery.runtime.aligned", $"RuntimeHost instance '{runtimeHostInstanceId}' is already committed against authoritative revision {authority.Revision}.");
			SetRecovery(ControlHostRecoveryState.Recovered, authority.Revision, "Durable Control authority and Runtime committed authority snapshot are aligned.");
			cancellationToken.ThrowIfCancellationRequested();
			PublishBinding(runtimeHostInstanceId, $"ControlHost reconciled with RuntimeHost instance '{runtimeHostInstanceId}' without execution replacement.");
			return;
		}

		SetOperationalState(ControlHostProcessState.Degraded, ControlHostHealthState.Degraded, "Runtime execution is behind or not committed; durable authority is being reapplied.");
		var providers = await BindingPhaseAsync("provider-snapshot", () => transport.GetProviderDescriptorsAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
		control.RefreshProviderSnapshot(providers);
		var revisionBefore = authority.Revision;
		if (_ipcServer is not null && authority.CompositingState is not null)
		{
			if (restoreWithinMutationGate)
				await BindingPhaseAsync("restore-graphics", () => _ipcServer.RestoreGraphicsStateWithinMutationAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
			else
				await _ipcServer.RestoreGraphicsStateAsync(cancellationToken).ConfigureAwait(false);
		}
		var execution = await BindingPhaseAsync("reconcile-prepare", () => ValueTask.FromResult(control.PrepareCurrentExecution()), cancellationToken).ConfigureAwait(false);
		var remote = await BindingPhaseAsync("reconcile-commit", () => transport.ApplyExecutionAsync(execution.PreparedExecution, execution.ProgramSinkId, null, cancellationToken), cancellationToken).ConfigureAwait(false);
		if (!remote.Committed || remote.Commit is null) throw new InvalidOperationException(remote.Commit?.Failure?.Message ?? remote.Prepare.Failure?.Message ?? "Runtime reconciliation was rejected.");
		if (control.State.Revision != revisionBefore) throw new InvalidOperationException("Runtime reconciliation must not advance authoritative revision.");

		if (_ipcServer is not null)
		{
			if (restoreWithinMutationGate)
				await BindingPhaseAsync("restore-audio", () => _ipcServer.RestoreAudioRoutingStateWithinMutationAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
			else
				await _ipcServer.RestoreAudioRoutingStateAsync(cancellationToken).ConfigureAwait(false);
		}

		await AcceptBindingAsync(control, transport, cancellationToken).ConfigureAwait(false);

		control.RecordObservation("recovery", "recovery.runtime.reapplied", $"Authoritative revision {revisionBefore} was reapplied to RuntimeHost instance '{runtimeHostInstanceId}' at Runtime execution revision {remote.Commit.ExecutionRevision}.");
		SetRecovery(ControlHostRecoveryState.Recovered, revisionBefore, "Durable Control authority was reapplied to RuntimeHost without authority revision advancement.");
		cancellationToken.ThrowIfCancellationRequested();
		PublishBinding(runtimeHostInstanceId, $"ControlHost resynchronized RuntimeHost instance '{runtimeHostInstanceId}'.");
	}

	private static void ValidateRuntimeAuthority(RuntimeRemoteSnapshot runtimeSnapshot, AuthoritativeProductionState authority)
	{
		if (runtimeSnapshot.Runtime.Status == RuntimeExecutionStatus.Committed &&
			(runtimeSnapshot.AuthorityStateId is null || runtimeSnapshot.AuthorityRevision is null))
		{
			throw new RuntimeRecoveryConflictException(
				authority.Revision,
				null,
				"RuntimeHost reports committed execution without an authoritative snapshot reference; automatic reconciliation is prohibited.");
		}

		if (runtimeSnapshot.AuthorityStateId is not { } runtimeAuthorityStateId ||
			runtimeSnapshot.AuthorityRevision is not { } runtimeAuthorityRevision)
			return;

		if (runtimeAuthorityStateId != authority.ProductionId.Value)
		{
			throw new RuntimeRecoveryConflictException(
				authority.Revision,
				runtimeAuthorityRevision,
				$"RuntimeHost committed authority state '{runtimeAuthorityStateId}' does not match Control production '{authority.ProductionId}'; automatic overwrite is prohibited.");
		}

		if (runtimeAuthorityRevision.CompareTo(authority.Revision) > 0)
		{
			throw new RuntimeRecoveryConflictException(
				authority.Revision,
				runtimeAuthorityRevision,
				$"RuntimeHost committed authority revision {runtimeAuthorityRevision} is ahead of durable Control authority revision {authority.Revision}; automatic overwrite is prohibited.");
		}
	}

	private static bool RuntimeMatchesAuthority(RuntimeRemoteSnapshot runtimeSnapshot, AuthoritativeProductionState authority) =>
		runtimeSnapshot.Runtime.Status == RuntimeExecutionStatus.Committed &&
		runtimeSnapshot.AuthorityStateId == authority.ProductionId.Value &&
		runtimeSnapshot.AuthorityRevision == authority.Revision &&
		RuntimeCompositingMatchesAuthority(runtimeSnapshot, authority);

	private static bool RuntimeCompositingMatchesAuthority(
		RuntimeRemoteSnapshot runtimeSnapshot,
		AuthoritativeProductionState authority)
	{
		if (authority.CompositingState is null)
			return true;

		var runtimeLayers = (runtimeSnapshot.CompositingLayers ?? Array.Empty<RuntimeCompositingLayerSnapshot>())
			.OrderBy(layer => layer.Order)
			.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
			.ToArray();
		var authoritativeLayers = authority.CompositingState.Layers;
		if (runtimeLayers.Length != authoritativeLayers.Count)
			return false;

		for (var index = 0; index < authoritativeLayers.Count; index++)
		{
			var expected = authoritativeLayers[index];
			var actual = runtimeLayers[index];
			if (!string.Equals(expected.LayerId, actual.LayerId, StringComparison.Ordinal) ||
				(int)expected.Kind != actual.Kind ||
				expected.Order != actual.Order ||
				expected.Visible != actual.Visible ||
				expected.Opacity != actual.Opacity ||
				expected.PositionX != actual.PositionX ||
				expected.PositionY != actual.PositionY ||
				expected.Scale != actual.Scale ||
				expected.RotationDegrees != actual.RotationDegrees ||
				expected.AnchorX != actual.AnchorX ||
				expected.AnchorY != actual.AnchorY ||
				expected.CropLeft != actual.CropLeft ||
				expected.CropTop != actual.CropTop ||
				expected.CropRight != actual.CropRight ||
				expected.CropBottom != actual.CropBottom ||
				!ProcessingStackMatches(expected.ProcessingStack, actual.ProcessingStack) ||
				!string.Equals(expected.ContentIdentity, actual.ContentIdentity, StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}

	private static bool ProcessingStackMatches(
		IReadOnlyList<ProductionCompositingProcessingNodeState> expected,
		IReadOnlyList<PreparedCompositingProcessingNodeState> actual)
	{
		if (expected.Count != actual.Count)
			return false;
		for (var index = 0; index < expected.Count; index++)
		{
			if (!ProcessingNodeMatches(expected[index], actual[index]))
				return false;
		}
		return true;
	}

	private static bool ProcessingNodeMatches(
		ProductionCompositingProcessingNodeState expected,
		PreparedCompositingProcessingNodeState actual) =>
		expected.NodeId == actual.NodeId &&
		(int)expected.Kind == (int)actual.Kind &&
		expected.Enabled == actual.Enabled &&
		ColorGradeMatches(expected.ColorGrade, actual.ColorGrade) &&
		ChromaKeyMatches(expected.ChromaKey, actual.ChromaKey);

	private static bool ColorGradeMatches(ProductionColorGradeSettings? expected, PreparedColorGradeSettings? actual) =>
		expected is null
			? actual is null
			: actual is not null &&
				expected.Brightness == actual.Brightness &&
				expected.Contrast == actual.Contrast &&
				expected.Saturation == actual.Saturation;

	private static bool ChromaKeyMatches(ProductionChromaKeySettings? expected, PreparedChromaKeySettings? actual) =>
		expected is null
			? actual is null
			: actual is not null &&
				expected.KeyRed == actual.KeyRed &&
				expected.KeyGreen == actual.KeyGreen &&
				expected.KeyBlue == actual.KeyBlue &&
				expected.Tolerance == actual.Tolerance &&
				expected.Softness == actual.Softness &&
				expected.SpillSuppression == actual.SpillSuppression;

	private string ResolveDurabilityDirectory()
	{
		var safeEndpoint = string.Concat(_options.ListenEndpoint.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
		return Path.Combine(_options.DurabilityRoot, $"{_options.ProductionId}-{safeEndpoint}");
	}

	private void TryQueueConfirmedCheckpoint(AuthoritativeProductionState state)
	{
		try
		{
			if (_lastCheckpointRevision is { } revision && revision == state.Revision) return;
			var writer = _checkpointWriter;
			if (writer is null) return;
			var checkpoint = new ProductionCheckpoint(
				Identity.New(),
				state.ProductionId.Value,
				state.Revision,
				new UtcTimestamp(DateTimeOffset.UtcNow),
				ControlHostRecovery.CheckpointFormat,
				ControlHostRecovery.Serialize(state));
			if (writer.TryWrite(checkpoint)) _lastCheckpointRevision = state.Revision;
		}
		catch
		{
			// Durability observation is subordinate to an already-confirmed Runtime/Control commit.
			// Writer failure remains observable through durability statistics and orderly shutdown evidence.
		}
	}

    private async Task<ControlHostExitCode> StopAsync()
    {
        Update(ControlHostProcessState.Draining, ControlHostHealthState.Degraded, "Draining ControlHost IPC, runtime transport and durability resources.");
        _ipcServer?.NotifyObservableStateChanged();
        using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
        var started = Stopwatch.GetTimestamp();
        var phase = "external-control-server";
        async Task Observe(string name, Func<Task> operation)
        {
            phase = name;
            var phaseStarted = Stopwatch.GetTimestamp();
            var checkpointsBefore = _checkpointWriter?.Statistics;
            var journalBefore = _journal?.Statistics;
            string? failure = null;
            try { await operation().ConfigureAwait(false); }
            catch (Exception exception) { failure = exception.GetBaseException().GetType().Name; throw; }
            finally
            {
                var observation = new ControlHostShutdownPhaseObservation(name,
                    Stopwatch.GetElapsedTime(started, phaseStarted).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(phaseStarted).TotalMilliseconds, failure,
                    checkpointsBefore, _checkpointWriter?.Statistics, journalBefore, _journal?.Statistics);
                lock (_gate) _shutdownPhases.Add(observation);
            }
        }
        string Evidence() => System.Text.Json.JsonSerializer.Serialize(ShutdownPhases);
        try
        {
            if (_externalControlServer is not null)
                await Observe("external-control-server", () => _externalControlServer.DisposeAsync().AsTask().WaitAsync(timeout.Token)).ConfigureAwait(false);
            if (_runtimeBindingTask is not null)
                await Observe("runtime-binding-worker", async () =>
                {
                    try { await _runtimeBindingTask.WaitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) { }
                }).ConfigureAwait(false);
            if (_ipcServer is not null)
                await Observe("operator-ipc-server", () => _ipcServer.DisposeAsync().AsTask().WaitAsync(timeout.Token)).ConfigureAwait(false);
            if (_runtimeTransport is not null)
                await Observe("runtime-transport", () => _runtimeTransport.DisconnectAsync().AsTask().WaitAsync(timeout.Token)).ConfigureAwait(false);

            Exception? durabilityFailure = null;
            string? durabilityFailurePhase = null;
            async Task Drain(string name, Func<Task> operation)
            {
                try { await Observe(name, operation).ConfigureAwait(false); }
                catch (Exception exception)
                {
                    if (durabilityFailure is null) { durabilityFailure = exception; durabilityFailurePhase = name; }
                }
            }
            if (_checkpointWriter is not null)
            {
                await Drain("checkpoint-flush", () => _checkpointWriter.FlushAsync(timeout.Token).AsTask()).ConfigureAwait(false);
                await Drain("checkpoint-dispose", () => _checkpointWriter.DisposeAsync().AsTask().WaitAsync(timeout.Token)).ConfigureAwait(false);
            }
            if (_managementStore is not null)
                await Drain("management-dispose", () => _managementStore.DisposeAsync().AsTask().WaitAsync(timeout.Token)).ConfigureAwait(false);
            if (_journal is not null)
            {
                await Drain("journal-flush", () => _journal.FlushAsync(timeout.Token).AsTask()).ConfigureAwait(false);
                await Drain("journal-dispose", () => _journal.DisposeAsync().AsTask().WaitAsync(timeout.Token)).ConfigureAwait(false);
            }
            if (durabilityFailure is not null)
                throw new IOException($"ControlHost durability drain failed at {durabilityFailurePhase}: {durabilityFailure.GetBaseException().GetType().Name}: {durabilityFailure.GetBaseException().Message}", durabilityFailure);

            Update(ControlHostProcessState.Stopped, ControlHostHealthState.Stopped, "ControlHost stopped cleanly.");
            return ControlHostExitCode.Success;
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy,
                $"ControlHost shutdown exceeded the configured timeout during {phase}. Phase evidence: {Evidence()}");
            return ControlHostExitCode.ShutdownFailure;
        }
        catch (Exception exception)
        {
            Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy,
                $"ControlHost shutdown failed during {phase}: {exception.Message}. Phase evidence: {Evidence()}");
            return ControlHostExitCode.ShutdownFailure;
        }
    }

	private async Task CleanupStartupFailureAsync()
	{
		try { if (_externalControlServer is not null) await _externalControlServer.DisposeAsync().ConfigureAwait(false); } catch { }
		try { if (_ipcServer is not null) await _ipcServer.DisposeAsync().ConfigureAwait(false); } catch { }
		try { if (_runtimeTransport is not null) await _runtimeTransport.DisconnectAsync().ConfigureAwait(false); } catch { }
		try { if (_checkpointWriter is not null) await _checkpointWriter.DisposeAsync().ConfigureAwait(false); } catch { }
		try { if (_managementStore is not null) await _managementStore.DisposeAsync().ConfigureAwait(false); } catch { }
		try { if (_journal is not null) await _journal.DisposeAsync().ConfigureAwait(false); } catch { }
	}

	private void SetRecovery(ControlHostRecoveryState state, Revision? revision, string detail)
	{
		lock (_gate) _recovery = new ControlHostRecoverySnapshot(state, revision, detail);
	}

	private void SetOperationalState(ControlHostProcessState state, ControlHostHealthState health, string detail)
	{
		var changed = false;
		lock (_gate)
		{
			changed = _lifecycle.State != state || _lifecycle.Health != health || !string.Equals(_lifecycle.Detail, detail, StringComparison.Ordinal);
			if (changed) _lifecycle = new ControlHostLifecycleSnapshot(state, health, detail, DateTimeOffset.UtcNow);
		}
		if (changed) _ipcServer?.NotifyObservableStateChanged();
	}

	private void Update(ControlHostProcessState state, ControlHostHealthState health, string detail)
	{
		lock (_gate) _lifecycle = new ControlHostLifecycleSnapshot(state, health, detail, DateTimeOffset.UtcNow);
	}

	private sealed class RuntimeRecoveryConflictException : InvalidOperationException
	{
		public RuntimeRecoveryConflictException(Revision controlRevision, Revision? runtimeAuthorityRevision, string message) : base(message)
		{
			ControlRevision = controlRevision;
			RuntimeAuthorityRevision = runtimeAuthorityRevision;
		}

		public Revision ControlRevision { get; }
		public Revision? RuntimeAuthorityRevision { get; }
	}
}
