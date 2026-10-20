// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Media;
using rtaime.Provider.Contracts;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class LocalMediaResourceLedgerTests
{
    [Theory]
    [InlineData(1, LocalMediaCapacityProfile.Conservative)]
    [InlineData(2, LocalMediaCapacityProfile.Conservative)]
    [InlineData(4, LocalMediaCapacityProfile.OptInFour)]
    public void Concurrent_intents_never_exceed_profile_or_release_twice(int capacity, LocalMediaCapacityProfile profile)
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, capacity, profile);
        Parallel.For(0, 10_000, intentIndex =>
        {
            var identity = IdentityFor(ledger);
            if (ledger.TryReserve(Guid.NewGuid(), identity, out var ticket, out _))
            {
                Assert.InRange(ledger.Snapshot.ReservedOwners, 1, capacity);
                ticket!.RetainPoolLease();
                ticket.OwnerCompleted();
                ticket.OwnerCompleted();
                ticket.ReleasePoolLease();
                ticket.OwnerCompleted();
            }
            Assert.InRange(ledger.Snapshot.ReservedOwners, 0, capacity);
        });
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
        Assert.Equal(0L, ledger.Snapshot.KnownManagedBytes);
    }

    [Fact]
    public void Close_keeps_entire_old_pool_charged_until_last_consumer_releases()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 1);
        Assert.True(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger), out var ticket, out _));
        ticket!.RetainPoolLease();
        var before = ledger.Snapshot.KnownManagedBytes;
        ticket.OwnerCompleted();
        Assert.Equal(1, ledger.Snapshot.RetainedPoolOwners);
        Assert.Equal(before, ledger.Snapshot.KnownManagedBytes);
        Assert.False(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger), out _, out var failure));
        Assert.Equal("runtime.media_resources.capacity", failure!.Value.Code);
        ticket.ReleasePoolLease();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
        Assert.Equal(0L, ledger.Snapshot.KnownManagedBytes);
        ticket.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    [Fact]
    public void Quarantine_is_not_released_by_cancellation_or_later_completion()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 2);
        Assert.True(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger), out var ticket, out _));
        ticket!.Quarantine();
        ticket.OwnerCompleted();
        Assert.Equal(1, ledger.Snapshot.QuarantinedOwners);
        Assert.Equal(1, ledger.Snapshot.UnknownNativeOwners);
        Assert.Null(ledger.Snapshot.NativeBytes);
        Assert.Null(ledger.Snapshot.GpuBytes);
        Assert.False(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger), out _, out var failure));
        Assert.Equal("runtime.media_resources.quarantined", failure!.Value.Code);
    }

    [Fact]
    public void Intent_identity_is_idempotent_and_another_Runtime_epoch_is_rejected()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 2);
        var identity = IdentityFor(ledger);
        var intent = Guid.NewGuid();
        Assert.True(ledger.TryReserve(intent, identity, out var ticket, out _));
        Assert.True(ledger.TryReserve(intent, identity, out var same, out _));
        Assert.Same(ticket, same);
        Assert.False(ledger.TryReserve(intent, IdentityFor(ledger), out _, out var conflict));
        Assert.Equal("runtime.media_resources.intent_conflict", conflict!.Value.Code);
        var other = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 2);
        Assert.False(other.TryReserve(Guid.NewGuid(), identity, out _, out var stale));
        Assert.Equal("runtime.media_resources.identity", stale!.Value.Code);
        ticket!.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    [Fact]
    public void Optional_work_does_not_wait_for_or_displace_committed_Program_ownership()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 2);
        var program = IdentityFor(ledger);
        ledger.ObserveProgram(program.SourceId);
        Assert.True(ledger.TryReserve(Guid.NewGuid(), program, out var ticket, out _));
        Assert.False(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger) with { Purpose = LocalMediaResourcePurpose.Preflight }, out _, out var failure));
        Assert.Equal("runtime.media_resources.pressure_unknown", failure!.Value.Code);
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);
        Assert.StartsWith("UNKNOWN", ledger.Snapshot.StoragePressure);
        ticket!.OwnerCompleted();
    }

    [Fact]
    public void Noncooperative_optional_owner_retains_capacity_instead_of_granting_an_unchecked_decoder()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 2);
        Assert.True(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger) with { Purpose = LocalMediaResourcePurpose.Preflight }, out var ticket, out _));
        Assert.False(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger), out _, out var failure));
        Assert.Equal("runtime.media_resources.optional_releasing", failure!.Value.Code);
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);
        ticket!.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    [Fact]
    public void Four_decoders_require_the_explicit_development_profile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 5, LocalMediaCapacityProfile.OptInFour));
    }

    [Fact]
    public void Shutdown_closes_new_admission_but_final_retained_leases_can_still_release()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 1);
        var identity = IdentityFor(ledger);
        var intent = Guid.NewGuid();
        Assert.True(ledger.TryReserve(intent, identity, out var ticket, out _));
        ticket!.RetainPoolLease();
        ledger.CloseAdmission();
        ticket.OwnerCompleted();
        Assert.False(ledger.TryReserve(intent, identity, out _, out var stopped));
        Assert.Equal("runtime.media_resources.stopped", stopped!.Value.Code);
        Assert.Equal(1, ledger.Snapshot.RetainedPoolOwners);
        ticket.ReleasePoolLease();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
        Assert.Equal(0L, ledger.Snapshot.KnownManagedBytes);
    }

    [Fact]
    public void Authority_state_is_part_of_the_idempotent_ticket_identity()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 1);
        var identity = IdentityFor(ledger) with { AuthorityStateId = Identity.New() };
        var intent = Guid.NewGuid();
        Assert.True(ledger.TryReserve(intent, identity, out var ticket, out _));
        Assert.False(ledger.TryReserve(intent, identity with { AuthorityStateId = Identity.New() }, out _, out var conflict));
        Assert.Equal("runtime.media_resources.intent_conflict", conflict!.Value.Code);
        ticket!.OwnerCompleted();
    }

    [Fact]
    public void Sequence_group_is_atomic_and_cannot_displace_Program_or_retained_pools()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 4,
            LocalMediaCapacityProfile.OptInFour);
        var program = IdentityFor(ledger);
        Assert.True(ledger.TryReserve(Guid.NewGuid(), program, out var committed, out _));
        committed!.RetainPoolLease();
        var group = Enumerable.Range(0, 4).Select(_ =>
            new LocalMediaResourceRequest(Guid.NewGuid(), IdentityFor(ledger))).ToArray();

        Assert.False(ledger.TryReserveGroup(group, out var rejected, out var reason));
        Assert.Empty(rejected);
        Assert.Equal("runtime.media_resources.capacity", reason?.Code);
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);

        committed.OwnerCompleted();
        Assert.Equal(1, ledger.Snapshot.RetainedPoolOwners);
        Assert.False(ledger.TryReserveGroup(group, out rejected, out reason));
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);
        committed.ReleasePoolLease();

        Assert.True(ledger.TryReserveGroup(group, out var admitted, out reason));
        Assert.Null(reason);
        Assert.Equal(4, admitted.Count);
        Assert.Equal(4, ledger.Snapshot.ReservedOwners);
        Assert.False(ledger.TryReserveGroup(group, out _, out reason));
        Assert.Equal("runtime.media_resources.sequence_intent_conflict", reason?.Code);
        foreach (var ticket in admitted) ticket.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    [Fact]
    public void Sequence_group_rollback_does_not_charge_partial_admissions_or_weaken_optional_exclusion()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 4,
            LocalMediaCapacityProfile.OptInFour);
        var optional = IdentityFor(ledger) with { Purpose = LocalMediaResourcePurpose.Analysis };
        Assert.True(ledger.TryReserve(Guid.NewGuid(), optional, out var occupied, out _));
        var group = Enumerable.Range(0, 3).Select(_ =>
            new LocalMediaResourceRequest(Guid.NewGuid(), IdentityFor(ledger))).ToArray();
        Assert.False(ledger.TryReserveGroup(group, out var refused, out var failure));
        Assert.Empty(refused);
        Assert.Equal("runtime.media_resources.optional_releasing", failure?.Code);
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);
        occupied!.OwnerCompleted();
        Assert.True(ledger.TryReserveGroup(group, out var admitted, out failure));
        Assert.Equal(3, admitted.Count);
        foreach (var ticket in admitted) ticket.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    [Fact]
    public void Sequence_group_rejects_stale_identity_without_retaining_any_reserved_part()
    {
        var ledger = new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 4,
            LocalMediaCapacityProfile.OptInFour);
        var ids = new[]
        {
            new LocalMediaResourceRequest(Guid.NewGuid(), IdentityFor(ledger)),
            new LocalMediaResourceRequest(Guid.NewGuid(), IdentityFor(ledger) with { RuntimeEpoch = "stale" })
        };
        Assert.False(ledger.TryReserveGroup(ids, out var tickets, out var failure));
        Assert.Empty(tickets);
        Assert.Equal("runtime.media_resources.identity", failure?.Code);
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    [Fact]
    public void Provider_admitted_six_owner_sequence_group_is_all_or_none_and_release_safe()
    {
        var format = VideoFormat.Hd1080p50Rgba8;
        var profile = new RuntimeResourceCapabilityProfile(
            "reference.provider", "software.test", null,
            [format], [], 8, 8, 8, 1_000_000_000, 0, 0, false);
        var ledger = new LocalMediaResourceLedger(format, 6, profile);
        var requests = Enumerable.Range(0, 6)
            .Select(_ => new LocalMediaResourceRequest(Guid.NewGuid(), IdentityFor(ledger)))
            .ToArray();
        Assert.True(ledger.TryReserveGroup(requests, out var tickets, out var failure),
            failure?.Message);
        Assert.Equal(6, tickets.Count);
        Assert.Equal(6, ledger.Snapshot.ReservedOwners);
        foreach (var ticket in tickets)
            ticket.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
        Assert.Equal(0L, ledger.Snapshot.KnownManagedBytes);
    }

    [Fact]
    public void Device_profile_cannot_allocate_more_owner_pools_than_verified_managed_budget()
    {
        var format = VideoFormat.Hd1080p50Rgba8;
        var profile = new RuntimeResourceCapabilityProfile(
            "reference.provider", "software.test", null,
            [format], [], 8, 8, 8, 100_000_000, 0, 0, false);
        Assert.Throws<ArgumentException>(() =>
            new LocalMediaResourceLedger(format, 6, profile));
        var hdOnly = new RuntimeResourceCapabilityProfile(
            "reference.provider", "software.test", null,
            [format], [], 8, 8, 8, 1_000_000_000, 0, 0, false);
        var uhd = new VideoFormat(3840, 2160, FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive);
        Assert.Throws<ArgumentException>(() =>
            new LocalMediaResourceLedger(uhd, 6, hdOnly));
    }

    [Fact]
    public void Live_provider_inventory_and_pressure_are_rechecked_inside_atomic_native_reservations()
    {
        var format = VideoFormat.Hd1080p50Rgba8;
        var providerId = ProviderId.New();
        ProviderDescriptor MakeProvider(int slots, bool available = true) => new(
            ProviderContractVersion.Current, providerId, "Observed decoder",
            available
                ? new ProviderAvailability(ProviderAvailabilityState.Available)
                : new ProviderAvailability(ProviderAvailabilityState.Unavailable,
                    new Failure("device.disconnected", "The native device disconnected.")),
            [new ProviderCapabilityDescriptor(CapabilityId.New(), LocalMediaCapabilityKinds.Decode, [format]),
             new ProviderCapabilityDescriptor(CapabilityId.New(), LocalMediaCapabilityKinds.MediaRoute, [format])],
            [new ProviderResourceDescriptor(ProviderResourceId.New(), providerId,
                 LocalMediaCapabilityKinds.Decode, (uint)slots, true),
             new ProviderResourceDescriptor(ProviderResourceId.New(), providerId,
                 LocalMediaCapabilityKinds.MediaRoute, (uint)slots, true)]);
        var current = MakeProvider(4);
        var underPressure = false;
        var profile = new RuntimeResourceCapabilityProfile(providerId.ToString(),
            "device.observed", null, [format], [], 4, 4, 8, 1_000_000_000, 0, 0, false);
        var ledger = new LocalMediaResourceLedger(format, 4, profile,
            () => current, () => underPressure);
        Assert.True(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger),
            out var retained, out _));
        Assert.NotNull(retained);

        current = MakeProvider(1);
        Assert.False(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger),
            out _, out var reduced));
        Assert.Equal("runtime.media_resources.live_capacity", reduced?.Code);
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);

        current = MakeProvider(4);
        underPressure = true;
        var rejectedGroup = Enumerable.Range(0, 3).Select(_ =>
            new LocalMediaResourceRequest(Guid.NewGuid(), IdentityFor(ledger))).ToArray();
        Assert.False(ledger.TryReserveGroup(rejectedGroup, out var none, out var pressure));
        Assert.Empty(none);
        Assert.Equal("runtime.media_resources.host_memory_pressure", pressure?.Code);
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);

        underPressure = false;
        Assert.True(ledger.TryReserveGroup(rejectedGroup, out var tickets, out var failure),
            failure?.Message);
        Assert.Equal(4, ledger.Snapshot.ReservedOwners);
        foreach (var ticket in tickets) ticket.OwnerCompleted();
        Assert.Equal(1, ledger.Snapshot.ReservedOwners);

        current = MakeProvider(4, available: false);
        Assert.False(ledger.TryReserve(Guid.NewGuid(), IdentityFor(ledger),
            out _, out var disconnected));
        Assert.Equal("runtime.media_resources.provider_changed", disconnected?.Code);
        retained!.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    [Fact]
    public void Live_provider_loss_mid_sequence_group_rolls_back_every_provisional_ticket()
    {
        var format = VideoFormat.Hd1080p50Rgba8;
        var providerId = ProviderId.New();
        ProviderDescriptor Observe(bool available) => new(
            ProviderContractVersion.Current, providerId, "Observed decoder",
            available
                ? new ProviderAvailability(ProviderAvailabilityState.Available)
                : new ProviderAvailability(ProviderAvailabilityState.Unavailable,
                    new Failure("device.disconnected", "Provider became unavailable.")),
            [new ProviderCapabilityDescriptor(CapabilityId.New(), LocalMediaCapabilityKinds.Decode, [format]),
             new ProviderCapabilityDescriptor(CapabilityId.New(), LocalMediaCapabilityKinds.MediaRoute, [format])],
            [new ProviderResourceDescriptor(ProviderResourceId.New(), providerId,
                LocalMediaCapabilityKinds.Decode, 3U, true),
             new ProviderResourceDescriptor(ProviderResourceId.New(), providerId,
                LocalMediaCapabilityKinds.MediaRoute, 3U, true)]);

        // Constructor observes once; group preflight observes once; each
        // provisional owner must be re-admitted against a fresh live snapshot.
        var observations = 0;
        var profile = new RuntimeResourceCapabilityProfile(
            providerId.ToString(), "live-device", null, [format], [],
            3, 3, 8, 1_000_000_000, 0, 0, false);
        var ledger = new LocalMediaResourceLedger(format, 3, profile,
            () => Observe(++observations < 4), () => false);
        var group = Enumerable.Range(0, 2)
            .Select(_ => new LocalMediaResourceRequest(Guid.NewGuid(), IdentityFor(ledger)))
            .ToArray();

        Assert.False(ledger.TryReserveGroup(group, out var tickets, out var rejected));
        Assert.Empty(tickets);
        Assert.Equal("runtime.media_resources.provider_changed", rejected?.Code);
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
        Assert.Equal(0, ledger.Snapshot.RetainedPoolOwners);
        Assert.Equal(0L, ledger.Snapshot.KnownManagedBytes);

        // The failure is fail-closed for the candidate, not a permanent
        // corruption of ledger ownership or the committed Program.
        observations = 0;
        Assert.True(ledger.TryReserveGroup(group, out var admitted, out var failure),
            failure?.Message);
        Assert.Equal(2, admitted.Count);
        foreach (var ticket in admitted) ticket.OwnerCompleted();
        Assert.Equal(0, ledger.Snapshot.ReservedOwners);
    }

    private static LocalMediaResourceIdentity IdentityFor(LocalMediaResourceLedger ledger) =>
        new(ledger.RuntimeEpoch, MediaSourceId.New(), null, null, null, LocalMediaResourcePurpose.Resident);
}
