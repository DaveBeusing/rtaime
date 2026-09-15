using System.Diagnostics;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Performance;

public sealed class MediaPipelinePerformanceTests
{
    [Fact]
    public void Descriptor_pipeline_sustains_ten_minutes_of_50fps_reference_frames()
    {
        RunLongSimulation(VideoFormat.Hd1080p50Rgba8, frameCount: 30_000);
    }

    [Fact]
    public void Descriptor_pipeline_sustains_ten_minutes_of_5994fps_reference_frames()
    {
        RunLongSimulation(VideoFormat.Hd1080p59_94Rgba8, frameCount: 36_000);
    }

    private static void RunLongSimulation(VideoFormat format, int frameCount)
    {
        using var pipeline = new MediaFramePipeline(new MediaPipelineOptions(
            queueCapacity: 4,
            backpressurePolicy: MediaBackpressurePolicy.Wait,
            lateToleranceTicks: 0));

        var sourceId = new MediaSourceId(Identity.Parse("91000000-0000-0000-0000-000000000001"));
        var timebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
        var consumed = 0;
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < frameCount; index++)
        {
            var sequence = (ulong)index;
            var surfaceId = new SurfaceId(CreateStableIdentity(sequence));
            var surface = new SurfaceDescriptor(
                surfaceId,
                format,
                SurfaceStorageDomain.Host,
                SurfaceOwnership.ProducerOwned,
                new SurfaceLifetimeDescriptor(new Generation(sequence), null),
                new OpaqueSurfaceHandle("performance.virtual", surfaceId.ToString()));
            var frame = new FrameDescriptor(
                MediaContractVersion.Current,
                sourceId,
                surface,
                new FrameTiming(sequence, index, timebase));
            var position = new MediaClockPosition(index, timebase);

            var submit = pipeline.Submit(frame, position);
            Assert.True(submit.Accepted, submit.Failure?.ToString());

            var consume = pipeline.ConsumeNext(position, _ => consumed++);
            Assert.True(consume.Consumed, consume.Failure?.ToString());
        }

        pipeline.Complete();
        var completed = pipeline.ConsumeNext(
            new MediaClockPosition(frameCount, timebase),
            _ => throw new Xunit.Sdk.XunitException("Completed pipeline must not emit another frame."));

        stopwatch.Stop();

        Assert.Equal(MediaFrameConsumeStatus.Completed, completed.Status);
        Assert.Equal(frameCount, consumed);
        Assert.Equal((ulong)frameCount, pipeline.Statistics.Submitted);
        Assert.Equal((ulong)frameCount, pipeline.Statistics.Consumed);
        Assert.Equal((ulong)0, pipeline.Statistics.Dropped);
        Assert.Equal((ulong)0, pipeline.Statistics.Late);
        Assert.Equal((ulong)0, pipeline.Statistics.Rejected);
        Assert.True(pipeline.Statistics.Queue.MaximumDepth <= 4);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Descriptor-only media pipeline processed {frameCount} frames in {stopwatch.Elapsed.TotalSeconds:F3}s; qualification limit is 15s.");
    }

    private static Identity CreateStableIdentity(ulong sequence)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = 0x92;
        BitConverter.TryWriteBytes(bytes[8..], sequence + 1);
        return new Identity(new Guid(bytes));
    }
}
