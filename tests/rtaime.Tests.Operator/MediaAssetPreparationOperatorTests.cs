// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;
namespace rtaime.Tests.Operator;
public sealed class MediaAssetPreparationOperatorTests
{
    [Fact]
    public async Task Batch_failure_rolls_back_partial_observations_and_never_calls_production_transport()
    {
        var forbidden = new ForbiddenTransport();
        var client = new OperatorControlClient(forbidden);
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        var catalog = new Catalog { FailSecond = true };
        using var pool = new MediaPoolInspectorViewModel(owner, deck, catalog);
        owner.SynchronizeCommand.Execute(null);
        await Until(() => owner.IsConnected && !owner.IsBusy);
        await pool.LoadCatalogAsync();
        pool.SelectedCategory = "Clips";
        Assert.All(pool.FilteredItems, item => { Assert.False(item.IsReady); Assert.Contains("UNVERIFIED", item.ReadinessLabel); });
        pool.UpdateSelection(pool.FilteredItems.Take(2));
        pool.ShowPreparation();
        var program = owner.ProgramSourceId;
        var preview = owner.PreviewSourceId;
        await pool.PrepareSelectedAssetsAsync();
        Assert.Equal(2, catalog.Begins);
        Assert.DoesNotContain(pool.PreflightEvidence, value => value.RepresentativeVerificationPassed);
        Assert.Contains("rolled back", pool.PreparationStatus);
        Assert.Equal(program, owner.ProgramSourceId);
        Assert.Equal(preview, owner.PreviewSourceId);
        Assert.Equal(0, forbidden.Calls);
        Assert.Equal(0, catalog.Mutations);
        Assert.True(pool.PrepareAssetsCommand.CanExecute(null));
        catalog.FailSecond = false;
        await pool.PrepareSelectedAssetsAsync();
        Assert.All(pool.PreflightEvidence, value => Assert.True(value.RepresentativeVerificationPassed));
        Assert.All(pool.FilteredItems, item => { Assert.False(item.IsReady); Assert.Contains("CONDITIONALLY_READY", item.ReadinessLabel); });
        owner.SynchronizeCommand.Execute(null);
        await Until(() => owner.IsConnected && !owner.IsBusy);
        await pool.LoadCatalogAsync();
        pool.SelectedCategory = "Clips";
        Assert.DoesNotContain(pool.PreflightEvidence, value => value.RepresentativeVerificationPassed);
    }
    [Fact]
    public async Task Cancellation_waits_for_admitted_job_identity_then_cancels_it_without_abandoning_owner()
    {
        var catalog = new Catalog { BlockBegin = true };
        var client = new OperatorControlClient(new ForbiddenTransport());
        await using var owner = new OperatorViewModel(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
        await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
        using var pool = new MediaPoolInspectorViewModel(owner, deck, catalog);
        owner.SynchronizeCommand.Execute(null);
        await Until(() => owner.IsConnected && !owner.IsBusy);
        await pool.LoadCatalogAsync();
        pool.SelectedCategory = "Clips";
        pool.UpdateSelection(pool.FilteredItems.Take(1));
        var pending = pool.PrepareSelectedAssetsAsync();
        Assert.Equal(1, catalog.Begins);
        pool.CancelPreparationCommand.Execute(null);
        catalog.BeginRelease.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(catalog.Cancels >= 1);
        Assert.False(pool.IsPreparing);
        Assert.DoesNotContain(pool.PreflightEvidence, value => value.RepresentativeVerificationPassed);
        Assert.Equal(0, catalog.Mutations);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class Catalog : IMediaAssetCatalogClient
    {
        public int Begins, Cancels, Mutations;
        public bool FailSecond, BlockBegin;
        public TaskCompletionSource BeginRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MediaAssetCatalogSnapshot Snapshot { get; } = new(MediaContractVersion.Current, 1,
            Enumerable.Range(0, 2).Select(index => {
                var time = new UtcTimestamp(DateTimeOffset.UtcNow);
                return new MediaAssetDescriptor(MediaAssetId.New(), MediaAssetOriginKind.LocalFile,
                    @"C:\media\clip" + index + ".mp4", "Clip " + index, MediaContainerFormat.Mp4,
                    MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8,
                    AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(1), 1024, new string((char)('A' + index), 64), time, time, MediaAssetAvailability.Online);
            }).ToArray());
        public ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
        public ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
        public async ValueTask<MediaAssetPreflightObservation> BeginMediaAssetPreflightAsync(MediaAssetId id, MediaAssetPreflightOptions options, CancellationToken cancellationToken = default)
        {
            Begins++;
            var asset = Snapshot.Assets.Single(item => item.AssetId == id);
            if (BlockBegin) await BeginRelease.Task;
            return new(Guid.NewGuid().ToString("N"), id.ToString(), Guid.NewGuid().ToString("N"), asset.SourceLocation,
                asset.FingerprintSha256, 1024, 1, "1080p50", new string('B', 64),
                FailSecond && Begins == 2 ? MediaAssetReadinessState.Unavailable : MediaAssetReadinessState.ConditionallyReady,
                BlockBegin, false, FailSecond && Begins == 2 ? "Decoder failed" : "Representative check passed",
                6, 3, true, DateTimeOffset.UtcNow);
        }
        public ValueTask<MediaAssetPreflightObservation> CancelMediaAssetPreflightAsync(string id, CancellationToken cancellationToken = default)
        {
            Cancels++;
            return ValueTask.FromResult(new MediaAssetPreflightObservation(id, Snapshot.Assets[0].AssetId.ToString(), Guid.NewGuid().ToString("N"),
                Snapshot.Assets[0].SourceLocation, new string('A', 64), 0, 0, "1080p50", "", MediaAssetReadinessState.Unverified,
                false, true, "Cancelled", 0, 0, true, DateTimeOffset.UtcNow));
        }
        public ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
        public ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(MediaAssetId id, string path, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
        public ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(MediaAssetId id, CancellationToken cancellationToken = default) { Mutations++; throw new NotSupportedException(); }
    }
    private sealed class ForbiddenTransport : IOperatorControlTransport
    {
        public int Calls;
        private ValueTask<T> Forbidden<T>() { Calls++; return ValueTask.FromException<T>(new InvalidOperationException("Preparation must not mutate production.")); }
        private readonly ProductionSourceId _source = ProductionSourceId.New();
        private readonly ProductionId _production = ProductionId.New();
        public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new OperatorStatusSnapshot(new(ControlContractVersion.Current, _production, new Revision(1), new(_source, _source)),
                [new(_source.ToString(), "Camera")], "READY", "READY", "READY", "UNKNOWN", "IDLE", false, 0));
        public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) => Forbidden<OperatorMutationResponse>();
        public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) => Forbidden<OperatorMutationResponse>();
        public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) => Forbidden<OperatorMutationResponse>();
    }
}
