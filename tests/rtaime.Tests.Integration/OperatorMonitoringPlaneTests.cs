// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class OperatorMonitoringPlaneTests
{
	private static readonly MediaSourceId SourceA =
		new(Identity.Parse("92000000-0000-0000-0000-000000000001"));
	private static readonly MediaSourceId SourceB =
		new(Identity.Parse("92000000-0000-0000-0000-000000000002"));

	[Fact]
	public async Task Bounded_monitor_subscriber_drops_old_frames_and_delivers_latest_without_blocking_publisher()
	{
		using var hub = new RuntimeMonitoringHub();
		await using var subscription = hub.Subscribe(capacity: 1);

		hub.Publish(CreateFrame(MonitoringStreamKind.Program, SourceA, 1, 10, 20, 30));
		hub.Publish(CreateFrame(MonitoringStreamKind.Program, SourceA, 2, 40, 50, 60));
		hub.Publish(CreateFrame(MonitoringStreamKind.Program, SourceB, 3, 70, 80, 90));

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
		var latest = await subscription.ReadAsync(timeout.Token);

		Assert.Equal(2UL, subscription.DroppedFrames);
		Assert.Equal(3UL, latest.Descriptor.Timing.SequenceNumber);
		Assert.Equal(SourceB, latest.Descriptor.SourceId);
		Assert.Equal((byte)70, latest.Pixels.Span[0]);
	}

	[Fact]
	public async Task Runtime_monitoring_tap_publishes_sampled_source_and_actual_program_frames()
	{
		using var hub = new RuntimeMonitoringHub();
		await using var subscription = hub.Subscribe(capacity: 4);
		await using var tap = new RuntimeMonitoringTap(hub);
		var format = new VideoFormat(4, 2, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
		var timing = new FrameTiming(0, 0, new Timebase(1, 50));

		var captured = tap.TryCapture(
			SourceA,
			Solid(format, 10, 20, 30),
			SourceB,
			Solid(format, 40, 50, 60),
			SourceB,
			Solid(format, 70, 80, 90),
			format,
			timing);

		Assert.True(captured);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		var first = await subscription.ReadAsync(timeout.Token);
		var second = await subscription.ReadAsync(timeout.Token);
		var third = await subscription.ReadAsync(timeout.Token);
		var frames = new[] { first, second, third };

		Assert.Equal(2, frames.Count(frame => frame.Descriptor.StreamKind == MonitoringStreamKind.Source));
		var program = Assert.Single(frames, frame => frame.Descriptor.StreamKind == MonitoringStreamKind.Program);
		Assert.Equal(SourceB, program.Descriptor.SourceId);
		Assert.Equal(RuntimeMonitoringTap.MonitorWidth, program.Descriptor.Width);
		Assert.Equal(RuntimeMonitoringTap.MonitorHeight, program.Descriptor.Height);
		Assert.Equal((byte)70, program.Pixels.Span[0]);
		Assert.Equal(1UL, tap.Statistics.Captured);
		Assert.Equal(1UL, tap.Statistics.Processed);
	}

	[Fact]
	public async Task Dedicated_named_pipe_monitoring_transport_delivers_frames_without_control_transport()
	{
		using var hub = new RuntimeMonitoringHub();
		var endpoint = $"rtaime.test.monitor.{Guid.NewGuid():N}";
		await using var server = new RuntimeHostMonitoringServer(endpoint, hub);
		await server.StartAsync();
		var transport = new NamedPipeOperatorMonitoringTransport(
			endpoint,
			TimeSpan.FromSeconds(2),
			TimeSpan.FromMilliseconds(25));
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await using var enumerator = transport.ReadFramesAsync(timeout.Token).GetAsyncEnumerator();
		var receive = enumerator.MoveNextAsync().AsTask();

		var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
		while (!hub.HasSubscribers && DateTimeOffset.UtcNow < deadline)
			await Task.Delay(10, timeout.Token);
		Assert.True(hub.HasSubscribers);

		hub.Publish(CreateFrame(MonitoringStreamKind.Program, SourceA, 9, 100, 110, 120));
		Assert.True(await receive);
		var frame = enumerator.Current;

		Assert.Equal(MonitoringStreamKind.Program, frame.Descriptor.StreamKind);
		Assert.Equal(9UL, frame.Descriptor.Timing.SequenceNumber);
		Assert.Equal((byte)100, frame.Pixels.Span[0]);
	}

	private static MonitoringFrame CreateFrame(
		MonitoringStreamKind kind,
		MediaSourceId sourceId,
		ulong sequence,
		byte red,
		byte green,
		byte blue)
	{
		const uint width = 2;
		const uint height = 2;
		var descriptor = new MonitoringFrameDescriptor(
			MonitoringContractVersion.Current,
			kind,
			sourceId,
			width,
			height,
			PixelFormat.Rgba8,
			new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));
		return new MonitoringFrame(descriptor, Solid(width, height, red, green, blue));
	}

	private static byte[] Solid(VideoFormat format, byte red, byte green, byte blue) =>
		Solid(format.Width, format.Height, red, green, blue);

	private static byte[] Solid(uint width, uint height, byte red, byte green, byte blue)
	{
		var pixels = new byte[checked((int)((ulong)width * height * 4UL))];
		for (var offset = 0; offset < pixels.Length; offset += 4)
		{
			pixels[offset] = red;
			pixels[offset + 1] = green;
			pixels[offset + 2] = blue;
			pixels[offset + 3] = byte.MaxValue;
		}
		return pixels;
	}
}
