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

public sealed class SequenceMediaPreparationIntegrationTests
{
    private static (PreparedExecutionContract Execution, SequenceRuntimePositionMap Map,
        SequenceMediaOwnerRequest[] Owners) Request(
        LocalMediaDeckRuntimeService decks, string path, string hash, int ownerCount = 2)
    {
        var provider = decks.ProviderDescriptor;
        var route = provider.Capabilities.Single(c => c.Kind == "media.route");
        var resources = provider.Resources.Where(r => r.Kind == "media.route").ToArray();
        var owners = Enumerable.Range(0, ownerCount).Select(_ =>
            new SequenceMediaOwnerRequest(MediaSourceId.New(), MediaAssetId.New(),
                path, hash, 0, 0)).ToArray();
        var sequence = Guid.NewGuid();
        var execution = new PreparedExecutionContract(RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(1)), new Generation(1),
            owners.Select((o, i) => new PreparedExecutionBinding(o.SourceId.Value,
                route.CapabilityId, resources[i], o.SourceId, null)).ToArray());
        var map = SequenceRuntimePositionMap.Prepare(sequence, 7, FrameRate.Fps50,
            20 * 4800, 100, 1);
        return (execution, map, owners);
    }

    [Fact]
    public void Shuttle_uses_real_prepared_native_workers_and_same_Runtime_frame_clock_without_Program_output()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        var staged = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.True(staged.Prepared, staged.Failure?.Message);
        var host = Guid.NewGuid().ToString("D");
        var reference = new SequencePreparationReference(SequencePreparationContract.Version,
            map.SequenceId, 7, host, execution.PreparedExecutionId.ToString(), 1);
        var authority = execution.AuthoritySnapshot;
        ulong slot = 100;
        var initial = decks.GetSequenceShuttle(reference, host,
            authority.StateId, authority.Revision, slot);
        Assert.Equal("STOPPED", initial.State);
        Assert.Equal("UNAVAILABLE", initial.OutputState);
        Assert.True(initial.NativePrepared);
        Assert.Equal(0UL, initial.StateRevision);

        var cued = decks.ApplySequenceShuttle(new(SequenceShuttleContract.Version,
            reference, 0, SequenceShuttleAction.Cue, PositionTicks: 5 * 4800),
            host, authority.StateId, authority.Revision, () => slot);
        Assert.Equal("PAUSED", cued.State);
        Assert.Equal(5 * 4800, cued.PositionTicks);
        Assert.True(cued.NativePrepared);
        Assert.Equal("PREPARED", decks.GetPreparedSequence(reference, host).State);
        Assert.Equal(1UL, decks.GetPreparedSequence(reference, host).Generation);
        Assert.Throws<InvalidOperationException>(() => decks.ApplySequenceShuttle(new(
            SequenceShuttleContract.Version, reference, 0, SequenceShuttleAction.Play),
            host, authority.StateId, authority.Revision, () => slot));

        var stepped = decks.ApplySequenceShuttle(new(SequenceShuttleContract.Version,
            reference, cued.StateRevision, SequenceShuttleAction.StepBackward),
            host, authority.StateId, authority.Revision, () => slot);
        Assert.Equal(4 * 4800, stepped.PositionTicks);
        Assert.True(stepped.DecodeGeneration > cued.DecodeGeneration);

        var playing = decks.ApplySequenceShuttle(new(SequenceShuttleContract.Version,
            reference, stepped.StateRevision, SequenceShuttleAction.Play),
            host, authority.StateId, authority.Revision, () => slot);
        Assert.Equal("PLAYING", playing.State);
        Assert.Equal(4 * 4800, playing.PositionTicks);
        slot++;
        var observed = decks.GetSequenceShuttle(reference, host,
            authority.StateId, authority.Revision, slot);
        Assert.Equal(5 * 4800, observed.PositionTicks);
        Assert.True(observed.NativePrepared);
        Assert.Equal("UNAVAILABLE", observed.OutputState);
        Assert.Equal(2, decks.ResourceSnapshot.ReservedOwners);

        var stopped = decks.ApplySequenceShuttle(new(SequenceShuttleContract.Version,
            reference, observed.StateRevision, SequenceShuttleAction.Stop),
            host, authority.StateId, authority.Revision, () => slot);
        Assert.Equal("STOPPED", stopped.State);
        Assert.False(stopped.NativePrepared);
        Assert.Equal(2, decks.ResourceSnapshot.ReservedOwners);
        var closed = decks.CancelPreparedSequence(reference, host);
        Assert.Equal("CANCELED", closed.State);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        Assert.Throws<InvalidOperationException>(() =>
            decks.GetSequenceShuttle(reference, host, authority.StateId, authority.Revision, slot));
    }

    [Fact]
    public void Shuttle_rejects_forged_Runtime_authority_and_unloaded_preparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        Assert.True(decks.PrepareSequenceMedia(execution, map, owners).Prepared);
        var host = Guid.NewGuid().ToString("D");
        var reference = new SequencePreparationReference(SequencePreparationContract.Version,
            map.SequenceId, 7, host, execution.PreparedExecutionId.ToString(), 1);
        Assert.Throws<InvalidOperationException>(() => decks.GetSequenceShuttle(
            reference, host, Identity.New(), execution.AuthoritySnapshot.Revision, 100));
        Assert.Throws<InvalidOperationException>(() => decks.GetSequenceShuttle(
            reference with { AuthoringRevision = 8 }, host,
            execution.AuthoritySnapshot.StateId, execution.AuthoritySnapshot.Revision, 100));
        Assert.Throws<InvalidOperationException>(() => decks.GetSequenceShuttle(
            reference, Guid.NewGuid().ToString("D"),
            execution.AuthoritySnapshot.StateId, execution.AuthoritySnapshot.Revision, 100));
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Failed_native_reposition_confirmed_cleanup_can_be_observed_and_reprepared()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 1);
        var (execution, map, owners) = Request(decks, asset.Path, hash, 1);
        // Explicit internal overflow fault injection: an ordinary authorized
        // SequenceRuntimePositionMap cannot request such an out-of-range tick.
        owners[0] = owners[0] with { SourceInTicks = 1 };
        Assert.True(decks.PrepareSequenceMedia(execution, map, owners).Prepared);
        var failed = decks.RepositionSequenceMedia(map.SequenceId, 7,
            new SequenceRuntimePosition(long.MaxValue, 2, 0));
        Assert.False(failed.Prepared);
        Assert.Equal("runtime.sequence.reposition_failed", failed.Failure?.Code);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        Assert.Equal(0, decks.ResourceSnapshot.QuarantinedOwners);
        var host = Guid.NewGuid().ToString("D");
        var reference = new SequencePreparationReference(
            SequencePreparationContract.Version, map.SequenceId, 7,
            host, execution.PreparedExecutionId.ToString(), 1);
        var observed = decks.GetPreparedSequence(reference, host);
        Assert.Equal("CANCELED", observed.State);
        Assert.Equal(0, observed.DecoderOwners);
        var canceled = decks.CancelPreparedSequence(reference, host);
        Assert.Equal("CANCELED", canceled.State);
        Assert.Throws<InvalidOperationException>(() => decks.GetPreparedSequence(reference, host));
        Assert.True(decks.PrepareSequenceMedia(execution, map, owners).Prepared);
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Native_video_and_audio_clips_share_exact_prepared_decoder_owners()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, fingerprint);
        var prepared = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.True(prepared.Prepared, prepared.Failure?.Message);
        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var position));
        Assert.True(decks.RepositionSequenceMedia(map.SequenceId, 7, position).Prepared);

        var clips = new[]
        {
            new SequenceClipFrameRequest(Guid.NewGuid(), owners[0].AssetId, fingerprint,
                SequenceMediaKind.Video, 0, 0, 0),
            new SequenceClipFrameRequest(Guid.NewGuid(), owners[0].AssetId, fingerprint,
                SequenceMediaKind.Audio, 0, 0, 0),
            new SequenceClipFrameRequest(Guid.NewGuid(), owners[1].AssetId, fingerprint,
                SequenceMediaKind.Video, 0, 0, 0),
            new SequenceClipFrameRequest(Guid.NewGuid(), owners[1].AssetId, fingerprint,
                SequenceMediaKind.Audio, 0, 0, 0)
        };
        var requests = new SequenceMediaFrameRequest[2];
        var frameLeases = new SequenceMediaFrameLease?[2];
        var indices = new int[clips.Length];
        Assert.True(decks.TryTakeSequenceClipFrameSet(map.SequenceId, 7, position,
            clips, requests, frameLeases, indices, out var ownerCount, out var issue), issue?.Message);
        Assert.Equal(2, ownerCount);
        Assert.Equal(new[] { 0, 0, 1, 1 }, indices);
        Assert.Equal(owners[0].SourceId, frameLeases[indices[0]]!.Frame.Video.SourceId);
        Assert.Equal(owners[1].SourceId, frameLeases[indices[2]]!.Frame.Video.SourceId);
        Assert.False(frameLeases[indices[0]]!.Frame.RgbaPixels.IsEmpty);
        Assert.False(frameLeases[indices[1]]!.Frame.AudioPayload.IsEmpty);
        Assert.False(frameLeases[indices[3]]!.Frame.AudioPayload.IsEmpty);
        Assert.All(frameLeases, frame => Assert.True(decks.IsCurrentSequenceFrame(frame!)));

        foreach (var lease in frameLeases) lease!.Dispose();
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Clip_selection_rejects_unbound_or_forged_source_before_consuming_decoder_frames()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, fingerprint);
        Assert.True(decks.PrepareSequenceMedia(execution, map, owners).Prepared);
        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var position));
        Assert.True(decks.RepositionSequenceMedia(map.SequenceId, 7, position).Prepared);
        var original = new SequenceClipFrameRequest(Guid.NewGuid(), owners[0].AssetId,
            fingerprint, SequenceMediaKind.Video, 0, 0, 0);
        var requests = new SequenceMediaFrameRequest[2];
        var leases = new SequenceMediaFrameLease?[2];
        var indexes = new int[2];

        Assert.False(decks.TryTakeSequenceClipFrameSet(map.SequenceId, 7, position,
            new[] { original with { FingerprintSha256 = new string('F', 64) } },
            requests, leases, indexes, out var rejectedCount, out var mismatch));
        Assert.Equal("runtime.sequence.clip_selection_unbound", mismatch?.Code);
        Assert.Equal(0, rejectedCount);
        Assert.All(leases, frame => Assert.Null(frame));

        Assert.False(decks.TryTakeSequenceClipFrameSet(map.SequenceId, 7, position,
            new[] { original, original }, requests, leases, indexes,
            out rejectedCount, out var duplicate));
        Assert.Equal("runtime.sequence.clip_selection_duplicate", duplicate?.Code);
        Assert.All(leases, frame => Assert.Null(frame));

        Assert.True(decks.TryTakeSequenceClipFrameSet(map.SequenceId, 7, position,
            new[] { original }, requests, leases, indexes,
            out var ownerCount, out var ready), ready?.Message);
        Assert.Equal(1, ownerCount);
        Assert.Equal(0, indexes[0]);
        Assert.True(decks.IsCurrentSequenceFrame(leases[0]!));
        leases[0]!.Dispose();
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Native_two_source_preroll_uses_one_ledger_and_selects_only_matching_generation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        var ready = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.True(ready.Prepared, ready.Failure?.Message);
        Assert.Equal(2, ready.DecoderOwners);
        Assert.Equal(2, decks.ResourceSnapshot.ReservedOwners);
        Assert.Equal(0, decks.ResourceSnapshot.QuarantinedOwners);

        // The first explicit Play changes the plan generation. No previous
        // decoder sample may be admitted until off-boundary repreroll completes.
        Assert.False(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[0].SourceId,
            new SequenceRuntimePosition(0, 2, 0), 0, out var stale, out var mismatch));
        Assert.Null(stale);
        Assert.Equal("runtime.sequence.stale", mismatch?.Code);
        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var position));
        var reprerolled = decks.RepositionSequenceMedia(map.SequenceId, 7, position);
        Assert.True(reprerolled.Prepared, reprerolled.Failure?.Message);

        Assert.True(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[0].SourceId,
            position, 0, out var first, out var issue0), issue0?.Message);
        Assert.NotNull(first);
        Assert.True(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[1].SourceId,
            position, 0, out var second, out var issue1), issue1?.Message);
        Assert.NotNull(second);
        Assert.Equal(owners[0].SourceId, first.Frame.Video.SourceId);
        Assert.Equal(owners[1].SourceId, second.Frame.Video.SourceId);
        Assert.Equal(first.Frame.Video.Timing.PresentationTimestamp,
            first.Frame.Audio!.Timing.PresentationTimestamp);
        Assert.False(first.Frame.RgbaPixels.IsEmpty);
        Assert.False(second.Frame.RgbaPixels.IsEmpty);
        Assert.False(first.Frame.AudioPayload.IsEmpty);
        Assert.False(second.Frame.AudioPayload.IsEmpty);
        Assert.True(decks.IsCurrentSequenceFrame(first));
        Assert.True(decks.IsCurrentSequenceFrame(second));

        Assert.False(decks.TryTakeSequenceMedia(map.SequenceId, 8, owners[0].SourceId,
            position, 0, out _, out var revisionFault));
        Assert.Equal("runtime.sequence.stale", revisionFault?.Code);
        Assert.False(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[0].SourceId,
            position, 4800, out _, out var sourceFault));
        Assert.Equal("runtime.sequence.source_mismatch", sourceFault?.Code);

        var sought = playing.Seek(101, 4800);
        Assert.True(sought.TryMap(101, out var advanced));
        var updated = decks.RepositionSequenceMedia(map.SequenceId, 7, advanced);
        Assert.True(updated.Prepared, updated.Failure?.Message);
        Assert.False(decks.IsCurrentSequenceFrame(first));
        Assert.False(decks.IsCurrentSequenceFrame(second));
        Assert.False(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[0].SourceId,
            position, 0, out _, out var retired));
        Assert.Equal("runtime.sequence.stale", retired?.Code);
        Assert.True(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[0].SourceId,
            advanced, 4800, out var newFrame, out var afterSeek), afterSeek?.Message);
        Assert.True(decks.IsCurrentSequenceFrame(newFrame!));
        newFrame!.Dispose();

        // Shutdown releases joined native workers, but the caller's retained
        // readback leases keep their true old pool charges until Dispose.
        decks.CloseSequenceMedia();
        Assert.False(decks.IsCurrentSequenceFrame(first));
        Assert.Equal(2, decks.ResourceSnapshot.RetainedPoolOwners);
        first.Dispose();
        Assert.Equal(1, decks.ResourceSnapshot.ReservedOwners);
        second.Dispose();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Large_boundary_jump_rejects_stale_native_sequence_samples()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, fingerprint);
        var ready = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.True(ready.Prepared, ready.Failure?.Message);

        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var start));
        var reprerolled = decks.RepositionSequenceMedia(map.SequenceId, 7, start);
        Assert.True(reprerolled.Prepared, reprerolled.Failure?.Message);

        // The native worker has at most eight queued 50-fps frames. Skipping
        // directly to frame 19 must not present a frame from that older queue.
        Assert.True(playing.TryMap(119, out var jumped));
        Assert.Equal(19 * 4800L, jumped.Tick);
        Assert.False(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[0].SourceId,
            jumped, jumped.Tick, out var stale, out var issue));
        Assert.Null(stale);
        Assert.Equal("runtime.sequence.stale_sample", issue?.Code);
        Assert.Equal(2, decks.ResourceSnapshot.ReservedOwners);

        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Native_frame_set_is_all_or_none_when_one_prepared_owner_has_no_due_frame()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, fingerprint);
        var prepared = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.True(prepared.Prepared, prepared.Failure?.Message);

        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var position));
        var preroll = decks.RepositionSequenceMedia(map.SequenceId, 7, position);
        Assert.True(preroll.Prepared, preroll.Failure?.Message);

        var requests = new[]
        {
            new SequenceMediaFrameRequest(owners[0].SourceId, 0),
            new SequenceMediaFrameRequest(owners[1].SourceId, 0)
        };
        var selected = new SequenceMediaFrameLease?[2];
        Assert.True(decks.TryTakeSequenceFrameSet(map.SequenceId, 7, position,
            requests, selected, out var failure), failure?.Message);
        Assert.All(selected, lease => Assert.NotNull(lease));
        Assert.Equal(owners[0].SourceId, selected[0]!.Frame.Video.SourceId);
        Assert.Equal(owners[1].SourceId, selected[1]!.Frame.Video.SourceId);
        Assert.False(selected[0]!.Frame.RgbaPixels.IsEmpty);
        Assert.False(selected[1]!.Frame.RgbaPixels.IsEmpty);
        Assert.False(selected[0]!.Frame.AudioPayload.IsEmpty);
        Assert.False(selected[1]!.Frame.AudioPayload.IsEmpty);
        Assert.True(decks.IsCurrentSequenceFrame(selected[0]!));
        Assert.True(decks.IsCurrentSequenceFrame(selected[1]!));
        Assert.Equal(2, decks.ResourceSnapshot.ReservedOwners);
        selected[0]!.Dispose();
        selected[1]!.Dispose();

        var sought = playing.Seek(101, 4800);
        Assert.True(sought.TryMap(101, out var nextPosition));
        var repositioned = decks.RepositionSequenceMedia(map.SequenceId, 7, nextPosition);
        Assert.True(repositioned.Prepared, repositioned.Failure?.Message);

        // Pre-consume the second owner's only frame due at tick 4800.
        // The first source can still supply a frame, but it must be
        // released if the second source cannot join that same frame set.
        Assert.True(decks.TryTakeSequenceMedia(map.SequenceId, 7,
            owners[1].SourceId, nextPosition, 4800, out var consumed, out var dueIssue),
            dueIssue?.Message);
        consumed!.Dispose();
        var partial = new SequenceMediaFrameLease?[2];
        var later = new[]
        {
            new SequenceMediaFrameRequest(owners[0].SourceId, 4800),
            new SequenceMediaFrameRequest(owners[1].SourceId, 4800)
        };
        Assert.False(decks.TryTakeSequenceFrameSet(map.SequenceId, 7, nextPosition,
            later, partial, out var notReady));
        Assert.Equal("runtime.sequence.buffering", notReady?.Code);
        Assert.All(partial, lease => Assert.Null(lease));
        Assert.Equal(2, decks.ResourceSnapshot.ReservedOwners);
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Native_frame_set_rejects_duplicate_and_unbound_sources_before_consumption()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, fingerprint);
        var prepared = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.True(prepared.Prepared, prepared.Failure?.Message);
        var playing = map.Play(100);
        Assert.True(playing.TryMap(100, out var position));
        var preroll = decks.RepositionSequenceMedia(map.SequenceId, 7, position);
        Assert.True(preroll.Prepared, preroll.Failure?.Message);

        var slots = new SequenceMediaFrameLease?[2];
        var duplicate = new[]
        {
            new SequenceMediaFrameRequest(owners[0].SourceId, 0),
            new SequenceMediaFrameRequest(owners[0].SourceId, 0)
        };
        Assert.False(decks.TryTakeSequenceFrameSet(map.SequenceId, 7, position,
            duplicate, slots, out var duplicated));
        Assert.Equal("runtime.sequence.frame_set_duplicate", duplicated?.Code);
        Assert.All(slots, lease => Assert.Null(lease));

        var invalidOffset = new[]
        {
            new SequenceMediaFrameRequest(owners[0].SourceId, 0),
            new SequenceMediaFrameRequest(owners[1].SourceId, 4800)
        };
        Assert.False(decks.TryTakeSequenceFrameSet(map.SequenceId, 7, position,
            invalidOffset, slots, out var offsetError));
        Assert.Equal("runtime.sequence.source_mismatch", offsetError?.Code);
        Assert.All(slots, lease => Assert.Null(lease));

        var valid = new[]
        {
            new SequenceMediaFrameRequest(owners[0].SourceId, 0),
            new SequenceMediaFrameRequest(owners[1].SourceId, 0)
        };
        Assert.True(decks.TryTakeSequenceFrameSet(map.SequenceId, 7, position,
            valid, slots, out var readyError), readyError?.Message);
        slots[0]!.Dispose();
        slots[1]!.Dispose();
        decks.CloseSequenceMedia();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Tampered_second_source_rolls_back_all_native_owners_without_publishing_a_session()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        owners[1] = owners[1] with { FingerprintSha256 = new string('F', 64) };
        var denied = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.False(denied.Prepared);
        Assert.Equal("runtime.sequence.prepare_failed", denied.Failure?.Code);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        Assert.Equal(0, decks.ResourceSnapshot.QuarantinedOwners);
        Assert.False(decks.TryTakeSequenceMedia(map.SequenceId, 7, owners[0].SourceId,
            new SequenceRuntimePosition(0, 1, 0), 0, out _, out var fault));
        Assert.Equal("runtime.sequence.stale", fault?.Code);
    }

    [Fact]
    public void Cancelled_sequence_staging_returns_all_preopened_native_reservations()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var refused = decks.PrepareSequenceMedia(execution, map, owners, cancelled.Token);
        Assert.False(refused.Prepared);
        Assert.Equal("runtime.sequence.prepare_failed", refused.Failure?.Code);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        Assert.Equal(0, decks.ResourceSnapshot.QuarantinedOwners);
    }

    [Fact]
    public void Unbound_and_overbudget_sequence_sources_fail_before_native_open()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 1);
        var (execution, map, owners) = Request(decks, asset.Path, hash);
        var denied = decks.PrepareSequenceMedia(execution, map, owners);
        Assert.False(denied.Prepared);
        Assert.Equal("runtime.sequence.sources", denied.Failure?.Code);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);

        var single = owners.Take(1).ToArray();
        var unbound = single[0] with { SourceId = MediaSourceId.New() };
        denied = decks.PrepareSequenceMedia(execution, map, [unbound]);
        Assert.False(denied.Prepared);
        Assert.Equal("runtime.sequence.binding", denied.Failure?.Code);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }
}
