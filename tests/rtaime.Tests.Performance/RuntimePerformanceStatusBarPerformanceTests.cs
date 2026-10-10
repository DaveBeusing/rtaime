// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.RuntimeHost;
using rtaime.Runtime;
using rtaime.Core;

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
	[Theory]
	[InlineData(50, 1)]
	[InlineData(60_000, 1_001)]
	public void Rational_schedule_and_scalar_timing_evidence_allocate_zero_after_warmup(long numerator, long denominator)
	{
		var rate = new FrameRate(numerator, denominator);
		var schedule = new RationalFrameSchedule(rate, TimeSpan.Zero);
		var period = schedule.NextDeadline;
		var probe = new RuntimeTimingQualificationProbe(new TimingQualificationThresholds(
			period, TimeSpan.FromTicks(period.Ticks / 4), period, 2048));
		var counter = new RuntimeFrameDropCounter();
		for (ulong sequence = 0; sequence < 128; sequence++)
		{
			var at = schedule.NextDeadline;
			schedule.TryTake(at, out _);
			probe.ObserveBoundary(sequence, at, TimeSpan.Zero);
			counter.ObserveScheduled(at, 0);
		}
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var start = GC.GetAllocatedBytesForCurrentThread();
		for (ulong sequence = 128; sequence < 100_128; sequence++)
		{
			var at = schedule.NextDeadline;
			schedule.TryTake(at, out _);
			probe.ObserveBoundary(sequence, at, TimeSpan.Zero);
			counter.ObserveScheduled(at, 0);
		}
		var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
		Assert.Equal(0, allocated);
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "Constant-space cadence regression exceeded its software guard.");
		Assert.Equal(TimingQualificationState.Healthy, probe.Snapshot(schedule.Deadline(100_128)).State);
	}

}
