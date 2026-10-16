// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.RuntimeHost;

public enum LocalMediaDecodeState
{
    Prerolling, Ready, Seeking, Starved, Ended, Failed, Stopped
}

public readonly record struct LocalMediaDecodeSnapshot(
    LocalMediaDecodeState State, long Generation, int QueuedFrames, int Capacity,
    int PeakQueuedFrames, ulong DiscardedFrames, ulong Starvations, Failure? Failure);

/// <summary>One reader owner, eight fixed slots: six queued, one reading and one admitted.</summary>
internal sealed class LocalMediaDecodeWorker : IDisposable
{
    internal const int QueueCapacity = 6;
    private const int SlotCount = QueueCapacity + 2;
    private const int LowWatermark = 2;
    private readonly object _gate = new();
    private readonly LocalMediaFileSource _source;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Queue<LocalMediaFrameLease> _frames = new(QueueCapacity);
    private readonly Stack<LocalMediaFrameSlot> _free = new(SlotCount);
    private CancellationTokenSource _epoch = new();
    private CancellationTokenSource? _reading;
    private long _generation;
    private long? _seek;
    private bool _ended;
    private bool _stopped;
    private bool _prerolled;
    private int _peak;
    private ulong _discarded;
    private ulong _starvations;
    private LocalMediaDecodeState _state = LocalMediaDecodeState.Prerolling;
    private Failure? _failure;
    private LocalMediaPlaybackMeasurements _measurements;

    internal LocalMediaDecodeWorker(LocalMediaFileSource source)
    {
        _source = source;
        try
        {
            var bytes = checked((int)((ulong)source.Probe.VideoFormat.Width * source.Probe.VideoFormat.Height * 4));
            for (var index = 0; index < SlotCount; index++)
                _free.Push(new LocalMediaFrameSlot(bytes, Release));
            _thread = new Thread(Run) { IsBackground = true, Name = "Local media decode" };
            _thread.Start();
        }
        catch
        {
            _epoch.Dispose();
            _wake.Dispose();
            throw;
        }
    }

    internal LocalMediaPlaybackMeasurements Measurements { get { lock (_gate) return _measurements; } }
    internal LocalMediaDecodeSnapshot Snapshot
    {
        get { lock (_gate) return new(_state, _generation, _frames.Count, QueueCapacity, _peak, _discarded, _starvations, _failure); }
    }
    internal bool IsPrepared { get { lock (_gate) return _prerolled && _failure is null && !_stopped; } }

    internal Failure? Reposition(long frame)
    {
        lock (_gate)
        {
            if (_stopped) return new Failure("runtime.media_decode.stopped", "Decode session is stopped.");
            _generation = checked(_generation + 1);
            _seek = frame;
            _ended = false;
            _prerolled = false;
            _failure = null;
            _state = LocalMediaDecodeState.Seeking;
            ClearUnsafe();
            var previous = _epoch;
            _epoch = new CancellationTokenSource();
            previous.Cancel();
            if (!ReferenceEquals(previous, _reading)) previous.Dispose();
            _wake.Set();
            return null;
        }
    }

    // Never waits for decode. Early PTS stays queued; late frames are bounded by the queue capacity.
    internal LocalMediaFrameLease? TakeDue(long targetTicks, out bool ended, out Failure? failure)
    {
        lock (_gate)
        {
            failure = _failure;
            ended = _ended && _frames.Count == 0 && failure is null;
            if (ended) { _state = LocalMediaDecodeState.Ended; return null; }
            if (!_prerolled || failure is not null) return null;
            LocalMediaFrameLease? selected = null;
            for (var index = 0; index < QueueCapacity && _frames.TryPeek(out var next); index++)
            {
                if (next.PresentationTicks > targetTicks) break;
                var due = _frames.Dequeue();
                if (selected is not null) { selected.Dispose(); _discarded++; }
                selected = due;
            }
            ended = _ended && _frames.Count == 0 && selected is null;
            if (ended) _state = LocalMediaDecodeState.Ended;
            else if (_frames.Count == 0 && selected is null)
            {
                if (_state != LocalMediaDecodeState.Starved) _starvations++;
                _state = LocalMediaDecodeState.Starved;
                _prerolled = false;
            }
            if (_frames.Count <= LowWatermark) _wake.Set();
            return selected;
        }
    }

    internal bool WaitForPreroll(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (!_prerolled && _failure is null && !_stopped)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(remaining));
            }
            return _prerolled && _failure is null && !_stopped;
        }
    }

    private void Run()
    {
        ulong sequence = 0;
        long? lastPresentationTicks = null;
        try
        {
            while (true)
            {
                LocalMediaFrameSlot? slot = null;
                CancellationTokenSource epoch;
                long generation;
                long? seek;
                lock (_gate)
                {
                    if (_stopped) break;
                    epoch = _epoch;
                    generation = _generation;
                    seek = _seek;
                    _seek = null;
                    if (seek.HasValue || (!_ended && _failure is null && _frames.Count < QueueCapacity && _free.Count > 0))
                    {
                        _reading = epoch;
                        if (!seek.HasValue) slot = _free.Pop();
                    }
                }
                if (seek.HasValue)
                {
                    try
                    {
                        epoch.Token.ThrowIfCancellationRequested();
                        var positioned = _source.SeekToFrame(seek.Value);
                        lastPresentationTicks = null;
                        lock (_gate)
                        {
                            if (generation == _generation && !positioned.Succeeded)
                                FailUnsafe(positioned.Failure!.Value);
                        }
                    }
                    catch (OperationCanceledException) when (epoch.IsCancellationRequested) { }
                    finally { FinishRead(epoch); }
                    continue;
                }
                if (slot is null) { _wake.WaitOne(); continue; }
                try
                {
                    var decoded = _source.ReadNext(sequence, epoch.Token);
                    epoch.Token.ThrowIfCancellationRequested();
                    LocalMediaFrameLease? owned = null;
                    if (decoded.Succeeded)
                    {
                        if (_source.Probe.AudioCodec != MediaAudioCodec.None &&
                            (decoded.Frame!.Audio is null || decoded.Frame.AudioPayload.IsEmpty))
                            throw new InvalidDataException("Decoded frame has no required audio window.");
                        slot.Copy(decoded.Frame!);
                        if (lastPresentationTicks is { } previous && slot.PresentationTicks <= previous)
                            throw new InvalidDataException("Decoded video PTS regressed within one playback generation.");
                        lastPresentationTicks = slot.PresentationTicks;
                        owned = slot.Lease(generation);
                        sequence = checked(sequence + 1);
                    }
                    lock (_gate)
                    {
                        _measurements = _source.Measurements;
                        if (generation != _generation || _stopped) { if (owned is not null) _discarded++; owned?.Dispose(); }
                        else if (decoded.Status == LocalMediaFrameReadStatus.Ended) _ended = true;
                        else if (!decoded.Succeeded) FailUnsafe(decoded.Failure ??
                            new Failure("runtime.media_decode.failed", "Decoder returned no frame or failure."));
                        else
                        {
                            _frames.Enqueue(owned!);
                            _peak = Math.Max(_peak, _frames.Count);
                        }
                        if (generation == _generation && (_frames.Count >= 3 || (_ended && _frames.Count > 0)))
                        {
                            _prerolled = true;
                            _state = LocalMediaDecodeState.Ready;
                        }
                        Monitor.PulseAll(_gate);
                    }
                    if (owned is not null) slot = null;
                }
                catch (OperationCanceledException) when (epoch.IsCancellationRequested) { }
                catch (LocalMediaReadCancellationException exception)
                {
                    lock (_gate) FailUnsafe(new Failure("runtime.media_decode.cancellation_failed", exception.Message));
                    return;
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        if (generation == _generation && !_stopped)
                            FailUnsafe(new Failure("runtime.media_decode.failed", $"Decode worker failed: {exception.Message}"));
                    }
                }
                finally
                {
                    if (slot is not null) Release(slot);
                    FinishRead(epoch);
                }
            }
        }
        catch (LocalMediaReadCancellationException exception)
        {
            lock (_gate) FailUnsafe(new Failure("runtime.media_decode.cancellation_failed", exception.Message));
        }
        catch (Exception exception)
        {
            lock (_gate) FailUnsafe(new Failure("runtime.media_decode.failed", $"Decode worker stopped: {exception.Message}"));
        }
        finally
        {
            try { _source.Dispose(); }
            catch (Exception exception)
            {
                lock (_gate) FailUnsafe(new Failure("runtime.media_decode.dispose_failed", exception.Message));
            }
            finally
            {
                lock (_gate)
                {
                    ClearUnsafe();
                    _state = _failure is null ? LocalMediaDecodeState.Stopped : LocalMediaDecodeState.Failed;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    private void FinishRead(CancellationTokenSource epoch)
    {
        lock (_gate)
        {
            _reading = null;
            if (!ReferenceEquals(epoch, _epoch)) epoch.Dispose();
        }
    }

    private void FailUnsafe(Failure failure)
    {
        _failure = failure;
        _state = LocalMediaDecodeState.Failed;
        _prerolled = false;
        ClearUnsafe();
        Monitor.PulseAll(_gate);
    }

    private void ClearUnsafe()
    {
        while (_frames.TryDequeue(out var frame)) { frame.Dispose(); _discarded++; }
    }

    private void Release(LocalMediaFrameSlot slot)
    {
        lock (_gate)
        {
            _free.Push(slot);
            if (!_stopped) _wake.Set();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _epoch.Cancel();
            _reading?.Cancel();
            ClearUnsafe();
            _wake.Set();
            Monitor.PulseAll(_gate);
        }
        // This method is called only by the lifecycle/control path, never by Program admission.
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Local media decoder did not acknowledge cancellation within ten seconds.");
        _epoch.Dispose();
        _wake.Dispose();
        if (Snapshot.Failure is { Code: "runtime.media_decode.cancellation_failed" or "runtime.media_decode.dispose_failed" } cleanupFailure)
            throw new IOException(cleanupFailure.Message);
    }
}

internal sealed class LocalMediaFrameSlot
{
    private readonly byte[] _video;
    private readonly byte[] _audio = new byte[32_768];
    private readonly Action<LocalMediaFrameSlot> _release;
    private int _references;
    internal LocalMediaDecodedFrame Frame { get; private set; } = null!;
    internal long PresentationTicks { get; private set; }

    internal LocalMediaFrameSlot(int videoBytes, Action<LocalMediaFrameSlot> release)
    {
        _video = new byte[videoBytes];
        _release = release;
    }

    internal void Copy(LocalMediaDecodedFrame frame)
    {
        if (frame.RgbaPixels.Length != _video.Length || frame.AudioPayload.Length > _audio.Length)
            throw new InvalidDataException("Decoded payload exceeds its fixed session slot.");
        frame.RgbaPixels.Span.CopyTo(_video);
        frame.AudioPayload.Span.CopyTo(_audio);
        if (frame.Audio is { } audio)
        {
            if (frame.AudioPayload.Length != checked((int)(audio.Timing.SampleCount * audio.Format.ChannelCount * sizeof(float))) ||
                audio.Timing.Timebase != frame.Video.Timing.Timebase ||
                audio.Timing.PresentationTimestamp != frame.Video.Timing.PresentationTimestamp)
                throw new InvalidDataException("Decoded audio extent/timebase does not match its video window.");
        }
        else if (!frame.AudioPayload.IsEmpty)
            throw new InvalidDataException("Decoded audio payload has no timing descriptor.");
        if (frame.Video.Timing.PresentationTimestamp < 0)
            throw new InvalidDataException("Decoded video PTS must be nonnegative.");
        PresentationTicks = checked((long)decimal.Round(
            (decimal)frame.Video.Timing.PresentationTimestamp * frame.Video.Timing.Timebase.Numerator *
            TimeSpan.TicksPerSecond / frame.Video.Timing.Timebase.Denominator, 0, MidpointRounding.AwayFromZero));
        Frame = frame with { RgbaPixels = _video, AudioPayload = _audio.AsMemory(0, frame.AudioPayload.Length) };
    }

    internal LocalMediaFrameLease Lease(long generation)
    {
        _references = 1;
        return new(this, generation);
    }
    internal void Retain() => Interlocked.Increment(ref _references);
    internal void Release() { if (Interlocked.Decrement(ref _references) == 0) _release(this); }
}

internal sealed class LocalMediaFrameLease : IDisposable
{
    private LocalMediaFrameSlot? _slot;
    internal LocalMediaFrameLease(LocalMediaFrameSlot slot, long generation) { _slot = slot; Generation = generation; }
    internal long Generation { get; }
    internal LocalMediaDecodedFrame Frame => _slot!.Frame;
    internal long PresentationTicks => _slot!.PresentationTicks;
    internal LocalMediaFrameLease Retain()
    {
        var slot = _slot ?? throw new ObjectDisposedException(nameof(LocalMediaFrameLease));
        slot.Retain();
        return new(slot, Generation);
    }
    public void Dispose() => Interlocked.Exchange(ref _slot, null)?.Release();
}
