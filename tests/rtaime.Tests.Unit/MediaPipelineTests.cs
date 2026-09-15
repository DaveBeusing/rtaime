using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class MediaPipelineTests
{
    [Fact]
    public void Frame_lifecycle_transitions_producer_queue_consumer_release()
    {
        var lease = new MediaFrameLease(CreateFrame(0));
        Assert.Equal(MediaFrameLeaseState.ProducerOwned, lease.State);

        using var queue = new BoundedMediaFrameQueue(2, MediaBackpressurePolicy.RejectIncoming);
        var enqueue = queue.Enqueue(lease);
        Assert.True(enqueue.Enqueued);
        Assert.Equal(MediaFrameLeaseState.Queued, lease.State);

        var dequeue = queue.Dequeue();
        Assert.True(dequeue.HasFrame);
        Assert.Same(lease, dequeue.Lease);
        Assert.Equal(MediaFrameLeaseState.ConsumerOwned, lease.State);

        lease.Release();
        Assert.Equal(MediaFrameLeaseState.Released, lease.State);
    }

    [Fact]
    public void Consumer_owned_surface_cannot_enter_pipeline()
    {
        var frame = CreateFrame(0, SurfaceOwnership.ConsumerOwned);
        Assert.Throws<ArgumentException>(() => new MediaFrameLease(frame));
    }

    [Fact]
    public void Reject_incoming_policy_never_exceeds_queue_boundary()
    {
        using var queue = new BoundedMediaFrameQueue(2, MediaBackpressurePolicy.RejectIncoming);
        var first = new MediaFrameLease(CreateFrame(0));
        var second = new MediaFrameLease(CreateFrame(1));
        var third = new MediaFrameLease(CreateFrame(2));

        Assert.True(queue.Enqueue(first).Enqueued);
        Assert.True(queue.Enqueue(second).Enqueued);
        var rejected = queue.Enqueue(third);

        Assert.Equal(MediaEnqueueStatus.DroppedIncoming, rejected.Status);
        Assert.Equal(MediaFrameLeaseState.Dropped, third.State);
        Assert.Equal(2, queue.Statistics.CurrentDepth);
        Assert.Equal(2, queue.Statistics.MaximumDepth);
        Assert.Equal((ulong)1, queue.Statistics.Dropped);
        Assert.Equal((ulong)1, queue.Statistics.Rejected);
    }

    [Fact]
    public void Producer_faster_than_consumer_drop_oldest_preserves_latest_frames()
    {
        using var queue = new BoundedMediaFrameQueue(2, MediaBackpressurePolicy.DropOldest);
        var leases = Enumerable.Range(0, 5)
            .Select(index => new MediaFrameLease(CreateFrame((ulong)index)))
            .ToArray();

        foreach (var lease in leases)
            queue.Enqueue(lease);

        Assert.Equal(MediaFrameLeaseState.Dropped, leases[0].State);
        Assert.Equal(MediaFrameLeaseState.Dropped, leases[1].State);
        Assert.Equal(MediaFrameLeaseState.Dropped, leases[2].State);
        Assert.Equal((ulong)3, queue.Statistics.Dropped);
        Assert.Equal(2, queue.Statistics.CurrentDepth);

        Assert.True(queue.TryDequeue(out var fourth));
        Assert.True(queue.TryDequeue(out var fifth));
        Assert.Equal((ulong)3, fourth!.Frame.Timing.SequenceNumber);
        Assert.Equal((ulong)4, fifth!.Frame.Timing.SequenceNumber);
        fourth.Release();
        fifth.Release();
    }

    [Fact]
    public void Late_frame_is_dropped_before_queueing()
    {
        using var pipeline = new MediaFramePipeline(new MediaPipelineOptions(
            2,
            MediaBackpressurePolicy.RejectIncoming,
            lateToleranceTicks: 0));

        var frame = CreateFrame(0);
        var current = new MediaClockPosition(1, frame.Timing.Timebase);
        var result = pipeline.Submit(frame, current);

        Assert.Equal(MediaFrameSubmitStatus.DroppedLate, result.Status);
        Assert.Equal((ulong)1, pipeline.Statistics.Dropped);
        Assert.Equal((ulong)1, pipeline.Statistics.Late);
        Assert.Equal(0, pipeline.Statistics.Queue.CurrentDepth);
    }

    [Fact]
    public void Frame_can_become_late_while_waiting_in_queue()
    {
        using var pipeline = new MediaFramePipeline(new MediaPipelineOptions(
            2,
            MediaBackpressurePolicy.RejectIncoming,
            lateToleranceTicks: 0));

        var frame = CreateFrame(0);
        Assert.True(pipeline.Submit(frame, new MediaClockPosition(0, frame.Timing.Timebase)).Accepted);

        var consume = pipeline.ConsumeNext(
            new MediaClockPosition(1, frame.Timing.Timebase),
            _ => throw new Xunit.Sdk.XunitException("Late frame must not reach the consumer."));

        Assert.Equal(MediaFrameConsumeStatus.DroppedLate, consume.Status);
        Assert.Equal((ulong)1, pipeline.Statistics.Dropped);
        Assert.Equal((ulong)1, pipeline.Statistics.Late);
        Assert.Equal((ulong)0, pipeline.Statistics.Consumed);
    }

    [Fact]
    public void Ordered_shutdown_drains_existing_frames_then_completes()
    {
        using var pipeline = new MediaFramePipeline(new MediaPipelineOptions(
            4,
            MediaBackpressurePolicy.Wait,
            lateToleranceTicks: 10));

        for (ulong sequence = 0; sequence < 3; sequence++)
        {
            var frame = CreateFrame(sequence);
            Assert.True(pipeline.Submit(
                frame,
                new MediaClockPosition(frame.Timing.PresentationTimestamp, frame.Timing.Timebase)).Accepted);
        }

        pipeline.Complete();
        var consumed = new List<ulong>();

        while (true)
        {
            var result = pipeline.ConsumeNext(
                new MediaClockPosition(2, CreateFrame(0).Timing.Timebase),
                frame => consumed.Add(frame.Timing.SequenceNumber));

            if (result.Status == MediaFrameConsumeStatus.Completed)
                break;

            Assert.Equal(MediaFrameConsumeStatus.Consumed, result.Status);
        }

        Assert.Equal(new ulong[] { 0, 1, 2 }, consumed);
        Assert.Equal(MediaQueueState.Completed, pipeline.State);
        Assert.Equal((ulong)3, pipeline.Statistics.Consumed);
        Assert.Equal((ulong)0, pipeline.Statistics.Dropped);
    }

    [Fact]
    public void Waiting_producer_honours_cancellation()
    {
        using var queue = new BoundedMediaFrameQueue(1, MediaBackpressurePolicy.Wait);
        Assert.True(queue.Enqueue(new MediaFrameLease(CreateFrame(0))).Enqueued);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var blocked = new MediaFrameLease(CreateFrame(1));

        Assert.Throws<OperationCanceledException>(() => queue.Enqueue(blocked, cancellation.Token));
        Assert.Equal(MediaFrameLeaseState.ProducerOwned, blocked.State);
        Assert.Equal(1, queue.Statistics.CurrentDepth);
    }

    [Fact]
    public void Sequence_gap_fails_closed_without_entering_queue()
    {
        using var pipeline = new MediaFramePipeline(new MediaPipelineOptions(2, MediaBackpressurePolicy.RejectIncoming));
        var frame = CreateFrame(1);
        var current = new MediaClockPosition(frame.Timing.PresentationTimestamp, frame.Timing.Timebase);

        var result = pipeline.Submit(frame, current);

        Assert.Equal(MediaFrameSubmitStatus.RejectedSequence, result.Status);
        Assert.Equal("media.pipeline.sequence_mismatch", result.Failure!.Value.Code);
        Assert.Equal((ulong)1, pipeline.Statistics.SequenceErrors);
        Assert.Equal(0, pipeline.Statistics.Queue.CurrentDepth);
    }

    [Fact]
    public void Descriptor_is_forwarded_without_payload_copy_or_replacement()
    {
        using var pipeline = new MediaFramePipeline(new MediaPipelineOptions(1, MediaBackpressurePolicy.Wait));
        var frame = CreateFrame(0);
        FrameDescriptor? observed = null;

        Assert.True(pipeline.Submit(
            frame,
            new MediaClockPosition(frame.Timing.PresentationTimestamp, frame.Timing.Timebase)).Accepted);

        var consume = pipeline.ConsumeNext(
            new MediaClockPosition(frame.Timing.PresentationTimestamp, frame.Timing.Timebase),
            value => observed = value);

        Assert.True(consume.Consumed);
        Assert.Same(frame, observed);
        Assert.Same(frame.Surface.Handle, observed!.Surface.Handle);
    }

    private static FrameDescriptor CreateFrame(
        ulong sequenceNumber,
        SurfaceOwnership ownership = SurfaceOwnership.ProducerOwned)
    {
        var timebase = new Timebase(1, 50);
        var sourceId = new MediaSourceId(Identity.Parse("81000000-0000-0000-0000-000000000001"));
        var surfaceId = new SurfaceId(new Identity(Guid.Parse($"82000000-0000-0000-0000-{sequenceNumber + 1:000000000000}")));
        var surface = new SurfaceDescriptor(
            surfaceId,
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Host,
            ownership,
            new SurfaceLifetimeDescriptor(new Generation(sequenceNumber), null),
            new OpaqueSurfaceHandle("test.frame", surfaceId.ToString()));

        return new FrameDescriptor(
            MediaContractVersion.Current,
            sourceId,
            surface,
            new FrameTiming(sequenceNumber, checked((long)sequenceNumber), timebase));
    }
}
