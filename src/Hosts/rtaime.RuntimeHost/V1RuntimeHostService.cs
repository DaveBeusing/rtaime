// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Collections.ObjectModel;
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

public enum V1TimingHealthState
{
	Healthy = 1,
	Degraded = 2,
	Unstable = 3,
	Lost = 4,
	Recovering = 5
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

public sealed record V1RuntimeHostSnapshot(
	RuntimeExecutionState Runtime,
	ulong NextSequenceNumber,
	V1TimingHealthState TimingHealth,
	IReadOnlyDictionary<MediaSourceId, V1InputSignalState> InputSignals,
	V1VisualLayerMode VisualLayerMode,
	V1GraphicsOverlaySnapshot GraphicsOverlay,
	AudioFollowVideoStatistics Audio,
	RecordingSnapshot Recording,
	int ActiveGpuSurfaces);

/// <summary>
/// Windows V1 reference composition root for committed execution, timed media, GPU composition,
/// Program output, Audio Follow Video and failure-isolated recording. It owns execution, never production authority.
/// </summary>
public sealed class V1RuntimeHostService : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly VideoFormat _format;
	private readonly VirtualMediaReferenceProvider _virtualMedia;
	private readonly MediaFramePipeline _sourceAPipeline;
	private readonly MediaFramePipeline _sourceBPipeline;
	private readonly ManagedReferenceGpuBackend _gpuBackend;
	private readonly GpuProcessingProvider _gpu;
	private readonly TransactionalRuntime _runtime;
	private readonly AudioFollowVideoEngine _audio;
	private readonly Dictionary<MediaSourceId, AudioStreamDescriptor> _audioStreams;
	private readonly Dictionary<MediaSourceId, RgbaFrameBuffer> _backgrounds;
	private readonly RgbaFrameBuffer _blackBackground;
	private readonly Dictionary<MediaSourceId, V1InputSignalState> _inputSignals;
	private readonly StaticRgbaSource _staticLayer;
	private readonly DynamicRgbaSource _dynamicLayer;
	private readonly DynamicRgbaSource _operatorGraphicsLayer;
	private readonly ProgramRecorder _recorder;
	private readonly RuntimeRecordingBridge _recordingBridge;
	private readonly IProgramRecordingPayloadWriter? _recordingPayloadWriter;
	private readonly RuntimeMonitoringHub _monitoringHub;
	private readonly RuntimeMonitoringTap _monitoringTap;
	private readonly List<string> _observations = new();

	private VirtualVideoOutput? _programOutput;
	private MediaSinkId? _programSinkId;
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
	private V1TimingHealthState _timingHealth = V1TimingHealthState.Recovering;
	private ulong _nextSequenceNumber;
	private bool _disposed;

	public V1RuntimeHostService(
		MediaSourceId sourceAId,
		MediaSourceId sourceBId,
		VideoFormat format,
		IProgramRecordingWriter recordingWriter)
	{
		_format = format;
		_virtualMedia = new VirtualMediaReferenceProvider(sourceAId, sourceBId, format);
		_sourceAPipeline = CreatePipeline();
		_sourceBPipeline = CreatePipeline();
		_gpuBackend = new ManagedReferenceGpuBackend();
		_gpu = new GpuProcessingProvider(_gpuBackend);
		_gpu.Start();
		_runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());

		var timingDomain = HostIdentity.Create("v1-audio-timing", format.FrameRate.ToString());
		var streamA = new AudioStreamDescriptor(
			MediaContractVersion.Current,
			new AudioStreamId(HostIdentity.Create("v1-audio-stream", sourceAId.ToString())),
			sourceAId,
			AudioFormat.Stereo48kFloat32,
			timingDomain);
		var streamB = new AudioStreamDescriptor(
			MediaContractVersion.Current,
			new AudioStreamId(HostIdentity.Create("v1-audio-stream", sourceBId.ToString())),
			sourceBId,
			AudioFormat.Stereo48kFloat32,
			timingDomain);
		_audioStreams = new[] { streamA, streamB }.ToDictionary(stream => stream.FollowedVideoSourceId);
		_audio = new AudioFollowVideoEngine(new[] { streamA, streamB }, format.FrameRate, sourceAId);

		_backgrounds = new Dictionary<MediaSourceId, RgbaFrameBuffer>
		{
			[sourceAId] = RgbaFrameBuffer.Solid(format, 32, 72, 196),
			[sourceBId] = RgbaFrameBuffer.Solid(format, 196, 72, 32)
		};
		_blackBackground = RgbaFrameBuffer.Solid(format, 0, 0, 0);
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
		_operatorGraphicsLayer = new DynamicRgbaSource(
			new MediaSourceId(HostIdentity.Create("v1-layer-source", "operator-graphics")),
			RgbaFrameBuffer.Solid(format, 0, 0, 0, 0));

		if (recordingWriter is null)
			throw new ArgumentNullException(nameof(recordingWriter));
		_recordingPayloadWriter = recordingWriter as IProgramRecordingPayloadWriter;
		_recorder = new ProgramRecorder(recordingWriter);
		_recordingBridge = new RuntimeRecordingBridge(_recorder);
		_monitoringHub = new RuntimeMonitoringHub();
		_monitoringTap = new RuntimeMonitoringTap(_monitoringHub);
	}

	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors =>
		Array.AsReadOnly(new[] { _virtualMedia.Descriptor, _gpu.Descriptor });

	public IReadOnlyList<VirtualOutputFrame> ProgramFrames =>
		_programOutput?.Frames ?? Array.Empty<VirtualOutputFrame>();

	public RuntimeMonitoringHub MonitoringHub => _monitoringHub;
	public RuntimeMonitoringTapStatistics MonitoringStatistics => _monitoringTap.Statistics;
	public VideoFormat Format => _format;

	public bool HasCommittedExecution
	{
		get
		{
			lock (_gate)
				return _runtime.ActiveExecution is not null;
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
			lock (_gate)
			{
				return new V1RuntimeHostSnapshot(
					_runtime.State,
					_nextSequenceNumber,
					_timingHealth,
					new ReadOnlyDictionary<MediaSourceId, V1InputSignalState>(new Dictionary<MediaSourceId, V1InputSignalState>(_inputSignals)),
					_operatorGraphicsVisible ? V1VisualLayerMode.Static : _visualLayerMode,
					GraphicsOverlaySnapshotUnsafe(),
					_audio.Statistics,
					_recorder.Snapshot,
					_gpu.ActiveSurfaceCount);
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

			var sequence = _nextSequenceNumber;
			var frameA = ProcessTimedInput(_virtualMedia.SourceA, _sourceAPipeline, sequence);
			var frameB = ProcessTimedInput(_virtualMedia.SourceB, _sourceBPipeline, sequence);
			var frames = new Dictionary<MediaSourceId, FrameDescriptor>
			{
				[frameA.SourceId] = frameA,
				[frameB.SourceId] = frameB
			};

			var contentA = ResolveInputContent(frameA);
			var contentB = ResolveInputContent(frameB);
			using var gpuA = MaterializeInput(frameA, contentA);
			using var gpuB = MaterializeInput(frameB, contentB);
			var gpuFrames = new Dictionary<MediaSourceId, GpuFrame>
			{
				[frameA.SourceId] = gpuA,
				[frameB.SourceId] = gpuB
			};

			var transitionKind = _transition?.Intent.Kind;
			var (fromFrame, toFrame, gpuTransition, blendWeight, transitionComplete) = ResolveTransition(committedSource, sequence, gpuFrames);
			using var layerFrame = MaterializeLayer(fromFrame.Descriptor.Timing);
			var layer = layerFrame is null ? null : new GpuKeyLayer(layerFrame);

			var composite = _gpu.Composite(new GpuCompositeRequest(
				committedSource,
				fromFrame,
				toFrame,
				gpuTransition,
				layer));
			if (!composite.Succeeded)
			{
				Observe($"gpu.composite.failed:{composite.Failure?.Code}");
				throw new InvalidOperationException(composite.Failure?.Message ?? "GPU composite failed.");
			}

			using var output = composite.Frame!;
			var pixels = _gpu.Readback(output);
			var probe = ProbeCenter(pixels, _format);
			_programOutput!.WriteFrame(output.Descriptor);
			_monitoringTap.TryCapture(
				frameA.SourceId,
				contentA.Pixels,
				frameB.SourceId,
				contentB.Pixels,
				committedSource,
				pixels,
				_format,
				output.Descriptor.Timing);

			var audioBuffer = CreateAudioBuffer(committedSource, sequence);
			var audio = _audio.ProcessBoundary(
				committedSource,
				sequence,
				audioBuffer,
				committedSource == _virtualMedia.SourceA.SourceId ? 0.42 : 0.64);

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
							MaterializeReferenceAudioPayload(audioBuffer, audio));
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
				recording,
				transitionKind,
				blendWeight,
				_operatorGraphicsVisible ? V1VisualLayerMode.Static : _visualLayerMode,
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

			_operatorGraphicsVisible = visible;
			_operatorGraphicsPositionX = positionX;
			_operatorGraphicsPositionY = positionY;
			_operatorGraphicsScale = scale;
			if (_operatorGraphicsAsset is not null)
				RebuildOperatorGraphicsLayerUnsafe();
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
			_operatorGraphicsLayer.Update(RgbaFrameBuffer.Solid(_format, 0, 0, 0, 0));
			Observe("graphics.overlay.cleared");
			return GraphicsOverlaySnapshotUnsafe();
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

	public void SetAudioInputState(MediaSourceId sourceId, AudioGain gain, bool muted)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_audioStreams.TryGetValue(sourceId, out var stream))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");
			_audio.SetInputState(stream.StreamId, gain, muted);
			Observe($"audio.input.state:{sourceId}:{gain.Linear}:{muted}");
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

	/// <summary>
	/// Replaces the current V1 working-frame content for one logical input without changing runtime authority or
	/// timing. Physical Media I/O uses this seam after copying an adapter lease into the bounded runtime frame.
	/// </summary>
	public void SetExternalInputContent(MediaSourceId sourceId, RgbaFrameBuffer content, V1InputSignalState state = V1InputSignalState.Valid)
	{
		ArgumentNullException.ThrowIfNull(content);
		if (content.Format != _format)
			throw new ArgumentException("External input content must match the RuntimeHost video format.", nameof(content));
		if (!Enum.IsDefined(typeof(V1InputSignalState), state))
			throw new ArgumentOutOfRangeException(nameof(state));
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_backgrounds.ContainsKey(sourceId))
				throw new KeyNotFoundException($"Unknown media source '{sourceId}'.");
			_backgrounds[sourceId] = content;
			_inputSignals[sourceId] = state;
			Observe($"input.external.updated:{sourceId}:{state}");
		}
	}

	public async ValueTask<RecordingStartResult> StartRecordingAsync(
		RecordingSessionId sessionId,
		RecordingOutputId outputId,
		CancellationToken cancellationToken = default)
	{
		MediaSinkId sink;
		lock (_gate)
		{
			ThrowIfDisposed();
			sink = _programSinkId ?? throw new InvalidOperationException("Program sink must be committed before recording starts.");
		}

		var result = await _recorder.StartAsync(
			new RecordingStartRequest(
				RecordingContractVersion.Current,
				sessionId,
				new RecordingOutputDescriptor(outputId, sink, "V1 Program")),
			cancellationToken).ConfigureAwait(false);
		lock (_gate)
			Observe($"recording.start:{result.Status}");
		return result;
	}

	public async ValueTask<RecordingStopResult> StopRecordingAsync(CancellationToken cancellationToken = default)
	{
		var result = await _recorder.StopAsync(cancellationToken).ConfigureAwait(false);
		lock (_gate)
			Observe($"recording.stop:{result.Status}");
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

	private RgbaFrameBuffer ResolveInputContent(FrameDescriptor frame)
	{
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
			state == V1InputSignalState.Lost ? "input-fallback" : "runtime-input");
	}

	private GpuFrame? MaterializeLayer(FrameTiming timing)
	{
		if (_operatorGraphicsVisible && _operatorGraphicsAsset is not null)
			return _operatorGraphicsLayer.Materialize(_gpu, timing);

		return _visualLayerMode switch
		{
			V1VisualLayerMode.Disabled => null,
			V1VisualLayerMode.Static => _staticLayer.Materialize(_gpu, timing),
			V1VisualLayerMode.Dynamic => _dynamicLayer.Materialize(_gpu, timing),
			_ => throw new InvalidOperationException($"Unsupported visual layer mode '{_visualLayerMode}'.")
		};
	}

	private V1GraphicsOverlaySnapshot GraphicsOverlaySnapshotUnsafe() => new(
		_operatorGraphicsAsset is not null,
		_operatorGraphicsAssetName,
		_operatorGraphicsAssetWidth,
		_operatorGraphicsAssetHeight,
		_operatorGraphicsVisible,
		_operatorGraphicsPositionX,
		_operatorGraphicsPositionY,
		_operatorGraphicsScale);

	private void RebuildOperatorGraphicsLayerUnsafe()
	{
		var output = new byte[RgbaFrameBuffer.RequiredByteLength(_format)];
		if (_operatorGraphicsAsset is null)
		{
			_operatorGraphicsLayer.Update(new RgbaFrameBuffer(_format, output));
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
				output[destinationOffset] = _operatorGraphicsAsset[sourceOffset];
				output[destinationOffset + 1] = _operatorGraphicsAsset[sourceOffset + 1];
				output[destinationOffset + 2] = _operatorGraphicsAsset[sourceOffset + 2];
				output[destinationOffset + 3] = _operatorGraphicsAsset[sourceOffset + 3];
			}
		}

		_operatorGraphicsLayer.Update(new RgbaFrameBuffer(_format, output));
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