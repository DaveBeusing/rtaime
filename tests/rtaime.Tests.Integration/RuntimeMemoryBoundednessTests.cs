// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeMemoryBoundednessTests
{
	[Fact]
	public async Task RuntimeHost_observations_and_recent_snapshot_cost_remain_bounded()
	{
		await using var runtime = new V1RuntimeHostService(
			MediaSourceId.New(),
			MediaSourceId.New(),
			VideoFormat.Hd1080p50Rgba8,
			new NoopRecordingWriter());

		var writes = V1RuntimeHostService.RetainedObservationCapacity + 64;
		for (var index = 0; index < writes; index++)
		{
			runtime.SetTimingHealth(
				index % 2 == 0
					? V1TimingHealthState.Healthy
					: V1TimingHealthState.Degraded);
		}

		Assert.Equal(V1RuntimeHostService.RetainedObservationCapacity, runtime.Observations.Count);
		Assert.True(runtime.OverwrittenObservationCount > 0);
		Assert.Equal(64, runtime.RecentObservations(64).Count);
		Assert.Equal("timing.health:Degraded", runtime.Observations[^1]);
	}

	private sealed class NoopRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
