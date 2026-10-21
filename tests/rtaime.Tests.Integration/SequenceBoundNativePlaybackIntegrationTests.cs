// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class SequenceBoundNativePlaybackIntegrationTests
{
    [Fact]
    public void Bound_two_video_two_audio_track_selection_uses_real_native_owners_and_single_Runtime_clock()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var media = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(media.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var provider = decks.ProviderDescriptor;
        var capability = provider.Capabilities.Single(c => c.Kind == "media.route");
        var resources = provider.Resources.Where(r => r.Kind == "media.route").ToArray();
        var owners = new[]
        {
            new SequenceMediaOwnerRequest(MediaSourceId.New(), MediaAssetId.New(), media.Path, hash, 0, 0),
            new SequenceMediaOwnerRequest(MediaSourceId.New(), MediaAssetId.New(), media.Path, hash, 0, 0)
        };
        var authority = new AuthoritySnapshotReference(Identity.New(), new Revision(1));
        var execution = new PreparedExecutionContract(RuntimeContractVersion.Current,
            PreparedExecutionId.New(), authority, new Generation(1),
            owners.Select((owner, index) => new PreparedExecutionBinding(owner.SourceId.Value,
                capability.CapabilityId, resources[index], owner.SourceId, null)).ToArray());
        var sequenceId = Guid.NewGuid();
        var map = SequenceRuntimePositionMap.Prepare(sequenceId, 7, FrameRate.Fps50,
            20 * 4800, 100, 1);
        SequenceExecutionClip Clip(int owner, SequenceMediaKind kind, int order) =>
            new(Guid.NewGuid(), Guid.Parse(owners[owner].AssetId.ToString()), hash,
                kind, owners[owner].SourceId.ToString(), order,
                kind == SequenceMediaKind.Audio ? "program" : null,
                0, 0, null, 0, 0, false);
        var plan = new SequenceExecutionSnapshot(
            SequenceExecutionSnapshotContract.Version, sequenceId, 7,
            new(50, 1), 20 * 4800,
            [new SequenceExecutionSlice(0, 20 * 4800,
            [
                Clip(0, SequenceMediaKind.Video, 0),
                Clip(1, SequenceMediaKind.Video, 1),
                Clip(0, SequenceMediaKind.Audio, 2),
                Clip(1, SequenceMediaKind.Audio, 3)
            ])]);
        var staged = decks.PrepareSequenceMedia(execution, map, owners, executionSnapshot: plan);
        Assert.True(staged.Prepared, staged.Failure?.Message);

        var host = Guid.NewGuid().ToString("D");
        var reference = new SequencePreparationReference(SequencePreparationContract.Version,
            sequenceId, 7, host, execution.PreparedExecutionId.ToString(), 1);
        ulong slot = 100;
        var playing = decks.ApplySequenceShuttle(
            new(SequenceShuttleContract.Version, reference, 0, SequenceShuttleAction.Play),
            host, authority.StateId, authority.Revision, () => slot);
        Assert.Equal("PLAYING", playing.State);

        var selected = new SequenceExecutionClip[4];
        var requests = new SequenceClipFrameRequest[4];
        var ownerRequests = new SequenceMediaFrameRequest[2];
        var frames = new SequenceMediaFrameLease?[2];
        var indexes = new int[4];
        Assert.True(decks.TrySelectBoundSequenceBoundary(
            reference, slot, selected, requests, ownerRequests, frames, indexes,
            out var position, out var clipCount, out var ownerCount, out var failure),
            failure?.Message);
        Assert.Equal(0L, position.Tick);
        Assert.Equal(4, clipCount);
        Assert.Equal(2, ownerCount);
        Assert.Equal(new[] { 0, 1, 0, 1 }, indexes);
        Assert.Equal(owners[0].SourceId, frames[0]!.SourceId);
        Assert.Equal(owners[1].SourceId, frames[1]!.SourceId);
        Assert.False(frames[0]!.Frame.RgbaPixels.IsEmpty);
        Assert.False(frames[1]!.Frame.AudioPayload.IsEmpty);
        Assert.All(frames, lease => Assert.True(decks.IsCurrentSequenceFrame(lease!)));

        foreach (var frame in frames) frame!.Dispose();
        var stopped = decks.ApplySequenceShuttle(
            new(SequenceShuttleContract.Version, reference,
                playing.StateRevision, SequenceShuttleAction.Stop),
            host, authority.StateId, authority.Revision, () => slot);
        Assert.Equal("STOPPED", stopped.State);
        Array.Clear(frames);
        Assert.False(decks.TrySelectBoundSequenceBoundary(
            reference, slot, selected, requests, ownerRequests, frames, indexes,
            out _, out var rejectedClips, out var rejectedOwners, out var stoppedFailure));
        Assert.Equal("runtime.sequence.boundary_stale", stoppedFailure?.Code);
        Assert.Equal(0, rejectedClips);
        Assert.Equal(0, rejectedOwners);
        var canceled = decks.CancelPreparedSequence(reference, host);
        Assert.Equal("CANCELED", canceled.State);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }
}
