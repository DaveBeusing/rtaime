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
	NotPlaying = 4
}

public sealed record LocalMediaRuntimeBoundaryResult(
	LocalMediaRuntimeBoundaryStatus Status,
	FrameDescriptor? Video,
	ReadOnlyMemory<byte> RgbaPixels,
	AudioBufferDescriptor? Audio,
	ReadOnlyMemory<byte> AudioPayload,
	MediaTransportSnapshot Transport,
	Failure? Failure)
{
	public bool Succeeded => Status == LocalMediaRuntimeBoundaryStatus.Frame && Video is not null;
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
		_transport = new LocalMediaTransportController(
			source.Probe,
			frame => source.SeekToFrame(frame).Failure);
	}

	public LocalMediaProbe Probe => _source.Probe;
	public RuntimeExecutionState RuntimeState => _runtime.State;
	public MediaTransportSnapshot Transport => _transport.Snapshot;
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

		var prepare = _runtime.Prepare(preparedExecution);
		if (prepare.Status != RuntimePrepareStatus.Prepared)
			return new RuntimeHostApplyResult(prepare, null, null);

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

	public void MarkEnded()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_transport.MarkEnded();
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

		return _transport.Apply(command);
	}

	public LocalMediaRuntimeBoundaryResult ProcessNextBoundary()
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

		var decoded = _source.ReadNext(_nextSequenceNumber);
		if (decoded.Status == LocalMediaFrameReadStatus.Ended)
		{
			_transport.MarkEnded();
			return new LocalMediaRuntimeBoundaryResult(
				LocalMediaRuntimeBoundaryStatus.Ended,
				null,
				ReadOnlyMemory<byte>.Empty,
				null,
				ReadOnlyMemory<byte>.Empty,
				_transport.Snapshot,
				null);
		}
		if (!decoded.Succeeded)
		{
			var failure = decoded.Failure ?? new Failure(
				"runtime.local_media.decode_failed",
				"Local media decoding failed without failure details.");
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

		var media = decoded.Frame!;
		var clock = new MediaClockPosition(media.Video.Timing.PresentationTimestamp, media.Video.Timing.Timebase);
		var submitted = _pipeline.Submit(media.Video, clock);
		if (!submitted.Accepted)
		{
			return Failed(
				submitted.Failure?.Code ?? "runtime.local_media.pipeline_rejected",
				submitted.Failure?.Message ?? "Local media frame was rejected by the media pipeline.");
		}

		FrameDescriptor? consumed = null;
		var consumption = _pipeline.ConsumeNext(clock, frame => consumed = frame);
		if (!consumption.Consumed || consumed is null)
		{
			return Failed(
				consumption.Failure?.Code ?? "runtime.local_media.pipeline_empty",
				consumption.Failure?.Message ?? "Local media frame could not be consumed from the media pipeline.");
		}

		_transport.ObserveDecodedTimestamp(
			consumed.Timing.PresentationTimestamp,
			consumed.Timing.Timebase);

		if (_nextSequenceNumber == ulong.MaxValue)
			return Failed("runtime.local_media.sequence_exhausted", "Local media sequence number exhausted.");
		_nextSequenceNumber++;

		return new LocalMediaRuntimeBoundaryResult(
			LocalMediaRuntimeBoundaryStatus.Frame,
			consumed,
			media.RgbaPixels,
			media.Audio,
			media.AudioPayload,
			_transport.Snapshot,
			null);
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_pipeline.Dispose();
		_source.Dispose();
	}

	private LocalMediaRuntimeBoundaryResult Failed(string code, string message)
	{
		var failure = new Failure(code, message);
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
