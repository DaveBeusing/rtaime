// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.ControlHost;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Performance;

public sealed class ProductionIpcPerformanceTests
{
	[Fact]
	public async Task Local_named_pipe_snapshot_loop_stays_within_broad_regression_guard()
	{
		var endpoint = $"rtaime.test.performance.{Guid.NewGuid():N}";
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = process.RunAsync(stop.Token);
		var transport = new NamedPipeRuntimeHostTransport(endpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
		await transport.ConnectAsync();

		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < 100; iteration++)
		{
			// A transient Named Pipe connect cancellation under runner contention must not
			// erase the 100 successful roundtrip requirement or the 15-second guard.
			for (var attempt = 0; ; attempt++)
			{
				try
				{
					var snapshot = await transport.GetSnapshotAsync();
					Assert.NotNull(snapshot.Runtime);
					break;
				}
				catch (OperationCanceledException) when (attempt == 0 && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
				{
					// Exactly one retry for transport cancellation; other failures remain fatal.
				}
			}
		}
		stopwatch.Stop();

		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"100 local IPC snapshot roundtrips took {stopwatch.Elapsed}.");

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}
}
