// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class SequenceTimelineWaveformTests
{
    private static SequenceClip AudioClip(long sourceEnd) =>
        new(Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), SequenceMediaKind.Audio, new string('A', 64)),
            0, sourceEnd, 0, sourceEnd);

    [Fact]
    public async Task Real_sample_buckets_keep_exact_source_range_and_separate_two_audio_channels()
    {
        var media = new RecordingWaveformClient();
        using var waveform = new SequenceTimelineWaveformService(media);
        var clip = AudioClip(3 * SequenceContract.TicksPerSecond);
        var output = await waveform.RequestAsync(clip,
            SequenceContract.TicksPerSecond, 2 * SequenceContract.TicksPerSecond, 64, 5);
        Assert.NotNull(output);
        Assert.Equal(2, output.Channels);
        Assert.Equal(10_000_000L, output.RequestedStartTicks);
        Assert.Equal(20_000_000L, output.RequestedEndTicks);
        Assert.Equal("READY · source PCM waveform", output.Status);
        Assert.Equal(64, media.LastRequest!.BucketCount);
        Assert.Equal(MediaAnalysisKind.Waveform, media.LastRequest.Kind);
        Assert.Equal(10_000_000L, media.LastRequest.PositionTicks);
        Assert.Equal(20_000_000L, media.LastRequest.EndTicks);
        Assert.Equal(2, output.Buckets[0].Minimum.Length);
        Assert.Equal(-0.75f, output.Buckets[0].Minimum[1]);
        Assert.Equal(0.80f, output.Buckets[0].Maximum[0]);
        Assert.Equal(0, media.Cancellations);
    }

    [Fact]
    public async Task Long_range_is_explicitly_partial_and_does_not_exceed_native_analysis_limit()
    {
        var media = new RecordingWaveformClient();
        using var waveform = new SequenceTimelineWaveformService(media);
        var clip = AudioClip(200 * SequenceContract.TicksPerSecond);
        var output = await waveform.RequestAsync(clip, 0, clip.SourceOut, 12_000, 12);
        Assert.NotNull(output);
        Assert.StartsWith("PARTIAL", output.Status);
        Assert.Equal(MediaAnalysisContract.MaximumBuckets, media.LastRequest!.BucketCount);
        Assert.Equal(MediaAnalysisContract.MaximumWaveformRangeTicks,
            media.LastRequest.EndTicks - media.LastRequest.PositionTicks);
        Assert.InRange(media.LastRequest.Level, 0, 8);
    }

    [Fact]
    public async Task Missing_pcm_discontinuities_are_not_replaced_with_a_synthetic_waveform()
    {
        var media = new RecordingWaveformClient { Missing = true };
        using var waveform = new SequenceTimelineWaveformService(media);
        var clip = AudioClip(2 * SequenceContract.TicksPerSecond);
        var output = await waveform.RequestAsync(clip, 0, clip.SourceOut, 10, 9);
        Assert.NotNull(output);
        Assert.StartsWith("PARTIAL", output.Status);
        Assert.All(output.Buckets, bucket => Assert.False(bucket.HasSamples));
        Assert.All(output.Buckets, bucket => Assert.True(bucket.Discontinuity));
    }

    private sealed class RecordingWaveformClient : IMediaAssetCatalogClient
    {
        public MediaAnalysisRequest? LastRequest { get; private set; }
        public int Cancellations { get; private set; }
        public bool Missing { get; init; }
        private readonly string _jobId = Guid.NewGuid().ToString("D");
        public ValueTask<MediaAnalysisBegin> BeginMediaAnalysisAsync(MediaAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            request.Validate();
            LastRequest = request;
            return ValueTask.FromResult(new MediaAnalysisBegin(
                MediaAnalysisContract.Version, _jobId, request.AssetId,
                request.ConsumerId, request.Generation, "runtime-epoch-1",
                MediaAnalysisState.Pending, "Native PCM analysis pending."));
        }

        public ValueTask<MediaAnalysisObservation> GetMediaAnalysisAsync(MediaAnalysisJobRequest job,
            CancellationToken cancellationToken = default)
        {
            var req = LastRequest!;
            var buckets = Enumerable.Range(0, req.BucketCount).Select(index =>
            {
                var start = req.PositionTicks +
                    (req.EndTicks - req.PositionTicks) * index / req.BucketCount;
                var end = req.PositionTicks +
                    (req.EndTicks - req.PositionTicks) * (index + 1) / req.BucketCount;
                return new MediaAnalysisWaveformBucket(start, end,
                    (ulong)index * 256, Missing ? 0u : 256u,
                    !Missing, Missing,
                    Missing ? [0f, 0f] : [0.1f, -0.75f],
                    Missing ? [0f, 0f] : [0.80f, 0.2f], null);
            }).ToArray();
            return ValueTask.FromResult(new MediaAnalysisObservation(
                MediaAnalysisContract.Version, job.JobId, req.AssetId, job.ConsumerId,
                req.Generation, job.RuntimeEpoch, MediaAnalysisKind.Waveform,
                Missing ? MediaAnalysisState.Partial : MediaAnalysisState.Complete,
                req.PositionTicks, null, 0, 0, null, 48000, 2, buckets,
                false, true, null, Missing ? "No PCM samples" : "Native PCM buckets"));
        }

        public ValueTask<MediaAnalysisObservation> CancelMediaAnalysisAsync(MediaAnalysisJobRequest job,
            CancellationToken cancellationToken = default)
        {
            Cancellations++;
            return ValueTask.FromException<MediaAnalysisObservation>(
                new OperationCanceledException("Test acknowledgement only"));
        }

        public ValueTask<MediaAssetCatalogSnapshot> GetMediaAssetCatalogAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogSnapshot>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogMutationResult> ImportMediaAssetsAsync(
            IReadOnlyList<string> sourceLocations, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogMutationResult> RelinkMediaAssetAsync(MediaAssetId assetId,
            string sourceLocation, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogMutationResult> RemoveMediaAssetAsync(MediaAssetId assetId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogMutationResult>(new NotSupportedException());
        public ValueTask<MediaAssetCatalogSnapshot> RefreshMediaAssetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<MediaAssetCatalogSnapshot>(new NotSupportedException());
    }
}
