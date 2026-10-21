// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class SequenceNativeExecutionSnapshotTests
{
    private static readonly string Fingerprint = new('A', 64);

    private static (SequenceExecutionSnapshot Snapshot, SequenceRuntimePositionMap Map,
        SequenceMediaOwnerRequest[] Owners) Fixture()
    {
        var sourceA = MediaSourceId.New();
        var sourceB = MediaSourceId.New();
        var assetA = MediaAssetId.New();
        var assetB = MediaAssetId.New();
        var id = Guid.NewGuid();
        var owners = new[]
        {
            new SequenceMediaOwnerRequest(sourceA, assetA, "C:\\media\\one.mp4", Fingerprint, 0, 0),
            new SequenceMediaOwnerRequest(sourceB, assetB, "C:\\media\\two.mp4", Fingerprint, 0, 0)
        };
        var snapshot = new SequenceExecutionSnapshot(
            SequenceExecutionSnapshotContract.Version, id, 7, new(50, 1), 9600,
            [new SequenceExecutionSlice(0, 9600,
            [
                new(Guid.NewGuid(), Guid.Parse(assetA.ToString()), Fingerprint,
                    SequenceMediaKind.Video, sourceA.ToString(), 0, null, 0, 0, null, 0, 0, false),
                new(Guid.NewGuid(), Guid.Parse(assetB.ToString()), Fingerprint,
                    SequenceMediaKind.Video, sourceB.ToString(), 1, null, 0, 0, null, 0, 0, false),
                new(Guid.NewGuid(), Guid.Parse(assetA.ToString()), Fingerprint,
                    SequenceMediaKind.Audio, sourceA.ToString(), 2, "program", 0, 0, null, 0, 0, false)
            ])]);
        var map = SequenceRuntimePositionMap.Prepare(id, 7, new FrameRate(50, 1),
            9600, 100, 11);
        return (snapshot, map, owners);
    }

    [Fact]
    public void Exact_prepared_source_ids_and_shared_video_audio_offsets_are_admitted()
    {
        var (snapshot, map, owners) = Fixture();
        SequenceNativeExecutionSnapshot.Validate(snapshot, map, owners);
        Assert.Equal(3, Assert.Single(snapshot.Slices).Clips.Length);
    }

    [Fact]
    public void Rebound_source_offset_fingerprint_and_new_revision_fail_before_native_reservation()
    {
        var (snapshot, map, owners) = Fixture();
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot, map, [owners[0] with { SourceInTicks = 4800 }, owners[1]]));
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot, map, [owners[0] with { FingerprintSha256 = new string('B', 64) }, owners[1]]));
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot with { AuthoringRevision = 8 }, map, owners));
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot, map, [owners[0], owners[1] with { SourceId = MediaSourceId.New() }]));
    }

    [Fact]
    public void Unreferenced_owner_gap_and_overlapping_slices_are_rejected()
    {
        var (snapshot, map, owners) = Fixture();
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot, map, [.. owners, new SequenceMediaOwnerRequest(
                MediaSourceId.New(), MediaAssetId.New(), "C:\\media\\unused.mp4", Fingerprint, 0, 0)]));
        var slice = Assert.Single(snapshot.Slices);
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot with { Slices = [slice with { StartTick = 1 }] }, map, owners));
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot with { Slices = [slice with { EndTick = 4800 }, slice] }, map, owners));
    }

    [Fact]
    public void Invalid_native_snapshot_cannot_retime_the_single_Program_clock()
    {
        var (snapshot, map, owners) = Fixture();
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot with { FrameRate = new(60000, 1001) }, map, owners));
        Assert.Throws<InvalidDataException>(() => SequenceNativeExecutionSnapshot.Validate(
            snapshot with { Version = "unsupported" }, map, owners));
    }
}
