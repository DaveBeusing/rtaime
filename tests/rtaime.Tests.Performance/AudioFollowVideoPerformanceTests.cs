using System.Diagnostics;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Performance;

public sealed class AudioFollowVideoPerformanceTests
{
    [Theory]
    [InlineData(false, 30_000)]
    [InlineData(true, 36_000)]
    public void Ten_minute_class_audio_follow_video_run_has_no_timing_discontinuities(bool use5994, int frameCount)
    {
        var videoFormat = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var sourceA = new MediaSourceId(Identity.Parse("95000000-0000-0000-0000-000000000001"));
        var sourceB = new MediaSourceId(Identity.Parse("95000000-0000-0000-0000-000000000002"));
        var provider = new VirtualEmbeddedAudioReferenceProvider(sourceA, sourceB, videoFormat);
        var afv = new AudioFollowVideoEngine(provider.Streams, videoFormat.FrameRate, sourceA);

        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < frameCount; index++)
        {
            var sequence = (ulong)index;
            var activeSource = (index / 1000) % 2 == 0 ? sourceA : sourceB;
            var packet = provider.GetSource(activeSource).GeneratePacket(sequence);
            var result = afv.ProcessBoundary(
                activeSource,
                sequence,
                packet.Descriptor,
                packet.PeakLevel);

            Assert.True(result.Emitted, result.Failure?.ToString());
        }
        stopwatch.Stop();

        var statistics = afv.Statistics;
        Assert.Equal((ulong)frameCount, statistics.Boundaries);
        Assert.Equal((ulong)frameCount, statistics.Emitted);
        Assert.Equal((ulong)0, statistics.Underruns);
        Assert.Equal((ulong)0, statistics.Rejected);
        Assert.Equal((ulong)((frameCount - 1) / 1000), statistics.Switches);

        var finalBoundary = AudioVideoTimingRelationship.GetSampleWindow(
            videoFormat.FrameRate,
            provider.AudioFormat.SampleRate,
            (ulong)frameCount);
        var expectedTotalSamples = finalBoundary.SamplePosition;

        if (!use5994)
            Assert.Equal((ulong)28_800_000, expectedTotalSamples);
        else
            Assert.Equal((ulong)28_828_800, expectedTotalSamples);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Audio AFV descriptor-path qualification exceeded 15 seconds: {stopwatch.Elapsed}.");
    }
}
