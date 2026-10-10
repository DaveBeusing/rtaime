// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Ndi;

namespace rtaime.Tests.Unit;

public sealed class NdiDiscoveryInputTests
{
    private static readonly MediaSourceId RuntimeSource =
        new(Identity.Parse("7b000000-0000-0000-0000-000000000001"));

    [Fact]
    public void Discovery_is_bounded_and_duplicate_display_names_keep_distinct_stable_identities()
    {
        using var backend = new MutableDiscoveryBackend(
            new NdiDiscoveredSourceEndpoint("Machine A (Camera)", "ndi://10.0.0.1/camera"),
            new NdiDiscoveredSourceEndpoint("Machine B (Camera)", "ndi://10.0.0.2/camera"),
            new NdiDiscoveredSourceEndpoint("Machine C (Other)", "ndi://10.0.0.3/other"));
        using var discovery = new NdiDiscoveryService(backend, maximumRetainedResults: 3);

        var snapshot = discovery.Refresh(new UtcTimestamp(DateTimeOffset.Parse("2026-10-10T08:00:00Z")));

        Assert.Equal(3, snapshot.Sources.Count);
        Assert.Equal(3, snapshot.MaximumRetainedResults);
        Assert.All(snapshot.Sources, source => Assert.Equal(ProviderAvailabilityState.Available, source.Availability.State));
        var cameras = snapshot.Sources.Where(source => source.DisplayName == "Camera").ToArray();
        Assert.Equal(2, cameras.Length);
        Assert.NotEqual(cameras[0].SourceId, cameras[1].SourceId);
        Assert.All(cameras, source => Assert.DoesNotContain("10.0.0.", source.SafeSourceIdentity, StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_never_retains_more_than_the_configured_bound()
    {
        using var backend = new MutableDiscoveryBackend(
            new NdiDiscoveredSourceEndpoint("Machine A (One)", "ndi://10.0.0.1/one"),
            new NdiDiscoveredSourceEndpoint("Machine B (Two)", "ndi://10.0.0.2/two"),
            new NdiDiscoveredSourceEndpoint("Machine C (Three)", "ndi://10.0.0.3/three"));
        using var discovery = new NdiDiscoveryService(backend, maximumRetainedResults: 2);

        var snapshot = discovery.Refresh(new UtcTimestamp(DateTimeOffset.Parse("2026-10-10T08:00:00Z")));

        Assert.Equal(2, snapshot.Sources.Count);
        Assert.Equal(2, snapshot.MaximumRetainedResults);
    }

    [Fact]
    public void Discovery_marks_missing_source_unavailable_then_expires_it_without_history_growth()
    {
        var backend = new MutableDiscoveryBackend(
            new NdiDiscoveredSourceEndpoint("Machine A (Camera)", "ndi://10.0.0.1/camera"));
        using var discovery = new NdiDiscoveryService(
            backend,
            maximumRetainedResults: 4,
            expiry: TimeSpan.FromSeconds(2));
        var firstTime = DateTimeOffset.Parse("2026-10-10T08:00:00Z");

        var first = discovery.Refresh(new UtcTimestamp(firstTime));
        var source = Assert.Single(first.Sources);
        backend.Set();

        var missing = discovery.Refresh(new UtcTimestamp(firstTime.AddSeconds(1)));
        Assert.Equal(ProviderAvailabilityState.Unavailable, Assert.Single(missing.Sources).Availability.State);

        var expired = discovery.Refresh(new UtcTimestamp(firstTime.AddSeconds(3)));
        Assert.Empty(expired.Sources);
        Assert.False(discovery.TryResolve(source.SourceId, out _));
    }

    [Fact]
    public void Discovery_identity_is_stable_across_address_changes()
    {
        var first = NdiDiscoveryService.CreateStableId("Machine A (Program)");
        var second = NdiDiscoveryService.CreateStableId("Machine A (Program)");

        Assert.Equal(first, second);
        Assert.Equal("Program", NdiDiscoveryService.DisplayName("Machine A (Program)"));
    }

    [Fact]
    public async Task Receive_queue_drops_oldest_media_instead_of_accumulating_latency()
    {
        var backend = new SequenceReceiveBackend(
            Sample(1),
            Sample(2),
            Sample(3));
        await using var session = new NdiInputSession(
            Configuration(queueCapacity: 2),
            () => backend);

        await WaitUntilAsync(
            () => session.Snapshot.Statistics.VideoFramesReceived >= 3,
            TimeSpan.FromSeconds(2));

        var snapshot = session.Snapshot;
        Assert.Equal(3UL, snapshot.Statistics.VideoFramesReceived);
        Assert.Equal(1UL, snapshot.Statistics.DroppedFrames);
        Assert.Equal(2, snapshot.Statistics.QueueDepth);
        Assert.True(session.TryDequeue(out var second));
        Assert.True(session.TryDequeue(out var third));
        Assert.Equal(2, second!.Video!.TimecodeHundredNanoseconds);
        Assert.Equal(3, third!.Video!.TimecodeHundredNanoseconds);
    }

    [Fact]
    public async Task Missing_runtime_fails_closed_without_substituting_another_source()
    {
        await using var session = new NdiInputSession(
            Configuration(),
            () => throw new DllNotFoundException("Synthetic missing NDI runtime."));

        await WaitUntilAsync(
            () => session.Snapshot.Lifecycle == MediaInputLifecycleState.Faulted,
            TimeSpan.FromSeconds(2));

        var snapshot = session.Snapshot;
        Assert.False(snapshot.Connected);
        Assert.Equal("network.input.ndi_runtime_unavailable", snapshot.Failure?.Code);
        Assert.Equal(Configuration().DiscoveredSourceId, snapshot.DiscoveredSourceId);
    }

    [Fact]
    public async Task Sender_loss_preserves_adopted_identity_and_recovers_when_same_receiver_returns()
    {
        var now = DateTimeOffset.Parse("2026-10-10T08:00:00Z");
        var backend = new LossRecoveryBackend(() => now);
        await using var session = new NdiInputSession(
            Configuration(sourceLossTimeout: TimeSpan.FromMilliseconds(250)),
            () => backend,
            () => now);

        backend.Publish(Sample(1));
        await WaitUntilAsync(
            () => session.Snapshot.Lifecycle == MediaInputLifecycleState.Connected,
            TimeSpan.FromSeconds(2));
        var identity = session.Snapshot.DiscoveredSourceId;

        backend.ConnectionCountValue = 0;
        now = now.AddSeconds(1);
        await WaitUntilAsync(
            () => session.Snapshot.Lifecycle == MediaInputLifecycleState.Lost,
            TimeSpan.FromSeconds(2));

        backend.ConnectionCountValue = 1;
        backend.Publish(Sample(2));
        await WaitUntilAsync(
            () => session.Snapshot.Lifecycle == MediaInputLifecycleState.Connected &&
                  session.Snapshot.Statistics.VideoFramesReceived >= 2,
            TimeSpan.FromSeconds(2));

        Assert.Equal(identity, session.Snapshot.DiscoveredSourceId);
        Assert.True(session.Snapshot.Statistics.ReconnectCount >= 1);
    }

    [Fact]
    public void Unsupported_video_cadence_fails_before_payload_acceptance()
    {
        var frame = new NdiVideoFrameV2
        {
            XRes = 1920,
            YRes = 1080,
            FourCc = NdiFourCc.Rgba,
            FrameRateNumerator = 25,
            FrameRateDenominator = 1,
            FrameFormatType = NdiFrameFormat.Progressive,
            Timecode = 1
        };

        Assert.Throws<NotSupportedException>(() => NdiReceiveNormalizer.NormalizeVideo(frame));
    }

    [Fact]
    public void Provider_descriptor_exposes_output_discovery_and_input_on_one_provider()
    {
        var provider = new NdiNetworkOutputProvider();

        Assert.Contains(provider.Descriptor.Capabilities, item => item.Kind == NetworkOutputCapabilityKinds.Output);
        Assert.Contains(provider.Descriptor.Capabilities, item => item.Kind == MediaSourceCapabilityKinds.Discovery);
        Assert.Contains(provider.Descriptor.Capabilities, item => item.Kind == MediaSourceCapabilityKinds.Input);
        Assert.Single(provider.Descriptor.Resources, item => item.Kind == MediaSourceCapabilityKinds.Discovery);
        Assert.Single(provider.Descriptor.Resources, item => item.Kind == MediaSourceCapabilityKinds.Input);
    }

    private static NdiInputConfiguration Configuration(
        int queueCapacity = 4,
        TimeSpan? sourceLossTimeout = null)
    {
        var endpoint = new NdiDiscoveredSourceEndpoint("Machine A (Program)", "ndi://10.0.0.1/program");
        return new NdiInputConfiguration(
            RuntimeSource,
            NdiDiscoveryService.CreateStableId(endpoint.NdiName),
            NdiDiscoveryService.SafeIdentity(endpoint.NdiName),
            endpoint,
            queueCapacity,
            sourceLossTimeout);
    }

    private static NdiInputMediaSample Sample(long timecode) =>
        NdiInputMediaSample.FromVideo(
            new NdiInputVideoFrame(
                VideoFormat.Hd1080p50Rgba8,
                timecode,
                new byte[] { 1, 2, 3, 4 }));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Expected NDI state was not observed within the bounded wait.");
            await Task.Delay(10);
        }
    }

    private sealed class MutableDiscoveryBackend : INdiDiscoveryBackend
    {
        private NdiDiscoveredSourceEndpoint[] _sources;
        public MutableDiscoveryBackend(params NdiDiscoveredSourceEndpoint[] sources) => _sources = sources;
        public void Set(params NdiDiscoveredSourceEndpoint[] sources) => _sources = sources;
        public IReadOnlyList<NdiDiscoveredSourceEndpoint> GetCurrentSources(TimeSpan waitForChange) => _sources;
        public void Dispose() { }
    }

    private sealed class SequenceReceiveBackend : INdiReceiveBackend
    {
        private readonly Queue<NdiInputMediaSample> _samples;
        public SequenceReceiveBackend(params NdiInputMediaSample[] samples) => _samples = new Queue<NdiInputMediaSample>(samples);
        public int ConnectionCount => 1;
        public NdiCaptureResult Capture(TimeSpan timeout)
        {
            lock (_samples)
                return _samples.Count > 0
                    ? new NdiCaptureResult(NdiCaptureStatus.Media, _samples.Dequeue())
                    : new NdiCaptureResult(NdiCaptureStatus.None);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LossRecoveryBackend : INdiReceiveBackend
    {
        private readonly Func<DateTimeOffset> _clock;
        private readonly Queue<NdiInputMediaSample> _samples = new();
        public LossRecoveryBackend(Func<DateTimeOffset> clock) => _clock = clock;
        public int ConnectionCountValue { get; set; } = 1;
        public int ConnectionCount => ConnectionCountValue;
        public void Publish(NdiInputMediaSample sample)
        {
            lock (_samples) _samples.Enqueue(sample);
        }
        public NdiCaptureResult Capture(TimeSpan timeout)
        {
            _ = _clock();
            lock (_samples)
                return _samples.Count > 0
                    ? new NdiCaptureResult(NdiCaptureStatus.Media, _samples.Dequeue())
                    : new NdiCaptureResult(NdiCaptureStatus.None);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
