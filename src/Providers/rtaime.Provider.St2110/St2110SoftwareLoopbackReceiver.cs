// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.St2110;

/// <summary>
/// Opt-in software input only, bound to 127.0.0.1, with finite video/audio
/// delivery queues. It is not an NMOS/ControlHost-admitted live source.
/// </summary>
public sealed class St2110SoftwareLoopbackReceiver : IAsyncDisposable
{
    private readonly UdpClient _videoSocket;
    private readonly UdpClient _audioSocket;
    private readonly St2110VideoFrameReceiver _video;
    private readonly St2110PcmRtpReceiver _audio;
    private readonly Channel<St2110PackedVideoFrame> _videoFrames =
        Channel.CreateBounded<St2110PackedVideoFrame>(
            new BoundedChannelOptions(2) { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<St2110PcmPacket> _audioPackets =
        Channel.CreateBounded<St2110PcmPacket>(
            new BoundedChannelOptions(64) { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _videoWorker;
    private readonly Task _audioWorker;
    private readonly long _monotonicStart = Stopwatch.GetTimestamp();
    private Task? _disposeTask;
    private readonly object _disposeGate = new();
    private long _videoQueueDrops;
    private long _audioQueueDrops;

    public St2110SoftwareLoopbackReceiver(St2110SoftwareProfile profile,
        St2110SoftwareEndpoint endpoint)
    {
        St2110TransportAdmission.RequireSoftwareReference(profile, endpoint, null);
        _video = new St2110VideoFrameReceiver(profile, endpoint.VideoSsrc);
        _audio = new St2110PcmRtpReceiver(endpoint.AudioSsrc);
        _videoSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, endpoint.VideoPort));
        try
        {
            _audioSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, endpoint.AudioPort));
        }
        catch
        {
            _videoSocket.Dispose();
            throw;
        }
        _videoWorker = Task.Run(VideoWorkerAsync);
        _audioWorker = Task.Run(AudioWorkerAsync);
    }

    public ChannelReader<St2110PackedVideoFrame> Frames => _videoFrames.Reader;
    public ChannelReader<St2110PcmPacket> AudioPackets => _audioPackets.Reader;

    public St2110SoftwareReceiveHealth Health => new(
        _video.SequenceHealth.Received, _video.SequenceHealth.Missing,
        _video.SequenceHealth.Reordered, _video.SequenceHealth.JitterTicks,
        _video.IncompleteFrames, _video.RejectedPackets, _video.LatePackets,
        _audio.SequenceHealth.Received, _audio.SequenceHealth.Missing,
        _audio.SequenceHealth.Reordered, _audio.SequenceHealth.JitterTicks,
        _audio.RejectedPackets, _audio.LatePackets,
        Interlocked.Read(ref _videoQueueDrops), Interlocked.Read(ref _audioQueueDrops));

    private async Task VideoWorkerAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var datagram = await _videoSocket.ReceiveAsync(_shutdown.Token).ConfigureAwait(false);
                if (!IPAddress.IsLoopback(datagram.RemoteEndPoint.Address))
                    continue;
                var arrival = Stopwatch.GetElapsedTime(_monotonicStart);
                if (_video.TryReceive(datagram.Buffer, arrival, out var frame) && frame is not null &&
                    !_videoFrames.Writer.TryWrite(frame))
                    Interlocked.Increment(ref _videoQueueDrops);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _videoFrames.Writer.TryComplete(exception);
            return;
        }
        _videoFrames.Writer.TryComplete();
    }

    private async Task AudioWorkerAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var datagram = await _audioSocket.ReceiveAsync(_shutdown.Token).ConfigureAwait(false);
                if (!IPAddress.IsLoopback(datagram.RemoteEndPoint.Address))
                    continue;
                var arrival = Stopwatch.GetElapsedTime(_monotonicStart);
                if (_audio.TryReceive(datagram.Buffer, arrival, out var pcm) && pcm is not null &&
                    !_audioPackets.Writer.TryWrite(pcm))
                    Interlocked.Increment(ref _audioQueueDrops);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _audioPackets.Writer.TryComplete(exception);
            return;
        }
        _audioPackets.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= ShutdownAsync());
    }

    private async Task ShutdownAsync()
    {
        _shutdown.Cancel();
        _videoSocket.Dispose();
        _audioSocket.Dispose();
        try { await Task.WhenAll(_videoWorker, _audioWorker).ConfigureAwait(false); }
        finally { _shutdown.Dispose(); }
    }
}

public sealed record St2110SoftwareReceiveHealth(
    ulong VideoPackets, ulong VideoMissing, ulong VideoReordered, double VideoJitterTicks,
    ulong IncompleteFrames, ulong VideoRejected, ulong VideoLate,
    ulong AudioPackets, ulong AudioMissing, ulong AudioReordered, double AudioJitterTicks,
    ulong AudioRejected, ulong AudioLate, long DroppedCompletedFrames, long DroppedPcmPackets);

public sealed class St2110SoftwareInputProvider
{
    // The reference provider deliberately does not claim a routable media-source
    // capability; ControlHost source admission requires independently qualified
    // capture/clock/format evidence that is not yet available.
    public St2110SoftwareLoopbackReceiver CreateReceiver(St2110SoftwareProfile profile,
        St2110SoftwareEndpoint endpoint) => new(profile, endpoint);
}
