// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeNetworkOutputBridgeAdmissionTests
{
    private static readonly VideoFormat Format = VideoFormat.Hd1080p50Rgba8;

    [Fact]
    public async Task Two_real_provider_sessions_for_one_Program_role_are_admitted_with_distinct_target_ids()
    {
        var registry = new ObservedRegistry(capacity: 2);
        await using var bridge = new RuntimeNetworkOutputBridge(
            [Target("first"), Target("second")], registry);
        Assert.Equal(2, registry.Created.Count);
        Assert.Equal(new[] { "first", "second" }, bridge.TargetIdsForRole("program"));
        Assert.Equal(2, bridge.SnapshotsForRole("program").Count);
    }

    [Fact]
    public void Live_provider_capacity_rejects_the_entire_graph_before_opening_any_session()
    {
        var registry = new ObservedRegistry(capacity: 2);
        Assert.Throws<NotSupportedException>(() => new RuntimeNetworkOutputBridge(
            [Target("first"), Target("second"), Target("third")], registry));
        Assert.Empty(registry.Created);
    }

    [Fact]
    public void Unavailable_or_mismatched_provider_cannot_be_overridden_by_configuration()
    {
        var missing = new ObservedRegistry(capacity: 2, available: false);
        Assert.Throws<NotSupportedException>(() => new RuntimeNetworkOutputBridge([Target("first")], missing));
        Assert.Empty(missing.Created);

        var incompatible = new ObservedRegistry(capacity: 2, supportsFormat: false);
        Assert.Throws<NotSupportedException>(() => new RuntimeNetworkOutputBridge([Target("first")], incompatible));
        Assert.Empty(incompatible.Created);
    }

    [Fact]
    public void Failed_second_actual_session_reservation_releases_the_complete_staged_graph()
    {
        var registry = new ObservedRegistry(capacity: 2, failSecond: true);
        Assert.Throws<IOException>(() => new RuntimeNetworkOutputBridge(
            [Target("first"), Target("second")], registry));
        var first = Assert.Single(registry.Created);
        Assert.Equal(1, first.DisposalCount);
    }

    private static RuntimeNetworkOutputTarget Target(string name) =>
        new("program", new NetworkOutputConfiguration(name,
            NetworkOutputProtocolFamily.Ndi, Format, AudioFormat.Stereo48kFloat32,
            new NdiNetworkOutputSettings(name)));

    private sealed class ObservedRegistry(
        uint capacity, bool available = true, bool supportsFormat = true, bool failSecond = false)
        : IRuntimeNetworkOutputProviderRegistry
    {
        private readonly ProviderId _id = ProviderId.New();
        public List<ObservedSession> Created { get; } = new();

        public IReadOnlyList<ProviderDescriptor> ProviderDescriptorsFor(
            IReadOnlyList<RuntimeNetworkOutputTarget> targets) =>
            [new ProviderDescriptor(
                ProviderContractVersion.Current, _id, "Observed output",
                available
                    ? new ProviderAvailability(ProviderAvailabilityState.Available)
                    : new ProviderAvailability(ProviderAvailabilityState.Unavailable,
                        new Failure("network.unavailable", "Observed provider not available.")),
                [new ProviderCapabilityDescriptor(CapabilityId.New(),
                    NetworkOutputCapabilityKinds.Output,
                    supportsFormat ? [Format] : [VideoFormat.Hd1080p59_94Rgba8])],
                [new ProviderResourceDescriptor(ProviderResourceId.New(), _id,
                    NetworkOutputCapabilityKinds.Output, capacity, true)])];

        public INetworkOutputSession CreateSession(RuntimeNetworkOutputTarget target)
        {
            if (failSecond && Created.Count > 0)
                throw new IOException("Observed second provider reservation failed.");
            var session = new ObservedSession(target.Configuration);
            Created.Add(session);
            return session;
        }
    }

    private sealed class ObservedSession(NetworkOutputConfiguration configuration)
        : INetworkOutputSession
    {
        public NetworkOutputConfiguration Configuration => configuration;
        public int DisposalCount { get; private set; }
        public NetworkOutputHealthSnapshot Snapshot => throw new NotSupportedException();
        public NetworkOutputEnqueueResult TrySubmit(NetworkOutputProgramSample sample)
        {
            sample.Video.Dispose();
            return NetworkOutputEnqueueResult.AcceptedSample();
        }
        public ValueTask DisposeAsync()
        {
            DisposalCount++;
            return ValueTask.CompletedTask;
        }
    }
}
