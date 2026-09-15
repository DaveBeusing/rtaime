using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Media;

public enum MediaFrameLeaseState
{
    ProducerOwned = 1,
    Queued = 2,
    ConsumerOwned = 3,
    Released = 4,
    Dropped = 5
}

public sealed class MediaFrameLease
{
    private readonly object _gate = new();
    private MediaFrameLeaseState _state = MediaFrameLeaseState.ProducerOwned;

    public MediaFrameLease(FrameDescriptor frame)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));

        if (frame.Surface.Ownership == SurfaceOwnership.ConsumerOwned)
        {
            throw new ArgumentException(
                "A frame entering the media pipeline cannot already be consumer-owned.",
                nameof(frame));
        }
    }

    public FrameDescriptor Frame { get; }

    public MediaFrameLeaseState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    internal void TransferToQueue() => Transition(MediaFrameLeaseState.ProducerOwned, MediaFrameLeaseState.Queued);

    internal void TransferToConsumer() => Transition(MediaFrameLeaseState.Queued, MediaFrameLeaseState.ConsumerOwned);

    public void Release() => Transition(MediaFrameLeaseState.ConsumerOwned, MediaFrameLeaseState.Released);

    public void Drop()
    {
        lock (_gate)
        {
            if (_state is MediaFrameLeaseState.Released or MediaFrameLeaseState.Dropped)
            {
                throw new InvalidOperationException(
                    $"Frame lease cannot be dropped from terminal state '{_state}'.");
            }

            _state = MediaFrameLeaseState.Dropped;
        }
    }

    private void Transition(MediaFrameLeaseState expected, MediaFrameLeaseState next)
    {
        lock (_gate)
        {
            if (_state != expected)
            {
                throw new InvalidOperationException(
                    $"Frame lease transition requires state '{expected}', current state is '{_state}'.");
            }

            _state = next;
        }
    }
}

public enum MediaBackpressurePolicy
{
    Wait = 1,
    RejectIncoming = 2,
    DropOldest = 3
}

public enum MediaQueueState
{
    Accepting = 1,
    Draining = 2,
    Completed = 3
}

public enum MediaEnqueueStatus
{
    Enqueued = 1,
    EnqueuedAfterDroppingOldest = 2,
    DroppedIncoming = 3,
    RejectedCompleted = 4
}

public sealed record MediaEnqueueResult(
    MediaEnqueueStatus Status,
    ulong SequenceNumber,
    ulong? DroppedSequenceNumber)
{
    public bool Enqueued => Status is MediaEnqueueStatus.Enqueued or MediaEnqueueStatus.EnqueuedAfterDroppingOldest;
}

public enum MediaDequeueStatus
{
    Dequeued = 1,
    Completed = 2
}

public sealed record MediaDequeueResult(MediaDequeueStatus Status, MediaFrameLease? Lease)
{
    public bool HasFrame => Status == MediaDequeueStatus.Dequeued && Lease is not null;
}

public readonly record struct MediaQueueStatistics(
    ulong Enqueued,
    ulong Dequeued,
    ulong Dropped,
    ulong Rejected,
    int CurrentDepth,
    int MaximumDepth);

public sealed record MediaQueueObservation(
    ulong Ordinal,
    string Code,
    ulong? SequenceNumber,
    Failure? Failure);

public sealed class BoundedMediaFrameQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<MediaFrameLease> _queue = new();
    private readonly List<MediaQueueObservation> _observations = new();
    private readonly int _capacity;
    private readonly MediaBackpressurePolicy _policy;

    private MediaQueueState _state = MediaQueueState.Accepting;
    private ulong _enqueued;
    private ulong _dequeued;
    private ulong _dropped;
    private ulong _rejected;
    private int _maximumDepth;
    private ulong _observationOrdinal;
    private bool _disposed;

    public BoundedMediaFrameQueue(int capacity, MediaBackpressurePolicy policy)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Media queue capacity must be greater than zero.");
        if (!Enum.IsDefined(typeof(MediaBackpressurePolicy), policy))
            throw new ArgumentOutOfRangeException(nameof(policy));

        _capacity = capacity;
        _policy = policy;
    }

    public int Capacity => _capacity;
    public MediaBackpressurePolicy Policy => _policy;

    public MediaQueueState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public MediaQueueStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new MediaQueueStatistics(
                    _enqueued,
                    _dequeued,
                    _dropped,
                    _rejected,
                    _queue.Count,
                    _maximumDepth);
            }
        }
    }

    public IReadOnlyList<MediaQueueObservation> Observations
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<MediaQueueObservation>(_observations.ToArray());
        }
    }

    public MediaEnqueueResult Enqueue(MediaFrameLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();

            while (_state == MediaQueueState.Accepting && _queue.Count >= _capacity)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_policy == MediaBackpressurePolicy.Wait)
                {
                    Monitor.Wait(_gate, TimeSpan.FromMilliseconds(5));
                    continue;
                }

                if (_policy == MediaBackpressurePolicy.RejectIncoming)
                {
                    lease.Drop();
                    _dropped++;
                    _rejected++;
                    Observe("media.queue.backpressure_reject", lease.Frame.Timing.SequenceNumber, null);
                    return new MediaEnqueueResult(
                        MediaEnqueueStatus.DroppedIncoming,
                        lease.Frame.Timing.SequenceNumber,
                        null);
                }

                var dropped = _queue.Dequeue();
                dropped.Drop();
                _dropped++;
                Observe("media.queue.drop_oldest", dropped.Frame.Timing.SequenceNumber, null);

                lease.TransferToQueue();
                _queue.Enqueue(lease);
                _enqueued++;
                UpdateMaximumDepth();
                Observe("media.queue.enqueued_after_drop", lease.Frame.Timing.SequenceNumber, null);
                Monitor.PulseAll(_gate);

                return new MediaEnqueueResult(
                    MediaEnqueueStatus.EnqueuedAfterDroppingOldest,
                    lease.Frame.Timing.SequenceNumber,
                    dropped.Frame.Timing.SequenceNumber);
            }

            if (_state != MediaQueueState.Accepting)
            {
                lease.Drop();
                _dropped++;
                _rejected++;
                Observe(
                    "media.queue.completed_reject",
                    lease.Frame.Timing.SequenceNumber,
                    new Failure("media.queue.completed", "The media queue no longer accepts frames."));

                return new MediaEnqueueResult(
                    MediaEnqueueStatus.RejectedCompleted,
                    lease.Frame.Timing.SequenceNumber,
                    null);
            }

            lease.TransferToQueue();
            _queue.Enqueue(lease);
            _enqueued++;
            UpdateMaximumDepth();
            Observe("media.queue.enqueued", lease.Frame.Timing.SequenceNumber, null);
            Monitor.PulseAll(_gate);

            return new MediaEnqueueResult(MediaEnqueueStatus.Enqueued, lease.Frame.Timing.SequenceNumber, null);
        }
    }

    public MediaDequeueResult Dequeue(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();

            while (_queue.Count == 0)
            {
                if (_state != MediaQueueState.Accepting)
                {
                    _state = MediaQueueState.Completed;
                    Observe("media.queue.completed", null, null);
                    return new MediaDequeueResult(MediaDequeueStatus.Completed, null);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(5));
            }

            var lease = _queue.Dequeue();
            lease.TransferToConsumer();
            _dequeued++;
            Observe("media.queue.dequeued", lease.Frame.Timing.SequenceNumber, null);

            if (_queue.Count == 0 && _state == MediaQueueState.Draining)
            {
                _state = MediaQueueState.Completed;
                Observe("media.queue.completed", null, null);
            }

            Monitor.PulseAll(_gate);
            return new MediaDequeueResult(MediaDequeueStatus.Dequeued, lease);
        }
    }

    public bool TryDequeue(out MediaFrameLease? lease)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_queue.Count == 0)
            {
                if (_state == MediaQueueState.Draining)
                {
                    _state = MediaQueueState.Completed;
                    Observe("media.queue.completed", null, null);
                }

                lease = null;
                return false;
            }

            lease = _queue.Dequeue();
            lease.TransferToConsumer();
            _dequeued++;
            Observe("media.queue.dequeued", lease.Frame.Timing.SequenceNumber, null);

            if (_queue.Count == 0 && _state == MediaQueueState.Draining)
            {
                _state = MediaQueueState.Completed;
                Observe("media.queue.completed", null, null);
            }

            Monitor.PulseAll(_gate);
            return true;
        }
    }

    public void CompleteAdding()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_state != MediaQueueState.Accepting)
                return;

            _state = _queue.Count == 0 ? MediaQueueState.Completed : MediaQueueState.Draining;
            Observe("media.queue.complete_requested", null, null);
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            while (_queue.Count > 0)
            {
                var lease = _queue.Dequeue();
                lease.Drop();
                _dropped++;
                Observe("media.queue.dispose_drop", lease.Frame.Timing.SequenceNumber, null);
            }

            _state = MediaQueueState.Completed;
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void UpdateMaximumDepth()
    {
        if (_queue.Count > _maximumDepth)
            _maximumDepth = _queue.Count;
    }

    private void Observe(string code, ulong? sequenceNumber, Failure? failure) =>
        _observations.Add(new MediaQueueObservation(_observationOrdinal++, code, sequenceNumber, failure));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public readonly record struct MediaClockPosition(long PresentationTimestamp, Timebase Timebase);

public enum MediaFrameTimeliness
{
    OnTime = 1,
    Late = 2
}

public static class MediaFrameTimingEvaluator
{
    public static MediaFrameTimeliness Evaluate(
        FrameDescriptor frame,
        MediaClockPosition currentPosition,
        long lateToleranceTicks)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (lateToleranceTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(lateToleranceTicks));
        if (frame.Timing.Timebase != currentPosition.Timebase)
            throw new ArgumentException("Frame and media clock positions must use the same timebase.", nameof(currentPosition));

        var deadline = frame.Timing.PresentationTimestamp > long.MaxValue - lateToleranceTicks
            ? long.MaxValue
            : frame.Timing.PresentationTimestamp + lateToleranceTicks;

        return currentPosition.PresentationTimestamp > deadline
            ? MediaFrameTimeliness.Late
            : MediaFrameTimeliness.OnTime;
    }
}

public sealed record MediaPipelineOptions
{
    public MediaPipelineOptions(
        int queueCapacity,
        MediaBackpressurePolicy backpressurePolicy,
        long lateToleranceTicks = 0,
        ulong initialSequenceNumber = 0)
    {
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (!Enum.IsDefined(typeof(MediaBackpressurePolicy), backpressurePolicy))
            throw new ArgumentOutOfRangeException(nameof(backpressurePolicy));
        if (lateToleranceTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(lateToleranceTicks));

        QueueCapacity = queueCapacity;
        BackpressurePolicy = backpressurePolicy;
        LateToleranceTicks = lateToleranceTicks;
        InitialSequenceNumber = initialSequenceNumber;
    }

    public int QueueCapacity { get; }
    public MediaBackpressurePolicy BackpressurePolicy { get; }
    public long LateToleranceTicks { get; }
    public ulong InitialSequenceNumber { get; }
}

public enum MediaFrameSubmitStatus
{
    Enqueued = 1,
    EnqueuedAfterDroppingOldest = 2,
    DroppedLate = 3,
    RejectedBackpressure = 4,
    RejectedCompleted = 5,
    RejectedSequence = 6
}

public sealed record MediaFrameSubmitResult(
    MediaFrameSubmitStatus Status,
    ulong SequenceNumber,
    Failure? Failure)
{
    public bool Accepted => Status is MediaFrameSubmitStatus.Enqueued or MediaFrameSubmitStatus.EnqueuedAfterDroppingOldest;
}

public enum MediaFrameConsumeStatus
{
    Consumed = 1,
    DroppedLate = 2,
    Completed = 3,
    ConsumerFailed = 4
}

public sealed record MediaFrameConsumeResult(
    MediaFrameConsumeStatus Status,
    ulong? SequenceNumber,
    Failure? Failure)
{
    public bool Consumed => Status == MediaFrameConsumeStatus.Consumed;
}

public readonly record struct MediaPipelineStatistics(
    ulong Submitted,
    ulong Consumed,
    ulong Dropped,
    ulong Late,
    ulong Rejected,
    ulong SequenceErrors,
    MediaQueueStatistics Queue);

public sealed record MediaPipelineObservation(
    ulong Ordinal,
    string Code,
    ulong? SequenceNumber,
    Failure? Failure);

public sealed class MediaFramePipeline : IDisposable
{
    private readonly object _gate = new();
    private readonly BoundedMediaFrameQueue _queue;
    private readonly List<MediaPipelineObservation> _observations = new();
    private readonly long _lateToleranceTicks;

    private ulong _nextExpectedSequence;
    private bool _sequenceExhausted;
    private ulong _submitted;
    private ulong _consumed;
    private ulong _dropped;
    private ulong _late;
    private ulong _rejected;
    private ulong _sequenceErrors;
    private ulong _observationOrdinal;
    private bool _disposed;

    public MediaFramePipeline(MediaPipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _queue = new BoundedMediaFrameQueue(options.QueueCapacity, options.BackpressurePolicy);
        _lateToleranceTicks = options.LateToleranceTicks;
        _nextExpectedSequence = options.InitialSequenceNumber;
    }

    public MediaQueueState State => _queue.State;

    public MediaPipelineStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new MediaPipelineStatistics(
                    _submitted,
                    _consumed,
                    _dropped,
                    _late,
                    _rejected,
                    _sequenceErrors,
                    _queue.Statistics);
            }
        }
    }

    public IReadOnlyList<MediaPipelineObservation> Observations
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<MediaPipelineObservation>(_observations.ToArray());
        }
    }

    public MediaFrameSubmitResult Submit(
        FrameDescriptor frame,
        MediaClockPosition currentPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_sequenceExhausted || frame.Timing.SequenceNumber != _nextExpectedSequence)
            {
                _rejected++;
                _sequenceErrors++;
                var failure = new Failure(
                    "media.pipeline.sequence_mismatch",
                    $"Expected frame sequence '{_nextExpectedSequence}', received '{frame.Timing.SequenceNumber}'.");
                Observe("media.pipeline.sequence_rejected", frame.Timing.SequenceNumber, failure);
                return new MediaFrameSubmitResult(
                    MediaFrameSubmitStatus.RejectedSequence,
                    frame.Timing.SequenceNumber,
                    failure);
            }

            AdvanceExpectedSequence();
            _submitted++;
        }

        var lease = new MediaFrameLease(frame);

        if (MediaFrameTimingEvaluator.Evaluate(frame, currentPosition, _lateToleranceTicks) == MediaFrameTimeliness.Late)
        {
            lease.Drop();
            lock (_gate)
            {
                _dropped++;
                _late++;
                Observe("media.pipeline.late_submit_drop", frame.Timing.SequenceNumber, null);
            }

            return new MediaFrameSubmitResult(MediaFrameSubmitStatus.DroppedLate, frame.Timing.SequenceNumber, null);
        }

        var enqueue = _queue.Enqueue(lease, cancellationToken);

        lock (_gate)
        {
            switch (enqueue.Status)
            {
                case MediaEnqueueStatus.Enqueued:
                    Observe("media.pipeline.enqueued", frame.Timing.SequenceNumber, null);
                    return new MediaFrameSubmitResult(MediaFrameSubmitStatus.Enqueued, frame.Timing.SequenceNumber, null);

                case MediaEnqueueStatus.EnqueuedAfterDroppingOldest:
                    _dropped++;
                    Observe("media.pipeline.backpressure_drop_oldest", enqueue.DroppedSequenceNumber, null);
                    return new MediaFrameSubmitResult(
                        MediaFrameSubmitStatus.EnqueuedAfterDroppingOldest,
                        frame.Timing.SequenceNumber,
                        null);

                case MediaEnqueueStatus.DroppedIncoming:
                    _dropped++;
                    _rejected++;
                    var fullFailure = new Failure(
                        "media.pipeline.backpressure_reject",
                        "The bounded media queue rejected the incoming frame because it was full.");
                    Observe("media.pipeline.backpressure_reject", frame.Timing.SequenceNumber, fullFailure);
                    return new MediaFrameSubmitResult(
                        MediaFrameSubmitStatus.RejectedBackpressure,
                        frame.Timing.SequenceNumber,
                        fullFailure);

                default:
                    _dropped++;
                    _rejected++;
                    var completedFailure = new Failure(
                        "media.pipeline.completed",
                        "The media pipeline no longer accepts frames.");
                    Observe("media.pipeline.completed_reject", frame.Timing.SequenceNumber, completedFailure);
                    return new MediaFrameSubmitResult(
                        MediaFrameSubmitStatus.RejectedCompleted,
                        frame.Timing.SequenceNumber,
                        completedFailure);
            }
        }
    }

    public MediaFrameConsumeResult ConsumeNext(
        MediaClockPosition currentPosition,
        Action<FrameDescriptor> consumer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
            ThrowIfDisposed();

        var dequeue = _queue.Dequeue(cancellationToken);
        if (!dequeue.HasFrame)
            return new MediaFrameConsumeResult(MediaFrameConsumeStatus.Completed, null, null);

        var lease = dequeue.Lease!;
        var frame = lease.Frame;

        if (MediaFrameTimingEvaluator.Evaluate(frame, currentPosition, _lateToleranceTicks) == MediaFrameTimeliness.Late)
        {
            lease.Drop();
            lock (_gate)
            {
                _dropped++;
                _late++;
                Observe("media.pipeline.late_consume_drop", frame.Timing.SequenceNumber, null);
            }

            return new MediaFrameConsumeResult(MediaFrameConsumeStatus.DroppedLate, frame.Timing.SequenceNumber, null);
        }

        try
        {
            consumer(frame);
            lease.Release();

            lock (_gate)
            {
                _consumed++;
                Observe("media.pipeline.consumed", frame.Timing.SequenceNumber, null);
            }

            return new MediaFrameConsumeResult(MediaFrameConsumeStatus.Consumed, frame.Timing.SequenceNumber, null);
        }
        catch (Exception exception)
        {
            lease.Drop();
            var failure = new Failure(
                "media.pipeline.consumer_exception",
                $"Media consumer failed unexpectedly: {exception.GetType().Name}.");

            lock (_gate)
            {
                _dropped++;
                _rejected++;
                Observe("media.pipeline.consumer_failed", frame.Timing.SequenceNumber, failure);
            }

            return new MediaFrameConsumeResult(MediaFrameConsumeStatus.ConsumerFailed, frame.Timing.SequenceNumber, failure);
        }
    }

    public void Complete() => _queue.CompleteAdding();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _queue.Dispose();
            _disposed = true;
        }
    }

    private void AdvanceExpectedSequence()
    {
        if (_nextExpectedSequence == ulong.MaxValue)
        {
            _sequenceExhausted = true;
            return;
        }

        _nextExpectedSequence++;
    }

    private void Observe(string code, ulong? sequenceNumber, Failure? failure) =>
        _observations.Add(new MediaPipelineObservation(_observationOrdinal++, code, sequenceNumber, failure));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
