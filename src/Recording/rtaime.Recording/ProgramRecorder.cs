using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

public interface IRecordingClock
{
    UtcTimestamp GetUtcNow();
}

public sealed class SystemRecordingClock : IRecordingClock
{
    public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UtcNow);
}

public interface IProgramRecordingWriter
{
    ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken);
    ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken);
    ValueTask FinalizeAsync(CancellationToken cancellationToken);
    ValueTask AbortAsync(CancellationToken cancellationToken);
}

public sealed class RecordingOutputUnavailableException : Exception
{
    public RecordingOutputUnavailableException(string message) : base(message)
    {
    }
}

public sealed class ProgramRecorder : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly Queue<RecordingProgramSample> _queue = new();
    private readonly List<RecordingObservation> _observations = new();
    private readonly IProgramRecordingWriter _writer;
    private readonly IRecordingClock _clock;
    private readonly int _capacity;

    private RecordingLifecycleState _state = RecordingLifecycleState.Idle;
    private RecordingStartRequest? _activeRequest;
    private Task? _worker;
    private bool _stopRequested;
    private ulong? _lastOfferedSequence;
    private ulong _accepted;
    private ulong _written;
    private ulong _dropped;
    private ulong _rejected;
    private ulong _writerFailures;
    private Failure? _failure;
    private bool _disposed;

    public ProgramRecorder(
        IProgramRecordingWriter writer,
        int capacity = 64,
        IRecordingClock? clock = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Recording queue capacity must be greater than zero.");

        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _capacity = capacity;
        _clock = clock ?? new SystemRecordingClock();
    }

    public int Capacity => _capacity;

    public RecordingSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return SnapshotUnsafe();
        }
    }

    public IReadOnlyList<RecordingObservation> Observations
    {
        get
        {
            lock (_gate)
                return _observations.ToArray();
        }
    }

    public async ValueTask<RecordingStartResult> StartAsync(
        RecordingStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_state is RecordingLifecycleState.Recording or RecordingLifecycleState.Finalizing)
                {
                    _rejected++;
                    return RecordingStartResult.Rejected(new Failure(
                        "recording.start.invalid_state",
                        $"Recording cannot start while state is {_state}."));
                }

                if (_worker is { IsCompleted: false })
                {
                    _rejected++;
                    return RecordingStartResult.Rejected(new Failure(
                        "recording.start.worker_active",
                        "Recording worker is still active."));
                }

                ResetSessionUnsafe(request);
            }

            try
            {
                await _writer.OpenAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                try
                {
                    await _writer.AbortAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Start failure remains the primary failure. Abort is best effort.
                }

                var failure = MapWriterFailure("recording.start", exception);
                lock (_gate)
                {
                    _state = RecordingLifecycleState.Failed;
                    _failure = failure;
                    _writerFailures++;
                    ObserveUnsafe("recording.start.failed", failure.Message, null);
                }

                return RecordingStartResult.Failed(failure);
            }

            lock (_gate)
            {
                _state = RecordingLifecycleState.Recording;
                ObserveUnsafe("recording.started", "Program recording started.", null);
                _worker = Task.Run(WorkerAsync);
            }

            return RecordingStartResult.Started();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public RecordingEnqueueResult TryEnqueue(
        FrameDescriptor video,
        AudioBufferDescriptor? audio = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        ThrowIfDisposed();

        bool releaseSignal = false;
        RecordingEnqueueResult result;

        lock (_gate)
        {
            if (_state != RecordingLifecycleState.Recording || _activeRequest is null || _stopRequested)
            {
                _rejected++;
                return RecordingEnqueueResult.Rejected(new Failure(
                    "recording.enqueue.not_recording",
                    "Recording is not accepting Program samples."));
            }

            var sequence = video.Timing.SequenceNumber;
            if (_lastOfferedSequence is { } previous && sequence <= previous)
            {
                _rejected++;
                ObserveUnsafe(
                    "recording.sequence.rejected",
                    $"Program sample sequence {sequence} is not newer than {previous}.",
                    sequence);
                return RecordingEnqueueResult.Rejected(new Failure(
                    "recording.sequence.non_monotonic",
                    "Program recording requires strictly increasing video sequence numbers."));
            }

            _lastOfferedSequence = sequence;

            if (_queue.Count >= _capacity)
            {
                _dropped++;
                ObserveUnsafe(
                    "recording.backpressure.dropped",
                    $"Recording queue capacity {_capacity} was reached; Program sample was dropped.",
                    sequence);
                return RecordingEnqueueResult.Dropped(new Failure(
                    "recording.backpressure.queue_full",
                    "Recording queue is full; sample was dropped without blocking Program."));
            }

            var sample = new RecordingProgramSample(
                RecordingContractVersion.Current,
                _activeRequest.Output.OutputId,
                video,
                audio);
            _queue.Enqueue(sample);
            _accepted++;
            releaseSignal = true;
            result = RecordingEnqueueResult.AcceptedSample();
        }

        if (releaseSignal)
            _queueSignal.Release();

        return result;
    }

    public async ValueTask<RecordingStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RecordingStopResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (result.Status == RecordingStopStatus.Noop)
                ObserveUnsafe("recording.shutdown.noop", "Runtime shutdown found no active recording.", null);
            else
                ObserveUnsafe("recording.shutdown", "Runtime shutdown completed recording shutdown handling.", null);
        }

        return result;
    }

    private async ValueTask<RecordingStopResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task? worker;
            lock (_gate)
            {
                if (_state is RecordingLifecycleState.Idle or RecordingLifecycleState.Completed)
                    return RecordingStopResult.Noop();

                if (_state == RecordingLifecycleState.Failed)
                    return RecordingStopResult.Failed(_failure ?? new Failure(
                        "recording.failed",
                        "Recording is in a failed state."));

                if (_state == RecordingLifecycleState.Recording)
                {
                    _state = RecordingLifecycleState.Finalizing;
                    _stopRequested = true;
                    ObserveUnsafe("recording.finalizing", "Recording stop requested; queued samples will drain before finalization.", null);
                    _queueSignal.Release();
                }

                worker = _worker;
            }

            if (worker is not null)
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (_state == RecordingLifecycleState.Completed)
                    return RecordingStopResult.Stopped();

                return RecordingStopResult.Failed(_failure ?? new Failure(
                    "recording.stop.failed",
                    "Recording did not complete cleanly."));
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task WorkerAsync()
    {
        while (true)
        {
            await _queueSignal.WaitAsync().ConfigureAwait(false);

            RecordingProgramSample? sample = null;
            bool shouldFinalize;
            lock (_gate)
            {
                if (_queue.Count > 0)
                    sample = _queue.Dequeue();

                shouldFinalize = _stopRequested && _queue.Count == 0 && sample is null;
            }

            if (sample is not null)
            {
                try
                {
                    await _writer.WriteAsync(sample, CancellationToken.None).ConfigureAwait(false);
                    lock (_gate)
                        _written++;
                }
                catch (Exception exception)
                {
                    await HandleWriterFailureAsync("recording.write", exception, sample.SequenceNumber).ConfigureAwait(false);
                    return;
                }

                bool wakeForFinalize;
                lock (_gate)
                    wakeForFinalize = _stopRequested && _queue.Count == 0;
                if (wakeForFinalize)
                    _queueSignal.Release();

                continue;
            }

            if (!shouldFinalize)
                continue;

            try
            {
                await _writer.FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _state = RecordingLifecycleState.Completed;
                    ObserveUnsafe("recording.finalized", "Recording finalized in order after the queue drained.", null);
                }
            }
            catch (Exception exception)
            {
                await HandleWriterFailureAsync("recording.finalize", exception, null).ConfigureAwait(false);
            }

            return;
        }
    }

    private async Task HandleWriterFailureAsync(string scope, Exception exception, ulong? sequence)
    {
        var failure = MapWriterFailure(scope, exception);
        lock (_gate)
        {
            _writerFailures++;
            _failure = failure;
            _state = RecordingLifecycleState.Finalizing;
            _stopRequested = true;
            _queue.Clear();
            ObserveUnsafe("recording.writer.failed", failure.Message, sequence);
        }

        var abortFailed = false;
        try
        {
            await _writer.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            abortFailed = true;
        }

        lock (_gate)
        {
            if (abortFailed)
                ObserveUnsafe("recording.abort.failed", "Recording writer abort also failed after the primary writer failure.", sequence);
            _state = RecordingLifecycleState.Failed;
        }
    }

    private void ResetSessionUnsafe(RecordingStartRequest request)
    {
        _queue.Clear();
        while (_queueSignal.Wait(0))
        {
        }

        _activeRequest = request;
        _state = RecordingLifecycleState.Idle;
        _stopRequested = false;
        _lastOfferedSequence = null;
        _failure = null;
        _accepted = 0;
        _written = 0;
        _dropped = 0;
        _rejected = 0;
        _writerFailures = 0;
    }

    private RecordingSnapshot SnapshotUnsafe() => new(
        _state,
        _activeRequest?.SessionId,
        _activeRequest?.Output,
        new RecordingStatistics(_accepted, _written, _dropped, _rejected, _writerFailures),
        _failure);

    private void ObserveUnsafe(string code, string message, ulong? sequence)
    {
        _observations.Add(new RecordingObservation(
            _clock.GetUtcNow(),
            code,
            message,
            _activeRequest?.SessionId,
            _activeRequest?.Output.OutputId,
            sequence));
    }

    private static Failure MapWriterFailure(string scope, Exception exception)
    {
        var code = exception is RecordingOutputUnavailableException
            ? "recording.output.unavailable"
            : $"{scope}.writer_failure";
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "Recording writer failed."
            : exception.Message;
        return new Failure(code, message);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProgramRecorder));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _lifecycleGate.Dispose();
        _queueSignal.Dispose();
    }
}
