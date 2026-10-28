// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Net;
using System.Net.Sockets;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.St2110;

/// <summary>
/// Software-only loopback transport; transmission is intentionally distinct
/// from physical ST 2110-21 shaping, receiver delivery and hardware qualification.
/// </summary>
public interface ISt2110DatagramSender : IAsyncDisposable
{
    ValueTask<int> SendVideoAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);
    ValueTask<int> SendAudioAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken);
}

public sealed class St2110LoopbackUdpSender : ISt2110DatagramSender
{
    private readonly UdpClient _video;
    private readonly UdpClient _audio;

    public St2110LoopbackUdpSender(St2110SoftwareEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IPAddress.IsLoopback(endpoint.Address))
            throw new NotSupportedException("ST 2110 software output cannot bind to a physical NIC.");
        _video = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        try
        {
            _audio = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            try
            {
                _video.Connect(endpoint.Address, endpoint.VideoPort);
                _audio.Connect(endpoint.Address, endpoint.AudioPort);
            }
            catch
            {
                _audio.Dispose();
                throw;
            }
        }
        catch
        {
            _video.Dispose();
            throw;
        }
    }

    public ValueTask<int> SendVideoAsync(ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken) => _video.SendAsync(datagram, cancellationToken);
    public ValueTask<int> SendAudioAsync(ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken) => _audio.SendAsync(datagram, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _video.Dispose();
        _audio.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Runtime-owned target projection only; the provider never
/// changes ControlHost routing, production clocks or prepared execution.</summary>
public sealed class St2110NetworkOutputProvider
{
    private static readonly ProviderId Identity = new(
        new Identity(new Guid("8c4a0935-bbf8-46da-88ab-12836bcc2f6e")));
    private static readonly CapabilityId Capability = new(
        new Identity(new Guid("e1b26cdf-59c1-4d83-a856-039370cf18d2")));
    private static readonly ProviderResourceId Resource = new(
        new Identity(new Guid("aa3593ef-8c77-42e2-b917-dfbc2c813965")));

    public static IReadOnlyList<VideoFormat> SupportedFormats { get; } =
        Array.AsReadOnly(new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 });

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderContractVersion.Current, Identity, "ST 2110 Software Loopback Reference",
        new ProviderAvailability(ProviderAvailabilityState.Available),
        new[] { new ProviderCapabilityDescriptor(
            Capability, NetworkOutputCapabilityKinds.Output, SupportedFormats) },
        new[] { new ProviderResourceDescriptor(
            Resource, Identity, NetworkOutputCapabilityKinds.Output, 2, true) });

    public St2110NetworkOutputSession CreateSession(NetworkOutputConfiguration configuration,
        Func<St2110SoftwareEndpoint, ISt2110DatagramSender>? senderFactory = null) =>
        new(configuration, senderFactory ?? (endpoint => new St2110LoopbackUdpSender(endpoint)));
}

public sealed class St2110NetworkOutputSession : INetworkOutputSession
{
    private readonly NetworkOutputConfiguration _configuration;
    private readonly St2110SoftwareProfile _profile;
    private readonly Func<St2110SoftwareEndpoint, ISt2110DatagramSender> _senderFactory;
    private readonly object _gate = new();
    private readonly Queue<NetworkOutputProgramSample> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private Task? _disposeTask;
    private NetworkOutputLifecycleState _lifecycle = NetworkOutputLifecycleState.Disabled;
    private Failure? _failure;
    private DateTimeOffset? _lastSent;
    private bool _disposed;
    private bool _connected;
    private ulong _accepted;
    private ulong _dropped;
    private ulong _rejected;
    private ulong _sent;
    private ulong _packets;
    private ulong _bytes;
    private int _maximumQueue;
    private uint _videoSequence;
    private readonly St2110PcmRtpPacketizer _pcm;
    private ulong? _lastQueuedFrame;

    public St2110NetworkOutputSession(NetworkOutputConfiguration configuration,
        Func<St2110SoftwareEndpoint, ISt2110DatagramSender> senderFactory)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
        if (configuration.Protocol != NetworkOutputProtocolFamily.St2110 ||
            configuration.St2110Settings is not { } settings)
            throw new ArgumentException("ST 2110 requires typed loopback settings.", nameof(configuration));
        if (!St2110NetworkOutputProvider.SupportedFormats.Contains(configuration.VideoFormat))
            throw new NotSupportedException("ST 2110 software output requires an exact 1080p50/59.94 format.");
        _profile = St2110SoftwareProfile.FromRuntime(configuration.VideoFormat);
        St2110TransportAdmission.RequireSoftwareReference(_profile, settings.Endpoint, null);
        _pcm = new St2110PcmRtpPacketizer(settings.Endpoint.AudioSsrc);
        _worker = Task.Run(WorkerAsync);
    }

    public NetworkOutputConfiguration Configuration => _configuration;

    public NetworkOutputHealthSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new NetworkOutputHealthSnapshot(
                    _configuration.TargetId, "st2110-software-loopback",
                    NetworkOutputProtocolFamily.St2110, _configuration.SafeTargetIdentity,
                    _lifecycle, _connected, _configuration.VideoFormat,
                    _configuration.AudioFormat,
                    NetworkOutputVideoCodec.Rfc4175Ycbcr422TenBit,
                    NetworkOutputAudioCodec.Pcm24, 0, 0, 0,
                    new NetworkOutputStatistics(_accepted, _sent, _dropped,
                        _rejected, 0, _packets, _bytes, _queue.Count,
                        _maximumQueue, _configuration.QueueCapacity),
                    _lastSent, _failure);
            }
        }
    }

    public NetworkOutputEnqueueResult TrySubmit(NetworkOutputProgramSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            if (_disposed || _lifecycle == NetworkOutputLifecycleState.Faulted)
                return Reject(sample, "network.output.st2110_unavailable",
                    "ST 2110 software transport has stopped or faulted.");
            if (!sample.HasCompatibleRuntimeTiming(_configuration.VideoFormat, _configuration.AudioFormat) ||
                !HasExactWindow(sample))
                return Reject(sample, "network.output.st2110_clock_mismatch",
                    "ST 2110 requires the exact Runtime frame and PCM sample clock.");

            if (_lastQueuedFrame is { } previous && sample.SequenceNumber != previous + 1)
                return Reject(sample, "network.output.st2110_discontinuity",
                    "A dropped or discontinuous RTP source requires explicit session re-admission.");
            _lastQueuedFrame = sample.SequenceNumber;

            Failure? dropped = null;
            if (_queue.Count >= _configuration.QueueCapacity)
            {
                _queue.Dequeue().Video.Dispose();
                _dropped++;
                dropped = new Failure("network.output.backpressure.drop_oldest",
                    "ST 2110 software sender queue dropped an older complete A/V sample.");
            }
            _queue.Enqueue(sample);
            _accepted++;
            _maximumQueue = Math.Max(_maximumQueue, _queue.Count);
            if (dropped is null) _signal.Release();
            return dropped is null ? NetworkOutputEnqueueResult.AcceptedSample()
                : NetworkOutputEnqueueResult.AcceptedAfterDroppingOldest(dropped.Value);
        }
    }

    private bool HasExactWindow(NetworkOutputProgramSample sample)
    {
        var first = (Int128)sample.SequenceNumber * _profile.FrameRate.Denominator *
            St2110SoftwareProfile.AudioSampleRate / _profile.FrameRate.Numerator;
        var next = ((Int128)sample.SequenceNumber + 1) * _profile.FrameRate.Denominator *
            St2110SoftwareProfile.AudioSampleRate / _profile.FrameRate.Numerator;
        return sample.AudioTiming.SamplePosition == first &&
            sample.AudioTiming.SampleCount == next - first &&
            sample.Audio.Length == checked((int)((next - first) * 8));
    }

    private NetworkOutputEnqueueResult Reject(NetworkOutputProgramSample sample,
        string code, string reason)
    {
        _rejected++;
        sample.Video.Dispose();
        return NetworkOutputEnqueueResult.Rejected(new Failure(code, reason));
    }

    private async Task WorkerAsync()
    {
        ISt2110DatagramSender? sender = null;
        var cancellationToken = _stop.Token;
        try
        {
            // Connection is intentionally bound to loopback only, never arbitrary hosts.
            sender = _senderFactory(_configuration.St2110Settings!.Endpoint);
            lock (_gate)
            {
                _lifecycle = NetworkOutputLifecycleState.Connected;
                _connected = true;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                NetworkOutputProgramSample? sample;
                lock (_gate) sample = _queue.Count > 0 ? _queue.Dequeue() : null;
                if (sample is null) continue;
                try
                {
                    var packed = St2110RgbaReferenceConversion.ToPackedYcbcr422TenBit(
                        _profile, sample.Video.Memory);
                    var timestamp = St2110TransportAdmission.VideoTimestamp(
                        _profile, sample.SequenceNumber);
                    foreach (var packet in St2110VideoRtp.Packetize(_profile, packed,
                        timestamp, _configuration.St2110Settings!.Endpoint.VideoSsrc,
                        _videoSequence))
                    {
                        var length = await sender.SendVideoAsync(packet, cancellationToken)
                            .ConfigureAwait(false);
                        if (length != packet.Length)
                            throw new IOException("Video RTP datagram was not fully submitted.");
                        _videoSequence = unchecked(_videoSequence + 1);
                        ObservePacket(length);
                    }
                    var pcmPackets = _pcm.Packetize(sample.Audio,
                        sample.AudioTiming.SamplePosition);
                    foreach (var packet in pcmPackets)
                    {
                        var length = await sender.SendAudioAsync(packet, cancellationToken)
                            .ConfigureAwait(false);
                        if (length != packet.Length)
                            throw new IOException("Audio RTP datagram was not fully submitted.");
                        ObservePacket(length);
                    }
                    lock (_gate)
                    {
                        _sent++;
                        _lastSent = DateTimeOffset.UtcNow;
                        _failure = null;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_gate) _dropped++;
                    break;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    lock (_gate)
                    {
                        _dropped++;
                        _failure = new Failure("network.output.st2110_transport_failed",
                            $"ST 2110 software reference failed: {exception.GetType().Name}.");
                        _connected = false;
                        _lifecycle = NetworkOutputLifecycleState.Faulted;
                    }
                    break;
                }
                finally
                {
                    sample.Video.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_gate)
            {
                _connected = false;
                _lifecycle = NetworkOutputLifecycleState.Faulted;
                _failure = new Failure("network.output.st2110_setup_failed",
                    $"ST 2110 loopback sender failed: {exception.GetType().Name}.");
            }
        }
        finally
        {
            try
            {
                if (sender is not null) await sender.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _lifecycle = NetworkOutputLifecycleState.Faulted;
                    _failure = new Failure("network.output.st2110_close_failed",
                        $"ST 2110 sender cleanup failed: {exception.GetType().Name}.");
                }
            }
            lock (_gate)
            {
                while (_queue.Count > 0)
                {
                    _queue.Dequeue().Video.Dispose();
                    _dropped++;
                }
                _connected = false;
                if (_lifecycle != NetworkOutputLifecycleState.Faulted)
                    _lifecycle = NetworkOutputLifecycleState.Disabled;
            }
        }
    }

    private void ObservePacket(int bytes)
    {
        lock (_gate)
        {
            _packets++;
            _bytes += checked((ulong)bytes);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposed = true;
            if (_lifecycle != NetworkOutputLifecycleState.Faulted)
                _lifecycle = NetworkOutputLifecycleState.Stopping;
            _stop.Cancel();
            _signal.Release();
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await _worker.ConfigureAwait(false); }
        finally
        {
            _signal.Dispose();
            _stop.Dispose();
        }
    }
}
