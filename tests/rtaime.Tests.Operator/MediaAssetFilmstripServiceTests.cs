// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MediaAssetFilmstripServiceTests
{
    private static MediaAssetDescriptor Asset(
        char fingerprint = 'A', bool online = true, FrameRate? rate = null,
        MediaAssetId? id = null)
    {
        var timestamp = new UtcTimestamp(DateTimeOffset.UnixEpoch.AddDays(10));
        return new MediaAssetDescriptor(id ?? MediaAssetId.New(),
            MediaAssetOriginKind.LocalFile, @"C:\media\source.mp4", "source.mp4",
            MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.Aac,
            new VideoFormat(1920, 1080, rate ?? FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive),
            AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(30), 1024,
            new string(fingerprint, 64), timestamp, timestamp,
            online ? MediaAssetAvailability.Online : MediaAssetAvailability.Offline);
    }

    [Theory]
    [InlineData(25L, 1L)]
    [InlineData(50L, 1L)]
    [InlineData(60000L, 1001L)]
    public void Sampling_aligns_to_rational_frame_boundaries(long numerator, long denominator)
    {
        var request = MediaFilmstripRequest.FromAsset(
            Asset(rate: new FrameRate(numerator, denominator)),
            0, TimeSpan.TicksPerSecond, 100, 56, 5);
        var timestamps = request.SamplePositions();
        Assert.Equal(5, timestamps.Length);
        Assert.Equal(timestamps.Order().ToArray(), timestamps);
        Assert.All(timestamps, ticks =>
        {
            Assert.InRange(ticks, 0, TimeSpan.TicksPerSecond - 1);
            var index = decimal.Floor((decimal)ticks * numerator /
                (TimeSpan.TicksPerSecond * (decimal)denominator));
            var aligned = decimal.Round(index * TimeSpan.TicksPerSecond * denominator /
                numerator, 0, MidpointRounding.AwayFromZero);
            Assert.InRange(Math.Abs(ticks - (long)aligned), 0, 1);
        });
    }

    [Fact]
    public void Short_ranges_deduplicate_seeks_and_reject_unbounded_requests()
    {
        var asset = Asset();
        var request = MediaFilmstripRequest.FromAsset(asset, 0, 1000, 80, 45, 8,
            MediaFilmstripQuality.Compact);
        Assert.Single(request.SamplePositions());
        Assert.Throws<ArgumentException>(() => MediaFilmstripRequest.FromAsset(asset, 0,
            TimeSpan.TicksPerSecond, 160, 90, 9).Validate());
        Assert.Throws<ArgumentException>(() => MediaFilmstripRequest.FromAsset(asset, 0,
            TimeSpan.TicksPerSecond, 160, 90, 8, MediaFilmstripQuality.Compact).Validate());
        Assert.Throws<ArgumentException>(() => MediaFilmstripRequest.FromAsset(asset, 0,
            TimeSpan.FromMinutes(2).Ticks, 80, 45, 4).Validate());
    }

    [Fact]
    public async Task Concurrent_identical_requests_share_one_job_series_and_cache_frozen_images()
    {
        var asset = Asset();
        var client = new FakeCatalogAndAnalysisClient(asset);
        using var service = new MediaAssetFilmstripService(client);
        var tasks = Enumerable.Range(0, 20).Select(_ => service.RequestAsync(
            asset, 0, TimeSpan.TicksPerSecond, 80, 45, 4,
            MediaFilmstripQuality.Compact)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.Equal(MediaAnalysisState.Complete, result.State);
            Assert.Equal(4, result.CompletedCount);
            Assert.All(result.Samples, sample =>
            {
                Assert.NotNull(sample.Image);
                Assert.True(sample.Image.IsFrozen);
                Assert.Equal(sample.RequestedTicks, sample.ActualTicks);
            });
        });
        Assert.Equal(4, client.Begins);
        Assert.All(results, result => Assert.Same(results[0], result));
        var cached = await service.RequestAsync(asset, 0, TimeSpan.TicksPerSecond, 80, 45, 4,
            MediaFilmstripQuality.Compact);
        Assert.Same(results[0], cached);
        Assert.Equal(4, client.Begins);
    }

    [Fact]
    public async Task Changed_fingerprint_and_offline_catalogue_never_reuse_old_pixels()
    {
        var asset = Asset();
        var client = new FakeCatalogAndAnalysisClient(asset);
        using var service = new MediaAssetFilmstripService(client);
        Assert.Equal(MediaAnalysisState.Complete,
            (await service.RequestAsync(asset, 0, TimeSpan.TicksPerSecond, 80, 45, 2))!.State);
        var changed = Asset('B', id: asset.AssetId);
        client.Replace(changed);
        var stale = await service.RequestAsync(asset, 0, TimeSpan.TicksPerSecond, 80, 45, 2);
        Assert.Equal(MediaAnalysisState.Stale, stale!.State);
        Assert.Empty(stale.Samples);
        Assert.Equal(0, service.CacheUsage.Entries);
        Assert.Equal(MediaAnalysisState.Complete,
            (await service.RequestAsync(changed, 0, TimeSpan.TicksPerSecond, 80, 45, 2))!.State);
        client.Replace(Asset('B', online: false, id: asset.AssetId));
        var offline = await service.RequestAsync(changed, 0, TimeSpan.TicksPerSecond, 80, 45, 2);
        Assert.Equal(MediaAnalysisState.Stale, offline!.State);
        Assert.Empty(offline.Samples);
        Assert.Equal(0, service.CacheUsage.Entries);
        Assert.Equal(4, client.Begins);
    }

    [Fact]
    public async Task Many_visible_ranges_obey_LRU_entry_and_memory_quota()
    {
        var asset = Asset();
        using var service = new MediaAssetFilmstripService(new FakeCatalogAndAnalysisClient(asset));
        for (var i = 0; i < 20; i++)
        {
            var from = i * TimeSpan.TicksPerSecond / 10;
            var result = await service.RequestAsync(asset, from,
                from + TimeSpan.TicksPerSecond, 160, 90, 4);
            Assert.Equal(MediaAnalysisState.Complete, result!.State);
            Assert.InRange(service.CacheUsage.Entries, 1, MediaAssetFilmstripService.MaximumCacheEntries);
            Assert.InRange(service.CacheUsage.Bytes, 1, MediaAssetFilmstripService.MaximumCacheBytes);
        }
        Assert.Equal(MediaAssetFilmstripService.MaximumCacheEntries, service.CacheUsage.Entries);
    }

    [Fact]
    public async Task Cancellation_during_Begin_still_sends_explicit_native_cleanup()
    {
        var asset = Asset();
        var client = new FakeCatalogAndAnalysisClient(asset) { HoldBegin = true };
        using var service = new MediaAssetFilmstripService(client);
        using var stop = new CancellationTokenSource();
        var pending = service.RequestAsync(asset, 0, TimeSpan.TicksPerSecond, 80, 45, 2,
            cancellationToken: stop.Token);
        await client.BeginSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        client.ReleaseBegin();
        for (var i = 0; i < 100 && client.Cancellations == 0; i++)
            await Task.Delay(20);
        Assert.Equal(1, client.Cancellations);
        Assert.Equal(0, service.CacheUsage.Entries);
    }

    [Fact]
    public async Task Absent_frame_payload_never_becomes_a_successful_thumbnail()
    {
        var asset = Asset();
        using var service = new MediaAssetFilmstripService(
            new FakeCatalogAndAnalysisClient(asset) { WithoutPixels = true });
        var result = await service.RequestAsync(asset, 0, TimeSpan.TicksPerSecond, 80, 45, 2);
        Assert.Equal(MediaAnalysisState.Unavailable, result!.State);
        Assert.All(result.Samples, sample => Assert.Null(sample.Image));
        Assert.Equal(0, service.CacheUsage.Entries);
    }

    private sealed class FakeCatalogAndAnalysisClient : IMediaAssetCatalogClient
    {
        private MediaAssetDescriptor _asset;
        private readonly Dictionary<string, MediaAnalysisRequest> _jobs = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BeginSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldBegin { get; set; }
        public bool WithoutPixels { get; set; }
        private int _begins;
        private int _cancellations;
        public int Begins => Volatile.Read(ref _begins);
        public int Cancellations => Volatile.Read(ref _cancellations);
        public FakeCatalogAndAnalysisClient(MediaAssetDescriptor asset) => _asset = asset;
        public void Replace(MediaAssetDescriptor asset) => _asset = asset;
        public void ReleaseBegin() => _release.TrySetResult();

        public ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MediaAssetCatalogSnapshot(MediaContractVersion.Current, 1, [_asset]));

        public async ValueTask<MediaAnalysisBegin> BeginMediaAnalysisAsync(
            MediaAnalysisRequest request, CancellationToken cancellationToken = default)
        {
            request.Validate();
            Interlocked.Increment(ref _begins);
            BeginSeen.TrySetResult();
            if (HoldBegin) await _release.Task;
            var id = Guid.NewGuid().ToString("D");
            lock (_jobs) _jobs.Add(id, request);
            return new MediaAnalysisBegin(MediaAnalysisContract.Version, id, request.AssetId,
                request.ConsumerId, request.Generation, "runtime-epoch", MediaAnalysisState.Pending, "Queued");
        }

        public ValueTask<MediaAnalysisObservation> GetMediaAnalysisAsync(
            MediaAnalysisJobRequest job, CancellationToken cancellationToken = default)
        {
            MediaAnalysisRequest request;
            lock (_jobs) request = _jobs[job.JobId];
            return ValueTask.FromResult(new MediaAnalysisObservation(MediaAnalysisContract.Version,
                job.JobId, request.AssetId, request.ConsumerId, request.Generation,
                job.RuntimeEpoch, MediaAnalysisKind.Frame, MediaAnalysisState.Complete,
                request.PositionTicks, request.PositionTicks, request.Width, request.Height,
                WithoutPixels ? null : new byte[request.Width * request.Height * 4],
                0, 0, null, false, false, null, "Decoded"));
        }

        public ValueTask<MediaAnalysisObservation> CancelMediaAnalysisAsync(
            MediaAnalysisJobRequest job, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _cancellations);
            return ValueTask.FromResult(new MediaAnalysisObservation(MediaAnalysisContract.Version,
                job.JobId, job.AssetId, job.ConsumerId, job.Generation, job.RuntimeEpoch,
                MediaAnalysisKind.Frame, MediaAnalysisState.Canceled, 0, null, 0, 0,
                null, 0, 0, null, false, true, null, "Canceled"));
        }
        public ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(
            CancellationToken cancellationToken = default) => GetMediaAssetCatalogAsync(cancellationToken);
        public ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(
            IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(
            MediaAssetId id, string path, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(
            MediaAssetId id, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
    }
}
