// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

/// <summary>
/// Failure-isolated bounded Program replay encoder. The queue owns only a small, bounded set of
/// retained Program payload leases while encoded history is stored as finalized MP4 segments.
/// </summary>
public sealed class ReplayCaptureEngine : IAsyncDisposable
{
	public const int DefaultQueueCapacity = 8;
	public const int RetainedObservationCapacity = 256;
	public static readonly TimeSpan DefaultLookback = TimeSpan.FromSeconds(10);

	private readonly object _gate = new();
	private readonly Queue<QueuedSample> _queue = [];
	private readonly SemaphoreSlim _signal = new(0);
	private readonly RollingReplaySegmentStore _store;
	private readonly Func<IReplaySegmentWriter> _writerFactory;
	private readonly string _rootDirectory;
	private readonly IRecordingClock _clock;
	private readonly int _queueCapacity;
	private readonly BoundedDiagnosticHistory<ReplayObservation> _observations = new(RetainedObservationCapacity);
	private readonly Task _worker;

	private ReplayCaptureState _state = ReplayCaptureState.Capturing;
	private Failure? _failure;
	private bool _stopping;
	private bool _disposed;
	private ulong? _lastOfferedSequence;
	private ulong _accepted;
	private ulong _dropped;
	private ulong _finalized;
	private ulong _discontinuities;
	private bool _discontinuityPending;
	private TimeSpan? _markIn;
	private TimeSpan? _markOut;

	public ReplayCaptureEngine(
		string rootDirectory,
		Func<IReplaySegmentWriter> writerFactory,
		ReplayBufferPolicy? policy = null,
		int queueCapacity = DefaultQueueCapacity,
		IRecordingClock? clock = null)
	{
		if (string.IsNullOrWhiteSpace(rootDirectory))
			throw new ArgumentException("Replay root directory is required.", nameof(rootDirectory));
		if (queueCapacity <= 0 || queueCapacity > 64)
			throw new ArgumentOutOfRangeException(nameof(queueCapacity), "Replay capture queue must remain bounded to 1..64 samples.");

		_rootDirectory = Path.GetFullPath(rootDirectory);
		_writerFactory = writerFactory ?? throw new ArgumentNullException(nameof(writerFactory));
		_store = new RollingReplaySegmentStore(policy);
		_queueCapacity = queueCapacity;
		_clock = clock ?? new SystemRecordingClock();
		_worker = Task.Run(WorkerAsync);
	}

	public RollingReplaySegmentStore Store => _store;
	public IReadOnlyList<ReplayObservation> Observations => _observations.Snapshot();
	public ulong OverwrittenObservationCount => _observations.OverwrittenCount;

	public ReplayBufferSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return SnapshotUnsafe();
		}
	}

	public ReplayEnqueueResult TryEnqueue(
		MediaSinkId programSinkId,
		FrameDescriptor video,
		AudioBufferDescriptor audio,
		IProgramRecordingPayloadLease videoPayload,
		ReadOnlyMemory<byte> audioPayload)
	{
		ArgumentNullException.ThrowIfNull(video);
		ArgumentNullException.ThrowIfNull(audio);
		ArgumentNullException.ThrowIfNull(videoPayload);

		lock (_gate)
		{
			if (_disposed || _stopping || _state is ReplayCaptureState.Disabled or ReplayCaptureState.Failed)
			{
				videoPayload.Dispose();
				return ReplayEnqueueResult.Rejected(new Failure(
					"replay.capture.not_accepting",
					"Replay capture is not accepting Program samples."));
			}

			var sequence = video.Timing.SequenceNumber;
			if (_lastOfferedSequence is { } previous && sequence <= previous)
			{
				videoPayload.Dispose();
				_discontinuityPending = true;
				_discontinuities++;
				ObserveUnsafe("replay.sequence.rejected", $"Program sequence {sequence} is not newer than {previous}.", sequence);
				return ReplayEnqueueResult.Rejected(new Failure(
					"replay.sequence.non_monotonic",
					"Replay capture requires strictly increasing Program sequence numbers."));
			}
			_lastOfferedSequence = sequence;

			if (_queue.Count >= _queueCapacity)
			{
				videoPayload.Dispose();
				_dropped++;
				_discontinuityPending = true;
				_discontinuities++;
				_state = ReplayCaptureState.Degraded;
				ObserveUnsafe("replay.backpressure.dropped", $"Replay queue capacity {_queueCapacity} was reached; Program sample was dropped.", sequence);
				return ReplayEnqueueResult.Dropped(new Failure(
					"replay.backpressure.queue_full",
					"Replay encoder queue is full; sample was dropped without blocking Program."));
			}

			_queue.Enqueue(new QueuedSample(programSinkId, video, audio, videoPayload, audioPayload));
			_accepted++;
			_signal.Release();
			return ReplayEnqueueResult.AcceptedSample();
		}
	}

	public ReplayRange MarkIn(TimeSpan? lookback = null)
	{
		var retained = _store.Snapshot();
		if (retained.Count == 0)
			throw new InvalidOperationException("Replay history is empty.");

		var requested = lookback ?? DefaultLookback;
		if (requested <= TimeSpan.Zero || requested > _store.Policy.RetentionDuration)
			throw new ArgumentOutOfRangeException(nameof(lookback), "Replay lookback must be positive and within retained duration.");

		lock (_gate)
		{
			var latest = retained[^1].End;
			var earliest = retained[0].Start;
			_markIn = latest - requested < earliest ? earliest : latest - requested;
			_markOut = null;
			return new ReplayRange(_markIn.Value, latest);
		}
	}

	public ReplayRange MarkOut()
	{
		var retained = _store.Snapshot();
		if (retained.Count == 0)
			throw new InvalidOperationException("Replay history is empty.");

		lock (_gate)
		{
			var latest = retained[^1].End;
			if (_markIn is null)
			{
				var earliest = retained[0].Start;
				_markIn = latest - DefaultLookback < earliest ? earliest : latest - DefaultLookback;
			}
			_markOut = latest;
			var selection = new ReplayRange(_markIn.Value, _markOut.Value);
			if (!_store.ContainsRange(selection))
				throw new InvalidOperationException("Replay selection crosses expired or discontinuous history.");
			return selection;
		}
	}

	public ReplayRange SelectRange(ReplayRange range)
	{
		ArgumentNullException.ThrowIfNull(range);
		if (!_store.ContainsRange(range))
			throw new InvalidOperationException("Replay range is not continuously retained.");
		lock (_gate)
		{
			_markIn = range.In;
			_markOut = range.Out;
			return range;
		}
	}

	public void ClearSelection()
	{
		lock (_gate)
		{
			_markIn = null;
			_markOut = null;
		}
	}

	public async ValueTask StopAsync(CancellationToken cancellationToken = default)
	{
		Task worker;
		lock (_gate)
		{
			if (_stopping || _state == ReplayCaptureState.Disabled)
				return;
			_stopping = true;
			_state = ReplayCaptureState.Stopping;
			worker = _worker;
			_signal.Release();
		}

		await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;
		try
		{
			await StopAsync(CancellationToken.None).ConfigureAwait(false);
		}
		finally
		{
			lock (_gate)
			{
				_disposed = true;
				while (_queue.Count > 0)
					_queue.Dequeue().VideoPayload.Dispose();
			}
			_signal.Dispose();
			_store.Dispose();
		}
	}

	private async Task WorkerAsync()
	{
		SegmentSession? segment = null;
		while (true)
		{
			await _signal.WaitAsync().ConfigureAwait(false);
			QueuedSample? sample = null;
			bool stop;
			lock (_gate)
			{
				if (_queue.Count > 0)
					sample = _queue.Dequeue();
				stop = _stopping && _queue.Count == 0 && sample is null;
			}

			if (sample is not null)
			{
				try
				{
					segment ??= await StartSegmentAsync(sample).ConfigureAwait(false);
					var recordingSample = new RecordingProgramSample(
						RecordingContractVersion.Current,
						segment.OutputId,
						sample.Video,
						sample.Audio);
					segment.Writer.StagePayload(sample.Video.Timing.SequenceNumber, sample.VideoPayload, sample.AudioPayload);
					sample = sample with { VideoPayload = NullPayloadLease.Instance };
					await segment.Writer.WriteAsync(recordingSample, CancellationToken.None).ConfigureAwait(false);
					segment.Observe(sample.Video);
					if (segment.Duration >= _store.Policy.SegmentDuration)
					{
						await FinalizeSegmentAsync(segment).ConfigureAwait(false);
						segment = null;
					}
					lock (_gate)
					{
						if (_state == ReplayCaptureState.Degraded && !_discontinuityPending)
							_state = ReplayCaptureState.Capturing;
					}
				}
				catch (Exception exception)
				{
					sample.VideoPayload.Dispose();
					if (segment is not null)
					{
						await AbortSegmentAsync(segment).ConfigureAwait(false);
						segment = null;
					}
					lock (_gate)
					{
						_dropped++;
						_discontinuityPending = true;
						_discontinuities++;
						_state = ReplayCaptureState.Degraded;
						_failure = new Failure("replay.capture.encoder_failure", $"Replay encoder/storage failure: {exception.Message}");
						ObserveUnsafe("replay.capture.failure", _failure.Value.Message, sample.Video.Timing.SequenceNumber);
					}
				}
				continue;
			}

			if (!stop)
				continue;

			if (segment is not null)
			{
				try
				{
					await FinalizeSegmentAsync(segment).ConfigureAwait(false);
				}
				catch (Exception exception)
				{
					await AbortSegmentAsync(segment).ConfigureAwait(false);
					lock (_gate)
					{
						_failure = new Failure("replay.capture.finalize_failure", $"Replay segment finalization failed: {exception.Message}");
						_state = ReplayCaptureState.Degraded;
						ObserveUnsafe("replay.segment.finalize_failed", _failure.Value.Message, null);
					}
				}
			}

			lock (_gate)
				_state = ReplayCaptureState.Disabled;
			return;
		}
	}

	private async ValueTask<SegmentSession> StartSegmentAsync(QueuedSample first)
	{
		Directory.CreateDirectory(_rootDirectory);
		var id = ReplaySegmentId.New();
		var outputId = new RecordingOutputId(id.Value);
		var writer = _writerFactory();
		var fileName = $"replay-{id}.mp4";
		writer.ConfigureTarget(_rootDirectory, fileName);
		var request = new RecordingStartRequest(
			RecordingContractVersion.Current,
			RecordingSessionId.New(),
			new RecordingOutputDescriptor(outputId, first.ProgramSinkId, $"Replay {id}"));
		await writer.OpenAsync(request, CancellationToken.None).ConfigureAwait(false);

		bool discontinuity;
		lock (_gate)
		{
			discontinuity = _discontinuityPending;
			_discontinuityPending = false;
		}
		return new SegmentSession(id, outputId, writer, discontinuity);
	}

	private async ValueTask FinalizeSegmentAsync(SegmentSession segment)
	{
		if (!segment.HasSamples)
		{
			await segment.Writer.AbortAsync(CancellationToken.None).ConfigureAwait(false);
			return;
		}

		await segment.Writer.FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
		var finalPath = segment.Writer.FinalPath
			?? throw new InvalidDataException("Replay segment writer finalized without publishing a path.");
		var info = new FileInfo(finalPath);
		if (!info.Exists || info.Length <= 0)
			throw new InvalidDataException("Replay segment finalization did not produce a valid encoded artifact.");

		var descriptor = new ReplaySegmentDescriptor(
			segment.Id,
			finalPath,
			segment.FirstSequence,
			segment.LastSequence,
			segment.Start,
			segment.End,
			info.Length,
			hasAudio: true,
			segment.DiscontinuityBefore);
		_store.AddFinalizedSegment(descriptor);

		lock (_gate)
		{
			_finalized++;
			_failure = null;
			ObserveUnsafe("replay.segment.finalized", $"Replay segment {segment.Id} finalized ({info.Length} bytes).", segment.LastSequence, segment.Id);
		}
	}

	private static async ValueTask AbortSegmentAsync(SegmentSession segment)
	{
		try { await segment.Writer.AbortAsync(CancellationToken.None).ConfigureAwait(false); }
		catch { }
	}

	private ReplayBufferSnapshot SnapshotUnsafe()
	{
		var segments = _store.Snapshot();
		ReplayRange? selection = null;
		if (_markIn is { } @in && _markOut is { } @out && @out > @in)
			selection = new ReplayRange(@in, @out);
		return new ReplayBufferSnapshot(
			ReplayContractVersion.Current,
			_state,
			selection is null ? ReplayClipState.Idle : ReplayClipState.Marked,
			_store.Policy,
			segments,
			selection,
			new ReplayStatistics(
				_accepted,
				_dropped,
				_finalized,
				_store.EvictedSegments,
				_discontinuities,
				_store.RetainedBytes),
			_failure);
	}

	private void ObserveUnsafe(string code, string message, ulong? sequence, ReplaySegmentId? segmentId = null) =>
		_observations.Add(new ReplayObservation(_clock.GetUtcNow(), code, message, sequence, segmentId));

	private sealed record QueuedSample(
		MediaSinkId ProgramSinkId,
		FrameDescriptor Video,
		AudioBufferDescriptor Audio,
		IProgramRecordingPayloadLease VideoPayload,
		ReadOnlyMemory<byte> AudioPayload);

	private sealed class SegmentSession
	{
		public SegmentSession(ReplaySegmentId id, RecordingOutputId outputId, IReplaySegmentWriter writer, bool discontinuityBefore)
		{
			Id = id;
			OutputId = outputId;
			Writer = writer;
			DiscontinuityBefore = discontinuityBefore;
		}

		public ReplaySegmentId Id { get; }
		public RecordingOutputId OutputId { get; }
		public IReplaySegmentWriter Writer { get; }
		public bool DiscontinuityBefore { get; }
		public bool HasSamples { get; private set; }
		public ulong FirstSequence { get; private set; }
		public ulong LastSequence { get; private set; }
		public TimeSpan Start { get; private set; }
		public TimeSpan End { get; private set; }
		public TimeSpan Duration => HasSamples ? End - Start : TimeSpan.Zero;

		public void Observe(FrameDescriptor frame)
		{
			var start = TimeSpan.FromTicks(WindowsMediaFoundationMp4RecordingWriter.ToHundredNanoseconds(
				frame.Timing.PresentationTimestamp,
				frame.Timing.Timebase));
			var duration = TimeSpan.FromTicks(FrameDurationTicks(frame.Surface.Format));
			if (!HasSamples)
			{
				HasSamples = true;
				FirstSequence = frame.Timing.SequenceNumber;
				Start = start;
			}
			LastSequence = frame.Timing.SequenceNumber;
			End = start + duration;
		}

		private static long FrameDurationTicks(VideoFormat format)
		{
			var numerator = checked((Int128)10_000_000 * format.FrameRate.Denominator);
			var denominator = format.FrameRate.Numerator;
			return checked((long)((numerator + (denominator / 2)) / denominator));
		}
	}

	private sealed class NullPayloadLease : IProgramRecordingPayloadLease
	{
		public static NullPayloadLease Instance { get; } = new();
		public ReadOnlyMemory<byte> Memory => ReadOnlyMemory<byte>.Empty;
		public void Dispose() { }
	}
}
