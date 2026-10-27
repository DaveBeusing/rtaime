// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Security.Cryptography;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class IndependentMediaAnalysisIntegrationTests
{
    private static (MediaAnalysisFileIdentity Identity, string Sha) IdentityFor(string path)
    {
        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return (new MediaAnalysisFileIdentity(hash, info.Length, info.LastWriteTimeUtc.Ticks), hash);
    }

    private static MediaAnalysisRequest Frame(MediaAssetId asset, long generation,
        long ticks = 0, string consumer = "library-scrub") =>
        new(MediaAnalysisContract.Version, asset.ToString(), consumer, generation,
            MediaAnalysisKind.Frame, ticks, 0, 160, 90, 0, 0);

    private static MediaAnalysisRequest Waveform(MediaAssetId asset, long generation) =>
        new(MediaAnalysisContract.Version, asset.ToString(), "timeline-waveform", generation,
            MediaAnalysisKind.Waveform, 0, TimeSpan.TicksPerSecond, 0, 0, 1, 64);

    private static async Task<MediaAnalysisObservation> Completed(
        LocalMediaDeckRuntimeService runtime, MediaAnalysisBegin begin, int seconds = 60)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var observation = runtime.GetAnalysis(begin.JobId);
            observation.Validate();
            if (observation.State is not (MediaAnalysisState.Pending or MediaAnalysisState.Running))
                return observation;
            await Task.Delay(50);
        }
        throw new TimeoutException("The bounded real media analysis failed to complete.");
    }

    [Fact]
    public async Task Reference_media_produces_actual_pts_frames_and_decoded_channel_waveform_without_touching_program()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        using var runtime = new LocalMediaDeckRuntimeService();
        var mediaId = MediaAssetId.New();
        var identity = IdentityFor(asset.Path).Identity;

        var frameJob = runtime.BeginAnalysis(Frame(mediaId, 1, TimeSpan.FromMilliseconds(300).Ticks),
            identity, asset.Path);
        Assert.NotNull(frameJob.ObservationToken);
        var frame = await Completed(runtime, frameJob);
        Assert.Equal(MediaAnalysisState.Complete, frame.State);
        Assert.NotNull(frame.ActualTicks);
        Assert.True(frame.ActualTicks >= frame.RequestedTicks);
        Assert.Equal(160 * 90 * 4, frame.RgbaPixels!.Length);
        Assert.True(runtime.TryReadAnalysisFrame(frameJob.JobId,
            Convert.FromHexString(frameJob.ObservationToken!), out var width, out var height, out var pixels));
        Assert.Equal(160, width);
        Assert.Equal(90, height);
        Assert.Equal(frame.RgbaPixels, pixels);
        Assert.False(runtime.TryReadAnalysisFrame(frameJob.JobId, new byte[32],
            out _, out _, out _));

        var audioJob = runtime.BeginAnalysis(Waveform(mediaId, 1), identity, asset.Path);
        var waveform = await Completed(runtime, audioJob);
        Assert.Contains(waveform.State, new[] { MediaAnalysisState.Complete, MediaAnalysisState.Partial });
        Assert.Equal(64, waveform.Waveform!.Length);
        Assert.Equal(2, waveform.ChannelCount);
        Assert.Equal(48000, waveform.SampleRate);
        Assert.True(waveform.Waveform.Any(bucket => bucket.HasSamples));
        Assert.Null(runtime.Snapshot.Probe);
        Assert.Equal(0, runtime.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public async Task Thousand_rapid_positions_are_latest_wins_and_all_pending_requests_remain_bounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        using var runtime = new LocalMediaDeckRuntimeService();
        var mediaId = MediaAssetId.New();
        var identity = IdentityFor(asset.Path).Identity;
        MediaAnalysisBegin? latest = null;
        for (var i = 0; i < 1000; i++)
        {
            latest = runtime.BeginAnalysis(Frame(mediaId, i + 1, (i % 10) * 100_000L),
                identity, asset.Path);
            Assert.Contains(latest.State, new[] { MediaAnalysisState.Running, MediaAnalysisState.Pending });
            Assert.InRange(runtime.ResourceSnapshot.ReservedOwners, 0, 1);
        }
        var completed = await Completed(runtime, latest!, seconds: 75);
        Assert.Equal(1000, completed.Generation);
        Assert.Equal(latest!.JobId, completed.JobId);
        Assert.NotEqual(MediaAnalysisState.Stale, completed.State);
        Assert.InRange(runtime.ResourceSnapshot.ReservedOwners, 0, 1);
    }

    [Fact]
    public async Task File_change_and_host_restart_invalidate_analysis_identity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var mediaId = MediaAssetId.New();
        var file = IdentityFor(asset.Path).Identity;
        using var runtime = new LocalMediaDeckRuntimeService();
        var job = runtime.BeginAnalysis(Frame(mediaId, 1), file, asset.Path);
        var before = await Completed(runtime, job);
        Assert.Equal(MediaAnalysisState.Complete, before.State);
        File.AppendAllText(asset.Path, "changed");
        var afterJob = runtime.BeginAnalysis(Frame(mediaId, 2), file, asset.Path);
        var after = await Completed(runtime, afterJob);
        Assert.Equal(MediaAnalysisState.Stale, after.State);
        Assert.Null(after.RgbaPixels);
        using var other = new LocalMediaDeckRuntimeService();
        Assert.Throws<InvalidOperationException>(() => other.GetAnalysis(job.JobId));
    }

    [Fact]
    public async Task Replacing_a_completed_frame_revokes_the_prior_observation_token()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        using var runtime = new LocalMediaDeckRuntimeService();
        var mediaId = MediaAssetId.New();
        var identity = IdentityFor(asset.Path).Identity;
        var first = runtime.BeginAnalysis(Frame(mediaId, 1), identity, asset.Path);
        var completed = await Completed(runtime, first);
        Assert.Equal(MediaAnalysisState.Complete, completed.State);
        Assert.True(runtime.TryReadAnalysisFrame(first.JobId,
            Convert.FromHexString(first.ObservationToken!), out _, out _, out _));

        var next = runtime.BeginAnalysis(Frame(mediaId, 2, TimeSpan.FromMilliseconds(300).Ticks),
            identity, asset.Path);
        Assert.NotEqual(first.JobId, next.JobId);
        Assert.False(runtime.TryReadAnalysisFrame(first.JobId,
            Convert.FromHexString(first.ObservationToken!), out _, out _, out _));
        var current = await Completed(runtime, next);
        Assert.Equal(2, current.Generation);
        Assert.Equal(MediaAnalysisState.Complete, current.State);
    }


    [Fact]
    public void Disk_cache_limits_complete_manifests_and_blobs_under_concurrent_requests()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rtaime-cache-bounded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cache = new MediaAnalysisDerivedCache(folder);
            var request = Frame(MediaAssetId.New(), 1);
            var result = new MediaAnalysisObservation(MediaAnalysisContract.Version,
                Guid.NewGuid().ToString("D"), request.AssetId, request.ConsumerId,
                request.Generation, Guid.NewGuid().ToString("N"), request.Kind,
                MediaAnalysisState.Complete, 0, 0, 160, 90,
                new byte[160 * 90 * 4], 0, 0, null, false, false, null, "Decoded.");
            Parallel.For(0, 90, index => cache.Write("fingerprint-A:frame:" + index, result));
            var manifests = Directory.GetFiles(folder, "*.json");
            Assert.InRange(manifests.Length, 1, MediaAnalysisDerivedCache.MaximumEntries);
            Assert.True(Directory.GetFiles(folder).Sum(file => new FileInfo(file).Length) <=
                MediaAnalysisDerivedCache.MaximumBytes);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            // An unchanged catalogue fingerprint may reuse the newest verified observation.
            Assert.NotNull(cache.Read("fingerprint-A:frame:89", request with { Width = 160, Height = 90 }));
            Assert.Null(cache.Read("fingerprint-B:frame:89", request with { Width = 160, Height = 90 }));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Cached_waveform_needs_exact_requested_range_and_finite_bucket_values()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rtaime-waveform-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var request = Waveform(MediaAssetId.New(), 1) with { EndTicks = 1_000_000, BucketCount = 2 };
            var buckets = new[]
            {
                new MediaAnalysisWaveformBucket(0, 500_000, 0, 2400, true, false,
                    [-0.5f, -0.25f], [0.5f, 0.25f], [0.25f, 0.125f]),
                new MediaAnalysisWaveformBucket(500_000, 1_000_000, 2400, 2400, true, false,
                    [-0.5f, -0.25f], [0.5f, 0.25f], [0.25f, 0.125f])
            };
            var result = new MediaAnalysisObservation(MediaAnalysisContract.Version,
                Guid.NewGuid().ToString("D"), request.AssetId, request.ConsumerId,
                request.Generation, "runtime-epoch", MediaAnalysisKind.Waveform,
                MediaAnalysisState.Complete, 0, null, 0, 0, null, 48000, 2,
                buckets, false, false, null, "Decoded.");
            var cache = new MediaAnalysisDerivedCache(folder);
            cache.Write("fingerprint-A:peaks:0-1000000", result);
            Assert.NotNull(cache.Read("fingerprint-A:peaks:0-1000000", request));
            Assert.Null(cache.Read("fingerprint-A:peaks:0-1000000", request with { EndTicks = 900_000 }));
            Assert.Null(cache.Read("fingerprint-A:peaks:0-1000000", request with { PositionTicks = 100 }));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }


    [Fact]
    public void Atomic_cache_requires_valid_manifest_checksum_and_matching_request_identity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rtaime-analysis-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cache = new MediaAnalysisDerivedCache(folder);
            var request = Frame(MediaAssetId.New(), 1);
            var completed = new MediaAnalysisObservation(MediaAnalysisContract.Version, Guid.NewGuid().ToString(),
                request.AssetId, request.ConsumerId, request.Generation, Guid.NewGuid().ToString("N"),
                request.Kind, MediaAnalysisState.Complete, 0, 0, 8, 8,
                new byte[8 * 8 * 4], 0, 0, null, false, false, null, "decoded");
            var matching = request with { Width = 8, Height = 8 };
            cache.Write("asset:stable:profile:position", completed);
            Assert.NotNull(cache.Read("asset:stable:profile:position", matching));
            Assert.Null(cache.Read("asset:other:profile:position", matching));
            Assert.Null(cache.Read("asset:stable:profile:position", matching with { Width = 7 }));
            var blob = Assert.Single(Directory.GetFiles(folder, "*.blob"));
            File.WriteAllBytes(blob, [1, 2, 3]);
            Assert.Null(cache.Read("asset:stable:profile:position", matching));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
