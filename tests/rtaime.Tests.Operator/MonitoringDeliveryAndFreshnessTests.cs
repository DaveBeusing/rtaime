// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class MonitoringDeliveryAndFreshnessTests
{
	[Fact]
	public void Blocked_dispatcher_keeps_one_callback_and_only_newest_Program_and_source_frames()
	{
		var queued = new Queue<Action>();
		var delivered = new List<MonitoringObservation>();
		using var delivery = new LatestMonitoringFrameDelivery(queued.Enqueue, delivered.Add);
		var sourceA = MediaSourceId.New();
		var sourceB = MediaSourceId.New();
		var program = MediaSourceId.New();

		for (ulong number = 1; number <= 2000; number++)
		{
			delivery.Enqueue(Observation(MonitoringStreamKind.Source, sourceA, number));
			delivery.Enqueue(Observation(MonitoringStreamKind.Source, sourceB, number));
			delivery.Enqueue(Observation(MonitoringStreamKind.Program, program, number));
			Assert.Equal(1, queued.Count);
			Assert.InRange(delivery.PendingFrameCount, 1, 3);
		}

		Assert.Equal(3, delivery.PendingFrameCount);
		Assert.Equal(3L * 1999, delivery.DroppedFrames);
		queued.Dequeue().Invoke();
		Assert.Empty(queued);
		Assert.Equal(0, delivery.PendingFrameCount);
		Assert.Equal(3, delivered.Count);
		Assert.All(delivered, observation => Assert.Equal(2000UL, observation.Frame.Descriptor.Timing.SequenceNumber));
		Assert.Equal(MonitoringStreamKind.Program, delivered[^1].Frame.Descriptor.StreamKind);
	}


	[Fact]
	public void Delayed_reordered_frames_cannot_evict_newer_pending_Program_or_source()
	{
		var callbacks = new Queue<Action>();
		var delivered = new List<MonitoringObservation>();
		using var delivery = new LatestMonitoringFrameDelivery(callbacks.Enqueue, delivered.Add);
		var program = MediaSourceId.New();
		var source = MediaSourceId.New();

		foreach (var (kind, id) in new[]
		{
			(MonitoringStreamKind.Program, program),
			(MonitoringStreamKind.Source, source)
		})
		{
			var newer = Observation(kind, id, 100);
			delivery.Enqueue(newer);
			var delayed = Observation(kind, id, 90) with { ReceivedAt = newer.ReceivedAt.AddSeconds(1) };
			delivery.Enqueue(delayed);
		}

		Assert.Single(callbacks);
		Assert.Equal(2, delivery.PendingFrameCount);
		Assert.Equal(2, delivery.DroppedFrames);
		callbacks.Dequeue().Invoke();
		Assert.Equal(2, delivered.Count);
		Assert.All(delivered, sample => Assert.Equal(100UL, sample.Frame.Descriptor.Timing.SequenceNumber));
	}

	[Fact]
	public void Pending_observations_reject_older_connection_or_route_even_if_sequence_is_higher()
	{
		var callbacks = new Queue<Action>();
		var delivered = new List<MonitoringObservation>();
		using var delivery = new LatestMonitoringFrameDelivery(callbacks.Enqueue, delivered.Add);
		var program = MediaSourceId.New();
		var source = MediaSourceId.New();

		delivery.Enqueue(Observation(MonitoringStreamKind.Program, program, 900));
		delivery.Enqueue(Observation(MonitoringStreamKind.Program, program, 1) with { ConnectionEpoch = 2 });
		delivery.Enqueue(Observation(MonitoringStreamKind.Program, program, 999) with { ConnectionEpoch = 1 });
		delivery.Enqueue(Observation(MonitoringStreamKind.Source, source, 900));
		delivery.Enqueue(Observation(MonitoringStreamKind.Source, source, 1) with { PreviewRouteVersion = 2 });
		delivery.Enqueue(Observation(MonitoringStreamKind.Source, source, 999) with { PreviewRouteVersion = 1 });

		Assert.Single(callbacks);
		callbacks.Dequeue().Invoke();
		Assert.Equal(2, delivered.Count);
		Assert.All(delivered, item => Assert.Equal(1UL, item.Frame.Descriptor.Timing.SequenceNumber));
		Assert.Contains(delivered, item => item.ConnectionEpoch == 2);
		Assert.Contains(delivered, item => item.PreviewRouteVersion == 2);
	}

	[Fact]
	public void Many_independent_source_bursts_are_bounded_without_eviction_of_Program()
	{
		var queued = new Queue<Action>();
		var delivered = new List<MonitoringObservation>();
		using var delivery = new LatestMonitoringFrameDelivery(queued.Enqueue, delivered.Add);
		var program = MediaSourceId.New();
		delivery.Enqueue(Observation(MonitoringStreamKind.Program, program, 7));
		var sourceIds = Enumerable.Range(0, 100).Select(_ => MediaSourceId.New()).ToArray();
		for (var index = 0; index < sourceIds.Length; index++)
			delivery.Enqueue(Observation(MonitoringStreamKind.Source, sourceIds[index], (ulong)(index + 1)));

		Assert.Single(queued);
		Assert.Equal(LatestMonitoringFrameDelivery.MaximumPendingFrames, delivery.PendingFrameCount);
		Assert.Equal(84L, delivery.DroppedFrames);
		queued.Dequeue().Invoke();
		Assert.Equal(LatestMonitoringFrameDelivery.MaximumPendingFrames, delivered.Count);
		Assert.Contains(delivered, item => item.Frame.Descriptor.StreamKind == MonitoringStreamKind.Program);
		Assert.Contains(delivered, item => item.Frame.Descriptor.SourceId == sourceIds[^1]);
		Assert.DoesNotContain(delivered, item => item.Frame.Descriptor.SourceId == sourceIds[0]);
	}

	[Fact]
	public void Invalidation_and_disposal_reject_queued_observations_and_release_payloads()
	{
		var callbacks = new Queue<Action>();
		var delivered = new List<MonitoringObservation>();
		using var delivery = new LatestMonitoringFrameDelivery(callbacks.Enqueue, delivered.Add);
		var source = MediaSourceId.New();
		delivery.Enqueue(Observation(MonitoringStreamKind.Source, source, 1));
		delivery.Invalidate();
		Assert.Equal(0, delivery.PendingFrameCount);
		delivery.Enqueue(Observation(MonitoringStreamKind.Source, source, 2));
		Assert.Single(callbacks);
		callbacks.Dequeue().Invoke();
		Assert.Equal(2UL, Assert.Single(delivered).Frame.Descriptor.Timing.SequenceNumber);

		delivery.Enqueue(Observation(MonitoringStreamKind.Source, source, 3));
		delivery.Dispose();
		callbacks.Dequeue().Invoke();
		Assert.Single(delivered);
		Assert.Equal(0, delivery.PendingFrameCount);
	}

	[Fact]
	public void Program_and_selected_Preview_expire_independently_of_other_streams()
	{
		var now = DateTimeOffset.UnixEpoch.AddDays(10);
		var preview = new MonitoringViewerFreshness();
		var program = new MonitoringViewerFreshness();
		preview.SelectSource("source-a", 3, 1);
		program.SelectSource("program", 7, 1);
		Assert.True(preview.Observe("source-a", 3, 1, now, 10));
		Assert.True(program.Observe("program", 7, 1, now, 20));

		for (var i = 1; i <= 10; i++)
		{
			var frameTime = now.AddMilliseconds(i * 500);
			Assert.True(preview.Observe("source-a", 3, 1, frameTime, (ulong)(10 + i)));
		}
		Assert.Equal("LIVE", preview.Status(hasFrame: true, now.AddSeconds(5)));
		Assert.Equal("STALE", program.Status(hasFrame: true, now.AddSeconds(5)));
		Assert.Equal("STALE", program.Status(hasFrame: true, now.AddSeconds(8)));
	}

	[Fact]
	public void Selected_Preview_stales_while_Program_and_unselected_sources_keep_arriving()
	{
		var now = DateTimeOffset.UnixEpoch.AddDays(10);
		var preview = new MonitoringViewerFreshness();
		var program = new MonitoringViewerFreshness();
		preview.SelectSource("source-b", 4, 1);
		program.SelectSource("program", 1, 1);
		Assert.True(preview.Observe("source-b", 4, 1, now, 8));
		Assert.True(program.Observe("program", 1, 1, now, 16));

		for (var index = 1; index <= 12; index++)
		{
			Assert.True(program.Observe("program", 1, 1,
				now.AddMilliseconds(index * 500), (ulong)(16 + index)));
		}
		Assert.Equal("STALE", preview.Status(true, now.AddSeconds(6)));
		Assert.Equal("LIVE", program.Status(true, now.AddSeconds(6)));
		Assert.False(preview.Observe("source-a", 4, 1, now.AddSeconds(6), 1000));
		Assert.True(preview.Observe("source-b", 4, 1, now.AddSeconds(6), 9));
		Assert.Equal("LIVE", preview.Status(true, now.AddSeconds(6)));
	}

	[Fact]
	public void Source_switch_and_reconnect_never_promote_retained_pixels_without_matching_evidence()
	{
		var now = DateTimeOffset.UnixEpoch.AddDays(10);
		var preview = new MonitoringViewerFreshness();
		preview.SelectSource("source-a", 4, 1);
		Assert.True(preview.Observe("source-a", 4, 1, now, 500));
		Assert.Equal("LIVE", preview.Status(true, now.AddMilliseconds(500)));

		preview.SelectSource("source-b", 5, 1);
		Assert.Equal("UNKNOWN", preview.Status(hasFrame: true, now)); // B may have an older cached thumbnail.
		Assert.False(preview.Observe("source-a", 4, 1, now.AddSeconds(1), 501));
		Assert.False(preview.Observe("source-b", 4, 1, now.AddSeconds(1), 90));
		Assert.True(preview.Observe("source-b", 5, 1, now.AddSeconds(1), 1)); // independent generation/sequence
		Assert.Equal("LIVE", preview.Status(true, now.AddSeconds(1)));
		Assert.False(preview.Observe("source-b", 5, 1, now.AddSeconds(2), 0)); // reordered delivery

		preview.SelectSource("source-b", 5, 2);
		Assert.Equal("UNKNOWN", preview.Status(true, now.AddSeconds(2)));
		Assert.False(preview.Observe("source-b", 5, 1, now.AddSeconds(3), 600));
		Assert.True(preview.Observe("source-b", 5, 2, now.AddSeconds(4), 1));
		Assert.Equal("STALE", preview.Status(true, now.AddSeconds(7)));
	}

	[Fact]
	public void Provider_replacement_starts_independent_sequence_epoch_without_transport_reconnect()
	{
		var now = DateTimeOffset.UnixEpoch.AddDays(10);
		var program = new MonitoringViewerFreshness();
		program.SelectSource("program", 1, 1);
		Assert.True(program.Observe("program", 1, 1, now, 400, providerInstanceId: "provider-a"));
		Assert.False(program.Observe("program", 1, 1, now.AddMilliseconds(10), 399, providerInstanceId: "provider-a"));
		Assert.True(program.Observe("program", 1, 1, now.AddMilliseconds(20), 1, providerInstanceId: "provider-b"));
		Assert.Equal("LIVE", program.Status(true, now.AddMilliseconds(20)));
	}

	[Fact]
	public void New_shared_resource_generation_accepts_reset_sequence_but_rejects_old_generation()
	{
		var now = DateTimeOffset.UnixEpoch.AddDays(10);
		var program = new MonitoringViewerFreshness();
		program.SelectSource("program", 4, 2);
		Assert.True(program.Observe("program", 4, 2, now, 1000, "provider-a", resourceGeneration: 9));
		Assert.True(program.Observe("program", 4, 2, now.AddMilliseconds(10), 1, "provider-a", resourceGeneration: 10));
		Assert.False(program.Observe("program", 4, 2, now.AddMilliseconds(20), 2000, "provider-a", resourceGeneration: 9));
		Assert.False(program.Observe("program", 4, 2, now.AddMilliseconds(30), 1, "provider-a", resourceGeneration: 10));
		Assert.True(program.Observe("program", 4, 2, now.AddMilliseconds(40), 2, "provider-a", resourceGeneration: 10));
		Assert.Equal("LIVE", program.Status(true, now.AddMilliseconds(40)));
	}

	[Fact]
	public void Blocked_dispatcher_cannot_refresh_expired_frame_using_delivery_time()
	{
		var receivedAt = DateTimeOffset.UnixEpoch.AddDays(10);
		var program = new MonitoringViewerFreshness();
		program.SelectSource("program", 1, 1);
		Assert.True(program.Observe("program", 1, 1, receivedAt, 1));
		Assert.Equal("STALE", program.Status(true, receivedAt.AddSeconds(10)));
	}

	private static MonitoringObservation Observation(MonitoringStreamKind kind, MediaSourceId sourceId, ulong sequence)
	{
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current, kind, sourceId,
			1, 1, PixelFormat.Rgba8,
			new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)),
			ColorDescription.SrgbFullRgba8);
		return new MonitoringObservation(
			new MonitoringFrame(descriptor, new byte[] { 1, 2, 3, 255 }),
			1, 1, 1, DateTimeOffset.UnixEpoch.AddSeconds(1).AddMilliseconds((double)sequence));
	}
}
