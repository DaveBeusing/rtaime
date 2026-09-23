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
	Healthy = 1,
	Degraded = 2,
	Unstable = 3,
	Lost = 4,
	Recovering = 5
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

public sealed record V1ProgramBoundaryResult(
	ulong SequenceNumber,
	MediaSourceId CommittedProgramSourceId,
	FrameDescriptor ProgramFrame,
	byte[] ProgramPixels,
	ProgramPixelProbe PixelProbe,
	AudioFollowVideoResult Audio,
	AudioBufferDescriptor ProgramAudioBuffer,
	byte[] ProgramAudioPayload,
	RecordingEnqueueResult? Recording,
	RuntimeProgramTransitionKind? TransitionKind,
	byte BlendWeight,
	V1VisualLayerMode VisualLayerMode,
	int ActiveGpuSurfacesAfterBoundary);

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

public sealed record V1CompositingLayerSnapshot(
	string LayerId,
	V1CompositingLayerKind Kind,
	int Order,
	bool Visible,
	byte Opacity,
	double PositionX,
	double PositionY,
	double Scale,
	string ContentIdentity);

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
	V1AudioHealthState Health);

public sealed record V1RecordingOperatorSnapshot(
	RecordingLifecycleState State,
	TimeSpan Elapsed,
	string? Destination,
	string? FileName,
	string? FinalPath,
	RecordingStatistics Statistics,
	Failure? Failure);

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
	IReadOnlyList<V1CompositingLayerSnapshot>? CompositingLayers = null);

/// <summary>
/// Windows V1 reference composition root for committed execution, timed media, GPU composition,
/// Program output, Audio Follow Video and failure-isolated recording. It owns execution, never production authority.
/// </summary>
public sealed class V1RuntimeHostService : IAsyncDisposable
{
	public const string LegacyVisualLayerId = "legacy-visual";
	public const string BitmapGraphicsLayerId = "bitmap-graphics";
	public const string ProductionCgLayerId = "production-cg";

	private readonly object _gate = new();
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
	private readonly RuntimeMonitoringHub _monitoringHub;
	private readonly RuntimeMonitoringTap _monitoringTap;
	private readonly Stopwatch _uptimeClock = Stopwatch.StartNew();
	private readonly SystemHardwareTelemetry _hardwareTelemetry = new();
	private readonly List<string> _observations = new();

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
	private byte _operatorGraphicsOpacity = byte.MaxValue;
	private int _operatorGraphicsLayerOrder = 1;
	private byte[]? _productionCgAsset;
	private uint _productionCgAssetWidth;
	private uint _productionCgAssetHeight;
	private double _productionCgPositionX;
	private double _productionCgPositionY;
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
		IGpuProcessingBackend? gpuBackend = null)
	{
		_format = format;
		_virtualMedia = new VirtualMediaReferenceProvider(sourceAId, sourceBId, format);
		_sourceAPipeline = CreatePipeline();
		_sourceBPipeline = CreatePipeline();
		_gpuBackend = gpuBackend ?? new ManagedReferenceGpuBackend();
		_gpu = new GpuProcessingProvider(_gpuBackend);
		_gpu.Start();
		_runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());

		_virtualAudio = new VirtualEmbeddedAudioReferenceProvider(sourceAId, sourceBId, format);
		_audioStreams = _virtualAudio.Streams.ToDictionary(stream => stream.FollowedVideoSourceId);
		_audio = new AudioFollowVideoEngine(_virtualAudio.Streams, format.FrameRate, sourceAId);
		_audioMeters = _audioStreams.Keys.ToDictionary(
			sourceId => sourceId,
			_ => new AudioMeterObservation(new AudioStereoMeter(0, 0), Available: false, External: false));
		_externalAudioQueues = _audioStreams.Keys.ToDictionary(
			sourceId => sourceId,
			_ => new Queue<float>());

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
		_recorder = new ProgramRecorder(recordingWriter);
		_recordingBridge = new RuntimeRecordingBridge(_recorder);
		_monitoringHub = new RuntimeMonitoringHub();
		_monitoringTap = new RuntimeMonitoringTap(_monitoringHub);
	}

	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors =>
		Array.AsReadOnly(new[] { _virtualMedia.Descriptor, _gpu.Descriptor });

	public IReadOnlyList<VirtualOutputFrame> ProgramFrames =>
		_programOutput?.Frames ?? Array.Empty<VirtualOutputFrame>();

	public IReadOnlyList<VirtualOutputFrame> AuxFrames =>
		_auxOutput?.Frames ?? Array.Empty<VirtualOutputFrame>();

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

	public IReadOnlyList<string> Observations
	{
		get
		{
			lock (_gate)
				return new ReadOnlyCollection<string>(_observations.ToArray());
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
					_audio.Statistics,
					_recorder.Snapshot,
					RecordingOperatorSnapshotUnsafe(),
					PerformanceSnapshotUnsafe(hardware),
					_gpu.ActiveSurfaceCount,
					AvSyncDiagnosticsSnapshotUnsafe(),
					_productionCgText,
					OutputRoleSnapshotsUnsafe(),
					CompositingLayerSnapshotsUnsafe());
			}
		}
	}

	public RuntimeHostApplyResult ApplyExecution(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition = null)
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

			var prepare = _runtime.Prepare(preparedExecution);
			if (prepare.Status != RuntimePrepareStatus.Prepared)
			{
				Observe($"runtime.prepare.rejected:{prepare.Failure?.Code}");
				return new RuntimeHostApplyResult(prepare, null, null);
			}

			var commit = _runtime.Commit(new RuntimeCommitRequest(
				RuntimeContractVersion.Current,
				preparedExecution.PreparedExecutionId,
				prepare.ReservationId!.Value,
				_runtime.State.ExecutionRevision));

			if (commit.Status != RuntimeCommitStatus.Committed)
			{
				Observe($"runtime.commit.rejected:{commit.Failure?.Code}");
				return new RuntimeHostApplyResult(prepare, commit, null);
			}

			_programSinkId = programSinkId;
			_programOutput ??= _virtualMedia.CreateOutput(programSinkId);
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
			Observe($"runtime.commit.committed:{commit.ExecutionRevision}");
			if (transition is not null)
				Observe($"runtime.transition.anchored:{transition.Kind}:{_nextSequenceNumber}:{transition.DurationFrames}");

			return new RuntimeHostApplyResult(prepare, commit, _nextSequenceNumber);
		}
	}

	public V1ProgramBoundaryResult ProcessNextBoundary()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			var execution = _runtime.ActiveExecution ?? throw new InvalidOperationException("RuntimeHost requires a committed execution before processing media.");
			var programSink = _programSinkId ?? throw new InvalidOperationException("RuntimeHost has no committed Program sink.");
			var programBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding => binding.MediaSinkId == programSink)
				?? throw new InvalidOperationException("Committed execution must contain exactly one Program binding.");
			var committedSource = programBinding.MediaSourceId
				?? throw new InvalidOperationException("Committed Program binding must contain a media source.");
			var avSyncEnabled = IsAvSyncDiagnosticsEnabledUnsafe(committedSource);
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

			var sequence = _nextSequenceNumber;
			var frameA = ProcessTimedInput(_virtualMedia.SourceA, _sourceAPipeline, sequence);
			var frameB = ProcessTimedInput(_virtualMedia.SourceB, _sourceBPipeline, sequence);
			var frames = new Dictionary<MediaSourceId, FrameDescriptor>
			{
				[frameA.SourceId] = frameA,
				[frameB.SourceId] = frameB
			};
			WriteAuxFrameUnsafe(execution.PreparedExecution, frames);

			var contentA = ResolveInputContent(frameA);
			var contentB = ResolveInputContent(frameB);
			using var gpuA = RequiresGpuSourceUnsafe(frameA.SourceId, committedSource)
				? MaterializeInput(frameA, contentA)
				: null;
			using var gpuB = RequiresGpuSourceUnsafe(frameB.SourceId, committedSource)
				? MaterializeInput(frameB, contentB)
				: null;
			var gpuFrames = new Dictionary<MediaSourceId, GpuFrame>();
			if (gpuA is not null)
				gpuFrames.Add(frameA.SourceId, gpuA);
			if (gpuB is not null)
				gpuFrames.Add(frameB.SourceId, gpuB);

			var transitionKind = _transition?.Intent.Kind;
			var (fromFrame, toFrame, gpuTransition, blendWeight, transitionComplete) = ResolveTransition(committedSource, sequence, gpuFrames);
			var materializedLayers = MaterializeLayers(fromFrame.Descriptor.Timing);
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
			}
			_lastCompositionDuration = composite.Duration;
			_lastCompositingLayerCount = composite.LayerCount;
			Observe($"gpu.composite.completed:layers={composite.LayerCount}:durationTicks={composite.Duration.Ticks}:surfaces={_gpu.ActiveSurfaceCount}");
			if (!composite.Succeeded)
			{
				Observe($"gpu.composite.failed:{composite.Failure?.Code}");
				throw new InvalidOperationException(composite.Failure?.Message ?? "GPU composite failed.");
			}

			using var output = composite.Frame!;
			var pixels = _gpu.Readback(output);
			var probe = ProbeCenter(pixels, _format);
			_programOutput!.WriteFrame(output.Descriptor);
			if (avSyncEnabled)
			{
				var videoEvent = _motionTimingTestSignal.InspectSyncEvent(output.Descriptor.Timing);
				if (videoEvent.IsFlashFrame)
					_avSyncDiagnostics.RecordVideoSubmit(videoEvent, Stopwatch.GetTimestamp());
			}
			_monitoringTap.TryCapture(
				frameA.SourceId,
				contentA.Pixels,
				frameB.SourceId,
				contentB.Pixels,
				committedSource,
				pixels,
				_format,
				output.Descriptor.Timing);

			RefreshVirtualAudioMetersUnsafe(sequence);
			var audioPacket = _virtualAudio.GetSource(committedSource).GeneratePacket(sequence);
			var audioObservation = _audioMeters[committedSource];
			var audioBuffer = audioPacket.Descriptor;
			var hasGeneratedSignal = _audioTestSignals.TryGetValue(committedSource, out var audioTestSignal);
			AvSyncAudioEventObservation? audioSyncEvent = avSyncEnabled
				? _avSyncTimeline.InspectAudio(audioBuffer.Timing, _format.FrameRate, audioBuffer.Format.SampleRate)
				: null;
			GeneratedAudioTestSignalFrameInfo generatedFrame = default;
			var generatedAudioPayload = hasGeneratedSignal
				? MaterializeGeneratedAudioPayload(audioBuffer, audioTestSignal!, out generatedFrame)
				: null;
			if (hasGeneratedSignal)
				_audioTestSignalFrames[committedSource] = generatedFrame;
			var externalAudioPayload = !hasGeneratedSignal && audioObservation.External
				? ConsumeExternalAudioPayloadUnsafe(committedSource, audioBuffer)
				: null;
			var measuredAudio = generatedAudioPayload is not null
				? new AudioStereoMeter(generatedFrame.LeftPeakLevel, generatedFrame.RightPeakLevel)
				: externalAudioPayload is { Length: > 0 }
					? AudioMetering.MeasureInterleavedStereoFloat32(externalAudioPayload)
					: audioObservation.Meter;
			var afvBuffer = hasGeneratedSignal
				? audioBuffer
				: audioObservation.External && !audioObservation.Available
					? null
					: audioBuffer;
			var audio = _audio.ProcessBoundary(
				committedSource,
				sequence,
				afvBuffer,
				measuredAudio);
			_lastAudioResult = audio;
			var programAudioPayload = generatedAudioPayload is not null
				? ApplyAudioStateToPayloadInPlace(generatedAudioPayload, audio)
				: externalAudioPayload is { Length: > 0 }
					? ApplyAudioStateToPayload(externalAudioPayload, audio)
					: MaterializeReferenceAudioPayload(audioBuffer, audio);
			if (audioSyncEvent is { ContainsPulse: true } pulseEvent)
				_avSyncDiagnostics.RecordAudioSubmit(pulseEvent, Stopwatch.GetTimestamp());

			RecordingEnqueueResult? recording = null;
			if (_recorder.Snapshot.State == RecordingLifecycleState.Recording)
			{
				if (_recordingPayloadWriter is not null)
				{
					try
					{
						_recordingPayloadWriter.StagePayload(
							sequence,
							pixels,
							programAudioPayload);
					}
					catch (Exception exception)
					{
						Observe($"recording.payload.stage.failed:{exception.GetType().Name}");
					}
				}

				recording = _recordingBridge.TryRecordCommittedProgram(execution, output.Descriptor, audioBuffer);
				if (recording is { Accepted: false })
					_recordingPayloadWriter?.DiscardPayload(sequence);
			}

			if (transitionComplete)
			{
				Observe($"runtime.transition.completed:{transitionKind}:{sequence}");
				_transition = null;
			}

			if (_nextSequenceNumber == ulong.MaxValue)
				throw new InvalidOperationException("RuntimeHost frame sequence exhausted.");
			_nextSequenceNumber++;
			Observe($"program.frame:{sequence}:{committedSource}");

			return new V1ProgramBoundaryResult(
				sequence,
				committedSource,
				output.Descriptor,
				pixels,
				probe,
				audio,
				audioBuffer,
				programAudioPayload,
				recording,
				transitionKind,
				blendWeight,
				(_operatorGraphicsVisible || _productionCgText.Visible) ? V1VisualLayerMode.Static : _visualLayerMode,
				_gpu.ActiveSurfaceCount - 1);
		}
	}

	public void SetVisualLayerMode(V1VisualLayerMode mode)
	{
		if (!Enum.IsDefined(typeof(V1VisualLayerMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
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

		lock (_gate)
		{
			ThrowIfDisposed();
			_operatorGraphicsAsset = rgbaPixels.ToArray();
			_operatorGraphicsAssetName = assetName.Trim();
			_operatorGraphicsAssetWidth = width;
			_operatorGraphicsAssetHeight = height;
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

		lock (_gate)
		{
			ThrowIfDisposed();
			if (visible && _operatorGraphicsAsset is null)
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
		lock (_gate)
		{
			ThrowIfDisposed();
			_operatorGraphicsAsset = null;
			_operatorGraphicsAssetName = null;
			_operatorGraphicsAssetWidth = 0;
			_operatorGraphicsAssetHeight = 0;
			_operatorGraphicsVisible = false;
			_operatorGraphicsOpacity = byte.MaxValue;
			_operatorGraphicsLayerScratch.AsSpan().Clear();
			_operatorGraphicsLayerBuffer.CopyPixelsFrom(_operatorGraphicsLayerScratch);
			_operatorGraphicsLayer.Update(_operatorGraphicsLayerBuffer);

			_productionCgAsset = null;
			_productionCgAssetWidth = 0;
			_productionCgAssetHeight = 0;
			_productionCgPositionX = 0;
			_productionCgPositionY = 0;
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

	public IReadOnlyList<V1CompositingLayerSnapshot> ReorderCompositingLayers(IReadOnlyList<string> orderedLayerIds)
	{
		ArgumentNullException.ThrowIfNull(orderedLayerIds);
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
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_audioStreams.TryGetValue(sourceId, out var stream))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");
			_audio.SetInputState(stream.StreamId, gain, muted);
			Observe($"audio.input.state:{sourceId}:{gain.Linear}:{muted}");
			return AudioInputSnapshotUnsafe(sourceId);
		}
	}

	public V1AudioInputSnapshot SetGeneratedAudioTestSignal(
		MediaSourceId sourceId,
		bool enabled,
		GeneratedAudioTestSignalMode mode = GeneratedAudioTestSignalMode.Tone,
		double frequencyHz = GeneratedAudioTestSignalConfiguration.DefaultFrequencyHz,
		double peakLevel = GeneratedAudioTestSignalConfiguration.DefaultPeakLevel)
	{
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
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_backgrounds.TryGetValue(sourceId, out var target))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");
			target.CopyPixelsFrom(rgbaPixels);
			_inputSignals[sourceId] = state;
			Observe($"input.external.updated:{sourceId}:{state}");
		}
	}

	public ValueTask<RecordingStartResult> StartRecordingAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		CancellationToken cancellationToken = default) =>
		StartRecordingCoreAsync(sessionId, outputId, null, null, cancellationToken);

	public async ValueTask<RecordingStartResult> StartRecordingAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		string destinationDirectory,
		string fileName,
		CancellationToken cancellationToken = default)
	{
		if (_recordingTargetWriter is null)
		{
			return RecordingStartResult.Rejected(new Failure(
				"recording.destination.unsupported",
				"Configured RuntimeHost recording writer does not support operator-selected destinations."));
		}

		var normalizedFileName = _recordingTargetWriter.ConfigureTarget(destinationDirectory, fileName);
		return await StartRecordingCoreAsync(
			sessionId,
			outputId,
			Path.GetFullPath(destinationDirectory.Trim()),
			normalizedFileName,
			cancellationToken).ConfigureAwait(false);
	}

	private async ValueTask<RecordingStartResult> StartRecordingCoreAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		string? destinationDirectory,
		string? fileName,
		CancellationToken cancellationToken)
	{
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
				new RecordingOutputDescriptor(outputId, sink, fileName ?? "V1 Program")),
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
		await _recorder.DisposeAsync().ConfigureAwait(false);
		_sourceAPipeline.Dispose();
		_sourceBPipeline.Dispose();
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

	private void WriteAuxFrameUnsafe(
		PreparedExecutionContract preparedExecution,
		IReadOnlyDictionary<MediaSourceId, FrameDescriptor> frames)
	{
		if (_auxSinkId is null || _auxOutput is null)
			return;

		var binding = preparedExecution.Bindings.SingleOrDefault(candidate =>
			string.Equals(candidate.OutputRoleId, "aux", StringComparison.Ordinal) &&
			candidate.MediaSinkId == _auxSinkId);
		if (binding?.MediaSourceId is not { } sourceId)
		{
			_auxFailure = new Failure("runtime.output.aux_binding_missing", "Committed Aux output binding is unavailable.");
			Observe($"runtime.output.aux.failed:{_auxFailure.Value.Code}");
			return;
		}

		if (!frames.TryGetValue(sourceId, out var frame))
		{
			_auxFailure = new Failure("runtime.output.aux_source_unavailable", "Committed Aux output source did not produce a frame at this boundary.");
			Observe($"runtime.output.aux.failed:{_auxFailure.Value.Code}");
			return;
		}

		try
		{
			_auxOutput.WriteFrame(frame);
			_auxFailure = null;
		}
		catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
		{
			_auxFailure = new Failure("runtime.output.aux_write_failed", $"Aux output provider rejected the frame: {exception.Message}");
			Observe($"runtime.output.aux.failed:{_auxFailure.Value.Code}");
		}
	}

	private IReadOnlyList<RuntimeOutputRoleSnapshot> OutputRoleSnapshotsUnsafe()
	{
		var execution = _runtime.ActiveExecution;
		if (execution is null)
			return Array.Empty<RuntimeOutputRoleSnapshot>();

		var snapshots = new List<RuntimeOutputRoleSnapshot>();
		if (_programSinkId is { } programSink)
		{
			var programBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding => binding.MediaSinkId == programSink);
			if (programBinding?.MediaSourceId is { } programSource)
			{
				var programEvidence = _programOutput?.LastFrame;
				var hasEvidence = programEvidence is not null && programEvidence.Frame.SourceId == programSource;
				snapshots.Add(new RuntimeOutputRoleSnapshot(
					"program", "PROGRAM", programSource, programSink, _format, _virtualMedia.Timing.FrameTimebase,
					programBinding.Resource.ProviderId, RuntimeOutputRoleLifecycleState.Active, true,
					hasEvidence ? RuntimeOutputRoleHealthState.Healthy : RuntimeOutputRoleHealthState.Unverified,
					hasEvidence ? $"Program provider confirmed frame sequence {programEvidence!.Frame.Timing.SequenceNumber}." : "Program output is committed; provider evidence for the configured source is pending."));
			}
		}

		if (_auxSinkId is { } auxSink)
		{
			var auxBinding = execution.PreparedExecution.Bindings.SingleOrDefault(binding =>
				string.Equals(binding.OutputRoleId, "aux", StringComparison.Ordinal) && binding.MediaSinkId == auxSink);
			if (auxBinding?.MediaSourceId is { } auxSource)
			{
				var auxEvidence = _auxOutput?.LastFrame;
				var hasEvidence = auxEvidence is not null && auxEvidence.Frame.SourceId == auxSource;
				var fault = _auxFailure;
				snapshots.Add(new RuntimeOutputRoleSnapshot(
					"aux", "AUX", auxSource, auxSink, _format, _virtualMedia.Timing.FrameTimebase,
					auxBinding.Resource.ProviderId,
					fault is null ? RuntimeOutputRoleLifecycleState.Active : RuntimeOutputRoleLifecycleState.Faulted,
					true,
					fault is not null ? RuntimeOutputRoleHealthState.Faulted : hasEvidence ? RuntimeOutputRoleHealthState.Healthy : RuntimeOutputRoleHealthState.Unverified,
					fault is not null ? "Aux provider reported an output failure." : hasEvidence ? $"Aux provider confirmed frame sequence {auxEvidence!.Frame.Timing.SequenceNumber}." : "Aux output is committed; provider evidence for the configured source is pending.",
					fault));
			}
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
					V1VisualLayerMode.Static => _staticLayer.Materialize(_gpu, timing),
					V1VisualLayerMode.Dynamic => _dynamicLayer.Materialize(_gpu, timing),
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
				var frame = _operatorGraphicsLayer.Materialize(_gpu, timing);
				layers.Add(new MaterializedCompositingLayer(
					BitmapGraphicsLayerId,
					_operatorGraphicsLayerOrder,
					frame,
					new GpuKeyLayer(frame, _operatorGraphicsOpacity)));
			}

			if (_productionCgText.Visible && _productionCgAsset is not null)
			{
				var frame = _productionCgLayer.Materialize(_gpu, timing);
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

		return new V1RecordingOperatorSnapshot(
			snapshot.State,
			elapsed,
			_recordingDestination,
			_recordingFileName,
			_recordingTargetWriter?.FinalPath,
			snapshot.Statistics,
			snapshot.Failure);
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
		var activeSource = _audio.ActiveVideoSourceId;
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

	private V1AudioProgramSnapshot AudioProgramSnapshotUnsafe()
	{
		var activeSource = _audio.ActiveVideoSourceId;
		var activeStream = _audio.ActiveStreamId;
		var state = _audio.GetInputState(activeStream);
		if (_lastAudioResult is not { } result)
		{
			return new V1AudioProgramSnapshot(
				activeSource,
				activeStream,
				state.Gain.Linear,
				state.Muted,
				0,
				0,
				0,
				false,
				state.Muted ? V1AudioHealthState.Muted : V1AudioHealthState.Silence);
		}

		var health = result.Status switch
		{
			AudioFollowVideoStatus.Underrun => V1AudioHealthState.Underrun,
			AudioFollowVideoStatus.Emitted when result.Muted => V1AudioHealthState.Muted,
			AudioFollowVideoStatus.Emitted when result.Clipping => V1AudioHealthState.Clipping,
			AudioFollowVideoStatus.Emitted when result.PeakLevel <= 0.000001 => V1AudioHealthState.Silence,
			AudioFollowVideoStatus.Emitted => V1AudioHealthState.Healthy,
			_ => V1AudioHealthState.Error
		};
		return new V1AudioProgramSnapshot(
			result.VideoSourceId,
			result.StreamId ?? activeStream,
			result.Gain.Linear,
			result.Muted,
			result.LeftPeakLevel,
			result.RightPeakLevel,
			result.PeakLevel,
			result.Clipping,
			health);
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
		if (_visualLayerMode != V1VisualLayerMode.Disabled)
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
				_operatorGraphicsAssetName ?? "bitmap"));
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
				1,
				_productionCgText.Text ?? "production-cg-text"));
		}
		return snapshots
			.OrderBy(layer => layer.Order)
			.ThenBy(layer => layer.LayerId, StringComparer.Ordinal)
			.ToArray();
	}

	private void RebuildOperatorGraphicsLayerUnsafe()
	{
		_operatorGraphicsLayerScratch.AsSpan().Clear();
		if (_operatorGraphicsAsset is null)
		{
			_operatorGraphicsLayerBuffer.CopyPixelsFrom(_operatorGraphicsLayerScratch);
			_operatorGraphicsLayer.Update(_operatorGraphicsLayerBuffer);
			return;
		}

		var sourceWidth = checked((int)_operatorGraphicsAssetWidth);
		var sourceHeight = checked((int)_operatorGraphicsAssetHeight);
		var targetWidth = Math.Max(1, checked((int)Math.Round(sourceWidth * _operatorGraphicsScale)));
		var targetHeight = Math.Max(1, checked((int)Math.Round(sourceHeight * _operatorGraphicsScale)));
		var outputWidth = checked((int)_format.Width);
		var outputHeight = checked((int)_format.Height);
		var originX = checked((int)Math.Round(_operatorGraphicsPositionX * Math.Max(0, outputWidth - 1)));
		var originY = checked((int)Math.Round(_operatorGraphicsPositionY * Math.Max(0, outputHeight - 1)));

		for (var y = 0; y < targetHeight; y++)
		{
			var destinationY = originY + y;
			if ((uint)destinationY >= (uint)outputHeight) continue;
			var sourceY = Math.Min(sourceHeight - 1, (int)((long)y * sourceHeight / targetHeight));
			for (var x = 0; x < targetWidth; x++)
			{
				var destinationX = originX + x;
				if ((uint)destinationX >= (uint)outputWidth) continue;
				var sourceX = Math.Min(sourceWidth - 1, (int)((long)x * sourceWidth / targetWidth));
				var sourceOffset = checked((sourceY * sourceWidth + sourceX) * 4);
				var destinationOffset = checked((destinationY * outputWidth + destinationX) * 4);
				_operatorGraphicsLayerScratch[destinationOffset] = _operatorGraphicsAsset[sourceOffset];
				_operatorGraphicsLayerScratch[destinationOffset + 1] = _operatorGraphicsAsset[sourceOffset + 1];
				_operatorGraphicsLayerScratch[destinationOffset + 2] = _operatorGraphicsAsset[sourceOffset + 2];
				_operatorGraphicsLayerScratch[destinationOffset + 3] = _operatorGraphicsAsset[sourceOffset + 3];
			}
		}

		_operatorGraphicsLayerBuffer.CopyPixelsFrom(_operatorGraphicsLayerScratch);
		_operatorGraphicsLayer.Update(_operatorGraphicsLayerBuffer);
	}

	private void RebuildProductionCgLayerUnsafe()
	{
		_productionCgLayerScratch.AsSpan().Clear();
		if (_productionCgAsset is null)
		{
			_productionCgLayerBuffer.CopyPixelsFrom(_productionCgLayerScratch);
			_productionCgLayer.Update(_productionCgLayerBuffer);
			return;
		}

		var sourceWidth = checked((int)_productionCgAssetWidth);
		var sourceHeight = checked((int)_productionCgAssetHeight);
		var outputWidth = checked((int)_format.Width);
		var outputHeight = checked((int)_format.Height);
		var originX = checked((int)Math.Round(_productionCgPositionX * Math.Max(0, outputWidth - 1)));
		var originY = checked((int)Math.Round(_productionCgPositionY * Math.Max(0, outputHeight - 1)));

		for (var y = 0; y < sourceHeight; y++)
		{
			var destinationY = originY + y;
			if ((uint)destinationY >= (uint)outputHeight) continue;
			for (var x = 0; x < sourceWidth; x++)
			{
				var destinationX = originX + x;
				if ((uint)destinationX >= (uint)outputWidth) continue;
				var sourceOffset = checked((y * sourceWidth + x) * 4);
				var destinationOffset = checked((destinationY * outputWidth + destinationX) * 4);
				_productionCgLayerScratch[destinationOffset] = _productionCgAsset[sourceOffset];
				_productionCgLayerScratch[destinationOffset + 1] = _productionCgAsset[sourceOffset + 1];
				_productionCgLayerScratch[destinationOffset + 2] = _productionCgAsset[sourceOffset + 2];
				_productionCgLayerScratch[destinationOffset + 3] = _productionCgAsset[sourceOffset + 3];
			}
		}

		_productionCgLayerBuffer.CopyPixelsFrom(_productionCgLayerScratch);
		_productionCgLayer.Update(_productionCgLayerBuffer);
	}

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

	private static ProgramPixelProbe ProbeCenter(byte[] pixels, VideoFormat format)
	{
		var width = checked((int)format.Width);
		var height = checked((int)format.Height);
		var offset = checked(((height / 2) * width + width / 2) * 4);
		return new ProgramPixelProbe(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]);
	}

	private void Observe(string value) => _observations.Add(value);

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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