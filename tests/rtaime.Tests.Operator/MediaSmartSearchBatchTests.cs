// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaSmartSearchBatchTests
{
    private static MediaPoolItemViewModel Item => new("clip:id", "Clips", MediaPoolItemKind.Clip, "Evening show.mp4",
        "misleading 720p", "display only", "ONLINE", "id", null, true, true, @"Z:\media\Evening show.mp4",
        TimeSpan.FromSeconds(20), new(MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8));
    [Theory]
    [InlineData("name:\"Evening show\" codec:h264 resolution:1920x1080 fps:50 ready:true availability:online type:clip", true)]
    [InlineData("evening -name:morning codec:aac", true)]
    [InlineData("fps:50/1", true)]
    [InlineData("Z:\\media", true)]
    [InlineData("resolution:1280x720", false)]
    [InlineData("codec:hevc", false)]
    [InlineData("fps:59.94", false)]
    [InlineData("-availability:online", false)]
    public void Composed_terms_use_confirmed_typed_metadata_and_invariant_semantics(string text, bool expected)
    {
        var search = MediaLibrarySearch.Parse(text); Assert.Null(search.Error); Assert.Equal(expected, search.Matches(Item));
        if (text.Contains("codec:") || text.Contains("resolution:") || text.Contains("fps:"))
            Assert.False(search.Matches(Item with { SearchMetadata = null }));
    }
    [Theory]
    [InlineData("tag:news")]
    [InlineData("recent:true")]
    [InlineData("used:today")]
    [InlineData("fps:NaN")]
    [InlineData("fps:0")]
    [InlineData("resolution:0x1080")]
    [InlineData("ready:maybe")]
    [InlineData("unknown:value")]
    [InlineData("name:\"unclosed")]
    [InlineData("codec:")]
    public void Invalid_or_unsupported_queries_fail_closed_with_actionable_feedback(string text)
    {
        var search = MediaLibrarySearch.Parse(text); Assert.NotNull(search.Error); Assert.False(search.Matches(Item));
    }
    [Fact]
    public void Large_catalogue_query_is_parsed_once_bounded_and_has_no_file_or_transport_work()
    {
        Assert.NotNull(MediaLibrarySearch.Parse(new string('x', 257)).Error);
        Assert.NotNull(MediaLibrarySearch.Parse(string.Join(" ", Enumerable.Repeat("a", 17))).Error);
        var query = MediaLibrarySearch.Parse("codec:h264 -name:absent resolution:1920x1080 fps:50 ready:true");
        var items = Enumerable.Range(0, 4096).Select(i => Item with { Key = i.ToString() }).ToArray();
        var watch = Stopwatch.StartNew();
        for (var pass = 0; pass < 100; pass++) Assert.Equal(4096, items.Count(query.Matches));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.False(MediaLibrarySearch.Parse("-codec:hevc").Matches(Item with { SearchMetadata = null }));
    }
    [Fact]
    public async Task Batch_remove_preflights_mixed_selection_reports_partial_failure_and_retries_only_failed_items()
    {
        await using var f = await Fixture.Create();
        f.Pool.UpdateSelection(f.Pool.FilteredItems.Where(i => i.Kind is MediaPoolItemKind.Source or MediaPoolItemKind.Clip).Take(4));
        // Explicitly include both catalogue records and one non-catalogue source.
        f.Pool.UpdateSelection(f.Pool.FilteredItems.Where(i => i.Kind == MediaPoolItemKind.Clip)
            .Concat(f.Pool.FilteredItems.Where(i => i.Kind == MediaPoolItemKind.Source).Take(1)));
        f.Pool.PrepareBatchRemoveCommand.Execute(null);
        Assert.Equal(0, f.Catalog.Mutations); Assert.Equal(2, f.Pool.BatchItems.Count(i => i.State == "READY"));
        Assert.Single(f.Pool.BatchItems, i => i.State == "SKIPPED");
        f.Catalog.RejectSecond = true; f.Pool.ExecuteBatchCommand.Execute(null); await Until(() => !f.Pool.BatchRunning);
        Assert.Equal(2, f.Catalog.Mutations); Assert.Single(f.Pool.BatchItems, i => i.State == "DONE");
        Assert.Single(f.Pool.BatchItems, i => i.State == "FAILED"); Assert.Equal(1, f.Catalog.MaximumActive);
        f.Catalog.RejectSecond = false; f.Pool.RetryBatchCommand.Execute(null);
        Assert.Single(f.Pool.BatchItems); Assert.Equal(2, f.Catalog.Mutations);
        f.Pool.ExecuteBatchCommand.Execute(null); await Until(() => !f.Pool.BatchRunning);
        Assert.Equal(3, f.Catalog.Mutations); Assert.Empty(f.Catalog.Snapshot.Assets);
        Assert.Equal(0, f.Transport.MutationCalls); Assert.Equal(f.Program, f.Owner.ProgramSourceId);
    }
    [Fact]
    public async Task Import_cancel_finishes_current_confirmation_and_retry_does_not_repeat_successes()
    {
        await using var f = await Fixture.Create(["one.mp4", "two.mp4", "three.mp4"]);
        f.Pool.ImportCommand.Execute(null); Assert.Equal(0, f.Catalog.Mutations);
        f.Catalog.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Pool.ExecuteBatchCommand.Execute(null); await Until(() => f.Catalog.Mutations == 1);
        f.Pool.CancelBatchCommand.Execute(null); Assert.True(f.Pool.BatchRunning);
        f.Catalog.Hold.SetResult(); await Until(() => !f.Pool.BatchRunning);
        Assert.Single(f.Pool.BatchItems, i => i.State == "DONE");
        Assert.Equal(2, f.Pool.BatchItems.Count(i => i.State == "CANCELLED"));
        Assert.Equal(1, f.Catalog.Mutations);
        f.Catalog.Hold = null; f.Pool.RetryBatchCommand.Execute(null); Assert.Equal(2, f.Pool.BatchItems.Count);
        f.Pool.ExecuteBatchCommand.Execute(null); await Until(() => !f.Pool.BatchRunning);
        Assert.Equal(3, f.Catalog.Mutations); Assert.All(f.Pool.BatchItems, i => Assert.Equal("DONE", i.State));
        Assert.Equal(1, f.Catalog.MaximumActive); Assert.Equal(0, f.Transport.MutationCalls);
    }
    [Fact]
    public async Task Stale_preflight_sends_nothing_and_unconfirmed_outcome_stops_admission_without_automatic_retry()
    {
        await using var f = await Fixture.Create(["one.mp4", "two.mp4"]);
        f.Pool.ImportCommand.Execute(null); f.Catalog.Revision++;
        f.Pool.ExecuteBatchCommand.Execute(null); await Until(() => !f.Pool.BatchRunning);
        Assert.Contains("STALE", f.Pool.BatchStatus); Assert.Equal(0, f.Catalog.Mutations);
        Assert.False(f.Pool.ExecuteBatchCommand.CanExecute(null));
        f.Pool.ImportCommand.Execute(null); f.Catalog.ThrowTimeout = true;
        f.Pool.ExecuteBatchCommand.Execute(null); await Until(() => !f.Pool.BatchRunning);
        Assert.Single(f.Pool.BatchItems, i => i.State == "UNCONFIRMED");
        Assert.Single(f.Pool.BatchItems, i => i.State == "CANCELLED");
        f.Pool.RetryBatchCommand.Execute(null); Assert.Single(f.Pool.BatchItems);
        Assert.Equal("two.mp4", f.Pool.BatchItems[0].Path); Assert.Equal(1, f.Catalog.Mutations);
    }
    [Fact]
    public async Task Oversized_import_and_stale_connection_are_disabled_without_partial_admission()
    {
        await using var f = await Fixture.Create(Enumerable.Range(0, 129).Select(i => i + ".mp4").ToArray());
        f.Pool.ImportCommand.Execute(null); Assert.Empty(f.Pool.BatchItems);
        Assert.Contains("128", f.Pool.BatchStatus); Assert.False(f.Pool.ExecuteBatchCommand.CanExecute(null));
        f.Transport.FailSnapshots = true; f.Owner.SynchronizeCommand.Execute(null); await Until(() => !f.Owner.IsBusy);
        Assert.False(f.Pool.ImportCommand.CanExecute(null)); Assert.False(f.Pool.PrepareBatchRemoveCommand.CanExecute(null));
        Assert.Equal(0, f.Catalog.Mutations);
    }
    private static async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < end) await Task.Delay(10);
        Assert.True(predicate());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public InspectorPrecisionTests.InspectorTransport Transport { get; } = new();
        public Catalog Catalog { get; } = new();
        public OperatorViewModel Owner { get; }
        public MediaDeckViewModel Deck { get; }
        public MediaPoolInspectorViewModel Pool { get; }
        public string Program { get; private set; } = "";
        private Fixture(string[] paths)
        {
            var client = new OperatorControlClient(Transport);
            Owner = new(client, runtimeReadiness: new InspectorPrecisionTests.ReadyService());
            Deck = new(new MediaDeckController(client), () => null, () => null);
            Pool = new(Owner, Deck, Catalog, () => paths);
        }
        public static async Task<Fixture> Create(string[]? paths = null)
        {
            var f = new Fixture(paths ?? []); f.Owner.SynchronizeCommand.Execute(null);
            await Until(() => f.Owner.IsConnected && !f.Owner.IsBusy); f.Program = f.Owner.ProgramSourceId; await f.Pool.LoadCatalogAsync(); return f;
        }
        public async ValueTask DisposeAsync() { Pool.Dispose(); await Deck.DisposeAsync(); await Owner.DisposeAsync(); }
    }
    private sealed class Catalog : IMediaAssetCatalogClient
    {
        public ulong Revision = 1;
        public int Mutations, MaximumActive, Active;
        public bool RejectSecond, ThrowTimeout;
        public TaskCompletionSource? Hold;
        private readonly List<MediaAssetDescriptor> _assets = [Asset(1), Asset(2)];
        public MediaAssetCatalogSnapshot Snapshot => new(MediaContractVersion.Current, Revision, _assets.ToArray());
        private static MediaAssetDescriptor Asset(int index)
        {
            var time = new UtcTimestamp(DateTimeOffset.UnixEpoch);
            return new(new MediaAssetId(Identity.Parse($"66000000-0000-0000-0000-{index:000000000000}")),
                MediaAssetOriginKind.LocalFile, index + ".mp4", index + ".mp4", MediaContainerFormat.Mp4,
                MediaVideoCodec.H264, MediaAudioCodec.Aac, VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32,
                TimeSpan.FromSeconds(5), 1024, index.ToString("x64"), time, time, MediaAssetAvailability.Online);
        }
        public ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
        public ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Snapshot);
        public async ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Mutations++; Active++; MaximumActive = Math.Max(MaximumActive, Active);
            try
            {
                if (Hold is { } pending) await pending.Task;
                if (ThrowTimeout) throw new TimeoutException("Confirmation lost");
                Revision++;
                return new(Snapshot, [new(paths.Single(), null, MediaAssetMutationDisposition.Duplicate, null)]);
            }
            finally { Active--; }
        }
        public ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(MediaAssetId id, CancellationToken cancellationToken = default)
        {
            Mutations++; Active++; MaximumActive = Math.Max(MaximumActive, Active);
            var asset = _assets.Single(a => a.AssetId == id);
            var reject = RejectSecond && asset.DisplayName == "2.mp4";
            if (!reject) { _assets.Remove(asset); Revision++; }
            Active--;
            return ValueTask.FromResult(new MediaAssetCatalogMutationResult(Snapshot,
                [new(asset.SourceLocation, id, reject ? MediaAssetMutationDisposition.Rejected : MediaAssetMutationDisposition.Removed,
                    reject ? new Failure("test.rejected", "Asset rejected") : null)]));
        }
        public ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(MediaAssetId id, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
