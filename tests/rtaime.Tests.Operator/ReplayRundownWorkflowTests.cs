// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class ReplayRundownWorkflowTests
{
    private static readonly string Fingerprint = new('A', 64);

    private static MediaAssetDescriptor Asset(MediaAssetAvailability availability = MediaAssetAvailability.Online)
    {
        var now = new UtcTimestamp(DateTimeOffset.UtcNow);
        return new MediaAssetDescriptor(MediaAssetId.New(), MediaAssetOriginKind.LocalFile,
            @"C:\replay\clips\highlight.mp4", "Highlight.mp4", MediaContainerFormat.Mp4,
            MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8,
            AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(3), 1024,
            Fingerprint, now, now, availability);
    }

    private static ReplayClipAssetResult Clip(MediaAssetDescriptor asset) =>
        new(ReplayControlContractVersion.Current, true, Guid.NewGuid().ToString("D"),
            asset.AssetId.ToString(), asset.SourceLocation,
            TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(45), asset.FingerprintSha256, null);

    [Fact]
    public void Only_finalized_online_exactly_fingerprinted_replay_qualifies_for_a_rundown()
    {
        var asset = Asset();
        var clip = Clip(asset);
        Assert.True(ReplayRundownEligibility.TryVerifyAsset(clip, asset, out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip with { Sha256 = new string('B', 64) }, asset, out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip with { SourceLocation = @"C:\unrelated.mp4" }, asset, out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip with { SourceOut = clip.SourceIn }, asset, out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip with { Succeeded = false }, asset, out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip, Asset(MediaAssetAvailability.Missing), out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip, asset with { Duration = TimeSpan.FromSeconds(1) }, out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip, asset with { AudioCodec = MediaAudioCodec.None }, out _));
        Assert.False(ReplayRundownEligibility.TryVerifyAsset(
            clip, null, out _));
    }

    [Fact]
    public async Task Replay_selection_advertises_candidate_not_ready_when_gap_storage_or_range_is_stale()
    {
        var client = new OperatorControlClient(new UnavailableOperatorControlTransport());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var replay = new ReplayViewModel(client, deck);
        var marked = ReplayControlSnapshot.Unavailable with
        {
            CaptureState = ReplayControlCaptureState.Degraded,
            ClipState = ReplayControlClipState.Marked,
            Retention = TimeSpan.FromMinutes(1),
            RetainedDuration = TimeSpan.FromSeconds(14),
            MaximumStorageBytes = 1024 * 1024,
            RetainedBytes = 8000,
            RetainedSegmentCount = 4,
            FinalizedSegments = 4,
            MarkIn = TimeSpan.FromSeconds(10),
            MarkOut = TimeSpan.FromSeconds(13),
            DroppedSamples = 2,
            Discontinuities = 1,
            Failure = null
        };
        replay.ApplyConfirmedSnapshot(marked);
        Assert.True(replay.CanCreateClip);
        Assert.Contains("capture gaps", replay.ReplayRangeEligibility);
        Assert.False(replay.CanOpenClip);
        Assert.False(replay.CanQueueInRundown);
        replay.ApplyConfirmedSnapshot(marked with { MarkOut = TimeSpan.FromSeconds(25) });
        Assert.False(replay.CanCreateClip);
        Assert.Contains("INVALID", replay.ReplayRangeEligibility);
        replay.ApplyConfirmedSnapshot(marked with { ClipState = ReplayControlClipState.Finalizing });
        Assert.False(replay.CanCreateClip);
        Assert.False(replay.CanMark);
        Assert.Contains("FINALIZING", replay.ReplayRangeEligibility);
        replay.ApplyConfirmedSnapshot(marked with { Failure = new Failure(
            "replay.storage.cleanup_pending", "Unreleased physical bytes still count.") });
        Assert.False(replay.CanCreateClip);
        Assert.Contains("STORAGE", replay.ReplayRangeEligibility);
    }

    [Fact]
    public async Task Verified_replay_queue_is_one_manual_draft_and_never_saves_or_executes()
    {
        var transport = new ObservingTransport();
        var client = new OperatorControlClient(transport);
        await using var owner = new OperatorViewModel(client);
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck);
        var source = ProductionSourceId.New();
        var authority = new AuthoritativeProductionState(ControlContractVersion.Current,
            ProductionId.New(), new Revision(1), new ProductionRoutingState(source, source));
        owner.ApplyConfirmedSnapshot(new OperatorStatusSnapshot(
            authority, [new OperatorSourceDescriptor(source.ToString(), "Replay deck A")],
            "READY", "LOCKED", "HEALTHY", "OFF", "IDLE", false, 0.2));
        var rundown = new RundownViewModel(client, owner, pool);
        var hold = new RundownHoldItem(RundownItemId.New(), "Opener", 25);
        var definition = new RundownDefinition(RundownContractVersion.Current,
            RundownId.New(), "Live", [hold]);
        rundown.ApplyConfirmedObservation(new RundownWorkspaceSnapshot(
            definition, RundownExecutionSnapshot.Idle, 5));
        var asset = Asset();
        var clip = Clip(asset);

        Assert.True(rundown.TryStageReplayClip(clip, asset, source, "Replay highlight"));
        Assert.Equal(2, rundown.Items.Count);
        var queued = Assert.IsType<RundownMediaItem>(rundown.SelectedItem!.Item);
        Assert.Equal(asset.AssetId.ToString(), queued.AssetId.ToString());
        Assert.Equal(source, queued.SourceId);
        Assert.Equal(RundownTransitionKind.Cut, queued.Transition!.Kind);
        Assert.Equal(RundownFollowActionKind.Manual, queued.FollowAction.Kind);
        Assert.Equal(RundownRepeatMode.None, queued.Repeat.Mode);
        Assert.Contains("UNSAVED DRAFT", rundown.StorageLabel);
        Assert.False(rundown.PrepareCommand.CanExecute(null));
        Assert.False(rundown.GoCommand.CanExecute(null));
        Assert.False(rundown.TryStageReplayClip(clip, asset, source, "Replay highlight"));
        Assert.Equal(2, rundown.Items.Count);
        Assert.Equal(0, transport.Mutations);

        rundown.UndoDraftCommand.Execute(null);
        Assert.Single(rundown.Items);
        Assert.Equal(0, transport.Mutations);

        Assert.True(rundown.TryStageReplayClip(clip, asset, source, "Replay highlight"));
        rundown.ApplyConfirmedObservation(new RundownWorkspaceSnapshot(
            definition, RundownExecutionSnapshot.Idle with { Revision = 6 }, 6));
        Assert.Contains("CONFLICT", rundown.StorageLabel);
        Assert.False(rundown.TryStageReplayClip(clip with { ClipId = Guid.NewGuid().ToString("D") },
            asset, source, "Other"));
        Assert.False(rundown.SaveCommand.CanExecute(null));
        Assert.Equal(0, transport.Mutations);
    }

    private sealed class ObservingTransport : IOperatorControlTransport
    {
        public int Mutations { get; private set; }

        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<OperatorStatusSnapshot>(new NotSupportedException());

        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
            SelectPreviewCommand command, CancellationToken cancellationToken = default)
        {
            Mutations++;
            return ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
        }

        public ValueTask<OperatorMutationResponse> CutProgramAsync(
            CutProgramCommand command, CancellationToken cancellationToken = default)
        {
            Mutations++;
            return ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
        }

        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
            DissolveProgramCommand command, CancellationToken cancellationToken = default)
        {
            Mutations++;
            return ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
        }
    }
}
