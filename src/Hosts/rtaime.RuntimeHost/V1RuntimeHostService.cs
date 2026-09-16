// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

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
    ProgramPixelProbe PixelProbe,
    AudioFollowVideoResult Audio,
    RecordingEnqueueResult? Recording,
    RuntimeProgramTransitionKind? TransitionKind,
    byte BlendWeight,
    V1VisualLayerMode VisualLayerMode,
    int ActiveGpuSurfacesAfterBoundary);

public sealed record V1RuntimeHostSnapshot(
    RuntimeExecutionState Runtime,
    ulong NextSequenceNumber,
    V1TimingHealthState TimingHealth,
    IReadOnlyDictionary<MediaSourceId, V1InputSignalState> InputSignals,
    V1VisualLayerMode VisualLayerMode,
    AudioFollowVideoStatistics Audio,
    RecordingSnapshot Recording,
    int ActiveGpuSurfaces);

/// <summary>
/// Windows V1 reference composition root for committed execution, virtual timed media, GPU composition,
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
    private readonly ProgramRecorder _recorder;
    private readonly RuntimeRecordingBridge _recordingBridge;
    private readonly RuntimeMonitoringHub _monitoringHub;
    private readonly RuntimeMonitoringTap _monitoringTap;
    private readonly List<string> _observations = new();

    private VirtualVideoOutput? _programOutput;
    private MediaSinkId? _programSinkId;
    private AnchoredTransition? _transition;
    private V1VisualLayerMode _visualLayerMode = V1VisualLayerMode.Disabled;
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

        _recorder = new ProgramRecorder(recordingWriter ?? throw new ArgumentNullException(nameof(recordingWriter)));
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
                    V1TimingHealthState.Healthy,
                    new ReadOnlyDictionary<MediaSourceId, V1InputSignalState>(new Dictionary<MediaSourceId, V1InputSignalState>(_inputSignals)),
                    _visualLayerMode,
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
                recording = _recordingBridge.TryRecordCommittedProgram(execution, output.Descriptor, audioBuffer);

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
                probe,
                audio,
                recording,
                transitionKind,
                blendWeight,
                _visualLayerMode,
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
            state == V1InputSignalState.Lost ? "input-fallback" : "virtual-input");
    }

    private GpuFrame? MaterializeLayer(FrameTiming timing) => _visualLayerMode switch
    {
        V1VisualLayerMode.Disabled => null,
        V1VisualLayerMode.Static => _staticLayer.Materialize(_gpu, timing),
        V1VisualLayerMode.Dynamic => _dynamicLayer.Materialize(_gpu, timing),
        _ => throw new InvalidOperationException($"Unsupported visual layer mode '{_visualLayerMode}'.")
    };

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
