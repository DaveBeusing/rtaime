// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public enum V1VisualLayerMode
{
	Disabled = 1,
	Static = 2,
	Dynamic = 3
}

public enum V1InputSignalState
{
	Valid = 1,
	Unstable = 2,
	Lost = 3,
	Recovering = 4
}

public enum V1BroadcastTestPatternMode
{
	Static = 1,
	MotionTiming = 2
}

public enum V1TimingHealthState
{
	Healthy = (int)RuntimeTimingHealthState.Healthy,
	Degraded = (int)RuntimeTimingHealthState.Degraded,
	Unstable = (int)RuntimeTimingHealthState.Unstable,
	Lost = (int)RuntimeTimingHealthState.Lost,
	Recovering = (int)RuntimeTimingHealthState.Recovering
}

public enum V1AudioHealthState
{
	Healthy = 1,
	Muted = 2,
	Silence = 3,
	Clipping = 4,
	Underrun = 5,
	Error = 6
}

public readonly record struct ProgramPixelProbe(byte Red, byte Green, byte Blue, byte Alpha);

public sealed record RuntimeHostApplyResult(
	RuntimePrepareResult Prepare,
	RuntimeCommitResult? Commit,
	ulong? ActivationSequence)
{
	public bool Committed => Commit?.Status == RuntimeCommitStatus.Committed;
}

internal sealed record CompositingRollbackState(
	PreparedCompositingState State,
	int LegacyVisualLayerOrder,
	int BitmapGraphicsLayerOrder,
	int ProductionCgLayerOrder);

public sealed class V1ProgramBoundaryResult : IDisposable
{
	private GpuReadbackLease? _programPixels;

	internal V1ProgramBoundaryResult(
		ulong sequenceNumber,
		MediaSourceId committedProgramSourceId,
		FrameDescriptor programFrame,
		GpuReadbackLease programPixels,
		ProgramPixelProbe pixelProbe,
		AudioFollowVideoResult audio,
		AudioBufferDescriptor programAudioBuffer,
		byte[] programAudioPayload,
		RecordingEnqueueResult? recording,
		RuntimeProgramTransitionKind? transitionKind,
		byte blendWeight,
		V1VisualLayerMode visualLayerMode,
		int activeGpuSurfacesAfterBoundary)
	{
		SequenceNumber = sequenceNumber;
		CommittedProgramSourceId = committedProgramSourceId;
		ProgramFrame = programFrame ?? throw new ArgumentNullException(nameof(programFrame));
		_programPixels = programPixels ?? throw new ArgumentNullException(nameof(programPixels));
		PixelProbe = pixelProbe;
		Audio = audio ?? throw new ArgumentNullException(nameof(audio));
		ProgramAudioBuffer = programAudioBuffer ?? throw new ArgumentNullException(nameof(programAudioBuffer));
		ProgramAudioPayload = programAudioPayload ?? throw new ArgumentNullException(nameof(programAudioPayload));
		Recording = recording;
		TransitionKind = transitionKind;
		BlendWeight = blendWeight;
		VisualLayerMode = visualLayerMode;
		ActiveGpuSurfacesAfterBoundary = activeGpuSurfacesAfterBoundary;
	}

	public ulong SequenceNumber { get; }
	public MediaSourceId CommittedProgramSourceId { get; }
	public FrameDescriptor ProgramFrame { get; }
	public ReadOnlyMemory<byte> ProgramPixels => ProgramPixelsLease.Memory;
	public ProgramPixelProbe PixelProbe { get; }
	public AudioFollowVideoResult Audio { get; }
	public AudioBufferDescriptor ProgramAudioBuffer { get; }
	public byte[] ProgramAudioPayload { get; }
	public RecordingEnqueueResult? Recording { get; }
	public RuntimeProgramTransitionKind? TransitionKind { get; }
	public byte BlendWeight { get; }
	public V1VisualLayerMode VisualLayerMode { get; }
	public int ActiveGpuSurfacesAfterBoundary { get; }
	public bool IsDisposed => Volatile.Read(ref _programPixels) is null;

	internal GpuReadbackLease RetainProgramPixels() => ProgramPixelsLease.Retain();

	public void Dispose()
	{
		Interlocked.Exchange(ref _programPixels, null)?.Dispose();
	}

	private GpuReadbackLease ProgramPixelsLease =>
		Volatile.Read(ref _programPixels) ?? throw new ObjectDisposedException(nameof(V1ProgramBoundaryResult));
}

public sealed record V1GraphicsOverlaySnapshot(
	bool AssetLoaded,
	string? AssetName,
	uint AssetWidth,
	uint AssetHeight,
	bool Visible,
	double PositionX,
	double PositionY,
	double Scale);

public enum V1CompositingLayerKind
{
	LegacyVisual = 1,
	BitmapGraphics = 2,
	ProductionCg = 3
}

public sealed record V1CompositingLayerSnapshot
{
	private readonly ReadOnlyCollection<PreparedCompositingProcessingNodeState> _processingStack;

	public V1CompositingLayerSnapshot(
		string layerId,
		V1CompositingLayerKind kind,
		int order,
		bool visible,
		byte opacity,
		double positionX,
		double positionY,
		double scale,
		string contentIdentity,
		double rotationDegrees = 0.0,
		double anchorX = 0.0,
		double anchorY = 0.0,
		double cropLeft = 0.0,
		double cropTop = 0.0,
		double cropRight = 0.0,
		double cropBottom = 0.0,
		PreparedCompositingProcessingNodeState? processingNode = null,
		IReadOnlyList<PreparedCompositingProcessingNodeState>? processingStack = null)
	{
		if (processingNode is not null && processingStack is not null &&
			(processingStack.Count == 0 || !Equals(processingNode, processingStack[0])))
		{
			throw new ArgumentException("Legacy processing-node projection must match the first canonical processing-stack node.", nameof(processingStack));
		}
		var canonical = processingStack is null
			? processingNode is null ? Array.Empty<PreparedCompositingProcessingNodeState>() : new[] { processingNode }
			: processingStack.ToArray();
		if (canonical.Length > PreparedCompositingProcessingStackLimits.MaximumNodeCount)
			throw new ArgumentException($"Runtime compositing processing stack supports at most {PreparedCompositingProcessingStackLimits.MaximumNodeCount} nodes.", nameof(processingStack));
		if (canonical.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != canonical.Length)
			throw new ArgumentException("Runtime compositing processing node identities must be unique.", nameof(processingStack));

		LayerId = layerId;
		Kind = kind;
		Order = order;
		Visible = visible;
		Opacity = opacity;
		PositionX = positionX;
		PositionY = positionY;
		Scale = scale;
		ContentIdentity = contentIdentity;
		RotationDegrees = rotationDegrees;
		AnchorX = anchorX;
		AnchorY = anchorY;
		CropLeft = cropLeft;
		CropTop = cropTop;
		CropRight = cropRight;
		CropBottom = cropBottom;
		_processingStack = Array.AsReadOnly(canonical);
	}

	public string LayerId { get; }
	public V1CompositingLayerKind Kind { get; }
	public int Order { get; }
	public bool Visible { get; }
	public byte Opacity { get; }
	public double PositionX { get; }
	public double PositionY { get; }
	public double Scale { get; }
	public string ContentIdentity { get; }
	public double RotationDegrees { get; }
	public double AnchorX { get; }
	public double AnchorY { get; }
	public double CropLeft { get; }
	public double CropTop { get; }
	public double CropRight { get; }
	public double CropBottom { get; }
	public IReadOnlyList<PreparedCompositingProcessingNodeState> ProcessingStack => _processingStack;
	public PreparedCompositingProcessingNodeState? ProcessingNode => _processingStack.Count == 0 ? null : _processingStack[0];
}

public sealed record V1AudioInputSnapshot(
	MediaSourceId SourceId,
	AudioStreamId StreamId,
	double Gain,
	bool Muted,
	double LeftPeak,
	double RightPeak,
	double MasterPeak,
	bool Clipping,
	V1AudioHealthState Health,
	bool TestSignalEnabled = false,
	GeneratedAudioTestSignalMode? TestSignalMode = null,
	string? TestSignalActiveChannel = null,
	double? TestSignalFrequencyHz = null,
	double? TestSignalPeakLevel = null);

public sealed record V1AudioProgramSnapshot(
	MediaSourceId ActiveVideoSourceId,
	AudioStreamId ActiveStreamId,
	double Gain,
	bool Muted,
	double LeftPeak,
	double RightPeak,
	double MasterPeak,
	bool Clipping,
	V1AudioHealthState Health,
	AudioRoutingMode RoutingMode = AudioRoutingMode.FollowVideo,
	ulong RoutingRevision = 0,
	MediaSourceId? ActiveAudioSourceId = null);

public sealed record V1AudioProductionBusSnapshot(
	string BusId,
	double MasterGain,
	bool Muted);

public sealed record V1AudioProductionSourceSnapshot(
	MediaSourceId SourceId,
	double Gain,
	bool Muted,
	bool FollowRoutedSource,
	IReadOnlyList<string> BusAssignments);

public sealed record V1AudioCrossfadeSnapshot(
	string BusId,
	MediaSourceId FromSourceId,
	MediaSourceId ToSourceId,
	ulong StartSamplePosition,
	uint DurationSamples,
	AudioCrossfadeLaw Law);

public sealed record V1AudioDuckingSnapshot(
	string BusId,
	bool Enabled,
	MediaSourceId SidechainSourceId,
	IReadOnlyList<MediaSourceId> TargetSourceIds,
	double Threshold,
	double Attenuation,
	uint AttackSamples,
	uint HoldSamples,
	uint ReleaseSamples);

public sealed record V1AudioProductionSnapshot(
	ulong Revision,
	IReadOnlyList<V1AudioProductionBusSnapshot> Buses,
	double ProgramMasterGain,
	bool ProgramMuted,
	AudioClipStrategy ClipStrategy,
	IReadOnlyList<V1AudioProductionSourceSnapshot> Sources,
	V1AudioCrossfadeSnapshot? Crossfade,
	V1AudioDuckingSnapshot? Ducking,
	double LeftPeak,
	double RightPeak,
	double PreClipPeak,
	bool Clipping,
	ulong ClippedSampleValues,
	double DuckingGain,
	double DuckingReduction,
	bool SidechainAvailable,
	double? CrossfadeProgress,
	int ActiveSourceCount,
	int MissingSourceCount);

public sealed record V1RecordingOperatorSnapshot(
	RecordingLifecycleState State,
	TimeSpan Elapsed,
	string? Destination,
	string? FileName,
	string? FinalPath,
	RecordingStatistics Statistics,
	Failure? Failure,
	RecordingProfileId? ActiveProfileId = null,
	RecordingWriterProviderId? ActiveProviderId = null,
	RecordingProfileId? DefaultProfileId = null,
	IReadOnlyList<RecordingProfileDescriptor>? Profiles = null);

public sealed record V1RuntimePerformanceSnapshot(
	TimeSpan Uptime,
	TimeSpan FrameBudget,
	TimeSpan LastFrameProcessingTime,
	ulong DroppedFrames,
	string GpuDeviceName,
	bool GpuHardwareAccelerated,
	double? GpuUtilizationPercent,
	ulong? GpuVramUsedBytes,
	ulong? GpuVramTotalBytes,
	string GpuTelemetryEvidence,
	string CpuDeviceName = "UNVERIFIED",
	int CpuLogicalProcessorCount = 0,
	double? CpuUtilizationPercent = null,
	ulong? SystemMemoryUsedBytes = null,
	ulong? SystemMemoryTotalBytes = null,
	string SystemTelemetryEvidence = "UNVERIFIED",
	string PhysicalGpuDeviceName = "UNVERIFIED",
	double? OutputFramesPerSecond = null,
	TimeSpan LastCompositionDuration = default,
	int ActiveCompositingLayerCount = 0);

public sealed record V1AvSyncDiagnosticsSnapshot(
	bool Enabled,
	string State,
	ulong? EventId,
	string? ExpectedMediaTime,
	ulong? TargetVideoFrameSequence,
	ulong? TargetAudioSamplePosition,
	double? ScheduledVideoOffsetMilliseconds,
	double? SubmitOffsetMilliseconds,
	double? DriftFromBaselineMilliseconds,
	string Detail);

public sealed record V1RuntimeHostSnapshot(
	RuntimeExecutionState Runtime,
	ulong NextSequenceNumber,
	V1TimingHealthState TimingHealth,
	IReadOnlyDictionary<MediaSourceId, V1InputSignalState> InputSignals,
	IReadOnlyCollection<MediaSourceId> BroadcastTestPatternSources,
	IReadOnlyDictionary<MediaSourceId, V1BroadcastTestPatternMode> BroadcastTestPatternModes,
	V1VisualLayerMode VisualLayerMode,
	V1GraphicsOverlaySnapshot GraphicsOverlay,
	IReadOnlyDictionary<MediaSourceId, V1AudioInputSnapshot> AudioInputs,
	V1AudioProgramSnapshot AudioProgram,
	AudioFollowVideoStatistics Audio,
	RecordingSnapshot Recording,
	V1RecordingOperatorSnapshot RecordingOperator,
	V1RuntimePerformanceSnapshot Performance,
	int ActiveGpuSurfaces,
	V1AvSyncDiagnosticsSnapshot? AvSyncDiagnostics = null,
	V1ProductionCgTextSnapshot? ProductionCgText = null,
	IReadOnlyList<RuntimeOutputRoleSnapshot>? OutputRoles = null,
	IReadOnlyList<V1CompositingLayerSnapshot>? CompositingLayers = null,
	V1AudioProductionSnapshot? AudioProduction = null,
	ReplayBufferSnapshot? Replay = null);

/// <summary>
/// Windows V1 reference composition root for committed execution, timed media, GPU composition,
/// Program output, Audio Follow Video and failure-isolated recording. It owns execution, never production authority.
/// </summary>
public sealed class V1RuntimeHostService : IAsyncDisposable
{
	public const int ProgramReadbackBufferCapacity = ProgramRecorder.DefaultQueueCapacity + 3;
	public const int RetainedObservationCapacity = 512;
	public const string LegacyVisualLayerId = "legacy-visual";
	public const string BitmapGraphicsLayerId = "bitmap-graphics";
	public const string ProductionCgLayerId = "production-cg";

	private readonly object _gate = new();
	private readonly object _boundaryExecutionGate = new();
	private readonly object _boundaryCaptureGate = new();
	private readonly VideoFormat _format;
	private readonly VirtualMediaReferenceProvider _virtualMedia;
	private readonly MediaFramePipeline _sourceAPipeline;
	private readonly MediaFramePipeline _sourceBPipeline;
	private readonly IGpuProcessingBackend _gpuBackend;
	private readonly GpuProcessingProvider _gpu;
	private readonly TransactionalRuntime _runtime;
	private readonly VirtualEmbeddedAudioReferenceProvider _virtualAudio;
	private readonly AudioFollowVideoEngine _audio;
	private readonly Dictionary<MediaSourceId, AudioStreamDescriptor> _audioStreams;
	private readonly Dictionary<MediaSourceId, AudioMeterObservation> _audioMeters;
	private readonly Dictionary<MediaSourceId, Queue<float>> _externalAudioQueues;
	private readonly AudioProductionEngine _audioProduction;
	private readonly MediaSourceId[] _audioProductionSourceOrder;
	private readonly float[][] _audioProductionSourceSamples;
	private readonly AudioProductionSourceBuffer[] _audioProductionSourceBuffers;
	private readonly float[] _programAudioMixSamples;
	private readonly AudioStreamId _programAudioStreamId;
	private readonly Dictionary<MediaSourceId, GeneratedAudioTestSignalGenerator> _audioTestSignals = [];
	private readonly Dictionary<MediaSourceId, GeneratedAudioTestSignalFrameInfo> _audioTestSignalFrames = [];
	private readonly Dictionary<MediaSourceId, RgbaFrameBuffer> _backgrounds;
	private readonly RgbaFrameBuffer _blackBackground;
	private readonly RgbaFrameBuffer _broadcastTestPattern;
	private readonly RgbaFrameBuffer _motionTimingTestPattern;
	private readonly MotionTimingTestSignalGenerator _motionTimingTestSignal;
	private readonly AvSyncEventTimeline _avSyncTimeline = new();
	private readonly AvSyncDiagnosticsTracker _avSyncDiagnostics = new();
	private readonly HashSet<MediaSourceId> _broadcastTestPatternSources = [];
	private readonly Dictionary<MediaSourceId, V1BroadcastTestPatternMode> _broadcastTestPatternModes = [];
	private readonly Dictionary<MediaSourceId, V1InputSignalState> _inputSignals;
	private readonly StaticRgbaSource _staticLayer;
	private readonly DynamicRgbaSource _dynamicLayer;
	private readonly DynamicRgbaSource _operatorGraphicsLayer;
	private readonly RgbaFrameBuffer _operatorGraphicsLayerBuffer;
	private readonly byte[] _operatorGraphicsLayerScratch;
	private readonly DynamicRgbaSource _productionCgLayer;
	private readonly RgbaFrameBuffer _productionCgLayerBuffer;
	private readonly byte[] _productionCgLayerScratch;
	private readonly ProductionCgTextRenderer _productionCgRenderer = new();
	private readonly ProgramRecorder _recorder;
	private readonly RuntimeRecordingBridge _recordingBridge;
	private readonly IProgramRecordingPayloadWriter? _recordingPayloadWriter;
	private readonly IConfigurableProgramRecordingWriter? _recordingTargetWriter;
	private readonly IProfileConfigurableProgramRecordingWriter? _profileRecordingTargetWriter;
	private readonly IProgramRecordingProfileCatalogProvider? _recordingProfileCatalogProvider;
	private readonly IProgramRecordingProfileStateProvider? _recordingProfileStateProvider;
	private readonly RuntimeMonitoringHub _monitoringHub;
	private readonly RuntimeMonitoringTap _monitoringTap;
	private readonly RuntimeNetworkOutputBridge _networkOutputBridge;
	private readonly RuntimeReplayService? _replay;
	private readonly Stopwatch _uptimeClock = Stopwatch.StartNew();
	private readonly SystemHardwareTelemetry _hardwareTelemetry = new();
	private readonly BoundedDiagnosticHistory<string> _observations = new(RetainedObservationCapacity);

	private VirtualVideoOutput? _programOutput;
	private MediaSinkId? _programSinkId;
	private VirtualVideoOutput? _auxOutput;
	private MediaSinkId? _auxSinkId;
	private Failure? _auxFailure;
	private AnchoredTransition? _transition;
	private V1VisualLayerMode _visualLayerMode = V1VisualLayerMode.Disabled;
	private byte[]? _operatorGraphicsAsset;
	private string? _operatorGraphicsAssetName;
	private uint _operatorGraphicsAssetWidth;
	private uint _operatorGraphicsAssetHeight;
	private bool _operatorGraphicsVisible;
	private double _operatorGraphicsPositionX = 0.72;
	private double _operatorGraphicsPositionY = 0.06;
	private double _operatorGraphicsScale = 1.0;
	private double _operatorGraphicsRotationDegrees;
	private double _operatorGraphicsAnchorX;
	private double _operatorGraphicsAnchorY;
	private double _operatorGraphicsCropLeft;
	private double _operatorGraphicsCropTop;
	private double _operatorGraphicsCropRight;
	private double _operatorGraphicsCropBottom;
	private IReadOnlyList<PreparedCompositingProcessingNodeState> _operatorGraphicsProcessingStack = Array.Empty<PreparedCompositingProcessingNodeState>();
	private byte _operatorGraphicsOpacity = byte.MaxValue;
	private int _operatorGraphicsLayerOrder = 1;
	private byte[]? _productionCgAsset;
	private uint _productionCgAssetWidth;
	private uint _productionCgAssetHeight;
	private double _productionCgPositionX;
	private double _productionCgPositionY;
	private double _productionCgScale = 1.0;
	private double _productionCgRotationDegrees;
	private double _productionCgAnchorX;
	private double _productionCgAnchorY;
	private double _productionCgCropLeft;
	private double _productionCgCropTop;
	private double _productionCgCropRight;
	private double _productionCgCropBottom;
	private IReadOnlyList<PreparedCompositingProcessingNodeState> _productionCgProcessingStack = Array.Empty<PreparedCompositingProcessingNodeState>();
	private byte _productionCgOpacity = byte.MaxValue;
	private int _productionCgLayerOrder = 2;
	private int _legacyVisualLayerOrder;
	private byte _legacyVisualLayerOpacity = byte.MaxValue;
	private V1ProductionCgTextDefinition? _productionCgDefinition;
	private V1ProductionCgTextSnapshot _productionCgText = V1ProductionCgTextSnapshot.Empty;
	private V1TimingHealthState _timingHealth = V1TimingHealthState.Recovering;
	private TimeSpan _lastFrameProcessingTime;
	private TimeSpan _lastCompositionDuration;
	private int _lastCompositingLayerCount;
	private ulong _droppedFrames;
	private double? _outputFramesPerSecond;
	private ulong _nextSequenceNumber;
	private AudioFollowVideoResult? _lastAudioResult;
	private AudioProductionBlockResult _lastAudioProductionResult;
	private AudioFollowVideoStatistics _publishedAudioStatistics;
	private DateTimeOffset? _recordingStartedAtUtc;
	private DateTimeOffset? _recordingCompletedAtUtc;
	private string? _recordingDestination;
	private string? _recordingFileName;
	private MediaSourceId? _avSyncDiagnosticsSourceId;
	private bool _disposed;

	public V1RuntimeHostService(
		MediaSourceId sourceAId,
		MediaSourceId sourceBId,
		VideoFormat format,
		IProgramRecordingWriter recordingWriter,
		IGpuProcessingBackend? gpuBackend = null,
		IReadOnlyList<RuntimeNetworkOutputTarget>? networkOutputs = null,
		RuntimeReplayService? replayService = null)
	{
		_format = format;
		var configuredNetworkOutputs = networkOutputs ?? Array.Empty<RuntimeNetworkOutputTarget>();
		_virtualMedia = new VirtualMediaReferenceProvider(sourceAId, sourceBId, format);
		_sourceAPipeline = CreatePipeline();
		_sourceBPipeline = CreatePipeline();
		_gpuBackend = gpuBackend ?? new ManagedReferenceGpuBackend();
		var networkReadbackCapacity = configuredNetworkOutputs
			.Sum(target => target.Configuration.QueueCapacity);
		_gpu = new GpuProcessingProvider(
			_gpuBackend,
			checked(ProgramReadbackBufferCapacity + networkReadbackCapacity + (replayService?.QueueCapacity ?? 0)));
		_gpu.Start();
		_runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());

		_virtualAudio = new VirtualEmbeddedAudioReferenceProvider(sourceAId, sourceBId, format);
		_audioStreams = _virtualAudio.Streams.ToDictionary(stream => stream.FollowedVideoSourceId);
		_audio = new AudioFollowVideoEngine(_virtualAudio.Streams, format.FrameRate, sourceAId);
		_publishedAudioStatistics = _audio.Statistics;
		_audioMeters = _audioStreams.Keys.ToDictionary(
			sourceId => sourceId,
			_ => new AudioMeterObservation(new AudioStereoMeter(0, 0), Available: false, External: false));
		_externalAudioQueues = _audioStreams.Keys.ToDictionary(
			sourceId => sourceId,
			_ => new Queue<float>());
		_audioProductionSourceOrder = _audioStreams.Keys
			.OrderBy(sourceId => sourceId.ToString(), StringComparer.Ordinal)
			.ToArray();
		_audioProduction = new AudioProductionEngine(
			AudioProductionConfiguration.CreateLegacyCompatible(_audioProductionSourceOrder));
		_lastAudioProductionResult = new AudioProductionBlockResult(
			AudioBusId.Program, 0, 0, 0, 0, 0, 1, 0, true, null, 0, 0);
		var productionAudioFormat = _audioStreams.Values.Select(stream => stream.Format).Distinct().Single();
		var maximumAudioFramesPerBoundary = checked((int)(
			((long)productionAudioFormat.SampleRate * format.FrameRate.Denominator + format.FrameRate.Numerator - 1) /
			format.FrameRate.Numerator));
		var maximumAudioValuesPerBoundary = checked(maximumAudioFramesPerBoundary * (int)productionAudioFormat.ChannelCount);
		_audioProductionSourceSamples = _audioProductionSourceOrder
			.Select(_ => new float[maximumAudioValuesPerBoundary])
			.ToArray();
		_audioProductionSourceBuffers = new AudioProductionSourceBuffer[_audioProductionSourceOrder.Length];
		_programAudioMixSamples = new float[maximumAudioValuesPerBoundary];
		_programAudioStreamId = new AudioStreamId(HostIdentity.Create("audio-bus", AudioBusId.Program.Value));

		_backgrounds = new Dictionary<MediaSourceId, RgbaFrameBuffer>
		{
			[sourceAId] = RgbaFrameBuffer.Solid(format, 32, 72, 196),
			[sourceBId] = RgbaFrameBuffer.Solid(format, 196, 72, 32)
		};
		_blackBackground = RgbaFrameBuffer.Solid(format, 0, 0, 0);
		var broadcastTestPattern = new BroadcastTestPatternGenerator(new BroadcastTestPatternConfiguration(format));
		_broadcastTestPattern = new RgbaFrameBuffer(format, broadcastTestPattern.Pixels.Span);
		_motionTimingTestPattern = new RgbaFrameBuffer(format, broadcastTestPattern.Pixels.Span);
		var syncAudioSampleRate = _audioStreams.Values.Select(stream => stream.Format.SampleRate).Distinct().Single();
		_motionTimingTestSignal = new MotionTimingTestSignalGenerator(format, syncAudioSampleRate, _avSyncTimeline);
		_inputSignals = new Dictionary<MediaSourceId, V1InputSignalState>
		{
			[sourceAId] = V1InputSignalState.Valid,
			[sourceBId] = V1InputSignalState.Valid
		};

		_staticLayer = new StaticRgbaSource(
			new MediaSourceId(HostIdentity.Create("v1-layer-source", "static")),
			RgbaFrameBuffer.Solid(format, 24, 220, 88, 72));
		_dynamicLayer = new DynamicRgbaSource(
			new MediaSourceId(HostIdentity.Create("v1-layer-source", "dynamic")),
			RgbaFrameBuffer.Solid(format, 235, 200, 24, 72));
		_operatorGraphicsLayerBuffer = RgbaFrameBuffer.Solid(format, 0, 0, 0, 0);
		_operatorGraphicsLayerScratch = new byte[RgbaFrameBuffer.RequiredByteLength(format)];
		_operatorGraphicsLayer = new DynamicRgbaSource(
			new MediaSourceId(HostIdentity.Create("v1-layer-source", "operator-graphics")),
			_operatorGraphicsLayerBuffer);
		_productionCgLayerBuffer = RgbaFrameBuffer.Solid(format, 0, 0, 0, 0);
		_productionCgLayerScratch = new byte[RgbaFrameBuffer.RequiredByteLength(format)];
		_productionCgLayer = new DynamicRgbaSource(
			new MediaSourceId(HostIdentity.Create("v1-layer-source", "production-cg")),
			_productionCgLayerBuffer);

		if (recordingWriter is null)
			throw new ArgumentNullException(nameof(recordingWriter));
		_recordingPayloadWriter = recordingWriter as IProgramRecordingPayloadWriter;
		_recordingTargetWriter = recordingWriter as IConfigurableProgramRecordingWriter;
		_profileRecordingTargetWriter = recordingWriter as IProfileConfigurableProgramRecordingWriter;
		_recordingProfileCatalogProvider = recordingWriter as IProgramRecordingProfileCatalogProvider;
		_recordingProfileStateProvider = recordingWriter as IProgramRecordingProfileStateProvider;
		_recorder = new ProgramRecorder(recordingWriter);
		_recordingBridge = new RuntimeRecordingBridge(_recorder);
		_networkOutputBridge = new RuntimeNetworkOutputBridge(configuredNetworkOutputs);
		_replay = replayService;
		_monitoringHub = new RuntimeMonitoringHub();
		_monitoringTap = new RuntimeMonitoringTap(
			_monitoringHub,
			_gpu.CanExportSharedMonitoringResources
				? MonitoringSharedResourceCapabilityState.Available
				: MonitoringSharedResourceCapabilityState.Unavailable);
	}

	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors
	{
		get
		{
			var providers = new List<ProviderDescriptor> { _virtualMedia.Descriptor, _gpu.Descriptor };
			if (_networkOutputBridge.Enabled)
				providers.Add(_networkOutputBridge.ProviderDescriptor);
			return Array.AsReadOnly(providers.ToArray());
		}
	}

	public IReadOnlyList<VirtualOutputFrame> ProgramFrames =>
		_programOutput?.Frames ?? Array.Empty<VirtualOutputFrame>();

	public IReadOnlyList<VirtualOutputFrame> AuxFrames =>
		_auxOutput?.Frames ?? Array.Empty<VirtualOutputFrame>();

	public MediaSinkId? ProgramOutputSinkId
	{
		get
		{
			lock (_gate)
				return _programOutput?.SinkId;
		}
	}

	public MediaSinkId? AuxOutputSinkId
	{
		get
		{
			lock (_gate)
				return _auxOutput?.SinkId;
		}
	}

	public RuntimeMonitoringHub MonitoringHub => _monitoringHub;
	public RuntimeMonitoringTapStatistics MonitoringStatistics => _monitoringTap.Statistics;
	public VideoFormat Format => _format;
	public int ProductionCgCachedSurfaceCount => _productionCgRenderer.CachedSurfaceCount;

	public bool HasCommittedExecution
	{
		get
		{
			lock (_gate)
				return _runtime.ActiveExecution is not null;
		}
	}

	public MediaSourceId? CommittedProgramSourceId
	{
		get
		{
			lock (_gate)
			{
				var execution = _runtime.ActiveExecution;
				var sink = _programSinkId;
				if (execution is null || sink is null)
					return null;
				return execution.PreparedExecution.Bindings
					.FirstOrDefault(binding => binding.MediaSinkId == sink.Value)
					?.MediaSourceId;
			}
		}
	}

	public MediaSourceId? CommittedAuxSourceId
	{
		get
		{
			lock (_gate)
			{
				var execution = _runtime.ActiveExecution;
				if (execution is null || _auxSinkId is null)
					return null;
				return execution.PreparedExecution.Bindings
					.SingleOrDefault(binding => string.Equals(binding.OutputRoleId, "aux", StringComparison.Ordinal) && binding.MediaSinkId == _auxSinkId)
					?.MediaSourceId;
			}
		}
	}

	public IReadOnlyList<string> Observations => _observations.Snapshot();
	public ulong OverwrittenObservationCount => _observations.OverwrittenCount;
	public IReadOnlyList<string> RecentObservations(int maximumCount) => _observations.SnapshotNewest(maximumCount);
	public GpuReadbackPoolStatistics ProgramReadbackPoolStatistics => _gpu.ReadbackPoolStatistics;
	public GpuMemoryTransferStatistics GpuMemoryTransfers => _gpu.MemoryTransferStatistics;

	public ulong ProgramFramesWritten
	{
		get
		{
			lock (_gate)
				return _programOutput?.TotalFramesWritten ?? 0;
		}
	}

	public ulong ProgramFramesOverwritten
	{
		get
		{
			lock (_gate)
				return _programOutput?.OverwrittenFrameCount ?? 0;
		}
	}

	public ulong AuxFramesWritten
	{
		get
		{
			lock (_gate)
				return _auxOutput?.TotalFramesWritten ?? 0;
		}
	}

	public ulong AuxFramesOverwritten
	{
		get
		{
			lock (_gate)
				return _auxOutput?.OverwrittenFrameCount ?? 0;
		}
	}

	public V1RuntimeHostSnapshot Snapshot
	{
		get
		{
			var hardware = _hardwareTelemetry.Sample();
			lock (_gate)
			{
				return new V1RuntimeHostSnapshot(
					_runtime.State,
					_nextSequenceNumber,
					_timingHealth,
					new ReadOnlyDictionary<MediaSourceId, V1InputSignalState>(
						_inputSignals.ToDictionary(
							pair => pair.Key,
							pair => _broadcastTestPatternSources.Contains(pair.Key) ? V1InputSignalState.Valid : pair.Value)),
					Array.AsReadOnly(_broadcastTestPatternSources.OrderBy(sourceId => sourceId.ToString(), StringComparer.Ordinal).ToArray()),
					new ReadOnlyDictionary<MediaSourceId, V1BroadcastTestPatternMode>(
						_broadcastTestPatternModes.ToDictionary(pair => pair.Key, pair => pair.Value)),
					_operatorGraphicsVisible ? V1VisualLayerMode.Static : _visualLayerMode,
					GraphicsOverlaySnapshotUnsafe(),
					AudioInputSnapshotsUnsafe(),
					AudioProgramSnapshotUnsafe(),
					_publishedAudioStatistics,
					_recorder.Snapshot,
					RecordingOperatorSnapshotUnsafe(),
					PerformanceSnapshotUnsafe(hardware),
					_gpu.ActiveSurfaceCount,
					AvSyncDiagnosticsSnapshotUnsafe(),
					_productionCgText,
					OutputRoleSnapshotsUnsafe(),
					CompositingLayerSnapshotsUnsafe(),
					AudioProductionSnapshotUnsafe(),
					_replay?.Snapshot);
			}
		}
	}

	public RuntimeHostApplyResult ApplyExecution(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition = null)
	{
		lock (_boundaryExecutionGate)
		{
			ArgumentNullException.ThrowIfNull(preparedExecution);
			lock (_gate)
			{
			ThrowIfDisposed();

			var programBinding = preparedExecution.Bindings.SingleOrDefault(binding => binding.MediaSinkId == programSinkId)
				?? throw new InvalidOperationException("Prepared execution does not contain the requested Program sink.");
			if (programBinding.OutputRoleId is { Length: > 0 } programRoleId &&
				!string.Equals(programRoleId, "program", StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Requested Program sink is bound to a different output role.");
			}

			var auxBindings = preparedExecution.Bindings
				.Where(binding => string.Equals(binding.OutputRoleId, "aux", StringComparison.Ordinal))
				.ToArray();
			if (auxBindings.Length > 1)
				throw new InvalidOperationException("Prepared execution contains more than one Aux output binding.");
			if (auxBindings.Length == 1 && (auxBindings[0].MediaSourceId is null || auxBindings[0].MediaSinkId is null))
				throw new InvalidOperationException("Aux output binding requires both source and sink identities.");

			var recording = _recorder.Snapshot;
			if (recording.State is RecordingLifecycleState.Recording or RecordingLifecycleState.Finalizing &&
				recording.Output is { } recordingOutput &&
				recordingOutput.ProgramSinkId != programSinkId)
			{
				var failure = new Failure(
					"runtime.output.program_rebind_recording_active",
					"Program sink cannot change while the active recording session is bound to the current Program sink.");
				Observe($"runtime.prepare.rejected:{failure.Code}");
				return new RuntimeHostApplyResult(
					new RuntimePrepareResult(
						RuntimeContractVersion.Current,
						preparedExecution.PreparedExecutionId,
						RuntimePrepareStatus.Rejected,
						null,
						failure),
					null,
					null);
			}

			var compositingFailure = ValidatePreparedCompositingStateUnsafe(preparedExecution.CompositingState);
			if (compositingFailure is not null)
			{
				Observe($"runtime.prepare.rejected:{compositingFailure.Value.Code}");
				return new RuntimeHostApplyResult(
					new RuntimePrepareResult(
						RuntimeContractVersion.Current,
						preparedExecution.PreparedExecutionId,
						RuntimePrepareStatus.Rejected,
						null,
						compositingFailure),
					null,
					null);
			}

			var rollbackCompositing = preparedExecution.CompositingState is null
				? null
				: CaptureCompositingRollbackStateUnsafe();
			var compositingStaged = false;
			if (preparedExecution.CompositingState is not null)
			{
				try
				{
					compositingStaged = true;
					ApplyPreparedCompositingStateUnsafe(preparedExecution.CompositingState, observe: false);
				}
				catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
				{
					RestoreCompositingRollbackStateUnsafe(rollbackCompositing);
					var failure = new Failure(
						"runtime.compositing.stage_failed",
						$"Prepared compositing state could not be staged: {exception.GetType().Name}.");
					Observe($"runtime.prepare.rejected:{failure.Code}");
					return new RuntimeHostApplyResult(
						new RuntimePrepareResult(
							RuntimeContractVersion.Current,
							preparedExecution.PreparedExecutionId,
							RuntimePrepareStatus.Rejected,
							null,
							failure),
						null,
						null);
				}
			}

			RuntimePrepareResult prepare;
			try
			{
				prepare = _runtime.Prepare(preparedExecution);
			}
			catch
			{
				if (compositingStaged)
					RestoreCompositingRollbackStateUnsafe(rollbackCompositing);
				throw;
			}
			if (prepare.Status != RuntimePrepareStatus.Prepared)
			{
				if (compositingStaged)
					RestoreCompositingRollbackStateUnsafe(rollbackCompositing);
				Observe($"runtime.prepare.rejected:{prepare.Failure?.Code}");
				return new RuntimeHostApplyResult(prepare, null, null);
			}

			RuntimeCommitResult commit;
			try
			{
				commit = _runtime.Commit(new RuntimeCommitRequest(
					RuntimeContractVersion.Current,
					preparedExecution.PreparedExecutionId,
					prepare.ReservationId!.Value,
					_runtime.State.ExecutionRevision));
			}
			catch
			{
				if (compositingStaged)
					RestoreCompositingRollbackStateUnsafe(rollbackCompositing);
				throw;
			}

			if (commit.Status != RuntimeCommitStatus.Committed)
			{
				if (compositingStaged)
					RestoreCompositingRollbackStateUnsafe(rollbackCompositing);
				Observe($"runtime.commit.rejected:{commit.Failure?.Code}");
				return new RuntimeHostApplyResult(prepare, commit, null);
			}

			if (_programSinkId != programSinkId || _programOutput is null)
				_programOutput = _virtualMedia.CreateOutput(programSinkId);
			_programSinkId = programSinkId;
			var auxBinding = auxBindings.SingleOrDefault();
			if (auxBinding is null)
			{
				_auxSinkId = null;
				_auxOutput = null;
				_auxFailure = null;
			}
			else
			{
				var auxSinkId = auxBinding.MediaSinkId!.Value;
				if (_auxSinkId != auxSinkId || _auxOutput is null)
					_auxOutput = _virtualMedia.CreateOutput(auxSinkId);
				_auxSinkId = auxSinkId;
				_auxFailure = null;
			}
			_transition = transition is null ? null : new AnchoredTransition(transition, _nextSequenceNumber);
			if (compositingStaged)
				Observe($"compositing.scene.applied:{string.Join(",", preparedExecution.CompositingState!.Layers.Select(layer => layer.LayerId))}");
			Observe($"runtime.commit.committed:{commit.ExecutionRevision}");
			if (transition is not null)
				Observe($"runtime.transition.anchored:{transition.Kind}:{_nextSequenceNumber}:{transition.DurationFrames}");

			return new RuntimeHostApplyResult(prepare, commit, _nextSequenceNumber);
			}
		}
	}

	public V1ProgramBoundaryResult ProcessNextBoundary()
	{
		lock (_boundaryExecutionGate)
		{
			GpuFrame? gpuA = null;
			GpuFrame? gpuB = null;
			GpuFrame? previewMonitoringFrame = null;
			IReadOnlyList<MaterializedCompositingLayer>? materializedLayers = null;
			try
			{
				CommittedRuntimeExecution execution;
				MediaSourceId committedSource;
				MediaSourceId committedPreviewSource;
				MediaSourceId routedAudioSource;
				MediaSinkId programSinkId;
				ulong sequence;
				FrameDescriptor frameA;
				FrameDescriptor frameB;
				GpuFrame fromFrame;
				GpuFrame toFrame;
				GpuTransition gpuTransition;
				byte blendWeight;
				bool transitionComplete;
				RuntimeProgramTransitionKind? transitionKind;
				AnchoredTransition? boundaryTransition;
				VirtualVideoOutput programOutput;
				bool avSyncEnabled;
				AudioBufferDescriptor routedAudioBuffer;
				AudioBufferDescriptor programAudioBuffer;
				AudioFollowVideoResult audio;
				byte[] programAudioPayload;
				AudioStereoMeter measuredAudio;
				AudioBufferDescriptor? afvBuffer;
				AvSyncAudioEventObservation? audioSyncEvent;
				V1VisualLayerMode visualLayerMode;
				RuntimeMonitoringSourceSnapshot? monitoringSources;
				bool recordingActive;
				Failure? auxFailure;
				MediaSourceId? auxNetworkSource = null;
				FrameDescriptor? auxNetworkFrame = null;
				Dictionary<MediaSourceId, GpuFrame>? gpuFrames = null;

				lock (_boundaryCaptureGate)
				{
					lock (_gate)
					{
						ThrowIfDisposed();
						execution = _runtime.ActiveExecution ?? throw new InvalidOperationException("RuntimeHost requires a committed execution before processing media.");
						var programSink = _programSinkId ?? throw new InvalidOperationException("RuntimeHost has no committed Program sink.");
						programSinkId = programSink;
						var programBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding => binding.MediaSinkId == programSink)
							?? throw new InvalidOperationException("Committed execution must contain exactly one Program binding.");
						committedSource = programBinding.MediaSourceId
							?? throw new InvalidOperationException("Committed Program binding must contain a media source.");
						var previewBindings = execution.PreparedExecution.Bindings
							.Where(binding =>
								binding.OutputRoleId is null &&
								binding.MediaSourceId is not null &&
								binding.MediaSinkId is not null)
							.ToArray();
						if (previewBindings.Length != 1)
							throw new InvalidOperationException("Committed execution must contain exactly one Preview route binding.");
						committedPreviewSource = previewBindings[0].MediaSourceId!.Value;
						programOutput = _programOutput ?? throw new InvalidOperationException("RuntimeHost has no bound Program output.");
						routedAudioSource = _audio.ResolveAudioSource(committedSource);
						avSyncEnabled = _audio.RoutingState.Mode == AudioRoutingMode.FollowVideo &&
							IsAvSyncDiagnosticsEnabledUnsafe(committedSource);
						if (!avSyncEnabled)
						{
							if (_avSyncDiagnosticsSourceId is not null)
								ResetAvSyncDiagnosticsUnsafe();
						}
						else if (_avSyncDiagnosticsSourceId != committedSource)
						{
							ResetAvSyncDiagnosticsUnsafe();
							_avSyncDiagnosticsSourceId = committedSource;
						}
						sequence = _nextSequenceNumber;
						if (sequence == ulong.MaxValue)
							throw new InvalidOperationException("RuntimeHost frame sequence exhausted.");
					}

					frameA = ProcessTimedInput(_virtualMedia.SourceA, _sourceAPipeline, sequence);
					frameB = ProcessTimedInput(_virtualMedia.SourceB, _sourceBPipeline, sequence);
					var frames = new Dictionary<MediaSourceId, FrameDescriptor>
					{
						[frameA.SourceId] = frameA,
						[frameB.SourceId] = frameB
					};
					auxFailure = WriteAuxFrame(execution.PreparedExecution, frames);
					if (_networkOutputBridge.HasRole("aux"))
					{
						var auxNetworkBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding =>
							string.Equals(binding.OutputRoleId, "aux", StringComparison.Ordinal));
						if (auxNetworkBinding?.MediaSourceId is { } configuredAuxSource &&
							frames.TryGetValue(configuredAuxSource, out var configuredAuxFrame))
						{
							auxNetworkSource = configuredAuxSource;
							auxNetworkFrame = configuredAuxFrame;
						}
					}

					var contentA = ResolveInputContent(frameA);
					var contentB = ResolveInputContent(frameB);
					monitoringSources = _monitoringTap.CaptureSources(
						frameA.SourceId,
						contentA.Pixels,
						frameB.SourceId,
						contentB.Pixels,
						_format,
						frameA.Timing);
					gpuA = RequiresGpuSourceUnsafe(frameA.SourceId, committedSource) ||
						(monitoringSources is not null && frameA.SourceId == committedPreviewSource) ||
						(auxNetworkSource is { } configuredAuxA && frameA.SourceId == configuredAuxA)
						? MaterializeInput(frameA, contentA)
						: null;
					gpuB = RequiresGpuSourceUnsafe(frameB.SourceId, committedSource) ||
						(monitoringSources is not null && frameB.SourceId == committedPreviewSource) ||
						(auxNetworkSource is { } configuredAuxB && frameB.SourceId == configuredAuxB)
						? MaterializeInput(frameB, contentB)
						: null;
					gpuFrames = new Dictionary<MediaSourceId, GpuFrame>();
					if (gpuA is not null)
						gpuFrames.Add(frameA.SourceId, gpuA);
					if (gpuB is not null)
						gpuFrames.Add(frameB.SourceId, gpuB);
					if (monitoringSources is not null)
						gpuFrames.TryGetValue(committedPreviewSource, out previewMonitoringFrame);

					boundaryTransition = _transition;
					transitionKind = boundaryTransition?.Intent.Kind;
					(fromFrame, toFrame, gpuTransition, blendWeight, transitionComplete) =
						ResolveTransition(committedSource, sequence, gpuFrames);
					materializedLayers = MaterializeLayers(fromFrame.Descriptor.Timing);

					lock (_gate)
					{
						RefreshVirtualAudioMetersUnsafe(sequence);
						PrepareAudioProductionBlockUnsafe(
							sequence,
							routedAudioSource,
							out routedAudioBuffer,
							out measuredAudio,
							out afvBuffer);
						programAudioBuffer = CreateProgramAudioBuffer(sequence);
						visualLayerMode = (_operatorGraphicsVisible || _productionCgText.Visible)
							? V1VisualLayerMode.Static
							: _visualLayerMode;
						recordingActive = _recorder.Snapshot.State == RecordingLifecycleState.Recording;
					}

					audioSyncEvent = avSyncEnabled
						? _avSyncTimeline.InspectAudio(routedAudioBuffer.Timing, _format.FrameRate, routedAudioBuffer.Format.SampleRate)
						: null;
				}

				GpuProcessingResult composite;
				try
				{
					composite = _gpu.Composite(GpuCompositeRequest.WithLayers(
						committedSource,
						fromFrame,
						toFrame,
						gpuTransition,
						materializedLayers.Select(layer => layer.Layer)));
				}
				finally
				{
					foreach (var materialized in materializedLayers)
						materialized.Frame.Dispose();
					materializedLayers = null;
				}

				Observe($"gpu.composite.completed:layers={composite.LayerCount}:durationTicks={composite.Duration.Ticks}:surfaces={_gpu.ActiveSurfaceCount}");
				if (!composite.Succeeded)
				{
					Observe($"gpu.composite.failed:{composite.Failure?.Code}");
					throw new InvalidOperationException(composite.Failure?.Message ?? "GPU composite failed.");
				}

				using var output = composite.Frame!;
				var pixels = _gpu.RentReadback(output);
				try
				{
					var probe = ProbeCenter(pixels.Memory.Span, _format);
					programOutput.WriteFrame(output.Descriptor);
					audio = _audio.ProcessBoundary(
						committedSource,
						sequence,
						afvBuffer,
						measuredAudio);
					var requiredProgramAudioValues = checked((int)(
						programAudioBuffer.Timing.SampleCount * programAudioBuffer.Format.ChannelCount));
					_lastAudioProductionResult = _audioProduction.ProcessBus(
						AudioBusId.Program,
						programAudioBuffer.Timing.SamplePosition,
						programAudioBuffer.Timing.SampleCount,
						routedAudioSource,
						_audioProductionSourceBuffers,
						_programAudioMixSamples.AsSpan(0, requiredProgramAudioValues));
					programAudioPayload =
						audio.Status == AudioFollowVideoStatus.Underrun &&
						_lastAudioProductionResult.ActiveSourceCount == 0
							? Array.Empty<byte>()
							: MemoryMarshal.AsBytes(
								_programAudioMixSamples.AsSpan(0, requiredProgramAudioValues)).ToArray();
					var videoSyncEvent = avSyncEnabled
						? _motionTimingTestSignal.InspectSyncEvent(output.Descriptor.Timing)
						: default;
					GpuSharedMonitoringResourceLease? previewSharedMonitoringResource = null;
					GpuSharedMonitoringResourceLease? programSharedMonitoringResource = null;
					if (monitoringSources is not null)
					{
						try
						{
							if (previewMonitoringFrame is not null)
								_gpu.TryExportMonitoringResource(previewMonitoringFrame, out previewSharedMonitoringResource);
						}
						catch (Exception exception)
						{
							Observe($"monitoring.preview_gpu_resource.export_failed:{exception.GetType().Name}");
							previewSharedMonitoringResource?.Dispose();
							previewSharedMonitoringResource = null;
						}

						try
						{
							_gpu.TryExportMonitoringResource(output, out programSharedMonitoringResource);
						}
						catch (Exception exception)
						{
							Observe($"monitoring.program_gpu_resource.export_failed:{exception.GetType().Name}");
							programSharedMonitoringResource?.Dispose();
							programSharedMonitoringResource = null;
						}
					}

					try
					{
						_monitoringTap.TryCapture(
							monitoringSources,
							committedSource,
							pixels,
							_format,
							output.Descriptor.Timing,
							programSharedMonitoringResource,
							committedPreviewSource,
							previewSharedMonitoringResource);
						previewSharedMonitoringResource = null;
						programSharedMonitoringResource = null;
					}
					finally
					{
						previewSharedMonitoringResource?.Dispose();
						programSharedMonitoringResource?.Dispose();
					}

					RecordingEnqueueResult? recording = null;
					if (recordingActive)
					{
						var payloadStaged = false;
						if (_recordingPayloadWriter is not null)
						{
							try
							{
								GpuRecordingPayloadLease? recordingPayload = new GpuRecordingPayloadLease(pixels.Retain());
								try
								{
									_recordingPayloadWriter.StagePayload(
										sequence,
										recordingPayload,
										programAudioPayload);
									recordingPayload = null;
									payloadStaged = true;
								}
								finally
								{
									recordingPayload?.Dispose();
								}
							}
							catch (Exception exception)
							{
								Observe($"recording.payload.stage.failed:{exception.GetType().Name}");
							}
						}

						try
						{
							recording = _recordingBridge.TryRecordCommittedProgram(execution, output.Descriptor, programAudioBuffer);
						}
						catch (Exception exception)
						{
							if (payloadStaged)
								_recordingPayloadWriter?.DiscardPayload(sequence);
							var failure = new Failure(
								"recording.runtime.enqueue_failed",
								$"Recording enqueue failed without interrupting Program execution: {exception.GetType().Name}.");
							recording = RecordingEnqueueResult.Rejected(failure);
							Observe($"recording.runtime.enqueue_failed:{exception.GetType().Name}");
						}

						if (payloadStaged && recording is { Accepted: false })
							_recordingPayloadWriter?.DiscardPayload(sequence);
					}

					if (_replay is not null)
					{
						GpuRecordingPayloadLease? replayPayload = null;
						try
						{
							replayPayload = new GpuRecordingPayloadLease(pixels.Retain());
							var replay = _replay.TryCapture(
								programSinkId,
								output.Descriptor,
								programAudioBuffer,
								replayPayload,
								programAudioPayload);
							replayPayload = null;
							if (!replay.Accepted)
								Observe($"replay.capture:{replay.Status}:{replay.Failure?.Code}");
						}
						catch (Exception exception)
						{
							Observe($"replay.capture.enqueue_failed:{exception.GetType().Name}");
						}
						finally
						{
							replayPayload?.Dispose();
						}
					}

					try
					{
						var networkOutput = _networkOutputBridge.TrySubmit(
							"program",
							pixels.Retain(),
							output.Descriptor.Timing,
							programAudioBuffer,
							programAudioPayload);
						if (networkOutput is { Status: not NetworkOutputEnqueueStatus.Accepted })
							Observe($"network.output.program:{networkOutput.Status}:{networkOutput.Failure?.Code}");
					}
					catch (Exception exception)
					{
						Observe($"network.output.program.enqueue_failed:{exception.GetType().Name}");
					}

					if (auxNetworkSource is { } networkAuxSource &&
						auxNetworkFrame is { } networkAuxFrame &&
						gpuFrames is not null && gpuFrames.TryGetValue(networkAuxSource, out var networkAuxGpuFrame))
					{
						try
						{
							var auxPixels = _gpu.RentReadback(networkAuxGpuFrame);
							var networkOutput = _networkOutputBridge.TrySubmit(
								"aux",
								auxPixels,
								networkAuxFrame.Timing,
								programAudioBuffer,
								programAudioPayload);
							if (networkOutput is { Status: not NetworkOutputEnqueueStatus.Accepted })
								Observe($"network.output.aux:{networkOutput.Status}:{networkOutput.Failure?.Code}");
						}
						catch (Exception exception)
						{
							Observe($"network.output.aux.enqueue_failed:{exception.GetType().Name}");
						}
					}

					output.Dispose();
					gpuB?.Dispose();
					gpuB = null;
					gpuA?.Dispose();
					gpuA = null;
					var activeGpuSurfacesAfterBoundary = _gpu.ActiveSurfaceCount;

					lock (_gate)
					{
						_auxFailure = auxFailure;
						_lastAudioResult = audio;
						_publishedAudioStatistics = _audio.Statistics;
						_lastCompositionDuration = composite.Duration;
						_lastCompositingLayerCount = composite.LayerCount;
						if (avSyncEnabled && videoSyncEvent.IsFlashFrame)
							_avSyncDiagnostics.RecordVideoSubmit(videoSyncEvent, Stopwatch.GetTimestamp());
						if (audioSyncEvent is { ContainsPulse: true } pulseEvent)
							_avSyncDiagnostics.RecordAudioSubmit(pulseEvent, Stopwatch.GetTimestamp());

						if (transitionComplete && Equals(_transition, boundaryTransition))
						{
							Observe($"runtime.transition.completed:{transitionKind}:{sequence}");
							_transition = null;
						}

						_nextSequenceNumber++;
						Observe($"program.frame:{sequence}:{committedSource}");
					}

					return new V1ProgramBoundaryResult(
						sequence,
						committedSource,
						output.Descriptor,
						pixels,
						probe,
						audio,
						programAudioBuffer,
						programAudioPayload,
						recording,
						transitionKind,
						blendWeight,
						visualLayerMode,
						activeGpuSurfacesAfterBoundary);
				}
				catch
				{
					pixels.Dispose();
					throw;
				}
			}
			finally
			{
				if (materializedLayers is not null)
				{
					foreach (var materialized in materializedLayers)
						materialized.Frame.Dispose();
				}
				gpuB?.Dispose();
				gpuA?.Dispose();
			}
		}
	}

	public void SetVisualLayerMode(V1VisualLayerMode mode)
	{
		if (!Enum.IsDefined(typeof(V1VisualLayerMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			_visualLayerMode = mode;
			Observe($"graphics.layer.mode:{mode}");
		}
	}

	public V1GraphicsOverlaySnapshot LoadGraphicsOverlay(
		string assetName,
		uint width,
		uint height,
		ReadOnlySpan<byte> rgbaPixels)
	{
		if (string.IsNullOrWhiteSpace(assetName))
			throw new ArgumentException("Graphics asset name is required.", nameof(assetName));
		if (width == 0 || height == 0 || width > 384 || height > 384)
			throw new ArgumentOutOfRangeException(nameof(width), "V1 graphics assets must be between 1x1 and 384x384 pixels.");
		var expected = checked((int)((ulong)width * height * 4UL));
		if (rgbaPixels.Length != expected)
			throw new ArgumentException($"Graphics RGBA payload requires exactly '{expected}' bytes.", nameof(rgbaPixels));

		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			_operatorGraphicsAsset = rgbaPixels.ToArray();
			_operatorGraphicsAssetName = assetName.Trim();
			_operatorGraphicsAssetWidth = width;
			_operatorGraphicsAssetHeight = height;
			_operatorGraphicsRotationDegrees = 0;
			_operatorGraphicsAnchorX = 0;
			_operatorGraphicsAnchorY = 0;
			_operatorGraphicsCropLeft = 0;
			_operatorGraphicsCropTop = 0;
			_operatorGraphicsCropRight = 0;
			_operatorGraphicsCropBottom = 0;
			_operatorGraphicsProcessingStack = Array.Empty<PreparedCompositingProcessingNodeState>();
			RebuildOperatorGraphicsLayerUnsafe();
			Observe($"graphics.overlay.asset.loaded:{_operatorGraphicsAssetName}:{width}x{height}");
			return GraphicsOverlaySnapshotUnsafe();
		}
	}

	public V1GraphicsOverlaySnapshot ApplyProductionCgText(V1ProductionCgTextDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		if (definition.BoxWidth > _format.Width || definition.BoxHeight > _format.Height)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG bounding box must fit inside the active Program format.");

		lock (_boundaryCaptureGate)
		{
			lock (_gate)
				ThrowIfDisposed();

			var rendered = _productionCgRenderer.Render(definition);
			var (originX, originY) = ResolveProductionCgOrigin(definition);
			lock (_gate)
			{
				ThrowIfDisposed();
				_productionCgAsset = rendered.RgbaPixels;
				_productionCgAssetWidth = rendered.Width;
				_productionCgAssetHeight = rendered.Height;
				_productionCgPositionX = _format.Width <= 1 ? 0 : originX / (double)(_format.Width - 1);
				_productionCgPositionY = _format.Height <= 1 ? 0 : originY / (double)(_format.Height - 1);
				_productionCgScale = 1.0;
				_productionCgRotationDegrees = 0;
				_productionCgAnchorX = 0;
				_productionCgAnchorY = 0;
				_productionCgCropLeft = 0;
				_productionCgCropTop = 0;
				_productionCgCropRight = 0;
				_productionCgCropBottom = 0;
				_productionCgProcessingStack = Array.Empty<PreparedCompositingProcessingNodeState>();
				_productionCgDefinition = definition;
				_productionCgText = new V1ProductionCgTextSnapshot(
					true,
					definition.Text,
					definition.Typeface.Trim(),
					rendered.ResolvedTypeface,
					definition.FontSizePixels,
					definition.BoxWidth,
					definition.BoxHeight,
					definition.Alignment,
					definition.Anchor,
					definition.Panel.Enabled,
					definition.Visible,
					definition.Layer,
					definition.ZOrder,
					rendered.CacheHit,
					rendered.RenderDuration);
				RebuildProductionCgLayerUnsafe();
				Observe($"graphics.cg.rendered:{rendered.ResolvedTypeface}:{definition.BoxWidth}x{definition.BoxHeight}:cache={rendered.CacheHit}");
				return ProductionCgOverlaySnapshotUnsafe();
			}
		}
	}

	public V1GraphicsOverlaySnapshot SetGraphicsOverlay(
		bool visible,
		double positionX,
		double positionY,
		double scale)
	{
		if (!double.IsFinite(positionX) || positionX is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(positionX), "Graphics X position must be normalized to 0..1.");
		if (!double.IsFinite(positionY) || positionY is < 0 or > 1)
			throw new ArgumentOutOfRangeException(nameof(positionY), "Graphics Y position must be normalized to 0..1.");
		if (!double.IsFinite(scale) || scale is < 0.05 or > 4.0)
			throw new ArgumentOutOfRangeException(nameof(scale), "Graphics scale must be between 0.05 and 4.0.");

		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			if (visible && _operatorGraphicsAsset is null && _productionCgDefinition is null)
				throw new InvalidOperationException("A graphics asset must be loaded before the overlay can be shown.");

			if (_operatorGraphicsAsset is not null)
			{
				_operatorGraphicsVisible = visible;
				_operatorGraphicsPositionX = positionX;
				_operatorGraphicsPositionY = positionY;
				_operatorGraphicsScale = scale;
				RebuildOperatorGraphicsLayerUnsafe();
			}
			else if (_productionCgDefinition is not null)
			{
				if (Math.Abs(positionX - _productionCgPositionX) > 0.000001 ||
					Math.Abs(positionY - _productionCgPositionY) > 0.000001 ||
					Math.Abs(scale - 1.0) > 0.000001)
				{
					throw new NotSupportedException("Production CG placement must be changed by reapplying its CG definition.");
				}
				_productionCgDefinition = _productionCgDefinition with { Visible = visible };
				_productionCgText = _productionCgText with { Visible = visible };
			}
			Observe($"graphics.overlay.state:{visible}:{positionX:0.###}:{positionY:0.###}:{scale:0.###}");
			return GraphicsOverlaySnapshotUnsafe();
		}
	}

	public V1GraphicsOverlaySnapshot ClearGraphicsOverlay()
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			_operatorGraphicsAsset = null;
			_operatorGraphicsAssetName = null;
			_operatorGraphicsAssetWidth = 0;
			_operatorGraphicsAssetHeight = 0;
			_operatorGraphicsVisible = false;
			_operatorGraphicsOpacity = byte.MaxValue;
			_operatorGraphicsRotationDegrees = 0;
			_operatorGraphicsAnchorX = 0;
			_operatorGraphicsAnchorY = 0;
			_operatorGraphicsCropLeft = 0;
			_operatorGraphicsCropTop = 0;
			_operatorGraphicsCropRight = 0;
			_operatorGraphicsCropBottom = 0;
			_operatorGraphicsProcessingStack = Array.Empty<PreparedCompositingProcessingNodeState>();
			_operatorGraphicsLayerScratch.AsSpan().Clear();
			_operatorGraphicsLayerBuffer.CopyPixelsFrom(_operatorGraphicsLayerScratch);
			_operatorGraphicsLayer.Update(_operatorGraphicsLayerBuffer);

			_productionCgAsset = null;
			_productionCgAssetWidth = 0;
			_productionCgAssetHeight = 0;
			_productionCgPositionX = 0;
			_productionCgPositionY = 0;
			_productionCgScale = 1.0;
			_productionCgRotationDegrees = 0;
			_productionCgAnchorX = 0;
			_productionCgAnchorY = 0;
			_productionCgCropLeft = 0;
			_productionCgCropTop = 0;
			_productionCgCropRight = 0;
			_productionCgCropBottom = 0;
			_productionCgProcessingStack = Array.Empty<PreparedCompositingProcessingNodeState>();
			_productionCgOpacity = byte.MaxValue;
			_productionCgDefinition = null;
			_productionCgText = V1ProductionCgTextSnapshot.Empty;
			_productionCgLayerScratch.AsSpan().Clear();
			_productionCgLayerBuffer.CopyPixelsFrom(_productionCgLayerScratch);
			_productionCgLayer.Update(_productionCgLayerBuffer);

			Observe("graphics.overlay.cleared");
			return GraphicsOverlaySnapshotUnsafe();
		}
	}

	public IReadOnlyList<V1CompositingLayerSnapshot> SetCompositingLayerState(
		string layerId,
		bool visible,
		byte opacity)
	{
		if (string.IsNullOrWhiteSpace(layerId))
			throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));

		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			switch (layerId.Trim())
			{
				case BitmapGraphicsLayerId:
					if (_operatorGraphicsAsset is null)
						throw new InvalidOperationException("Bitmap graphics layer is not loaded.");
					_operatorGraphicsVisible = visible;
					_operatorGraphicsOpacity = opacity;
					break;
				case ProductionCgLayerId:
					if (_productionCgDefinition is null || _productionCgAsset is null)
						throw new InvalidOperationException("Production CG layer is not active.");
					_productionCgDefinition = _productionCgDefinition with { Visible = visible };
					_productionCgText = _productionCgText with { Visible = visible };
					_productionCgOpacity = opacity;
					break;
				case LegacyVisualLayerId:
					throw new NotSupportedException("Legacy visual layer state remains governed by the existing visual-layer command.");
				default:
					throw new ArgumentOutOfRangeException(nameof(layerId), "Unknown compositing layer identity.");
			}
			Observe($"compositing.layer.state:{layerId.Trim()}:{visible}:{opacity}");
			return CompositingLayerSnapshotsUnsafe();
		}
	}

	public IReadOnlyList<V1CompositingLayerSnapshot> SetCompositingLayerTransform(
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
		double cropBottom)
	{
		if (string.IsNullOrWhiteSpace(layerId))
			throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));

		var normalizedLayerId = layerId.Trim();
		_ = new PreparedCompositingLayerState(
			normalizedLayerId,
			normalizedLayerId switch
			{
				BitmapGraphicsLayerId => PreparedCompositingLayerKind.BitmapGraphics,
				ProductionCgLayerId => PreparedCompositingLayerKind.ProductionCg,
				LegacyVisualLayerId => PreparedCompositingLayerKind.LegacyVisual,
				_ => throw new ArgumentOutOfRangeException(nameof(layerId), "Unknown compositing layer identity.")
			},
			0,
			true,
			byte.MaxValue,
			positionX,
			positionY,
			scale,
			"validation",
			rotationDegrees,
			anchorX,
			anchorY,
			cropLeft,
			cropTop,
			cropRight,
			cropBottom);

		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			switch (normalizedLayerId)
			{
				case BitmapGraphicsLayerId:
					if (_operatorGraphicsAsset is null)
						throw new InvalidOperationException("Bitmap graphics layer is not loaded.");
					_operatorGraphicsPositionX = positionX;
					_operatorGraphicsPositionY = positionY;
					_operatorGraphicsScale = scale;
					_operatorGraphicsRotationDegrees = rotationDegrees;
					_operatorGraphicsAnchorX = anchorX;
					_operatorGraphicsAnchorY = anchorY;
					_operatorGraphicsCropLeft = cropLeft;
					_operatorGraphicsCropTop = cropTop;
					_operatorGraphicsCropRight = cropRight;
					_operatorGraphicsCropBottom = cropBottom;
					RebuildOperatorGraphicsLayerUnsafe();
					break;
				case ProductionCgLayerId:
					if (_productionCgDefinition is null || _productionCgAsset is null)
						throw new InvalidOperationException("Production CG layer is not active.");
					_productionCgPositionX = positionX;
					_productionCgPositionY = positionY;
					_productionCgScale = scale;
					_productionCgRotationDegrees = rotationDegrees;
					_productionCgAnchorX = anchorX;
					_productionCgAnchorY = anchorY;
					_productionCgCropLeft = cropLeft;
					_productionCgCropTop = cropTop;
					_productionCgCropRight = cropRight;
					_productionCgCropBottom = cropBottom;
					RebuildProductionCgLayerUnsafe();
					break;
				case LegacyVisualLayerId:
					throw new NotSupportedException("Legacy visual layer transform remains governed by its existing fixed semantics.");
			}

			Observe($"compositing.layer.transform:{normalizedLayerId}");
			return CompositingLayerSnapshotsUnsafe();
		}
	}

	public IReadOnlyList<V1CompositingLayerSnapshot> SetCompositingLayerProcessingNode(
		string layerId,
		PreparedCompositingProcessingNodeState? processingNode) =>
		SetCompositingLayerProcessingStack(
			layerId,
			processingNode is null
				? Array.Empty<PreparedCompositingProcessingNodeState>()
				: new[] { processingNode });

	public IReadOnlyList<V1CompositingLayerSnapshot> SetCompositingLayerProcessingStack(
		string layerId,
		IReadOnlyList<PreparedCompositingProcessingNodeState> processingStack)
	{
		if (string.IsNullOrWhiteSpace(layerId))
			throw new ArgumentException("Compositing layer identity is required.", nameof(layerId));
		ArgumentNullException.ThrowIfNull(processingStack);
		if (processingStack.Count > PreparedCompositingProcessingStackLimits.MaximumNodeCount)
			throw new ArgumentException($"Compositing processing stack supports at most {PreparedCompositingProcessingStackLimits.MaximumNodeCount} nodes.", nameof(processingStack));

		var canonical = processingStack.ToArray();
		if (canonical.Any(node => node is null))
			throw new ArgumentException("Compositing processing stack must not contain null nodes.", nameof(processingStack));
		if (canonical.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != canonical.Length)
			throw new ArgumentException("Compositing processing node identities must be unique within a layer.", nameof(processingStack));
		foreach (var node in canonical)
		{
			if (node.Kind is not PreparedCompositingProcessingNodeKind.ColorGrade and
				not PreparedCompositingProcessingNodeKind.ChromaKey)
			{
				throw new NotSupportedException($"Processing node kind '{node.Kind}' is not supported.");
			}
		}

		var normalizedLayerId = layerId.Trim();
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			switch (normalizedLayerId)
			{
				case BitmapGraphicsLayerId:
					if (_operatorGraphicsAsset is null)
						throw new InvalidOperationException("Bitmap graphics layer is not loaded.");
					_operatorGraphicsProcessingStack = canonical;
					RebuildOperatorGraphicsLayerUnsafe();
					break;
				case ProductionCgLayerId:
					if (_productionCgDefinition is null || _productionCgAsset is null)
						throw new InvalidOperationException("Production CG layer is not active.");
					_productionCgProcessingStack = canonical;
					RebuildProductionCgLayerUnsafe();
					break;
				case LegacyVisualLayerId:
					throw new NotSupportedException("Legacy visual layer does not expose processing nodes.");
				default:
					throw new ArgumentOutOfRangeException(nameof(layerId), "Unknown compositing layer identity.");
			}

			Observe($"compositing.layer.processing:{normalizedLayerId}:{string.Join(",", canonical.Select(node => node.NodeId))}");
			return CompositingLayerSnapshotsUnsafe();
		}
	}

	public IReadOnlyList<V1CompositingLayerSnapshot> ReorderCompositingLayers(IReadOnlyList<string> orderedLayerIds)
	{
		ArgumentNullException.ThrowIfNull(orderedLayerIds);
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			var active = CompositingLayerSnapshotsUnsafe().Select(layer => layer.LayerId).ToArray();
			if (orderedLayerIds.Count != active.Length || orderedLayerIds.Count > GpuCompositeLimits.MaxActiveLayers)
				throw new ArgumentException("Compositing reorder must contain every active layer exactly once.", nameof(orderedLayerIds));

			var normalized = orderedLayerIds.Select(layerId =>
			{
				if (string.IsNullOrWhiteSpace(layerId))
					throw new ArgumentException("Compositing reorder contains an empty layer identity.", nameof(orderedLayerIds));
				return layerId.Trim();
			}).ToArray();
			if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length ||
				active.Except(normalized, StringComparer.Ordinal).Any() ||
				normalized.Except(active, StringComparer.Ordinal).Any())
			{
				throw new ArgumentException("Compositing reorder contains duplicate, missing, or unknown layers.", nameof(orderedLayerIds));
			}

			for (var index = 0; index < normalized.Length; index++)
			{
				switch (normalized[index])
				{
					case LegacyVisualLayerId:
						_legacyVisualLayerOrder = index;
						break;
					case BitmapGraphicsLayerId:
						_operatorGraphicsLayerOrder = index;
						break;
					case ProductionCgLayerId:
						_productionCgLayerOrder = index;
						break;
				}
			}
			Observe($"compositing.layers.reordered:{string.Join(",", normalized)}");
			return CompositingLayerSnapshotsUnsafe();
		}
	}

	private Failure? ValidatePreparedCompositingStateUnsafe(PreparedCompositingState? state)
	{
		if (state is null)
			return null;

		var active = CompositingLayerSnapshotsUnsafe();
		if (state.Layers.Count != active.Count)
		{
			return new Failure(
				"runtime.compositing.resource_set_mismatch",
				"Prepared compositing state must reference every currently admitted layer resource exactly once.");
		}

		var activeById = active.ToDictionary(layer => layer.LayerId, StringComparer.Ordinal);
		foreach (var layer in state.Layers)
		{
			if (!activeById.TryGetValue(layer.LayerId, out var admitted))
				return new Failure("runtime.compositing.resource_missing", $"Prepared compositing layer '{layer.LayerId}' is not admitted.");
			if ((int)layer.Kind != (int)admitted.Kind)
				return new Failure("runtime.compositing.kind_mismatch", $"Prepared compositing layer '{layer.LayerId}' kind does not match the admitted resource.");
			if (!string.Equals(layer.ContentIdentity, admitted.ContentIdentity, StringComparison.Ordinal))
				return new Failure("runtime.compositing.content_mismatch", $"Prepared compositing layer '{layer.LayerId}' content identity does not match the admitted resource.");

			switch (layer.LayerId)
			{
				case LegacyVisualLayerId:
					if (!layer.Visible ||
						layer.PositionX != 0 ||
						layer.PositionY != 0 ||
						layer.Scale != 1 ||
						layer.RotationDegrees != 0 ||
						layer.AnchorX != 0 ||
						layer.AnchorY != 0 ||
						layer.CropLeft != 0 ||
						layer.CropTop != 0 ||
						layer.CropRight != 0 ||
						layer.CropBottom != 0 ||
						layer.ProcessingStack.Count != 0)
					{
						return new Failure("runtime.compositing.legacy_state_unsupported", "Legacy visual layer Scene recall supports order and opacity only.");
					}
					break;
				case BitmapGraphicsLayerId:
					if (_operatorGraphicsAsset is null)
						return new Failure("runtime.compositing.bitmap_missing", "Prepared bitmap graphics resource is not loaded.");
					break;
				case ProductionCgLayerId:
					if (_productionCgAsset is null || _productionCgDefinition is null)
						return new Failure("runtime.compositing.cg_missing", "Prepared Production CG resource is not loaded.");
					break;
				default:
					return new Failure("runtime.compositing.layer_unknown", $"Prepared compositing layer '{layer.LayerId}' is not supported.");
			}
		}

		return null;
	}

	private CompositingRollbackState CaptureCompositingRollbackStateUnsafe() =>
		new(
			new PreparedCompositingState(
				PreparedCompositingState.CurrentVersion,
				CompositingLayerSnapshotsUnsafe()
					.Select((layer, order) => new PreparedCompositingLayerState(
						layer.LayerId,
						(PreparedCompositingLayerKind)(int)layer.Kind,
						order,
						layer.Visible,
						layer.Opacity,
						layer.PositionX,
						layer.PositionY,
						layer.Scale,
						layer.ContentIdentity,
						layer.RotationDegrees,
						layer.AnchorX,
						layer.AnchorY,
						layer.CropLeft,
						layer.CropTop,
						layer.CropRight,
						layer.CropBottom,
						processingStack: layer.ProcessingStack))
					.ToArray()),
			_legacyVisualLayerOrder,
			_operatorGraphicsLayerOrder,
			_productionCgLayerOrder);

	private void RestoreCompositingRollbackStateUnsafe(CompositingRollbackState? rollback)
	{
		if (rollback is null)
			return;

		ApplyPreparedCompositingStateUnsafe(rollback.State, observe: false);
		_legacyVisualLayerOrder = rollback.LegacyVisualLayerOrder;
		_operatorGraphicsLayerOrder = rollback.BitmapGraphicsLayerOrder;
		_productionCgLayerOrder = rollback.ProductionCgLayerOrder;
	}

	private void ApplyPreparedCompositingStateUnsafe(PreparedCompositingState? state, bool observe = true)
	{
		if (state is null)
			return;

		foreach (var layer in state.Layers)
		{
			switch (layer.LayerId)
			{
				case LegacyVisualLayerId:
					_legacyVisualLayerOrder = layer.Order;
					_legacyVisualLayerOpacity = layer.Opacity;
					break;
				case BitmapGraphicsLayerId:
					_operatorGraphicsLayerOrder = layer.Order;
					_operatorGraphicsVisible = layer.Visible;
					_operatorGraphicsOpacity = layer.Opacity;
					_operatorGraphicsPositionX = layer.PositionX;
					_operatorGraphicsPositionY = layer.PositionY;
					_operatorGraphicsScale = layer.Scale;
					_operatorGraphicsRotationDegrees = layer.RotationDegrees;
					_operatorGraphicsAnchorX = layer.AnchorX;
					_operatorGraphicsAnchorY = layer.AnchorY;
					_operatorGraphicsCropLeft = layer.CropLeft;
					_operatorGraphicsCropTop = layer.CropTop;
					_operatorGraphicsCropRight = layer.CropRight;
					_operatorGraphicsCropBottom = layer.CropBottom;
					_operatorGraphicsProcessingStack = layer.ProcessingStack.ToArray();
					RebuildOperatorGraphicsLayerUnsafe();
					break;
				case ProductionCgLayerId:
					_productionCgLayerOrder = layer.Order;
					_productionCgText = _productionCgText with { Visible = layer.Visible };
					_productionCgDefinition = _productionCgDefinition! with { Visible = layer.Visible };
					_productionCgOpacity = layer.Opacity;
					_productionCgPositionX = layer.PositionX;
					_productionCgPositionY = layer.PositionY;
					_productionCgScale = layer.Scale;
					_productionCgRotationDegrees = layer.RotationDegrees;
					_productionCgAnchorX = layer.AnchorX;
					_productionCgAnchorY = layer.AnchorY;
					_productionCgCropLeft = layer.CropLeft;
					_productionCgCropTop = layer.CropTop;
					_productionCgCropRight = layer.CropRight;
					_productionCgCropBottom = layer.CropBottom;
					_productionCgProcessingStack = layer.ProcessingStack.ToArray();
					RebuildProductionCgLayerUnsafe();
					break;
			}
		}

		if (observe)
			Observe($"compositing.scene.applied:{string.Join(",", state.Layers.Select(layer => layer.LayerId))}");
	}

	public void SetTimingHealth(V1TimingHealthState state)
	{
		if (!Enum.IsDefined(typeof(V1TimingHealthState), state)) throw new ArgumentOutOfRangeException(nameof(state));
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_timingHealth == state) return;
			_timingHealth = state;
			Observe($"timing.health:{state}");
		}
	}

	public void SetPerformanceObservations(TimeSpan processingDuration, ulong droppedFrames, double? outputFramesPerSecond = null)
	{
		if (processingDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(processingDuration));
		if (outputFramesPerSecond is { } framesPerSecond && (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0))
			throw new ArgumentOutOfRangeException(nameof(outputFramesPerSecond));
		lock (_gate)
		{
			ThrowIfDisposed();
			_lastFrameProcessingTime = processingDuration;
			_droppedFrames = droppedFrames;
			_outputFramesPerSecond = outputFramesPerSecond;
		}
	}

	/// <summary>
	/// Materializes a visible dynamic RGBA region. This is an effect/composite input and contains no AI authority.
	/// An outer governed AI effect policy may derive the region from a timed segmentation result.
	/// </summary>
	public void UpdateDynamicLayerRegion(
		double left,
		double top,
		double right,
		double bottom,
		byte red = 48,
		byte green = 224,
		byte blue = 112,
		byte alpha = 112)
	{
		if (!(left >= 0 && left < right && right <= 1 && top >= 0 && top < bottom && bottom <= 1))
			throw new ArgumentOutOfRangeException(nameof(left), "Normalized dynamic layer region must be within 0..1 and non-empty.");

		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			var pixels = new byte[RgbaFrameBuffer.RequiredByteLength(_format)];
			var x0 = (int)Math.Floor(left * _format.Width);
			var x1 = (int)Math.Ceiling(right * _format.Width);
			var y0 = (int)Math.Floor(top * _format.Height);
			var y1 = (int)Math.Ceiling(bottom * _format.Height);
			var width = checked((int)_format.Width);
			for (var y = y0; y < y1; y++)
			{
				for (var x = x0; x < x1; x++)
				{
					var offset = checked((y * width + x) * 4);
					pixels[offset] = red;
					pixels[offset + 1] = green;
					pixels[offset + 2] = blue;
					pixels[offset + 3] = alpha;
				}
			}

			_dynamicLayer.Update(new RgbaFrameBuffer(_format, pixels));
			Observe($"graphics.layer.dynamic.updated:{_dynamicLayer.Generation}");
		}
	}

	public V1AudioInputSnapshot SetAudioInputState(MediaSourceId sourceId, AudioGain gain, bool muted)
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_audioStreams.TryGetValue(sourceId, out var stream))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");

			var current = _audioProduction.Configuration;
			if (current.Revision == ulong.MaxValue)
				throw new InvalidOperationException("Audio production revision cannot advance beyond UInt64.MaxValue.");
			var sources = current.Sources
				.Select(source => source.SourceId == sourceId
					? new AudioProductionSourceConfiguration(
						source.SourceId,
						gain.Linear,
						muted,
						source.FollowRoutedSource,
						source.BusAssignments)
					: source)
				.ToArray();
			var updated = new AudioProductionConfiguration(
				current.Revision + 1,
				current.Buses,
				sources,
				current.Crossfade,
				current.Ducking,
				current.ClipStrategy);
			_audioProduction.ApplyConfiguration(updated);
			_audio.SetInputState(stream.StreamId, gain, muted);
			Observe($"audio.input.state:{sourceId}:{gain.Linear}:{muted}:mix-revision={updated.Revision}");
			return AudioInputSnapshotUnsafe(sourceId);
		}
	}

	public V1AudioProductionSnapshot SetAudioProductionConfiguration(AudioProductionConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			ValidateAudioProductionConfigurationUnsafe(configuration);
			_audioProduction.ApplyConfiguration(configuration);
			foreach (var source in configuration.Sources)
			{
				var stream = _audioStreams[source.SourceId];
				_audio.SetInputState(stream.StreamId, new AudioGain(source.Gain), source.Muted);
			}
			Observe($"audio.production.configuration:{configuration.Revision}");
			return AudioProductionSnapshotUnsafe();
		}
	}

	public V1AudioProgramSnapshot SetAudioRouting(AudioRoutingMode mode, MediaSourceId? breakawaySourceId = null)
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			var state = _audio.SetRouting(mode, breakawaySourceId);
			Observe($"audio.routing.state:{state.Revision}:{state.Mode}:{state.BreakawaySourceId?.ToString() ?? "follow-video"}");
			ResetAvSyncDiagnosticsUnsafe();
			return AudioProgramSnapshotUnsafe();
		}
	}

	public V1AudioInputSnapshot SetGeneratedAudioTestSignal(
		MediaSourceId sourceId,
		bool enabled,
		GeneratedAudioTestSignalMode mode = GeneratedAudioTestSignalMode.Tone,
		double frequencyHz = GeneratedAudioTestSignalConfiguration.DefaultFrequencyHz,
		double peakLevel = GeneratedAudioTestSignalConfiguration.DefaultPeakLevel)
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_audioStreams.TryGetValue(sourceId, out var stream))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");

			if (!enabled)
			{
				var removed = _audioTestSignals.Remove(sourceId);
				_audioTestSignalFrames.Remove(sourceId);
				if (removed)
				{
					ResetAvSyncDiagnosticsUnsafe();
					Observe($"audio.test_signal:{sourceId}:disabled");
				}
				return AudioInputSnapshotUnsafe(sourceId);
			}

			var configuration = new GeneratedAudioTestSignalConfiguration(
				stream.Format,
				mode,
				frequencyHz,
				peakLevel);
			var generator = new GeneratedAudioTestSignalGenerator(configuration);
			_audioTestSignals[sourceId] = generator;
			_audioTestSignalFrames[sourceId] = generator.Inspect(CreateAudioBuffer(sourceId, _nextSequenceNumber).Timing);
			ResetAvSyncDiagnosticsUnsafe();
			Observe($"audio.test_signal:{sourceId}:enabled:{mode}:{frequencyHz:0.###}:{peakLevel:0.###}");
			return AudioInputSnapshotUnsafe(sourceId);
		}
	}

	public void SetExternalAudioMeter(
		MediaSourceId sourceId,
		double leftPeak,
		double rightPeak,
		bool available = true)
	{
		var meter = new AudioStereoMeter(leftPeak, rightPeak);
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			EnsureAudioSourceUnsafe(sourceId);
			_externalAudioQueues[sourceId].Clear();
			_audioMeters[sourceId] = new AudioMeterObservation(meter, available, External: true);
		}
	}

	public void SetExternalAudioInput(
		MediaSourceId sourceId,
		ReadOnlySpan<float> interleavedStereoSamples,
		bool available = true)
	{
		var meter = AudioMetering.MeasureInterleavedStereoFloat32(interleavedStereoSamples);
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			EnsureAudioSourceUnsafe(sourceId);
			var queue = _externalAudioQueues[sourceId];
			var maximumBufferedValues = checked((int)(_audioStreams[sourceId].Format.SampleRate * _audioStreams[sourceId].Format.ChannelCount / 2));
			foreach (var sample in interleavedStereoSamples)
			{
				queue.Enqueue(float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f);
				while (queue.Count > maximumBufferedValues)
					queue.Dequeue();
			}
			_audioMeters[sourceId] = new AudioMeterObservation(meter, available, External: true);
		}
	}

	public void SetExternalAudioInput(
		MediaSourceId sourceId,
		ReadOnlySpan<byte> float32InterleavedStereoPayload,
		bool available = true)
	{
		if ((float32InterleavedStereoPayload.Length % sizeof(float)) != 0)
			throw new ArgumentException("Float32 audio payload length must be aligned to four bytes.", nameof(float32InterleavedStereoPayload));
		SetExternalAudioInput(
			sourceId,
			MemoryMarshal.Cast<byte, float>(float32InterleavedStereoPayload),
			available);
	}

	public void ClearExternalAudioMeter(MediaSourceId sourceId)
	{
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			EnsureAudioSourceUnsafe(sourceId);
			_externalAudioQueues[sourceId].Clear();
			_audioMeters[sourceId] = new AudioMeterObservation(new AudioStereoMeter(0, 0), Available: false, External: false);
		}
	}

	public void SetInputSignalState(MediaSourceId sourceId, V1InputSignalState state)
	{
		if (!Enum.IsDefined(typeof(V1InputSignalState), state)) throw new ArgumentOutOfRangeException(nameof(state));
		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_inputSignals.ContainsKey(sourceId))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");
			_inputSignals[sourceId] = state;
			Observe($"input.signal:{sourceId}:{state}");
		}
	}

	public bool SetBroadcastTestPattern(
		MediaSourceId sourceId,
		bool enabled,
		V1BroadcastTestPatternMode mode = V1BroadcastTestPatternMode.Static)
	{
		if (!Enum.IsDefined(typeof(V1BroadcastTestPatternMode), mode))
			throw new ArgumentOutOfRangeException(nameof(mode));

		lock (_boundaryCaptureGate)
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_backgrounds.ContainsKey(sourceId))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");

			if (!enabled)
			{
				var removed = _broadcastTestPatternSources.Remove(sourceId);
				_broadcastTestPatternModes.Remove(sourceId);
				if (!removed)
					return false;

				ResetAvSyncDiagnosticsUnsafe();
				Observe($"input.test_pattern:{sourceId}:disabled");
				return true;
			}

			var added = _broadcastTestPatternSources.Add(sourceId);
			var modeChanged = !_broadcastTestPatternModes.TryGetValue(sourceId, out var previousMode) || previousMode != mode;
			_broadcastTestPatternModes[sourceId] = mode;
			if (!added && !modeChanged)
				return false;

			ResetAvSyncDiagnosticsUnsafe();
			Observe($"input.test_pattern:{sourceId}:enabled:{mode}");
			return true;
		}
	}

	/// <summary>
	/// Replaces the current V1 working-frame content for one logical input without changing runtime authority or
	/// timing. Physical Media I/O uses this seam after copying an adapter lease into the bounded runtime frame.
	/// </summary>
	public void SetExternalInputContent(MediaSourceId sourceId, RgbaFrameBuffer content, V1InputSignalState state = V1InputSignalState.Valid)
	{
		ArgumentNullException.ThrowIfNull(content);
		if (content.Format != _format)
			throw new ArgumentException("External input content must match the RuntimeHost video format.", nameof(content));
		SetExternalInputContent(sourceId, content.Pixels.Span, state);
	}

	public void SetExternalInputContent(MediaSourceId sourceId, ReadOnlySpan<byte> rgbaPixels, V1InputSignalState state = V1InputSignalState.Valid)
	{
		if (rgbaPixels.Length != RgbaFrameBuffer.RequiredByteLength(_format))
			throw new ArgumentException("External input payload must match the RuntimeHost video format.", nameof(rgbaPixels));
		if (!Enum.IsDefined(typeof(V1InputSignalState), state))
			throw new ArgumentOutOfRangeException(nameof(state));

		lock (_boundaryCaptureGate)
		{
			RgbaFrameBuffer target;
			lock (_gate)
			{
				ThrowIfDisposed();
				if (!_backgrounds.TryGetValue(sourceId, out target!))
					throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");
			}

			target.CopyPixelsFrom(rgbaPixels);

			lock (_gate)
			{
				ThrowIfDisposed();
				_inputSignals[sourceId] = state;
				Observe($"input.external.updated:{sourceId}:{state}");
			}
		}
	}

	public ValueTask<RecordingStartResult> StartRecordingAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		CancellationToken cancellationToken = default) =>
		StartRecordingCoreAsync(sessionId, outputId, null, null, null, cancellationToken);

	public ValueTask<RecordingStartResult> StartRecordingAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		string destinationDirectory,
		string fileName,
		CancellationToken cancellationToken = default) =>
		StartRecordingAsync(sessionId, outputId, destinationDirectory, fileName, null, cancellationToken);

	public async ValueTask<RecordingStartResult> StartRecordingAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		string destinationDirectory,
		string fileName,
		RecordingProfileId? profileId,
		CancellationToken cancellationToken = default)
	{
		if (_recordingTargetWriter is null)
		{
			return RecordingStartResult.Rejected(new Failure(
				"recording.destination.unsupported",
				"Configured RuntimeHost recording writer does not support operator-selected destinations."));
		}

		var resolvedProfile = ResolveRecordingProfile(profileId, out var profileFailure);
		if (profileFailure is not null)
			return RecordingStartResult.Rejected(profileFailure.Value);

		string normalizedFileName;
		try
		{
			normalizedFileName = _profileRecordingTargetWriter is not null
				? _profileRecordingTargetWriter.ConfigureTarget(resolvedProfile?.ProfileId, destinationDirectory, fileName)
				: _recordingTargetWriter.ConfigureTarget(destinationDirectory, fileName);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or RecordingOutputUnavailableException)
		{
			return RecordingStartResult.Rejected(new Failure("recording.profile.target_rejected", exception.Message));
		}

		return await StartRecordingCoreAsync(
			sessionId,
			outputId,
			Path.GetFullPath(destinationDirectory.Trim()),
			normalizedFileName,
			resolvedProfile?.ProfileId,
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<RecordingStartResult> StartRecordingCoreAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		string? destinationDirectory,
		string? fileName,
		RecordingProfileId? profileId,
		CancellationToken cancellationToken)
	{
		var resolvedProfile = ResolveRecordingProfile(profileId, out var profileFailure);
		if (profileFailure is not null)
			return RecordingStartResult.Rejected(profileFailure.Value);

		MediaSinkId sink;
		lock (_gate)
		{
			ThrowIfDisposed();
			sink = _programSinkId ?? throw new InvalidOperationException("Program sink must be committed before recording starts.");
			_recordingDestination = destinationDirectory;
			_recordingFileName = fileName;
			_recordingStartedAtUtc = null;
			_recordingCompletedAtUtc = null;
		}

		var result = await _recorder.StartAsync(
			new RecordingStartRequest(
				RecordingContractVersion.Current,
				sessionId,
				new RecordingOutputDescriptor(outputId, sink, fileName ?? "V1 Program"),
				resolvedProfile?.ProfileId),
			cancellationToken).ConfigureAwait(false);
		lock (_gate)
		{
			if (result.Succeeded)
				_recordingStartedAtUtc = DateTimeOffset.UtcNow;
			Observe($"recording.start:{result.Status}");
		}
		return result;
	}

	public async ValueTask<RecordingStopResult> StopRecordingAsync(CancellationToken cancellationToken = default)
	{
		var result = await _recorder.StopAsync(cancellationToken).ConfigureAwait(false);
		lock (_gate)
		{
			if (result.Status == RecordingStopStatus.Stopped)
				_recordingCompletedAtUtc = DateTimeOffset.UtcNow;
			Observe($"recording.stop:{result.Status}");
		}
		return result;
	}

	public ReplayBufferSnapshot GetReplaySnapshot() =>
		_replay?.Snapshot ?? new ReplayBufferSnapshot(
			ReplayContractVersion.Current,
			ReplayCaptureState.Disabled,
			ReplayClipState.Idle,
			ReplayBufferPolicy.Default,
			Array.Empty<ReplaySegmentDescriptor>(),
			null,
			new ReplayStatistics(0, 0, 0, 0, 0, 0),
			new Failure("replay.runtime.unavailable", "Replay capture is not configured for this RuntimeHost."));

	public ReplayRange MarkReplayIn(TimeSpan? lookback = null) =>
		(_replay ?? throw new InvalidOperationException("Replay capture is not configured.")).MarkIn(lookback);

	public ReplayRange MarkReplayOut() =>
		(_replay ?? throw new InvalidOperationException("Replay capture is not configured.")).MarkOut();

	public ReplayRange SelectReplayRange(ReplayRange range) =>
		(_replay ?? throw new InvalidOperationException("Replay capture is not configured.")).SelectRange(range);

	public async ValueTask<ReplayClipResult> CreateReplayClipAsync(
		string name,
		CancellationToken cancellationToken = default) =>
		await (_replay ?? throw new InvalidOperationException("Replay capture is not configured."))
			.CreateClipAsync(name, cancellationToken)
			.ConfigureAwait(false);

	public async ValueTask DisposeAsync()
	{
		bool dispose;
		lock (_gate)
		{
			dispose = !_disposed;
			_disposed = true;
		}
		if (!dispose) return;

		await _monitoringTap.DisposeAsync().ConfigureAwait(false);
		_monitoringHub.Dispose();
		await _networkOutputBridge.DisposeAsync().ConfigureAwait(false);
		if (_replay is not null)
			await _replay.DisposeAsync().ConfigureAwait(false);
		await _recorder.DisposeAsync().ConfigureAwait(false);
		_sourceAPipeline.Dispose();
		_sourceBPipeline.Dispose();
		var readback = _gpu.ReadbackPoolStatistics;
		if (readback.ActiveBuffers != 0)
			throw new InvalidOperationException($"RuntimeHost shutdown retained '{readback.ActiveBuffers}' active Program readback buffer lease(s).");
		var sharedMonitoring = _gpu.SharedMonitoringResourceStatistics;
		if (sharedMonitoring.ActiveResources != 0)
			throw new InvalidOperationException($"RuntimeHost shutdown retained '{sharedMonitoring.ActiveResources}' active shared monitoring resource lease(s).");
		_gpu.Dispose();
		_hardwareTelemetry.Dispose();
	}

	private MediaFramePipeline CreatePipeline() =>
		new(new MediaPipelineOptions(3, MediaBackpressurePolicy.RejectIncoming));

	private FrameDescriptor ProcessTimedInput(
		VirtualSyntheticVideoSource source,
		MediaFramePipeline pipeline,
		ulong sequence)
	{
		var frame = source.GenerateFrame(sequence);
		var clock = new MediaClockPosition(frame.Timing.PresentationTimestamp, frame.Timing.Timebase);
		var submitted = pipeline.Submit(frame, clock);
		if (!submitted.Accepted)
			throw new InvalidOperationException(submitted.Failure?.Message ?? "Timed media input was rejected.");

		FrameDescriptor? consumed = null;
		var result = pipeline.ConsumeNext(clock, descriptor => consumed = descriptor);
		if (!result.Consumed || consumed is null)
			throw new InvalidOperationException(result.Failure?.Message ?? "Timed media input was not consumed.");
		return consumed;
	}

	private Failure? WriteAuxFrame(
		PreparedExecutionContract preparedExecution,
		IReadOnlyDictionary<MediaSourceId, FrameDescriptor> frames)
	{
		if (_auxSinkId is null || _auxOutput is null)
			return null;

		var binding = preparedExecution.Bindings.SingleOrDefault(candidate =>
			string.Equals(candidate.OutputRoleId, "aux", StringComparison.Ordinal) &&
			candidate.MediaSinkId == _auxSinkId);
		if (binding?.MediaSourceId is not { } sourceId)
		{
			var failure = new Failure("runtime.output.aux_binding_missing", "Committed Aux output binding is unavailable.");
			Observe($"runtime.output.aux.failed:{failure.Code}");
			return failure;
		}

		if (!frames.TryGetValue(sourceId, out var frame))
		{
			var failure = new Failure("runtime.output.aux_source_unavailable", "Committed Aux output source did not produce a frame at this boundary.");
			Observe($"runtime.output.aux.failed:{failure.Code}");
			return failure;
		}

		try
		{
			_auxOutput.WriteFrame(frame);
			return null;
		}
		catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
		{
			var failure = new Failure("runtime.output.aux_write_failed", $"Aux output provider rejected the frame: {exception.Message}");
			Observe($"runtime.output.aux.failed:{failure.Code}");
			return failure;
		}
	}

	private IReadOnlyList<RuntimeOutputRoleSnapshot> OutputRoleSnapshotsUnsafe()
	{
		var execution = _runtime.ActiveExecution;
		if (execution is null)
			return Array.Empty<RuntimeOutputRoleSnapshot>();

		var snapshots = new List<RuntimeOutputRoleSnapshot>();
		var programBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding =>
			string.Equals(binding.OutputRoleId, "program", StringComparison.Ordinal));
		if (programBinding is null && _programSinkId is { } storedProgramSink)
			programBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding => binding.MediaSinkId == storedProgramSink);

		if (programBinding?.MediaSourceId is { } programSource && programBinding.MediaSinkId is { } programSink)
		{
			var programOutput = _programOutput;
			var programEvidence = programOutput?.LastFrame;
			if (programEvidence is not null && programEvidence.Frame.Timing.SequenceNumber >= _nextSequenceNumber)
				programEvidence = null;
			Failure? fault = null;
			if (_programSinkId != programSink)
			{
				fault = new Failure(
					"runtime.output.program_stored_sink_mismatch",
					"RuntimeHost stored Program sink does not match the committed Program target.");
			}
			else if (programOutput is null)
			{
				fault = new Failure(
					"runtime.output.program_binding_missing",
					"Committed Program output has no bound provider output.");
			}
			else if (programOutput.SinkId != programSink)
			{
				fault = new Failure(
					"runtime.output.program_binding_mismatch",
					$"Program output is bound to sink '{programOutput.SinkId}' while committed target is '{programSink}'.");
			}
			else if (programEvidence is not null && programEvidence.SinkId != programSink)
			{
				fault = new Failure(
					"runtime.output.program_evidence_sink_mismatch",
					$"Program output evidence targets sink '{programEvidence.SinkId}' while committed target is '{programSink}'.");
			}

			var hasEvidence = fault is null &&
				programEvidence is not null &&
				programEvidence.Frame.SourceId == programSource;
			snapshots.Add(new RuntimeOutputRoleSnapshot(
				"program", "PROGRAM", programSource, programSink, _format, _virtualMedia.Timing.FrameTimebase,
				programBinding.Resource.ProviderId,
				fault is null ? RuntimeOutputRoleLifecycleState.Active : RuntimeOutputRoleLifecycleState.Faulted,
				true,
				fault is not null ? RuntimeOutputRoleHealthState.Faulted : hasEvidence ? RuntimeOutputRoleHealthState.Healthy : RuntimeOutputRoleHealthState.Unverified,
				fault is not null
					? fault.Value.Message
					: hasEvidence
						? $"Program provider confirmed frame sequence {programEvidence!.Frame.Timing.SequenceNumber} for sink '{programSink}'."
						: $"Program output is committed to sink '{programSink}'; matching source/frame evidence is pending.",
				fault,
				_networkOutputBridge.SnapshotForRole("program")));
		}

		var auxBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding =>
			string.Equals(binding.OutputRoleId, "aux", StringComparison.Ordinal));
		if (auxBinding?.MediaSourceId is { } auxSource && auxBinding.MediaSinkId is { } auxSink)
		{
			var auxOutput = _auxOutput;
			var auxEvidence = auxOutput?.LastFrame;
			if (auxEvidence is not null && auxEvidence.Frame.Timing.SequenceNumber >= _nextSequenceNumber)
				auxEvidence = null;
			Failure? bindingFault = null;
			if (_auxSinkId != auxSink)
			{
				bindingFault = new Failure(
					"runtime.output.aux_stored_sink_mismatch",
					"RuntimeHost stored Aux sink does not match the committed Aux target.");
			}
			else if (auxOutput is null)
			{
				bindingFault = new Failure(
					"runtime.output.aux_binding_missing",
					"Committed Aux output has no bound provider output.");
			}
			else if (auxOutput.SinkId != auxSink)
			{
				bindingFault = new Failure(
					"runtime.output.aux_binding_mismatch",
					$"Aux output is bound to sink '{auxOutput.SinkId}' while committed target is '{auxSink}'.");
			}
			else if (auxEvidence is not null && auxEvidence.SinkId != auxSink)
			{
				bindingFault = new Failure(
					"runtime.output.aux_evidence_sink_mismatch",
					$"Aux output evidence targets sink '{auxEvidence.SinkId}' while committed target is '{auxSink}'.");
			}

			var fault = bindingFault ?? _auxFailure;
			var hasEvidence = fault is null &&
				auxEvidence is not null &&
				auxEvidence.Frame.SourceId == auxSource;
			snapshots.Add(new RuntimeOutputRoleSnapshot(
				"aux", "AUX", auxSource, auxSink, _format, _virtualMedia.Timing.FrameTimebase,
				auxBinding.Resource.ProviderId,
				fault is null ? RuntimeOutputRoleLifecycleState.Active : RuntimeOutputRoleLifecycleState.Faulted,
				true,
				fault is not null ? RuntimeOutputRoleHealthState.Faulted : hasEvidence ? RuntimeOutputRoleHealthState.Healthy : RuntimeOutputRoleHealthState.Unverified,
				fault is not null
					? fault.Value.Message
					: hasEvidence
						? $"Aux provider confirmed frame sequence {auxEvidence!.Frame.Timing.SequenceNumber} for sink '{auxSink}'."
						: $"Aux output is committed to sink '{auxSink}'; matching source/frame evidence is pending.",
				fault,
				_networkOutputBridge.SnapshotForRole("aux")));
		}
		return snapshots.AsReadOnly();
	}

	private RgbaFrameBuffer ResolveInputContent(FrameDescriptor frame)
	{
		if (_broadcastTestPatternSources.Contains(frame.SourceId))
		{
			var mode = _broadcastTestPatternModes.TryGetValue(frame.SourceId, out var configuredMode)
				? configuredMode
				: V1BroadcastTestPatternMode.Static;
			if (mode == V1BroadcastTestPatternMode.MotionTiming)
			{
				var region = _motionTimingTestSignal.Render(frame.Timing);
				_motionTimingTestPattern.CopyRegionFrom(
					region.Pixels.Span,
					region.X,
					region.Y,
					region.Width,
					region.Height);
				return _motionTimingTestPattern;
			}

			return _broadcastTestPattern;
		}

		var state = _inputSignals[frame.SourceId];
		if (state != V1InputSignalState.Lost)
			return _backgrounds[frame.SourceId];

		Observe($"input.fallback.black:{frame.SourceId}:{frame.Timing.SequenceNumber}");
		return _blackBackground;
	}

	private GpuFrame MaterializeInput(FrameDescriptor frame, RgbaFrameBuffer content)
	{
		var state = _inputSignals[frame.SourceId];
		return _gpu.Upload(
			frame.SourceId,
			content,
			frame.Timing,
			new Generation(frame.Timing.SequenceNumber),
			_broadcastTestPatternSources.Contains(frame.SourceId)
				? _broadcastTestPatternModes.TryGetValue(frame.SourceId, out var mode) && mode == V1BroadcastTestPatternMode.MotionTiming
					? "internal-test-pattern-motion"
					: "internal-test-pattern"
				: state == V1InputSignalState.Lost ? "input-fallback" : "runtime-input");
	}

	private IReadOnlyList<MaterializedCompositingLayer> MaterializeLayers(FrameTiming timing)
	{
		var layers = new List<MaterializedCompositingLayer>(3);
		try
		{
			if (_visualLayerMode != V1VisualLayerMode.Disabled)
			{
				var frame = _visualLayerMode switch
				{
					V1VisualLayerMode.Static => _staticLayer.MaterializeReusable(_gpu, timing),
					V1VisualLayerMode.Dynamic => _dynamicLayer.MaterializeReusable(_gpu, timing),
					_ => throw new InvalidOperationException($"Unsupported visual layer mode '{_visualLayerMode}'.")
				};
				layers.Add(new MaterializedCompositingLayer(
					LegacyVisualLayerId,
					_legacyVisualLayerOrder,
					frame,
					new GpuKeyLayer(frame, _legacyVisualLayerOpacity)));
			}

			if (_operatorGraphicsVisible && _operatorGraphicsAsset is not null)
			{
				var frame = _operatorGraphicsLayer.MaterializeReusable(_gpu, timing);
				layers.Add(new MaterializedCompositingLayer(
					BitmapGraphicsLayerId,
					_operatorGraphicsLayerOrder,
					frame,
					new GpuKeyLayer(frame, _operatorGraphicsOpacity)));
			}

			if (_productionCgText.Visible && _productionCgAsset is not null)
			{
				var frame = _productionCgLayer.MaterializeReusable(_gpu, timing);
				layers.Add(new MaterializedCompositingLayer(
					ProductionCgLayerId,
					_productionCgLayerOrder,
					frame,
					new GpuKeyLayer(frame, _productionCgOpacity)));
			}

			layers.Sort(static (left, right) =>
			{
				var order = left.Order.CompareTo(right.Order);
				return order != 0 ? order : string.Compare(left.LayerId, right.LayerId, StringComparison.Ordinal);
			});
			return layers;
		}
		catch
		{
			foreach (var layer in layers)
				layer.Frame.Dispose();
			throw;
		}
	}

	private V1RuntimePerformanceSnapshot PerformanceSnapshotUnsafe(SystemHardwareTelemetrySnapshot hardware)
	{
		var backend = _gpu.BackendInfo;
		var frameBudget = TimeSpan.FromSeconds(_format.FrameRate.Denominator / (double)_format.FrameRate.Numerator);
		return new V1RuntimePerformanceSnapshot(
			_uptimeClock.Elapsed,
			frameBudget,
			_lastFrameProcessingTime,
			_droppedFrames,
			backend.DeviceName,
			backend.HardwareAccelerated,
			hardware.GpuUtilizationPercent,
			hardware.GpuVramUsedBytes,
			hardware.GpuVramTotalBytes ?? backend.TotalMemoryBytes,
			hardware.GpuTelemetryEvidence,
			hardware.CpuDeviceName,
			hardware.CpuLogicalProcessorCount,
			hardware.CpuUtilizationPercent,
			hardware.SystemMemoryUsedBytes,
			hardware.SystemMemoryTotalBytes,
			hardware.SystemTelemetryEvidence,
			hardware.GpuDeviceName ?? "UNVERIFIED",
			_outputFramesPerSecond,
			_lastCompositionDuration,
			_lastCompositingLayerCount);
	}

	private V1RecordingOperatorSnapshot RecordingOperatorSnapshotUnsafe()
	{
		var snapshot = _recorder.Snapshot;
		var elapsed = TimeSpan.Zero;
		if (_recordingStartedAtUtc is { } started)
		{
			var end = _recordingCompletedAtUtc ?? DateTimeOffset.UtcNow;
			elapsed = end > started ? end - started : TimeSpan.Zero;
		}

		var profileCatalog = _recordingProfileCatalogProvider?.ProfileCatalog;
		return new V1RecordingOperatorSnapshot(
			snapshot.State,
			elapsed,
			_recordingDestination,
			_recordingFileName,
			_recordingTargetWriter?.FinalPath,
			snapshot.Statistics,
			snapshot.Failure,
			_recordingProfileStateProvider?.ActiveProfile?.ProfileId,
			_recordingProfileStateProvider?.ActiveProviderId,
			profileCatalog?.DefaultProfileId,
			profileCatalog?.Profiles ?? Array.Empty<RecordingProfileDescriptor>());
	}

	private RecordingProfileDescriptor? ResolveRecordingProfile(
		RecordingProfileId? requestedProfileId,
		out Failure? failure)
	{
		failure = null;
		var catalog = _recordingProfileCatalogProvider?.ProfileCatalog;
		if (catalog is null)
		{
			if (requestedProfileId is not null)
			{
				failure = new Failure(
					"recording.profile.unsupported",
					"Configured RuntimeHost recording writer does not expose selectable production profiles.");
			}
			return null;
		}

		var profileId = requestedProfileId ?? catalog.DefaultProfileId;
		if (!catalog.TryGet(profileId, out var profile))
		{
			failure = new Failure(
				"recording.profile.unknown",
				$"Recording profile '{profileId}' is not registered.");
			return null;
		}
		if (!profile.Available)
		{
			failure = new Failure(
				"recording.profile.unavailable",
				profile.UnavailableReason ?? $"Recording profile '{profileId}' is unavailable.");
			return null;
		}

		return profile;
	}

	private IReadOnlyDictionary<MediaSourceId, V1AudioInputSnapshot> AudioInputSnapshotsUnsafe() =>
		new ReadOnlyDictionary<MediaSourceId, V1AudioInputSnapshot>(
			_audioStreams.Keys.ToDictionary(sourceId => sourceId, AudioInputSnapshotUnsafe));

	private V1AudioInputSnapshot AudioInputSnapshotUnsafe(MediaSourceId sourceId)
	{
		var stream = _audioStreams[sourceId];
		var state = _audio.GetInputState(stream.StreamId);
		var observation = _audioMeters[sourceId];
		var hasTestSignal = _audioTestSignals.TryGetValue(sourceId, out var testSignal);
		GeneratedAudioTestSignalFrameInfo? testFrame = null;
		if (hasTestSignal)
		{
			if (!_audioTestSignalFrames.TryGetValue(sourceId, out var frame))
			{
				frame = testSignal!.Inspect(CreateAudioBuffer(sourceId, _nextSequenceNumber).Timing);
				_audioTestSignalFrames[sourceId] = frame;
			}
			testFrame = frame;
		}

		var meter = testFrame is { } generated
			? new AudioStereoMeter(generated.LeftPeakLevel, generated.RightPeakLevel)
			: observation.Meter;
		var available = hasTestSignal || observation.Available;
		var leftUnclamped = state.Muted ? 0 : meter.LeftPeakLevel * state.Gain.Linear;
		var rightUnclamped = state.Muted ? 0 : meter.RightPeakLevel * state.Gain.Linear;
		var left = Math.Min(1, leftUnclamped);
		var right = Math.Min(1, rightUnclamped);
		var clipping = !state.Muted && (leftUnclamped >= 1 || rightUnclamped >= 1);
		var sourceAvailable = hasTestSignal ||
			(_inputSignals.TryGetValue(sourceId, out var signal) && signal != V1InputSignalState.Lost);
		var health = !sourceAvailable || !available
			? V1AudioHealthState.Error
			: state.Muted
				? V1AudioHealthState.Muted
				: clipping
					? V1AudioHealthState.Clipping
					: Math.Max(left, right) <= 0.000001
						? V1AudioHealthState.Silence
						: V1AudioHealthState.Healthy;

		return new V1AudioInputSnapshot(
			sourceId,
			stream.StreamId,
			state.Gain.Linear,
			state.Muted,
			left,
			right,
			Math.Max(left, right),
			clipping,
			health,
			hasTestSignal,
			testSignal?.Configuration.Mode,
			testFrame?.ActiveChannel,
			testSignal?.Configuration.FrequencyHz,
			testSignal?.Configuration.PeakLevel);
	}

	private V1AvSyncDiagnosticsSnapshot AvSyncDiagnosticsSnapshotUnsafe()
	{
		var activeSource = _lastAudioResult?.VideoSourceId ?? _audio.ActiveVideoSourceId;
		if (_audio.RoutingState.Mode != AudioRoutingMode.FollowVideo)
		{
			return new V1AvSyncDiagnosticsSnapshot(
				false,
				"UNAVAILABLE",
				null,
				null,
				null,
				null,
				null,
				null,
				null,
				"A/V sync diagnostics require FOLLOW_VIDEO routing.");
		}
		if (!IsAvSyncDiagnosticsEnabledUnsafe(activeSource))
		{
			return new V1AvSyncDiagnosticsSnapshot(
				false,
				"UNAVAILABLE",
				null,
				null,
				null,
				null,
				null,
				null,
				null,
				"Enable Motion/Timing video and Pulse audio on the active Program source to run synchronized A/V diagnostics.");
		}

		var snapshot = _avSyncDiagnostics.Snapshot;
		return new V1AvSyncDiagnosticsSnapshot(
			true,
			snapshot.State.ToString().ToUpperInvariant(),
			snapshot.EventId,
			snapshot.ExpectedMediaTime?.ToString(),
			snapshot.TargetVideoFrameSequence,
			snapshot.TargetAudioSamplePosition,
			snapshot.ScheduledVideoOffsetMilliseconds,
			snapshot.SubmitOffsetMilliseconds,
			snapshot.DriftFromBaselineMilliseconds,
			snapshot.Detail);
	}

	private void ResetAvSyncDiagnosticsUnsafe()
	{
		_avSyncDiagnostics.Reset();
		_avSyncDiagnosticsSourceId = null;
	}

	private bool IsAvSyncDiagnosticsEnabledUnsafe(MediaSourceId sourceId) =>
		_broadcastTestPatternSources.Contains(sourceId) &&
		_broadcastTestPatternModes.TryGetValue(sourceId, out var patternMode) &&
		patternMode == V1BroadcastTestPatternMode.MotionTiming &&
		_audioTestSignals.TryGetValue(sourceId, out var audioSignal) &&
		audioSignal.Configuration.Mode == GeneratedAudioTestSignalMode.Pulse;

	private V1AudioProductionSnapshot AudioProductionSnapshotUnsafe()
	{
		var configuration = _audioProduction.Configuration;
		var program = configuration.GetBus(AudioBusId.Program);
		var buses = configuration.Buses
			.Select(bus => new V1AudioProductionBusSnapshot(bus.BusId.Value, bus.MasterGain, bus.Muted))
			.ToArray();
		var sources = configuration.Sources
			.Select(source => new V1AudioProductionSourceSnapshot(
				source.SourceId,
				source.Gain,
				source.Muted,
				source.FollowRoutedSource,
				Array.AsReadOnly(source.BusAssignments.Select(bus => bus.Value).ToArray())))
			.ToArray();
		var crossfade = configuration.Crossfade is { } activeCrossfade
			? new V1AudioCrossfadeSnapshot(
				activeCrossfade.BusId.Value,
				activeCrossfade.FromSourceId,
				activeCrossfade.ToSourceId,
				activeCrossfade.StartSamplePosition,
				activeCrossfade.DurationSamples,
				activeCrossfade.Law)
			: null;
		var ducking = configuration.Ducking is { } activeDucking
			? new V1AudioDuckingSnapshot(
				activeDucking.BusId.Value,
				activeDucking.Enabled,
				activeDucking.SidechainSourceId,
				Array.AsReadOnly(activeDucking.TargetSourceIds.ToArray()),
				activeDucking.Threshold,
				activeDucking.Attenuation,
				activeDucking.AttackSamples,
				activeDucking.HoldSamples,
				activeDucking.ReleaseSamples)
			: null;
		var result = _lastAudioProductionResult;
		return new V1AudioProductionSnapshot(
			configuration.Revision,
			Array.AsReadOnly(buses),
			program.MasterGain,
			program.Muted,
			configuration.ClipStrategy,
			Array.AsReadOnly(sources),
			crossfade,
			ducking,
			result.LeftPeak,
			result.RightPeak,
			result.PreClipPeak,
			result.Clipping,
			result.ClippedSampleValues,
			result.DuckingGain,
			result.DuckingReduction,
			result.SidechainAvailable,
			result.CrossfadeProgress,
			result.ActiveSourceCount,
			result.MissingSourceCount);
	}

	private void ValidateAudioProductionConfigurationUnsafe(AudioProductionConfiguration configuration)
	{
		var expectedSources = _audioStreams.Keys.ToHashSet();
		var configuredSources = configuration.Sources.Select(source => source.SourceId).ToHashSet();
		if (!expectedSources.SetEquals(configuredSources))
			throw new ArgumentException("Audio production configuration must contain every admitted Runtime audio source exactly once.", nameof(configuration));
		if (configuration.Crossfade is { } crossfade)
		{
			if (!configuration.GetSource(crossfade.FromSourceId).IsAssignedTo(crossfade.BusId) ||
				!configuration.GetSource(crossfade.ToSourceId).IsAssignedTo(crossfade.BusId))
			{
				throw new ArgumentException("Audio crossfade sources must be assigned to the target bus.", nameof(configuration));
			}
		}
	}

	private V1AudioProgramSnapshot AudioProgramSnapshotUnsafe()
	{
		var activeSource = _lastAudioResult?.VideoSourceId ?? _audio.ActiveVideoSourceId;
		var routing = _audio.RoutingState;
		var routedAudioSource = _audio.ResolveAudioSource(activeSource);
		var routedStream = _audioStreams[routedAudioSource].StreamId;
		var routedState = _audio.GetInputState(routedStream);
		if (_lastAudioResult is not { } result ||
			result.RoutingRevision != routing.Revision ||
			result.AudioSourceId != routedAudioSource)
		{
			return new V1AudioProgramSnapshot(
				activeSource,
				routedStream,
				routedState.Gain.Linear,
				routedState.Muted,
				0,
				0,
				0,
				false,
				routedState.Muted ? V1AudioHealthState.Muted : V1AudioHealthState.Silence,
				routing.Mode,
				routing.Revision,
				routedAudioSource);
		}

		var mix = _lastAudioProductionResult;
		var audioProduction = _audioProduction.Configuration;
		var programBus = audioProduction.GetBus(AudioBusId.Program);
		var mixMuted = programBus.Muted || IsProgramMixMuted(audioProduction, routedAudioSource);
		var health = result.Status switch
		{
			AudioFollowVideoStatus.Underrun => V1AudioHealthState.Underrun,
			not AudioFollowVideoStatus.Emitted => V1AudioHealthState.Error,
			_ when mixMuted => V1AudioHealthState.Muted,
			_ when mix.MissingSourceCount > 0 => V1AudioHealthState.Error,
			_ when mix.Clipping => V1AudioHealthState.Clipping,
			_ when mix.MasterPeak <= 0.000001 => V1AudioHealthState.Silence,
			_ => V1AudioHealthState.Healthy
		};
		return new V1AudioProgramSnapshot(
			result.VideoSourceId,
			result.StreamId ?? routedStream,
			result.Gain.Linear,
			mixMuted,
			mix.LeftPeak,
			mix.RightPeak,
			mix.MasterPeak,
			mix.Clipping,
			health,
			routing.Mode,
			routing.Revision,
			routedAudioSource);
	}

	private static bool IsProgramMixMuted(
		AudioProductionConfiguration configuration,
		MediaSourceId routedAudioSource)
	{
		var hasEligibleSource = false;
		for (var index = 0; index < configuration.Sources.Count; index++)
		{
			var source = configuration.Sources[index];
			if (!source.IsAssignedTo(AudioBusId.Program))
				continue;
			if (source.FollowRoutedSource && source.SourceId != routedAudioSource)
				continue;
			hasEligibleSource = true;
			if (!source.Muted)
				return false;
		}
		return hasEligibleSource;
	}

	private void RefreshVirtualAudioMetersUnsafe(ulong sequence)
	{
		foreach (var sourceId in _audioStreams.Keys)
		{
			var packet = _virtualAudio.GetSource(sourceId).GeneratePacket(sequence);
			if (_audioTestSignals.TryGetValue(sourceId, out var testSignal))
			{
				_audioTestSignalFrames[sourceId] = testSignal.Inspect(packet.Descriptor.Timing);
				continue;
			}
			if (_audioMeters[sourceId].External)
				continue;
			_audioMeters[sourceId] = new AudioMeterObservation(
				new AudioStereoMeter(packet.LeftPeakLevel, packet.RightPeakLevel),
				Available: true,
				External: false);
		}
	}

	private V1GraphicsOverlaySnapshot GraphicsOverlaySnapshotUnsafe()
	{
		if (_operatorGraphicsAsset is not null)
		{
			return new V1GraphicsOverlaySnapshot(
				true,
				_operatorGraphicsAssetName,
				_operatorGraphicsAssetWidth,
				_operatorGraphicsAssetHeight,
				_operatorGraphicsVisible,
				_operatorGraphicsPositionX,
				_operatorGraphicsPositionY,
				_operatorGraphicsScale);
		}

		return new V1GraphicsOverlaySnapshot(
			_productionCgAsset is not null,
			_productionCgAsset is null ? null : "production-cg-text",
			_productionCgAssetWidth,
			_productionCgAssetHeight,
			_productionCgText.Visible,
			_productionCgPositionX,
			_productionCgPositionY,
			1.0);
	}

	private V1GraphicsOverlaySnapshot ProductionCgOverlaySnapshotUnsafe() => new(
		_productionCgAsset is not null,
		_productionCgAsset is null ? null : "production-cg-text",
		_productionCgAssetWidth,
		_productionCgAssetHeight,
		_productionCgText.Visible,
		_productionCgPositionX,
		_productionCgPositionY,
		1.0);

	private IReadOnlyList<V1CompositingLayerSnapshot> CompositingLayerSnapshotsUnsafe()
	{
		var snapshots = new List<V1CompositingLayerSnapshot>(3);
		// Dynamic visual state is a Runtime-local observational effect (for example the AI showcase)
		// and must not be promoted into Control's authoritative compositing resource set.
		if (_visualLayerMode == V1VisualLayerMode.Static)
		{
			snapshots.Add(new V1CompositingLayerSnapshot(
				LegacyVisualLayerId,
				V1CompositingLayerKind.LegacyVisual,
				_legacyVisualLayerOrder,
				true,
				_legacyVisualLayerOpacity,
				0,
				0,
				1,
				_visualLayerMode.ToString()));
		}
		if (_operatorGraphicsAsset is not null)
		{
			snapshots.Add(new V1CompositingLayerSnapshot(
				BitmapGraphicsLayerId,
				V1CompositingLayerKind.BitmapGraphics,
				_operatorGraphicsLayerOrder,
				_operatorGraphicsVisible,
				_operatorGraphicsOpacity,
				_operatorGraphicsPositionX,
				_operatorGraphicsPositionY,
				_operatorGraphicsScale,
				_operatorGraphicsAssetName ?? "bitmap",
				_operatorGraphicsRotationDegrees,
				_operatorGraphicsAnchorX,
				_operatorGraphicsAnchorY,
				_operatorGraphicsCropLeft,
				_operatorGraphicsCropTop,
				_operatorGraphicsCropRight,
				_operatorGraphicsCropBottom,
				processingStack: _operatorGraphicsProcessingStack));
		}
		if (_productionCgAsset is not null)
		{
			snapshots.Add(new V1CompositingLayerSnapshot(
				ProductionCgLayerId,
				V1CompositingLayerKind.ProductionCg,
				_productionCgLayerOrder,
				_productionCgText.Visible,
				_productionCgOpacity,
				_productionCgPositionX,
				_productionCgPositionY,
				_productionCgScale,
				_productionCgText.Text ?? "production-cg-text",
				_productionCgRotationDegrees,
				_productionCgAnchorX,
				_productionCgAnchorY,
				_productionCgCropLeft,
				_productionCgCropTop,
				_productionCgCropRight,
				_productionCgCropBottom,
				processingStack: _productionCgProcessingStack));
		}
		return snapshots
			.OrderBy(layer => layer.Order)
			.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
			.ToArray();
	}

	private void RebuildOperatorGraphicsLayerUnsafe()
	{
		RebuildCompositingLayerUnsafe(
			_operatorGraphicsAsset,
			_operatorGraphicsAssetWidth,
			_operatorGraphicsAssetHeight,
			_operatorGraphicsLayerScratch,
			_operatorGraphicsLayerBuffer,
			_operatorGraphicsLayer,
			_operatorGraphicsPositionX,
			_operatorGraphicsPositionY,
			_operatorGraphicsScale,
			_operatorGraphicsRotationDegrees,
			_operatorGraphicsAnchorX,
			_operatorGraphicsAnchorY,
			_operatorGraphicsCropLeft,
			_operatorGraphicsCropTop,
			_operatorGraphicsCropRight,
			_operatorGraphicsCropBottom,
			_operatorGraphicsProcessingStack);
	}

	private void RebuildProductionCgLayerUnsafe()
	{
		RebuildCompositingLayerUnsafe(
			_productionCgAsset,
			_productionCgAssetWidth,
			_productionCgAssetHeight,
			_productionCgLayerScratch,
			_productionCgLayerBuffer,
			_productionCgLayer,
			_productionCgPositionX,
			_productionCgPositionY,
			_productionCgScale,
			_productionCgRotationDegrees,
			_productionCgAnchorX,
			_productionCgAnchorY,
			_productionCgCropLeft,
			_productionCgCropTop,
			_productionCgCropRight,
			_productionCgCropBottom,
			_productionCgProcessingStack);
	}

	private void RebuildCompositingLayerUnsafe(
		byte[]? sourcePixels,
		uint sourceWidthValue,
		uint sourceHeightValue,
		byte[] targetPixels,
		RgbaFrameBuffer targetBuffer,
		DynamicRgbaSource targetSource,
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
		IReadOnlyList<PreparedCompositingProcessingNodeState> processingStack)
	{
		targetPixels.AsSpan().Clear();
		if (sourcePixels is null)
		{
			targetBuffer.CopyPixelsFrom(targetPixels);
			targetSource.Update(targetBuffer);
			return;
		}

		var sourceWidth = checked((int)sourceWidthValue);
		var sourceHeight = checked((int)sourceHeightValue);
		var sourceLeft = Math.Min(sourceWidth - 1, (int)Math.Floor(cropLeft * sourceWidth));
		var sourceTop = Math.Min(sourceHeight - 1, (int)Math.Floor(cropTop * sourceHeight));
		var sourceRightExclusive = Math.Max(sourceLeft + 1, Math.Min(sourceWidth, (int)Math.Ceiling((1.0 - cropRight) * sourceWidth)));
		var sourceBottomExclusive = Math.Max(sourceTop + 1, Math.Min(sourceHeight, (int)Math.Ceiling((1.0 - cropBottom) * sourceHeight)));
		var croppedWidth = sourceRightExclusive - sourceLeft;
		var croppedHeight = sourceBottomExclusive - sourceTop;
		var targetWidth = Math.Max(1, checked((int)Math.Round(croppedWidth * scale)));
		var targetHeight = Math.Max(1, checked((int)Math.Round(croppedHeight * scale)));
		var outputWidth = checked((int)_format.Width);
		var outputHeight = checked((int)_format.Height);
		var originX = checked((int)Math.Round(positionX * Math.Max(0, outputWidth - 1)));
		var originY = checked((int)Math.Round(positionY * Math.Max(0, outputHeight - 1)));
		var pivotX = anchorX * Math.Max(0, targetWidth - 1);
		var pivotY = anchorY * Math.Max(0, targetHeight - 1);
		var radians = rotationDegrees * (Math.PI / 180.0);
		var cos = Math.Cos(radians);
		var sin = Math.Sin(radians);

		var left = -pivotX;
		var right = Math.Max(0, targetWidth - 1) - pivotX;
		var top = -pivotY;
		var bottom = Math.Max(0, targetHeight - 1) - pivotY;
		var corner0X = (cos * left) - (sin * top);
		var corner0Y = (sin * left) + (cos * top);
		var corner1X = (cos * right) - (sin * top);
		var corner1Y = (sin * right) + (cos * top);
		var corner2X = (cos * left) - (sin * bottom);
		var corner2Y = (sin * left) + (cos * bottom);
		var corner3X = (cos * right) - (sin * bottom);
		var corner3Y = (sin * right) + (cos * bottom);
		var minX = Math.Max(0, checked((int)Math.Floor(originX + Math.Min(Math.Min(corner0X, corner1X), Math.Min(corner2X, corner3X)))));
		var maxX = Math.Min(outputWidth - 1, checked((int)Math.Ceiling(originX + Math.Max(Math.Max(corner0X, corner1X), Math.Max(corner2X, corner3X)))));
		var minY = Math.Max(0, checked((int)Math.Floor(originY + Math.Min(Math.Min(corner0Y, corner1Y), Math.Min(corner2Y, corner3Y)))));
		var maxY = Math.Min(outputHeight - 1, checked((int)Math.Ceiling(originY + Math.Max(Math.Max(corner0Y, corner1Y), Math.Max(corner2Y, corner3Y)))));

		for (var destinationY = minY; destinationY <= maxY; destinationY++)
		{
			for (var destinationX = minX; destinationX <= maxX; destinationX++)
			{
				var deltaX = destinationX - originX;
				var deltaY = destinationY - originY;
				var localX = (cos * deltaX) + (sin * deltaY) + pivotX;
				var localY = (-sin * deltaX) + (cos * deltaY) + pivotY;
				if (localX < -0.000001 || localY < -0.000001 ||
					localX > targetWidth - 1 + 0.000001 ||
					localY > targetHeight - 1 + 0.000001)
				{
					continue;
				}

				var scaledX = Math.Clamp(localX, 0, Math.Max(0, targetWidth - 1));
				var scaledY = Math.Clamp(localY, 0, Math.Max(0, targetHeight - 1));
				var sourceX = sourceLeft + Math.Min(croppedWidth - 1, (int)((long)Math.Floor(scaledX) * croppedWidth / targetWidth));
				var sourceY = sourceTop + Math.Min(croppedHeight - 1, (int)((long)Math.Floor(scaledY) * croppedHeight / targetHeight));
				var sourceOffset = checked((sourceY * sourceWidth + sourceX) * 4);
				var destinationOffset = checked((destinationY * outputWidth + destinationX) * 4);
				var red = sourcePixels[sourceOffset];
				var green = sourcePixels[sourceOffset + 1];
				var blue = sourcePixels[sourceOffset + 2];
				var alpha = sourcePixels[sourceOffset + 3];
				for (var processingIndex = 0; processingIndex < processingStack.Count; processingIndex++)
					ApplyProcessingNode(processingStack[processingIndex], ref red, ref green, ref blue, ref alpha);
				targetPixels[destinationOffset] = red;
				targetPixels[destinationOffset + 1] = green;
				targetPixels[destinationOffset + 2] = blue;
				targetPixels[destinationOffset + 3] = alpha;
			}
		}

		targetBuffer.CopyPixelsFrom(targetPixels);
		targetSource.Update(targetBuffer);
	}

	private static void ApplyProcessingNode(
		PreparedCompositingProcessingNodeState processingNode,
		ref byte red,
		ref byte green,
		ref byte blue,
		ref byte alpha)
	{
		if (!processingNode.Enabled)
			return;

		switch (processingNode.Kind)
		{
			case PreparedCompositingProcessingNodeKind.ColorGrade:
				ApplyColorGrade(processingNode.ColorGrade ?? throw new InvalidOperationException("Color Grade settings are required."), ref red, ref green, ref blue);
				break;
			case PreparedCompositingProcessingNodeKind.ChromaKey:
				ApplyChromaKey(processingNode.ChromaKey ?? throw new InvalidOperationException("Chroma Key settings are required."), ref red, ref green, ref blue, ref alpha);
				break;
			default:
				throw new NotSupportedException($"Processing node kind '{processingNode.Kind}' is not supported.");
		}
	}

	private static void ApplyColorGrade(
		PreparedColorGradeSettings grade,
		ref byte red,
		ref byte green,
		ref byte blue)
	{
		var r = ((red - 127.5) * grade.Contrast) + 127.5 + (grade.Brightness * 255.0);
		var g = ((green - 127.5) * grade.Contrast) + 127.5 + (grade.Brightness * 255.0);
		var b = ((blue - 127.5) * grade.Contrast) + 127.5 + (grade.Brightness * 255.0);
		var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
		r = luma + ((r - luma) * grade.Saturation);
		g = luma + ((g - luma) * grade.Saturation);
		b = luma + ((b - luma) * grade.Saturation);
		red = ClampByte(r);
		green = ClampByte(g);
		blue = ClampByte(b);
	}

	private static void ApplyChromaKey(
		PreparedChromaKeySettings key,
		ref byte red,
		ref byte green,
		ref byte blue,
		ref byte alpha)
	{
		var source = ToBt709Chroma(red, green, blue);
		var selected = ToBt709Chroma(key.KeyRed, key.KeyGreen, key.KeyBlue);
		var deltaCb = source.Cb - selected.Cb;
		var deltaCr = source.Cr - selected.Cr;
		var distance = Math.Min(1.0, Math.Sqrt((deltaCb * deltaCb) + (deltaCr * deltaCr)) * Math.Sqrt(2.0));

		double matte;
		if (key.Softness <= 0.0)
		{
			matte = distance <= key.Tolerance ? 0.0 : 1.0;
		}
		else
		{
			var edge = Math.Clamp((distance - key.Tolerance) / key.Softness, 0.0, 1.0);
			matte = edge * edge * (3.0 - (2.0 * edge));
		}

		alpha = ClampByte(alpha * matte);

		if (key.SpillSuppression <= 0.0 || matte >= 1.0)
			return;

		var keyMagnitudeSquared = (selected.Cb * selected.Cb) + (selected.Cr * selected.Cr);
		if (keyMagnitudeSquared <= double.Epsilon)
			return;

		var projection = Math.Max(0.0, ((source.Cb * selected.Cb) + (source.Cr * selected.Cr)) / keyMagnitudeSquared);
		var suppression = key.SpillSuppression * (1.0 - matte);
		var cb = source.Cb - (selected.Cb * projection * suppression);
		var cr = source.Cr - (selected.Cr * projection * suppression);

		var r = source.Luma + (1.5748 * cr);
		var b = source.Luma + (1.8556 * cb);
		var g = (source.Luma - (0.2126 * r) - (0.0722 * b)) / 0.7152;
		red = ClampByte(r * 255.0);
		green = ClampByte(g * 255.0);
		blue = ClampByte(b * 255.0);
	}

	private static (double Luma, double Cb, double Cr) ToBt709Chroma(byte red, byte green, byte blue)
	{
		var r = red / 255.0;
		var g = green / 255.0;
		var b = blue / 255.0;
		var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
		return (luma, (b - luma) / 1.8556, (r - luma) / 1.5748);
	}

	private static byte ClampByte(double value) =>
		(byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

	private (int X, int Y) ResolveProductionCgOrigin(V1ProductionCgTextDefinition definition)
	{
		var anchorX = checked((int)Math.Round(definition.PositionX * Math.Max(0, _format.Width - 1)));
		var anchorY = checked((int)Math.Round(definition.PositionY * Math.Max(0, _format.Height - 1)));
		var width = checked((int)definition.BoxWidth);
		var height = checked((int)definition.BoxHeight);
		var x = definition.Anchor switch
		{
			V1CgAnchor.TopLeft or V1CgAnchor.CenterLeft or V1CgAnchor.BottomLeft => anchorX,
			V1CgAnchor.TopCenter or V1CgAnchor.Center or V1CgAnchor.BottomCenter => anchorX - (width / 2),
			V1CgAnchor.TopRight or V1CgAnchor.CenterRight or V1CgAnchor.BottomRight => anchorX - width,
			_ => throw new ArgumentOutOfRangeException(nameof(definition), "Production CG anchor is invalid.")
		};
		var y = definition.Anchor switch
		{
			V1CgAnchor.TopLeft or V1CgAnchor.TopCenter or V1CgAnchor.TopRight => anchorY,
			V1CgAnchor.CenterLeft or V1CgAnchor.Center or V1CgAnchor.CenterRight => anchorY - (height / 2),
			V1CgAnchor.BottomLeft or V1CgAnchor.BottomCenter or V1CgAnchor.BottomRight => anchorY - height,
			_ => throw new ArgumentOutOfRangeException(nameof(definition), "Production CG anchor is invalid.")
		};

		if (x < 0 || y < 0 || x + width > _format.Width || y + height > _format.Height)
			throw new ArgumentOutOfRangeException(nameof(definition), "Production CG anchor and bounding box must resolve fully inside the active Program frame.");
		return (x, y);
	}

	private sealed record MaterializedCompositingLayer(
		string LayerId,
		int Order,
		GpuFrame Frame,
		GpuKeyLayer Layer);

	private bool RequiresGpuSourceUnsafe(MediaSourceId sourceId, MediaSourceId committedSource)
	{
		if (_transition is null)
			return sourceId == committedSource;

		return sourceId == _transition.Intent.FromSourceId ||
			sourceId == _transition.Intent.ToSourceId;
	}

	private (GpuFrame From, GpuFrame To, GpuTransition Transition, byte BlendWeight, bool Complete) ResolveTransition(
		MediaSourceId committedSource,
		ulong sequence,
		IReadOnlyDictionary<MediaSourceId, GpuFrame> frames)
	{
		if (_transition is null)
		{
			var current = frames[committedSource];
			return (current, current, GpuTransition.CutToA, 0, false);
		}

		var intent = _transition.Intent;
		var from = frames[intent.FromSourceId];
		var to = frames[intent.ToSourceId];
		if (intent.Kind == RuntimeProgramTransitionKind.Cut)
			return (from, to, GpuTransition.CutToB, byte.MaxValue, true);

		var offset = sequence - _transition.StartSequence;
		var completedFrame = offset + 1 >= intent.DurationFrames;
		var numerator = Math.Min((ulong)intent.DurationFrames, offset + 1) * byte.MaxValue;
		var weight = (byte)(numerator / intent.DurationFrames);
		return (from, to, GpuTransition.Dissolve(weight), weight, completedFrame);
	}

	private void PrepareAudioProductionBlockUnsafe(
		ulong sequence,
		MediaSourceId routedAudioSource,
		out AudioBufferDescriptor routedAudioBuffer,
		out AudioStereoMeter routedMeter,
		out AudioBufferDescriptor? afvBuffer)
	{
		routedAudioBuffer = CreateAudioBuffer(routedAudioSource, sequence);
		routedMeter = new AudioStereoMeter(0, 0);
		var routedAvailable = false;

		for (var sourceIndex = 0; sourceIndex < _audioProductionSourceOrder.Length; sourceIndex++)
		{
			var sourceId = _audioProductionSourceOrder[sourceIndex];
			var descriptor = CreateAudioBuffer(sourceId, sequence);
			var requiredValues = checked((int)(descriptor.Timing.SampleCount * descriptor.Format.ChannelCount));
			var destination = _audioProductionSourceSamples[sourceIndex].AsSpan(0, requiredValues);
			var available = true;
			AudioStereoMeter meter;

			if (_audioTestSignals.TryGetValue(sourceId, out var testSignal))
			{
				var generated = testSignal.FillInterleavedFloat32(descriptor.Timing, destination);
				_audioTestSignalFrames[sourceId] = generated;
				meter = new AudioStereoMeter(generated.LeftPeakLevel, generated.RightPeakLevel);
			}
			else
			{
				var observation = _audioMeters[sourceId];
				if (observation.External)
				{
					if (!observation.Available)
					{
						destination.Clear();
						available = false;
						meter = new AudioStereoMeter(0, 0);
					}
					else if (TryConsumeExternalAudioSamplesUnsafe(sourceId, destination))
					{
						meter = AudioMetering.MeasureInterleavedStereoFloat32(destination);
					}
					else
					{
						FillReferenceAudioSamples(descriptor, observation.Meter, destination);
						meter = observation.Meter;
					}
				}
				else
				{
					var signalAvailable = !_inputSignals.TryGetValue(sourceId, out var signal) ||
						signal != V1InputSignalState.Lost;
					if (!signalAvailable)
					{
						destination.Clear();
						available = false;
						meter = new AudioStereoMeter(0, 0);
					}
					else
					{
						var packet = _virtualAudio.GetSource(sourceId).GeneratePacket(sequence);
						meter = new AudioStereoMeter(packet.LeftPeakLevel, packet.RightPeakLevel);
						FillReferenceAudioSamples(descriptor, meter, destination);
					}
				}
			}

			_audioProductionSourceBuffers[sourceIndex] = new AudioProductionSourceBuffer(
				sourceId,
				_audioProductionSourceSamples[sourceIndex].AsMemory(0, requiredValues),
				available);

			if (sourceId == routedAudioSource)
			{
				routedMeter = meter;
				routedAvailable = available;
			}
		}

		afvBuffer = routedAvailable ? routedAudioBuffer : null;
	}

	private AudioBufferDescriptor CreateProgramAudioBuffer(ulong sequence)
	{
		var referenceStream = _audioStreams.Values.First();
		var window = AudioVideoTimingRelationship.GetSampleWindow(
			_format.FrameRate,
			referenceStream.Format.SampleRate,
			sequence);
		return new AudioBufferDescriptor(
			MediaContractVersion.Current,
			_programAudioStreamId,
			referenceStream.Format,
			referenceStream.TimingDomainId,
			new AudioBufferTiming(
				window.SamplePosition,
				window.SampleCount,
				window.PresentationTimestamp,
				window.Timebase),
			new OpaqueAudioHandle("runtime.audio.program.bus", $"{_programAudioStreamId}:{sequence}"));
	}

	private bool TryConsumeExternalAudioSamplesUnsafe(MediaSourceId sourceId, Span<float> destination)
	{
		var queue = _externalAudioQueues[sourceId];
		if (queue.Count < destination.Length)
			return false;

		for (var index = 0; index < destination.Length; index++)
			destination[index] = queue.Dequeue();
		return true;
	}

	private static void FillReferenceAudioSamples(
		AudioBufferDescriptor descriptor,
		AudioStereoMeter meter,
		Span<float> destination)
	{
		var requiredValues = checked((int)(descriptor.Timing.SampleCount * descriptor.Format.ChannelCount));
		if (descriptor.Format != AudioFormat.Stereo48kFloat32 || destination.Length != requiredValues)
			throw new InvalidOperationException("Advanced audio production currently requires an exact Stereo 48 kHz Float32 block.");

		var amplitude = checked((float)meter.PeakLevel);
		for (uint sampleIndex = 0; sampleIndex < descriptor.Timing.SampleCount; sampleIndex++)
		{
			var absoluteSample = descriptor.Timing.SamplePosition + sampleIndex;
			var value = (absoluteSample & 1UL) == 0 ? amplitude : -amplitude;
			var offset = checked((int)sampleIndex * 2);
			destination[offset] = value;
			destination[offset + 1] = value;
		}
	}

	private AudioBufferDescriptor CreateAudioBuffer(MediaSourceId sourceId, ulong sequence)
	{
		var stream = _audioStreams[sourceId];
		var window = AudioVideoTimingRelationship.GetSampleWindow(_format.FrameRate, stream.Format.SampleRate, sequence);
		return new AudioBufferDescriptor(
			MediaContractVersion.Current,
			stream.StreamId,
			stream.Format,
			stream.TimingDomainId,
			new AudioBufferTiming(window.SamplePosition, window.SampleCount, window.PresentationTimestamp, window.Timebase),
			new OpaqueAudioHandle("virtual.embedded.audio", $"{stream.StreamId}:{sequence}"));
	}

	private byte[]? ConsumeExternalAudioPayloadUnsafe(
		MediaSourceId sourceId,
		AudioBufferDescriptor descriptor)
	{
		var queue = _externalAudioQueues[sourceId];
		var requiredValues = checked((int)(descriptor.Timing.SampleCount * descriptor.Format.ChannelCount));
		if (queue.Count < requiredValues)
			return null;

		var samples = new float[requiredValues];
		for (var index = 0; index < requiredValues; index++)
			samples[index] = queue.Dequeue();
		return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
	}

	private static byte[] ApplyAudioStateToPayload(
		byte[] payload,
		AudioFollowVideoResult result)
	{
		if (!result.Emitted)
			return Array.Empty<byte>();
		return ApplyAudioStateToPayloadInPlace(payload.ToArray(), result);
	}

	private static byte[] ApplyAudioStateToPayloadInPlace(
		byte[] payload,
		AudioFollowVideoResult result)
	{
		if (!result.Emitted)
			return Array.Empty<byte>();
		var samples = MemoryMarshal.Cast<byte, float>(payload.AsSpan());
		for (var index = 0; index < samples.Length; index++)
		{
			var value = result.Muted ? 0f : samples[index] * checked((float)result.Gain.Linear);
			samples[index] = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
		}
		return payload;
	}

	private void EnsureAudioSourceUnsafe(MediaSourceId sourceId)
	{
		if (!_audioStreams.ContainsKey(sourceId))
			throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");
	}

	private static byte[] MaterializeGeneratedAudioPayload(
		AudioBufferDescriptor descriptor,
		GeneratedAudioTestSignalGenerator generator,
		out GeneratedAudioTestSignalFrameInfo frameInfo)
	{
		if (descriptor.Format.SampleFormat != AudioSampleFormat.Float32)
			throw new InvalidOperationException("Generated audio payload requires Float32 audio.");
		if (descriptor.Format != generator.Configuration.Format)
			throw new InvalidOperationException("Generated audio configuration must match the Runtime audio buffer format.");

		var valueCount = checked((int)(descriptor.Timing.SampleCount * descriptor.Format.ChannelCount));
		var payload = new byte[checked(valueCount * sizeof(float))];
		var samples = MemoryMarshal.Cast<byte, float>(payload.AsSpan());
		frameInfo = generator.FillInterleavedFloat32(descriptor.Timing, samples);
		return payload;
	}

	private static byte[] MaterializeReferenceAudioPayload(
		AudioBufferDescriptor descriptor,
		AudioFollowVideoResult result)
	{
		if (!result.Emitted)
			return Array.Empty<byte>();
		if (descriptor.Format.SampleFormat != AudioSampleFormat.Float32)
			throw new InvalidOperationException("V1 reference recording payload supports Float32 audio only.");

		var channelCount = checked((int)descriptor.Format.ChannelCount);
		var sampleCount = checked((int)descriptor.Timing.SampleCount);
		var payload = new byte[checked(sampleCount * channelCount * sizeof(float))];
		var amplitude = result.Muted ? 0f : checked((float)result.PeakLevel);
		var offset = 0;
		for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
		{
			var absoluteSample = descriptor.Timing.SamplePosition + checked((ulong)sampleIndex);
			var value = (absoluteSample & 1UL) == 0 ? amplitude : -amplitude;
			for (var channel = 0; channel < channelCount; channel++)
			{
				BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset, sizeof(float)), value);
				offset += sizeof(float);
			}
		}

		return payload;
	}

	private static ProgramPixelProbe ProbeCenter(ReadOnlySpan<byte> pixels, VideoFormat format)
	{
		var width = checked((int)format.Width);
		var height = checked((int)format.Height);
		var offset = checked(((height / 2) * width + width / 2) * 4);
		return new ProgramPixelProbe(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
	}

	private void Observe(string value) => _observations.Add(value);

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	private sealed class GpuRecordingPayloadLease : IProgramRecordingPayloadLease
	{
		private GpuReadbackLease? _lease;

		public GpuRecordingPayloadLease(GpuReadbackLease lease)
		{
			_lease = lease ?? throw new ArgumentNullException(nameof(lease));
		}

		public ReadOnlyMemory<byte> Memory =>
			(Volatile.Read(ref _lease) ?? throw new ObjectDisposedException(nameof(GpuRecordingPayloadLease))).Memory;

		public void Dispose()
		{
			Interlocked.Exchange(ref _lease, null)?.Dispose();
		}
	}

	private sealed record AudioMeterObservation(AudioStereoMeter Meter, bool Available, bool External);
	private sealed record AnchoredTransition(RuntimeProgramTransitionIntent Intent, ulong StartSequence);
}

internal static class HostIdentity
{
	public static Identity Create(string scope, params string[] parts)
	{
		var canonical = string.Join('\u001f', new[] { scope }.Concat(parts));
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return new Identity(new Guid(hash.AsSpan(0, 16)));
	}
}