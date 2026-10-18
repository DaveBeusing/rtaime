// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class LocalMediaResourceLedgerTests
{
    [Theory]
    [InlineData(1, LocalMediaCapacityProfile.Conservative)]
    [InlineData(2, LocalMediaCapacityProfile.Conservative)]
    [InlineData(4, LocalMediaCapacityProfile.DevelopmentFour)]
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalMediaResourceLedger(VideoFormat.Hd1080p50Rgba8, 5, LocalMediaCapacityProfile.DevelopmentFour));
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

    private static LocalMediaResourceIdentity IdentityFor(LocalMediaResourceLedger ledger) =>
        new(ledger.RuntimeEpoch, MediaSourceId.New(), null, null, null, LocalMediaResourcePurpose.Resident);
}
