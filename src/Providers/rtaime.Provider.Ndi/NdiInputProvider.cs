// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Ndi;

public enum NdiInputMediaKind
{
    Video = 1,
    Audio = 2
}

public sealed record NdiInputVideoFrame(
    VideoFormat Format,
    long TimecodeHundredNanoseconds,
    ReadOnlyMemory<byte> RgbaPixels);

public sealed record NdiInputAudioFrame(
    AudioFormat Format,
    long TimecodeHundredNanoseconds,
    uint SampleCount,
    ReadOnlyMemory<byte> InterleavedFloat32);

public sealed record NdiInputMediaSample(
    NdiInputMediaKind Kind,
    NdiInputVideoFrame? Video,
    NdiInputAudioFrame? Audio)
{
    public static NdiInputMediaSample FromVideo(NdiInputVideoFrame video) =>
        new(NdiInputMediaKind.Video, video ?? throw new ArgumentNullException(nameof(video)), null);

    public static NdiInputMediaSample FromAudio(NdiInputAudioFrame audio) =>
        new(NdiInputMediaKind.Audio, null, audio ?? throw new ArgumentNullException(nameof(audio)));
}

public enum NdiCaptureStatus
{
    None = 1,
    Media = 2,
    StatusChanged = 3,
    Error = 4
}

public sealed record NdiCaptureResult(
    NdiCaptureStatus Status,
    NdiInputMediaSample? Sample = null,
    Failure? Failure = null);

public interface INdiReceiveBackend : IAsyncDisposable
{
    int ConnectionCount { get; }
    NdiCaptureResult Capture(TimeSpan timeout);
}

public sealed record NdiInputConfiguration
{
    public NdiInputConfiguration(
        MediaSourceId sourceId,
        DiscoveredMediaSourceId discoveredSourceId,
        string safeSourceIdentity,
        NdiDiscoveredSourceEndpoint endpoint,
        int queueCapacity = 4,
        TimeSpan? sourceLossTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(safeSourceIdentity))
            throw new ArgumentException("Safe NDI source identity is required.", nameof(safeSourceIdentity));
        ArgumentNullException.ThrowIfNull(endpoint);
        if (queueCapacity is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        var lossTimeout = sourceLossTimeout ?? TimeSpan.FromSeconds(2);
        if (lossTimeout < TimeSpan.FromMilliseconds(250) || lossTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(sourceLossTimeout));

        SourceId = sourceId;
        DiscoveredSourceId = discoveredSourceId;
        SafeSourceIdentity = safeSourceIdentity.Trim();
        Endpoint = endpoint;
        QueueCapacity = queueCapacity;
        SourceLossTimeout = lossTimeout;
    }

    public MediaSourceId SourceId { get; }
    public DiscoveredMediaSourceId DiscoveredSourceId { get; }
    public string SafeSourceIdentity { get; }
    internal NdiDiscoveredSourceEndpoint Endpoint { get; }
    public int QueueCapacity { get; }
    public TimeSpan SourceLossTimeout { get; }
}

public sealed class NdiInputSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly NdiInputConfiguration _configuration;
    private readonly Func<INdiReceiveBackend> _backendFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<NdiInputMediaSample> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    private INdiReceiveBackend? _backend;
    private MediaInputLifecycleState _lifecycle = MediaInputLifecycleState.Disabled;
    private bool _connected;
    private ulong _videoReceived;
    private ulong _audioReceived;
    private ulong _dropped;
    private ulong _rejected;
    private ulong _reconnects;
    private int _maximumDepth;
    private DateTimeOffset? _lastMediaAt;
    private Failure? _failure;
    private bool _disposed;

    public NdiInputSession(
        NdiInputConfiguration configuration,
        Func<INdiReceiveBackend> backendFactory,
        Func<DateTimeOffset>? clock = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _worker = Task.Run(WorkerAsync);
    }

    public NdiInputConfiguration Configuration => _configuration;

    public MediaInputHealthSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new MediaInputHealthSnapshot(
                    _configuration.SourceId,
                    NdiNetworkOutputProvider.ProviderIdentity,
                    _configuration.DiscoveredSourceId,
                    _configuration.SafeSourceIdentity,
                    _lifecycle,
                    _connected,
                    _videoReceived > 0 ? LastVideoFormatUnsafe() : null,
                    _audioReceived > 0 ? AudioFormat.Stereo48kFloat32 : null,
                    new MediaInputStatistics(
                        _videoReceived,
                        _audioReceived,
                        _dropped,
                        _rejected,
                        _reconnects,
                        _queue.Count,
                        _maximumDepth),
                    _lastMediaAt is { } last ? new UtcTimestamp(last) : null,
                    _failure);
            }
        }
    }

    private VideoFormat? _lastVideoFormat;

    private VideoFormat? LastVideoFormatUnsafe() => _lastVideoFormat;

    public bool TryDequeue(out NdiInputMediaSample? sample)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                sample = null;
                return false;
            }

            sample = _queue.Dequeue();
            return true;
        }
    }

    private async Task WorkerAsync()
    {
        var cancellationToken = _shutdown.Token;
        try
        {
            lock (_gate)
                _lifecycle = MediaInputLifecycleState.Connecting;

            try
            {
                _backend = _backendFactory();
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException or
                PlatformNotSupportedException)
            {
                Fault(
                    "network.input.ndi_runtime_unavailable",
                    $"NDI runtime is unavailable or incompatible: {exception.GetType().Name}.");
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                NdiCaptureResult capture;
                try
                {
                    capture = _backend.Capture(TimeSpan.FromMilliseconds(100));
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or
                    NotSupportedException or
                    OverflowException)
                {
                    lock (_gate) _rejected++;
                    Fault("network.input.ndi_format_unsupported", exception.Message);
                    return;
                }
                catch (Exception exception)
                {
                    MarkLost(
                        "network.input.ndi_receive_failed",
                        $"NDI receive failed: {exception.GetType().Name}.");
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (capture.Status == NdiCaptureStatus.Media && capture.Sample is not null)
                {
                    Enqueue(capture.Sample);
                    continue;
                }

                if (capture.Status == NdiCaptureStatus.Error)
                {
                    MarkLost(
                        capture.Failure?.Code ?? "network.input.ndi_receive_failed",
                        capture.Failure?.Message ?? "NDI receiver reported an error.");
                    continue;
                }

                if (_backend.ConnectionCount <= 0 &&
                    _lastMediaAt is { } last &&
                    _clock() - last >= _configuration.SourceLossTimeout)
                {
                    MarkLost(
                        "network.input.ndi_source_lost",
                        "The adopted NDI source is currently unavailable; no substitute source was selected.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (_backend is not null)
                await _backend.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _connected = false;
                if (_lifecycle != MediaInputLifecycleState.Faulted)
                    _lifecycle = MediaInputLifecycleState.Disabled;
            }
        }
    }

    private void Enqueue(NdiInputMediaSample sample)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            var recovering = _lifecycle is MediaInputLifecycleState.Lost or MediaInputLifecycleState.Reconnecting;
            if (_queue.Count >= _configuration.QueueCapacity)
            {
                _queue.Dequeue();
                _dropped++;
            }

            _queue.Enqueue(sample);
            _maximumDepth = Math.Max(_maximumDepth, _queue.Count);
            if (sample.Kind == NdiInputMediaKind.Video)
            {
                _videoReceived++;
                _lastVideoFormat = sample.Video!.Format;
            }
            else
            {
                _audioReceived++;
            }

            _lastMediaAt = _clock();
            _connected = true;
            _lifecycle = MediaInputLifecycleState.Connected;
            _failure = null;
            if (recovering)
                _reconnects++;
        }
    }

    private void MarkLost(string code, string message)
    {
        lock (_gate)
        {
            if (_lifecycle is not MediaInputLifecycleState.Lost and not MediaInputLifecycleState.Reconnecting)
                _reconnects++;
            _connected = false;
            _lifecycle = MediaInputLifecycleState.Lost;
            _failure = new Failure(code, message);
        }
    }

    private void Fault(string code, string message)
    {
        lock (_gate)
        {
            _connected = false;
            _lifecycle = MediaInputLifecycleState.Faulted;
            _failure = new Failure(code, message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifecycle = MediaInputLifecycleState.Stopping;
        }

        _shutdown.Cancel();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}

public sealed class NativeNdiReceiveBackend : INdiReceiveBackend
{
    private readonly NdiNativeApi _api;
    private IntPtr _receiver;
    private bool _disposed;

    public NativeNdiReceiveBackend(
        NdiDiscoveredSourceEndpoint endpoint,
        string receiverName,
        string? runtimeLibraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var path = NdiRuntimeDiscovery.ResolveLibraryPath(runtimeLibraryPath)
            ?? throw new DllNotFoundException("NDI runtime is unavailable.");
        _api = NdiNativeApi.Load(path);
        try
        {
            if (!_api.Initialize())
                throw new PlatformNotSupportedException("NDI runtime initialization rejected the current CPU/platform.");
            _receiver = _api.CreateReceiver(endpoint, receiverName);
            if (_receiver == IntPtr.Zero)
                throw new InvalidOperationException("NDI receiver creation failed.");
        }
        catch
        {
            _api.Dispose();
            throw;
        }
    }

    public int ConnectionCount =>
        _disposed || _receiver == IntPtr.Zero ? 0 : _api.GetReceiverConnectionCount(_receiver, 0);

    public NdiCaptureResult Capture(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var milliseconds = checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
        var type = _api.Capture(_receiver, out var video, out var audio, milliseconds);
        switch (type)
        {
            case NdiFrameType.None:
                return new NdiCaptureResult(NdiCaptureStatus.None);
            case NdiFrameType.StatusChange:
                return new NdiCaptureResult(NdiCaptureStatus.StatusChanged);
            case NdiFrameType.Error:
                return new NdiCaptureResult(
                    NdiCaptureStatus.Error,
                    null,
                    new Failure("network.input.ndi_receive_error", "NDI receiver reported a capture error."));
            case NdiFrameType.Video:
                try
                {
                    return new NdiCaptureResult(
                        NdiCaptureStatus.Media,
                        NdiInputMediaSample.FromVideo(NdiReceiveNormalizer.NormalizeVideo(video)));
                }
                finally
                {
                    _api.FreeVideo(_receiver, ref video);
                }
            case NdiFrameType.Audio:
                try
                {
                    return new NdiCaptureResult(
                        NdiCaptureStatus.Media,
                        NdiInputMediaSample.FromAudio(NdiReceiveNormalizer.NormalizeAudio(audio)));
                }
                finally
                {
                    _api.FreeAudio(_receiver, ref audio);
                }
            default:
                return new NdiCaptureResult(NdiCaptureStatus.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        var receiver = Interlocked.Exchange(ref _receiver, IntPtr.Zero);
        if (receiver != IntPtr.Zero)
            _api.DestroyReceiver(receiver);
        _api.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class NdiReceiveNormalizer
{
    public static NdiInputVideoFrame NormalizeVideo(NdiVideoFrameV2 frame)
    {
        if (frame.XRes != 1920 || frame.YRes != 1080)
            throw new NotSupportedException($"NDI input format {frame.XRes}x{frame.YRes} is outside the qualified 1920x1080 baseline.");
        if (frame.FrameFormatType != NdiFrameFormat.Progressive)
            throw new NotSupportedException("NDI input currently requires progressive video.");
        if (frame.Timecode < 0 || frame.Timecode == long.MaxValue)
            throw new NotSupportedException("NDI input requires an explicit non-negative sender timecode.");

        var format = (frame.FrameRateNumerator, frame.FrameRateDenominator) switch
        {
            (50, 1) => VideoFormat.Hd1080p50Rgba8,
            (60_000, 1_001) => VideoFormat.Hd1080p59_94Rgba8,
            _ => throw new NotSupportedException(
                $"NDI input frame rate {frame.FrameRateNumerator}/{frame.FrameRateDenominator} is outside the qualified baseline.")
        };
        if (frame.FourCc != NdiFourCc.Rgba && frame.FourCc != NdiFourCc.Rgbx)
            throw new NotSupportedException("NDI input receiver returned an unsupported video pixel format.");
        if (frame.Data == IntPtr.Zero)
            throw new InvalidDataException("NDI video frame contains no payload.");

        var rowBytes = checked(frame.XRes * 4);
        var stride = frame.LineStrideInBytes == 0 ? rowBytes : frame.LineStrideInBytes;
        if (stride < rowBytes)
            throw new InvalidDataException("NDI video stride is smaller than the required RGBA row length.");

        var payload = new byte[checked(rowBytes * frame.YRes)];
        for (var row = 0; row < frame.YRes; row++)
            Marshal.Copy(IntPtr.Add(frame.Data, checked(row * stride)), payload, row * rowBytes, rowBytes);

        if (frame.FourCc == NdiFourCc.Rgbx)
        {
            for (var offset = 3; offset < payload.Length; offset += 4)
                payload[offset] = byte.MaxValue;
        }

        return new NdiInputVideoFrame(format, frame.Timecode, payload);
    }

    public static NdiInputAudioFrame NormalizeAudio(NdiAudioFrameV3 frame)
    {
        if (frame.SampleRate != 48_000)
            throw new NotSupportedException($"NDI input audio sample rate {frame.SampleRate} Hz is outside the qualified 48 kHz baseline.");
        if (frame.NoChannels != 2)
            throw new NotSupportedException($"NDI input currently requires stereo audio; received {frame.NoChannels} channels.");
        if (frame.NoSamples <= 0)
            throw new InvalidDataException("NDI audio frame contains no samples.");
        if (frame.Timecode < 0 || frame.Timecode == long.MaxValue)
            throw new NotSupportedException("NDI input requires an explicit non-negative audio timecode.");
        if (frame.FourCc != NdiFourCc.Fltp)
            throw new NotSupportedException("NDI input requires planar Float32 audio.");
        if (frame.Data == IntPtr.Zero)
            throw new InvalidDataException("NDI audio frame contains no payload.");

        var sampleCount = frame.NoSamples;
        var channelStride = frame.ChannelStrideInBytes == 0
            ? checked(sampleCount * sizeof(float))
            : frame.ChannelStrideInBytes;
        if (channelStride < checked(sampleCount * sizeof(float)))
            throw new InvalidDataException("NDI audio channel stride is smaller than the channel payload.");

        var left = new float[sampleCount];
        var right = new float[sampleCount];
        Marshal.Copy(frame.Data, left, 0, sampleCount);
        Marshal.Copy(IntPtr.Add(frame.Data, channelStride), right, 0, sampleCount);

        var payload = new byte[checked(sampleCount * 2 * sizeof(float))];
        var destination = MemoryMarshal.Cast<byte, float>(payload.AsSpan());
        for (var sample = 0; sample < sampleCount; sample++)
        {
            destination[sample * 2] = float.IsFinite(left[sample]) ? Math.Clamp(left[sample], -1f, 1f) : 0f;
            destination[(sample * 2) + 1] = float.IsFinite(right[sample]) ? Math.Clamp(right[sample], -1f, 1f) : 0f;
        }

        return new NdiInputAudioFrame(
            AudioFormat.Stereo48kFloat32,
            frame.Timecode,
            checked((uint)sampleCount),
            payload);
    }
}
