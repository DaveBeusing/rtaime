// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
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

		using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
		gpu.Start();
		using var programFrame = gpu.Upload(
			SourceB,
			new RgbaFrameBuffer(format, Solid(format, 70, 80, 90)),
			timing,
			Generation.Initial,
			"monitoring-test");
		var programPixels = gpu.RentReadback(programFrame);

		var captured = tap.TryCapture(
			SourceA,
			Solid(format, 10, 20, 30),
			SourceB,
			Solid(format, 40, 50, 60),
			SourceB,
			programPixels,
			format,
			timing);

		Assert.True(captured);
		programPixels.Dispose();
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
		Assert.True(program.HasFallbackPayload);
		Assert.Equal(MonitoringSharedResourceCapabilityState.Unavailable, program.Descriptor.SharedResourceCapability);
		Assert.Null(program.Descriptor.SharedResource);
		Assert.Equal(1UL, tap.Statistics.Captured);
		Assert.Equal(1UL, tap.Statistics.Processed);
		Assert.Equal(0, gpu.ReadbackPoolStatistics.ActiveBuffers);
	}

	[Fact]
	public async Task Shared_gpu_program_observation_can_omit_cpu_payload_and_releases_on_disconnect()
	{
		using var hub = new RuntimeMonitoringHub();
		var subscription = hub.Subscribe(capacity: 4, requiresCpuFallback: false);
		await using var tap = new RuntimeMonitoringTap(hub, MonitoringSharedResourceCapabilityState.Available);
		var format = new VideoFormat(4, 2, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
		var timing = new FrameTiming(0, 0, new Timebase(1, 50));

		using var backend = new DeviceResidentMonitoringBackend();
		using var gpu = new GpuProcessingProvider(backend);
		gpu.Start();
		var programFrame = gpu.Upload(
			SourceB,
			new RgbaFrameBuffer(format, Solid(format, 70, 80, 90)),
			timing,
			Generation.Initial,
			"shared-monitoring-test");
		using var programPixels = gpu.RentReadback(programFrame);
		var readbacksBeforeExport = backend.ReadbackIntoCount;
		Assert.True(gpu.TryExportMonitoringResource(programFrame, out var sharedResource));
		Assert.Equal(readbacksBeforeExport, backend.ReadbackIntoCount);

		Assert.True(tap.TryCapture(
			SourceA,
			Solid(format, 10, 20, 30),
			SourceB,
			Solid(format, 40, 50, 60),
			SourceB,
			programPixels,
			format,
			timing,
			sharedResource));
		programFrame.Dispose();

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		var frames = new[]
		{
			await subscription.ReadAsync(timeout.Token),
			await subscription.ReadAsync(timeout.Token),
			await subscription.ReadAsync(timeout.Token)
		};
		var program = Assert.Single(frames, frame => frame.Descriptor.StreamKind == MonitoringStreamKind.Program);

		Assert.False(program.HasFallbackPayload);
		Assert.Equal(MonitoringSharedResourceCapabilityState.Available, program.Descriptor.SharedResourceCapability);
		var descriptor = Assert.IsType<MonitoringSharedResourceDescriptor>(program.Descriptor.SharedResource);
		Assert.True(gpu.IsMonitoringResourceActive(descriptor));
		Assert.Equal(1, gpu.SharedMonitoringResourceStatistics.ActiveResources);
		Assert.Equal(1, tap.Statistics.ActiveSharedResources);
		Assert.Equal(1, backend.ActiveAllocationCount);

		await subscription.DisposeAsync();

		Assert.False(gpu.IsMonitoringResourceActive(descriptor));
		Assert.Equal(0, gpu.SharedMonitoringResourceStatistics.ActiveResources);
		Assert.Equal(0, tap.Statistics.ActiveSharedResources);
		Assert.Equal(0, backend.ActiveAllocationCount);
	}

	[Fact]
	public async Task Newer_sample_replaces_and_invalidates_previous_shared_gpu_resource()
	{
		using var hub = new RuntimeMonitoringHub();
		await using var subscription = hub.Subscribe(capacity: 8, requiresCpuFallback: false);
		await using var tap = new RuntimeMonitoringTap(hub, MonitoringSharedResourceCapabilityState.Available);
		var format = new VideoFormat(4, 2, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

		using var backend = new DeviceResidentMonitoringBackend();
		using var gpu = new GpuProcessingProvider(backend);
		gpu.Start();

		async Task<MonitoringSharedResourceDescriptor> PublishAsync(ulong sequence, byte red)
		{
			var timing = new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50));
			var frame = gpu.Upload(
				SourceB,
				new RgbaFrameBuffer(format, Solid(format, red, 80, 90)),
				timing,
				new Generation(sequence),
				"shared-monitoring-replacement");
			using var pixels = gpu.RentReadback(frame);
			Assert.True(gpu.TryExportMonitoringResource(frame, out var resource));
			Assert.True(tap.TryCapture(
				SourceA,
				Solid(format, 10, 20, 30),
				SourceB,
				Solid(format, 40, 50, 60),
				SourceB,
				pixels,
				format,
				timing,
				resource));
			frame.Dispose();

			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			for (var index = 0; index < 3; index++)
			{
				var observation = await subscription.ReadAsync(timeout.Token);
				if (observation.Descriptor.StreamKind == MonitoringStreamKind.Program)
					return Assert.IsType<MonitoringSharedResourceDescriptor>(observation.Descriptor.SharedResource);
			}
			throw new Xunit.Sdk.XunitException("Expected Program monitoring observation was not received.");
		}

		var first = await PublishAsync(0, 70);
		Assert.True(gpu.IsMonitoringResourceActive(first));
		var second = await PublishAsync(RuntimeMonitoringTap.SampleStride, 90);
		var replacementDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
		while (gpu.IsMonitoringResourceActive(first) && DateTimeOffset.UtcNow < replacementDeadline)
			await Task.Delay(10);

		Assert.False(gpu.IsMonitoringResourceActive(first));
		Assert.True(gpu.IsMonitoringResourceActive(second));
		Assert.Equal(1, gpu.SharedMonitoringResourceStatistics.ActiveResources);
		Assert.Equal(1, backend.ActiveAllocationCount);
	}

	[Fact]
	public async Task Monitoring_source_snapshot_does_not_observe_later_mutation()
	{
		using var hub = new RuntimeMonitoringHub();
		await using var subscription = hub.Subscribe(capacity: 4);
		await using var tap = new RuntimeMonitoringTap(hub);
		var format = new VideoFormat(4, 2, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
		var timing = new FrameTiming(0, 0, new Timebase(1, 50));
		var sourceA = Solid(format, 10, 20, 30);
		var sourceB = Solid(format, 40, 50, 60);

		using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
		gpu.Start();
		using var programFrame = gpu.Upload(
			SourceB,
			new RgbaFrameBuffer(format, Solid(format, 70, 80, 90)),
			timing,
			Generation.Initial,
			"monitoring-aliasing-test");
		using var programPixels = gpu.RentReadback(programFrame);

		var capturedSources = tap.CaptureSources(
			SourceA,
			sourceA,
			SourceB,
			sourceB,
			format,
			timing);
		Assert.NotNull(capturedSources);

		Array.Fill(sourceA, (byte)255);
		Array.Fill(sourceB, (byte)0);
		Assert.True(tap.TryCapture(capturedSources, SourceB, programPixels, format, timing));

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		var frames = new[]
		{
			await subscription.ReadAsync(timeout.Token),
			await subscription.ReadAsync(timeout.Token),
			await subscription.ReadAsync(timeout.Token)
		};

		var publishedA = Assert.Single(frames, frame =>
			frame.Descriptor.StreamKind == MonitoringStreamKind.Source &&
			frame.Descriptor.SourceId == SourceA);
		var publishedB = Assert.Single(frames, frame =>
			frame.Descriptor.StreamKind == MonitoringStreamKind.Source &&
			frame.Descriptor.SourceId == SourceB);
		Assert.Equal((byte)10, publishedA.Pixels.Span[0]);
		Assert.Equal((byte)40, publishedB.Pixels.Span[0]);
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

	private sealed class DeviceResidentMonitoringBackend : IGpuProcessingBackend
	{
		private readonly ManagedReferenceGpuBackend _inner = new();

		public GpuBackendInfo Info { get; } = new(
			GpuBackendKind.ManagedReference,
			"Device Resident Monitoring Test Backend",
			hardwareAccelerated: true,
			available: true);

		public SurfaceStorageDomain StorageDomain => SurfaceStorageDomain.Device;
		public int ActiveAllocationCount => _inner.ActiveAllocationCount;
		public int ReadbackIntoCount { get; private set; }
		public void Start() => _inner.Start();
		public void Stop() => _inner.Stop();
		public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels) =>
			_inner.Allocate(surfaceId, format, rgbaPixels);
		public void Composite(SurfaceId outputSurfaceId, VideoFormat format, GpuCompositeOperation operation) =>
			_inner.Composite(outputSurfaceId, format, operation);
		public byte[] Readback(SurfaceId surfaceId, VideoFormat format) => _inner.Readback(surfaceId, format);
		public void ReadbackInto(SurfaceId surfaceId, VideoFormat format, Span<byte> destination)
		{
			ReadbackIntoCount++;
			_inner.ReadbackInto(surfaceId, format, destination);
		}
		public void Release(SurfaceId surfaceId) => _inner.Release(surfaceId);
		public void Dispose() => _inner.Dispose();
	}
}
