// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.RuntimeHost;

namespace rtaime.Tests.Performance;

public sealed class RuntimePerformanceStatusBarPerformanceTests
{
	[Fact]
	public void Program_cadence_measurement_allocates_no_per_frame_history()
	{
		var counter = new RuntimeFrameDropCounter();
		var framePeriod = TimeSpan.FromMilliseconds(20);

		for (var index = 0; index < 128; index++)
			counter.Observe(TimeSpan.FromTicks(index * framePeriod.Ticks), framePeriod);

		var start = GC.GetAllocatedBytesForCurrentThread();
		for (var index = 128; index < 100_128; index++)
			counter.Observe(TimeSpan.FromTicks(index * framePeriod.Ticks), framePeriod);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

		Assert.Equal(0, allocated);
		Assert.NotNull(counter.OutputFramesPerSecond);
		Assert.Equal(50, counter.OutputFramesPerSecond.Value, 6);
	}
}
