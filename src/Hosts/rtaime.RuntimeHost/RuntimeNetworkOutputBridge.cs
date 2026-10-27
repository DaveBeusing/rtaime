// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

public sealed record RuntimeNetworkOutputTargetEvidence(
    string RoleId, string TargetId, NetworkOutputHealthSnapshot ProviderHealth,
    long EnqueuedVideoFrames, long EnqueuedAudioSampleFrames)
{
    // Requested versus observed are intentionally separate: queued Program frames
    // and provider send success cannot certify reception or physical scan-out.
    public NetworkOutputConfiguration? RequestedConfiguration { get; init; }
    public string CapabilityEvidence { get; init; } = "UNVERIFIED";
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset? LastAcceptedAtUtc { get; init; }
    public ulong? LastAcceptedSequence { get; init; }
    public TimeSpan? LastSendAge { get; init; }
    public ulong? ObservedBytesPerSecond { get; init; }
    public string DeliveryState { get; init; } = "UNKNOWN";
    public string PhysicalDeliveryEvidence { get; init; } = "UNVERIFIED";
}

/// <summary>
/// Runtime-owned output graph. Every provider session is constructed before the
/// immutable role/target graph becomes visible to the existing Program boundary.
/// Failed construction releases all provisional sessions; no partial graph starts
/// receiving Program frames. This is network transport fan-out, not physical
/// scan-out qualification or a second output routing authority.
/// </summary>
internal sealed class RuntimeNetworkOutputBridge : IAsyncDisposable
{
    private sealed class TargetSession(RuntimeNetworkOutputTarget target,
        INetworkOutputSession session, string capabilityEvidence)
    {
        private long _enqueuedVideoFrames;
        private long _enqueuedAudioSampleFrames;
        private long _lastAcceptedSequence = -1;
        private long _lastAcceptedUtcTicks;
        private readonly object _observeGate = new();
        private DateTimeOffset _lastRateAtUtc;
        private ulong _lastRateBytes;
        private ulong? _observedBytesPerSecond;

        public string RoleId { get; } = target.RoleId;
        public string TargetId { get; } = target.Configuration.TargetId;
        public NetworkOutputConfiguration Configuration { get; } = target.Configuration;
        public string CapabilityEvidence { get; } = capabilityEvidence;
        public INetworkOutputSession Session { get; } = session;

        public void ObserveEnqueued(long audioSampleFrames, ulong sequence)
        {
            Interlocked.Increment(ref _enqueuedVideoFrames);
            Interlocked.Add(ref _enqueuedAudioSampleFrames, audioSampleFrames);
            Interlocked.Exchange(ref _lastAcceptedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            if (sequence <= long.MaxValue)
                Interlocked.Exchange(ref _lastAcceptedSequence, (long)sequence);
        }

        public RuntimeNetworkOutputTargetEvidence Evidence
        {
            get
            {
                var snapshot = Session.Snapshot;
                var now = DateTimeOffset.UtcNow;
                var age = snapshot.LastSuccessfulSendUtc is { } sent && sent <= now
                    ? now - sent : (TimeSpan?)null;
                ulong? bytesPerSecond;
                lock (_observeGate)
                {
                    if (_lastRateAtUtc != default &&
                        now - _lastRateAtUtc >= TimeSpan.FromMilliseconds(250))
                    {
                        var elapsed = (now - _lastRateAtUtc).TotalSeconds;
                        if (snapshot.Statistics.BytesSent >= _lastRateBytes)
                        {
                            var rate = (double)(snapshot.Statistics.BytesSent - _lastRateBytes) / elapsed;
                            _observedBytesPerSecond = rate >= (double)ulong.MaxValue
                                ? ulong.MaxValue : (ulong)Math.Max(0, rate);
                        }
                        else _observedBytesPerSecond = null;
                        _lastRateBytes = snapshot.Statistics.BytesSent;
                        _lastRateAtUtc = now;
                    }
                    else if (_lastRateAtUtc == default)
                    {
                        _lastRateBytes = snapshot.Statistics.BytesSent;
                        _lastRateAtUtc = now;
                    }
                    bytesPerSecond = _observedBytesPerSecond;
                }
                var lastAcceptedAt = Interlocked.Read(ref _lastAcceptedUtcTicks);
                var lastSequence = Interlocked.Read(ref _lastAcceptedSequence);
                var delivery = snapshot.Lifecycle == NetworkOutputLifecycleState.Faulted ||
                    snapshot.Failure is not null ? "FAILED" :
                    snapshot.Connected && snapshot.Lifecycle == NetworkOutputLifecycleState.Connected &&
                    age is { } fresh && fresh <= TimeSpan.FromSeconds(5) &&
                    snapshot.Statistics.SentSamples > 0 ? "SENDER_ACTIVE" :
                    snapshot.Connected && age is { } stale && stale > TimeSpan.FromSeconds(5)
                        ? "STALE" : "UNKNOWN";
                return new RuntimeNetworkOutputTargetEvidence(
                    RoleId, TargetId, snapshot,
                    Interlocked.Read(ref _enqueuedVideoFrames),
                    Interlocked.Read(ref _enqueuedAudioSampleFrames))
                {
                    RequestedConfiguration = Configuration,
                    CapabilityEvidence = CapabilityEvidence,
                    ObservedAtUtc = now,
                    LastAcceptedAtUtc = lastAcceptedAt == 0 ? null : new DateTimeOffset(lastAcceptedAt, TimeSpan.Zero),
                    LastAcceptedSequence = lastSequence < 0 ? null : (ulong)lastSequence,
                    LastSendAge = age,
                    ObservedBytesPerSecond = bytesPerSecond,
                    DeliveryState = delivery,
                    PhysicalDeliveryEvidence = "UNVERIFIED"
                };
            }
        }
    }

    private readonly IReadOnlyDictionary<string, TargetSession> _sessions;
    private readonly IReadOnlyDictionary<string, TargetSession[]> _roles;
    private readonly IReadOnlyList<ProviderDescriptor> _providerDescriptors;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public RuntimeNetworkOutputBridge(
        IReadOnlyList<RuntimeNetworkOutputTarget> targets,
        IRuntimeNetworkOutputProviderRegistry? providerRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        providerRegistry ??= new RuntimeNetworkOutputProviderRegistry();

        if (targets.Count > 8 || targets.Any(target => target is null) ||
            targets.Any(target => string.IsNullOrWhiteSpace(target.Configuration.TargetId)) ||
            targets.Select(target => target.Configuration.TargetId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new ArgumentException(
                "Output graph requires at most eight uniquely identified provider targets.",
                nameof(targets));

        // Snapshot the actual provider inventory before creating any sink. The
        // production registry returns a descriptor per distinct protocol in this
        // order; synthetic test registries may deliberately advertise none.
        var protocols = targets.Select(target => target.Configuration.Protocol).Distinct().ToArray();
        var descriptors = providerRegistry.ProviderDescriptorsFor(targets);
        if (descriptors.Count > 0)
        {
            if (descriptors.Count != protocols.Length)
                throw new NotSupportedException("Provider output inventory does not cover the exact requested transport set.");
            for (var i = 0; i < protocols.Length; i++)
            {
                var descriptor = descriptors[i];
                var requested = targets.Where(target => target.Configuration.Protocol == protocols[i]).ToArray();
                if (descriptor is null || descriptor.Availability.State != ProviderAvailabilityState.Available ||
                    requested.Any(target => !descriptor.Capabilities.Any(capability =>
                        capability.Kind == NetworkOutputCapabilityKinds.Output &&
                        capability.SupportsVideoFormat(target.Configuration.VideoFormat))))
                    throw new NotSupportedException("A live network output provider is unavailable or does not support the requested format.");
                var capacity = descriptor.Resources
                    .Where(resource => resource.Kind == NetworkOutputCapabilityKinds.Output && resource.Reservable)
                    .Sum(resource => (long)resource.CapacityUnits);
                if (requested.LongLength > capacity)
                    throw new NotSupportedException("The observed network output provider has insufficient real reservable target slots.");
            }
        }

        var provisional = new Dictionary<string, TargetSession>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Construct actual provider sessions before a single role becomes
            // active. This is a finite reservation; no Program sample is emitted.
            foreach (var target in targets)
            {
                var session = providerRegistry.CreateSession(target);
                var capabilityEvidence = descriptors.Count > 0
                    ? "DECLARED_AVAILABLE_AND_ADMITTED" : "UNVERIFIED_TEST_PROVIDER";
                provisional.Add(target.Configuration.TargetId,
                    new TargetSession(target, session, capabilityEvidence));
            }
        }
        catch (Exception original)
        {
            var cleanupFailures = new List<Exception>();
            foreach (var item in provisional.Values)
            {
                try { item.Session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
            if (cleanupFailures.Count > 0)
                throw new AggregateException(
                    "Network output graph reservation failed and provider cleanup was not fully confirmed.",
                    new[] { original }.Concat(cleanupFailures));
            throw;
        }

        _sessions = provisional;
        _roles = targets.GroupBy(target => target.RoleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(target => provisional[target.Configuration.TargetId]).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        _providerDescriptors = descriptors;
    }

    public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => _providerDescriptors;
    public bool Enabled => _sessions.Count > 0;
    public bool HasRole(string roleId) => _roles.ContainsKey(roleId);

    public IReadOnlyList<string> TargetIdsForRole(string roleId) =>
        _roles.TryGetValue(roleId, out var targets)
            ? Array.AsReadOnly(targets.Select(target => target.TargetId).ToArray())
            : Array.Empty<string>();

    /// <summary>
    /// All configured sinks of a governed role receive the same Program slot and
    /// audio timing. Provider queue failures are reported per target; a transport
    /// that has already enqueued a frame cannot be rolled back retrospectively.
    /// </summary>
    public NetworkOutputEnqueueResult? TrySubmit(
        string roleId,
        GpuReadbackLease videoLease,
        FrameTiming videoTiming,
        AudioBufferDescriptor audio,
        ReadOnlyMemory<byte> audioPayload)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new ArgumentException("Output role identity is required.", nameof(roleId));
        ArgumentNullException.ThrowIfNull(videoLease);
        ArgumentNullException.ThrowIfNull(audio);

        if (!_roles.TryGetValue(roleId, out var targets))
        {
            videoLease.Dispose();
            return null;
        }

        // Retain all readers before submitting to any provider: an immediate
        // rejection/disposal in one provider cannot invalidate another target.
        var leases = new GpuReadbackLease?[targets.Length];
        leases[0] = videoLease;
        try
        {
            for (var i = 1; i < targets.Length; i++)
                leases[i] = videoLease.Retain();
        }
        catch
        {
            foreach (var lease in leases)
                lease?.Dispose();
            throw;
        }

        ReadOnlyMemory<byte> networkAudio = audioPayload;
        try
        {
            if (networkAudio.IsEmpty)
            {
                var count = checked((int)((long)audio.Timing.SampleCount *
                    audio.Format.ChannelCount * sizeof(float)));
                networkAudio = new byte[count];
            }
        }
        catch
        {
            foreach (var lease in leases)
                lease?.Dispose();
            throw;
        }

        var dropped = 0;
        var rejected = 0;
        string? rejectedTarget = null;
        for (var i = 0; i < targets.Length; i++)
        {
            GpuNetworkOutputPayloadLease? payload = new(leases[i]!);
            leases[i] = null;
            try
            {
                var sample = new NetworkOutputProgramSample(
                    videoTiming.SequenceNumber, videoTiming, audio.Timing, payload, networkAudio);
                var outcome = targets[i].Session.TrySubmit(sample);
                // Ownership transferred to the provider, including rejected
                // samples that the provider explicitly disposes.
                payload = null;
                if (outcome.Status == NetworkOutputEnqueueStatus.DroppedOldest) dropped++;
                if (outcome.Accepted)
                    targets[i].ObserveEnqueued(audio.Timing.SampleCount, videoTiming.SequenceNumber);
                else
                {
                    rejected++;
                    rejectedTarget ??= targets[i].TargetId;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                rejected++;
                rejectedTarget ??= targets[i].TargetId;
            }
            finally
            {
                payload?.Dispose();
            }
        }

        if (rejected > 0)
            return NetworkOutputEnqueueResult.Rejected(new Failure(
                "network.output.partial_target_rejection",
                $"One or more real provider output sessions rejected the Program sample ({rejected} of {targets.Length}; first: {rejectedTarget}). Other targets remain independently observable."));
        if (dropped > 0)
            return NetworkOutputEnqueueResult.AcceptedAfterDroppingOldest(new Failure(
                "network.output.fanout_backpressure",
                $"{dropped} of {targets.Length} network targets dropped an old sample under bounded pressure."));
        return NetworkOutputEnqueueResult.AcceptedSample();
    }

    // Preserve the historical single-role projection for existing peers.
    public NetworkOutputHealthSnapshot? SnapshotForRole(string roleId) =>
        _roles.TryGetValue(roleId, out var targets) && targets.Length > 0
            ? targets[0].Session.Snapshot : null;

    // New precise per-target telemetry; never collapse independent target health.
    public IReadOnlyList<NetworkOutputHealthSnapshot> SnapshotsForRole(string roleId) =>
        _roles.TryGetValue(roleId, out var targets)
            ? Array.AsReadOnly(targets.Select(target => target.Session.Snapshot).ToArray())
            : Array.Empty<NetworkOutputHealthSnapshot>();

    /// <summary>Accepted Program frames and audio samples per actual provider sink.
    /// Sent-frame and fault counts remain the provider's measured snapshot.
    /// </summary>
    public IReadOnlyList<RuntimeNetworkOutputTargetEvidence> TargetEvidence =>
        Array.AsReadOnly(_sessions.Values.Select(session => session.Evidence).ToArray());

    public NetworkOutputHealthSnapshot? SnapshotForTarget(string targetId) =>
        _sessions.TryGetValue(targetId, out var target) ? target.Session.Snapshot : null;

    public IReadOnlyList<NetworkOutputHealthSnapshot> Snapshots =>
        Array.AsReadOnly(_sessions.Values.Select(session => session.Session.Snapshot).ToArray());

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        foreach (var item in _sessions.Values)
        {
            try { await item.Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class GpuNetworkOutputPayloadLease : INetworkOutputPayloadLease
    {
        private GpuReadbackLease? _lease;

        public GpuNetworkOutputPayloadLease(GpuReadbackLease lease) =>
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));

        public ReadOnlyMemory<byte> Memory =>
            Volatile.Read(ref _lease)?.Memory ?? throw new ObjectDisposedException(nameof(GpuNetworkOutputPayloadLease));

        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}
