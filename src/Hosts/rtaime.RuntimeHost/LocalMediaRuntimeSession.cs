// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public enum LocalMediaRuntimeBoundaryStatus
{
	Frame = 1,
	Ended = 2,
	Failed = 3,
	NotPlaying = 4,
    Buffering = 5,
    Repeated = 6
}

public sealed record LocalMediaRuntimeBoundaryResult(
	LocalMediaRuntimeBoundaryStatus Status,
	FrameDescriptor? Video,
	ReadOnlyMemory<byte> RgbaPixels,
	AudioBufferDescriptor? Audio,
	ReadOnlyMemory<byte> AudioPayload,
	MediaTransportSnapshot Transport,
	Failure? Failure) : IDisposable
{
    internal LocalMediaFrameLease? Ownership { get; init; }
    public bool Succeeded => Status is LocalMediaRuntimeBoundaryStatus.Frame or LocalMediaRuntimeBoundaryStatus.Repeated && Video is not null;
    internal LocalMediaRuntimeBoundaryResult Retain() => this with { Ownership = Ownership?.Retain() };
    public void Dispose() => Ownership?.Dispose();
}

/// <summary>
/// RuntimeHost composition for one committed local-media source. Production authority remains in Control;
/// this class only accepts a PreparedExecutionContract, commits it through TransactionalRuntime and then
/// applies observable media transport commands before pushing decoded frames through MediaFramePipeline.
/// </summary>
public sealed class LocalMediaRuntimeSession : IDisposable
{
	private readonly LocalMediaFileProvider _provider;
	private readonly LocalMediaFileSource _source;
    private readonly LocalMediaDecodeWorker _decode;
    private LocalMediaRuntimeBoundaryResult? _current;
    private long _originFrame;
    private long _cadenceOrdinal;
    private ulong? _lastProgramSlot;
    private bool _wasBuffering;
	private readonly TransactionalRuntime _runtime;
	private readonly MediaFramePipeline _pipeline;
	private readonly LocalMediaTransportController _transport;
	private ulong _nextSequenceNumber;
	private bool _disposed;

	public LocalMediaRuntimeSession(LocalMediaFileProvider provider, LocalMediaFileSource source)
	{
		_provider = provider ?? throw new ArgumentNullException(nameof(provider));
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());
		_pipeline = new MediaFramePipeline(new MediaPipelineOptions(3, MediaBackpressurePolicy.RejectIncoming));
		try { _decode = new LocalMediaDecodeWorker(source); }
        catch
        {
            // Ownership transfers only after the worker and fixed payload slots exist.
            _pipeline.Dispose();
            source.Dispose();
            throw;
        }
        _transport = new LocalMediaTransportController(source.Probe, frame =>
        {
            _current?.Dispose();
            _current = null;
            _originFrame = frame;
            _cadenceOrdinal = 0;
            _lastProgramSlot = null;
            _wasBuffering = false;
            return _decode.Reposition(frame);
        });
	}

	public LocalMediaProbe Probe => _source.Probe;
    public LocalMediaPlaybackMeasurements Measurements => _decode.Measurements;
    public LocalMediaDecodeSnapshot DecodeSnapshot => _decode.Snapshot;
    public bool IsBuffering => !_decode.IsPrepared || _decode.Snapshot.State == LocalMediaDecodeState.Starved;
    public bool WaitForPreroll(TimeSpan timeout) => _decode.WaitForPreroll(timeout);
	public RuntimeExecutionState RuntimeState => _runtime.State;
	public MediaTransportSnapshot Transport
    {
        get
        {
            if (_decode.Snapshot.Failure is { } failure)
            {
                _current?.Dispose();
                _current = null;
                _transport.MarkError(failure);
            }
            return _transport.Snapshot;
        }
    }
	public ulong NextSequenceNumber => _nextSequenceNumber;

	public RuntimeHostApplyResult ApplyExecution(PreparedExecutionContract preparedExecution)
	{
		ArgumentNullException.ThrowIfNull(preparedExecution);
		ObjectDisposedException.ThrowIf(_disposed, this);

		var localBindings = preparedExecution.Bindings
			.Where(binding =>
				binding.MediaSourceId == _source.SourceId &&
				binding.Resource.ProviderId == _provider.Descriptor.ProviderId &&
				string.Equals(binding.Resource.Kind, LocalMediaCapabilityKinds.MediaRoute, StringComparison.Ordinal))
			.ToArray();
		if (localBindings.Length == 0)
		{
			var rejection = new RuntimePrepareResult(
				RuntimeContractVersion.Current,
				preparedExecution.PreparedExecutionId,
				RuntimePrepareStatus.Rejected,
				null,
				new Failure(
					"runtime.local_media.binding_missing",
					"Prepared execution does not bind this local media source to the local media provider."));
			return new RuntimeHostApplyResult(rejection, null, null);
		}

        if (!_decode.WaitForPreroll(TimeSpan.FromSeconds(10)))
        {
            var rejection = new RuntimePrepareResult(RuntimeContractVersion.Current,
                preparedExecution.PreparedExecutionId, RuntimePrepareStatus.Rejected, null,
                _decode.Snapshot.Failure ?? new Failure("runtime.local_media.preroll_timeout",
                    "Local media did not produce a qualified preroll within ten seconds."));
            return new RuntimeHostApplyResult(rejection, null, null);
        }

		var prepare = _runtime.Prepare(preparedExecution);
		if (prepare.Status != RuntimePrepareStatus.Prepared)
			return new RuntimeHostApplyResult(prepare, null, null);

        if (_runtime.ActiveExecution is not null)
        {
            var frame = _transport.Snapshot.Position.CurrentFrame;
            _current?.Dispose();
            _current = null;
            _originFrame = frame;
            _cadenceOrdinal = 0;
            _lastProgramSlot = null;
            _wasBuffering = false;
            _decode.Reposition(frame);
            if (!_decode.WaitForPreroll(TimeSpan.FromSeconds(10)))
            {
                _runtime.Abort(preparedExecution.PreparedExecutionId, prepare.ReservationId!.Value);
                var rejection = new RuntimePrepareResult(RuntimeContractVersion.Current,
                    preparedExecution.PreparedExecutionId, RuntimePrepareStatus.Rejected, null,
                    _decode.Snapshot.Failure ?? new Failure("runtime.local_media.preroll_timeout",
                        "Rebound local media did not produce qualified preroll."));
                return new RuntimeHostApplyResult(rejection, null, null);
            }
        }

		var commit = _runtime.Commit(new RuntimeCommitRequest(
			RuntimeContractVersion.Current,
			preparedExecution.PreparedExecutionId,
			prepare.ReservationId!.Value,
			_runtime.State.ExecutionRevision));
		return new RuntimeHostApplyResult(
			prepare,
			commit,
			commit.Status == RuntimeCommitStatus.Committed ? _nextSequenceNumber : null);
	}

	public void MarkEnded(long? holdFrame = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_transport.MarkEnded(holdFrame);
	}

	public MediaTransportCommandResult ApplyTransport(MediaTransportCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_runtime.ActiveExecution is null)
		{
			return MediaTransportCommandResult.Rejected(
				_transport.Snapshot,
				"runtime.local_media.execution_missing",
				"Media transport requires a committed execution.");
		}

		var result = _transport.Apply(command);
        if (result.Succeeded && command.Kind is MediaTransportCommandKind.Play or MediaTransportCommandKind.Pause)
            _lastProgramSlot = null;
        return result;
	}

	public LocalMediaRuntimeBoundaryResult ProcessNextBoundary(ulong? programSlot = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var execution = _runtime.ActiveExecution;
		if (execution is null)
		{
			return Failed(
				"runtime.local_media.execution_missing",
				"Local media processing requires a committed execution.");
		}

		if (!execution.PreparedExecution.Bindings.Any(binding =>
				binding.MediaSourceId == _source.SourceId &&
				binding.Resource.ProviderId == _provider.Descriptor.ProviderId))
		{
			return Failed(
				"runtime.local_media.binding_lost",
				"Committed execution no longer contains the local media source binding.");
		}

		if (_transport.Snapshot.State != MediaTransportState.Playing)
		{
			return new LocalMediaRuntimeBoundaryResult(
				LocalMediaRuntimeBoundaryStatus.NotPlaying,
				null,
				ReadOnlyMemory<byte>.Empty,
				null,
				ReadOnlyMemory<byte>.Empty,
				_transport.Snapshot,
				null);
		}

        if (programSlot is { } slot)
        {
            if (_lastProgramSlot is { } previous && slot > previous && !_wasBuffering)
                _cadenceOrdinal = checked(_cadenceOrdinal + (long)(slot - previous - 1));
            _lastProgramSlot = slot;
        }
        var rate = Probe.VideoFormat.FrameRate;
        var target = checked((long)decimal.Round(
            (decimal)checked(_originFrame + _cadenceOrdinal) * TimeSpan.TicksPerSecond *
            rate.Denominator / rate.Numerator, 0, MidpointRounding.AwayFromZero));
        var owned = _decode.TakeDue(target, out var ended, out var decodeFailure);
        if (decodeFailure is { } failure) return Failed(failure.Code, failure.Message);
        if (ended)
        {
            _current?.Dispose();
            _current = null;
            _transport.MarkEnded();
            return Empty(LocalMediaRuntimeBoundaryStatus.Ended);
        }
        if (owned is null)
        {
            // An explicitly queued future PTS permits a hold without replaying audio.
            // An empty/not-prerolled queue is an underrun: freeze media time, let Program advance.
            if (_decode.IsPrepared && _decode.Snapshot.QueuedFrames > 0)
            {
                _cadenceOrdinal = checked(_cadenceOrdinal + 1);
                if (_current is not { } previous) return Empty(LocalMediaRuntimeBoundaryStatus.Buffering);
                _current = previous with {
                    Status = LocalMediaRuntimeBoundaryStatus.Repeated, Audio = null,
                    AudioPayload = ReadOnlyMemory<byte>.Empty, Ownership = previous.Ownership?.Retain()
                };
                previous.Dispose();
                return _current;
            }
            _current?.Dispose();
            _current = null;
             _wasBuffering = true;
            return Empty(LocalMediaRuntimeBoundaryStatus.Buffering);
        }
        _wasBuffering = false;
        var media = owned.Frame;
        _current?.Dispose();
        var timing = new FrameTiming(_nextSequenceNumber, media.Video.Timing.PresentationTimestamp, media.Video.Timing.Timebase);
        var admitted = new FrameDescriptor(media.Video.Version, media.Video.SourceId, media.Video.Surface, timing);

		var clock = new MediaClockPosition(media.Video.Timing.PresentationTimestamp, media.Video.Timing.Timebase);
		var submitted = _pipeline.Submit(admitted, clock);
		if (!submitted.Accepted)
		{
			owned.Dispose();
            return Failed(
				submitted.Failure?.Code ?? "runtime.local_media.pipeline_rejected",
				submitted.Failure?.Message ?? "Local media frame was rejected by the media pipeline.");
		}

		FrameDescriptor? consumed = null;
		var consumption = _pipeline.ConsumeNext(clock, frame => consumed = frame);
		if (!consumption.Consumed || consumed is null)
		{
			owned.Dispose();
            return Failed(
				consumption.Failure?.Code ?? "runtime.local_media.pipeline_empty",
				consumption.Failure?.Message ?? "Local media frame could not be consumed from the media pipeline.");
		}

		_transport.ObserveDecodedTimestamp(
			consumed.Timing.PresentationTimestamp,
			consumed.Timing.Timebase);

		if (_nextSequenceNumber == ulong.MaxValue)
        {
            owned.Dispose();
            return Failed("runtime.local_media.sequence_exhausted", "Local media sequence number exhausted.");
        }
		_nextSequenceNumber++;
        _cadenceOrdinal = checked(_cadenceOrdinal + 1);

		_current = new LocalMediaRuntimeBoundaryResult(
			LocalMediaRuntimeBoundaryStatus.Frame,
			consumed,
			media.RgbaPixels,
			media.Audio,
			media.AudioPayload,
			_transport.Snapshot,
			null) { Ownership = owned };
        return _current;
	}

    private LocalMediaRuntimeBoundaryResult Empty(LocalMediaRuntimeBoundaryStatus status) =>
        new(status, null, ReadOnlyMemory<byte>.Empty, null, ReadOnlyMemory<byte>.Empty, _transport.Snapshot, null);

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_current?.Dispose();
        _current = null;
        _decode.Dispose();
        _pipeline.Dispose();
	}

	private LocalMediaRuntimeBoundaryResult Failed(string code, string message)
	{
		var failure = new Failure(code, message);
		_current?.Dispose();
        _current = null;
        _transport.MarkError(failure);
		return new LocalMediaRuntimeBoundaryResult(
			LocalMediaRuntimeBoundaryStatus.Failed,
			null,
			ReadOnlyMemory<byte>.Empty,
			null,
			ReadOnlyMemory<byte>.Empty,
			_transport.Snapshot,
			failure);
	}
}
