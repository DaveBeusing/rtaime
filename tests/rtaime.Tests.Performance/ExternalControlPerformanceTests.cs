// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using Grpc.Core;
using rtaime.ControlHost;

namespace rtaime.Tests.Performance;

public sealed class ExternalControlPerformanceTests
{
	[Fact]
	public async Task External_control_admission_stays_fast_and_allocation_bounded()
	{
		var limiter = new ExternalControlResourceLimiter(new ExternalControlServerOptions
		{
			MaxConcurrentOperationsPerClient = 8,
			RequestsPerSecond = 10_000,
			RequestBurst = 50_000
		});
		const int iterations = 10_000;

		for (var index = 0; index < 64; index++)
			using (await limiter.AcquireAsync("warmup", CancellationToken.None)) { }

		var before = GC.GetAllocatedBytesForCurrentThread();
		var started = Stopwatch.GetTimestamp();
		for (var index = 0; index < iterations; index++)
			using (await limiter.AcquireAsync("load-client", CancellationToken.None)) { }
		var elapsed = Stopwatch.GetElapsedTime(started);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.True(
			elapsed < TimeSpan.FromSeconds(5),
			$"External control admission regression exceeded five seconds for {iterations} requests: {elapsed}.");
		Assert.True(
			allocated < 16 * 1024 * 1024,
			$"External control admission allocated {allocated:N0} bytes for {iterations} requests.");
	}

	[Fact]
	public async Task External_control_inflight_limit_rejects_without_waiting()
	{
		var limiter = new ExternalControlResourceLimiter(new ExternalControlServerOptions
		{
			MaxConcurrentOperationsPerClient = 1,
			RequestsPerSecond = 100,
			RequestBurst = 100
		});
		using var held = await limiter.AcquireAsync("bounded-client", CancellationToken.None);

		var started = Stopwatch.GetTimestamp();
		var exception = await Assert.ThrowsAsync<RpcException>(
			() => limiter.AcquireAsync("bounded-client", CancellationToken.None).AsTask());
		var elapsed = Stopwatch.GetElapsedTime(started);

		Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
		Assert.True(elapsed < TimeSpan.FromSeconds(1), $"In-flight rejection unexpectedly blocked for {elapsed}.");
	}
}
