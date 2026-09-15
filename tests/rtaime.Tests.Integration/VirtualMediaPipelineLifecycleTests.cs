using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Integration;

public sealed class VirtualMediaPipelineLifecycleTests
{
    [Fact]
    public void Virtual_media_uses_bounded_drop_oldest_lifecycle_and_output_accepts_accounted_gaps()
    {
        var sourceA = new MediaSourceId(Identity.Parse("a1000000-0000-0000-0000-000000000001"));
        var sourceB = new MediaSourceId(Identity.Parse("a1000000-0000-0000-0000-000000000002"));
        var sink = new MediaSinkId(Identity.Parse("a2000000-0000-0000-0000-000000000001"));
        var provider = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
        var output = provider.CreateOutput(sink);

        using var pipeline = new MediaFramePipeline(new MediaPipelineOptions(
            queueCapacity: 2,
            backpressurePolicy: MediaBackpressurePolicy.DropOldest,
            lateToleranceTicks: 10));

        for (ulong sequence = 0; sequence < 5; sequence++)
        {
            var frame = provider.SourceA.GenerateFrame(sequence);
            var submit = pipeline.Submit(
                frame,
                new MediaClockPosition(frame.Timing.PresentationTimestamp, frame.Timing.Timebase));
            Assert.True(submit.Accepted, submit.Failure?.ToString());
        }

        pipeline.Complete();

        while (true)
        {
            var result = pipeline.ConsumeNext(
                new MediaClockPosition(4, provider.Timing.FrameTimebase),
                output.WriteFrame);

            if (result.Status == MediaFrameConsumeStatus.Completed)
                break;

            Assert.True(result.Consumed, result.Failure?.ToString());
        }

        Assert.Equal(new ulong[] { 3, 4 }, output.Frames.Select(frame => frame.Frame.Timing.SequenceNumber));
        Assert.Equal((ulong)3, pipeline.Statistics.Dropped);
        Assert.Equal((ulong)2, pipeline.Statistics.Consumed);
        Assert.Equal(2, pipeline.Statistics.Queue.MaximumDepth);
    }
}
