// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.RuntimeHost;
using rtaime.Runtime;
using rtaime.Core;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Performance;

public sealed class RuntimePerformanceStatusBarPerformanceTests
{
	[Fact]
	public void Program_cadence_measurement_allocates_no_per_frame_history()
	{
		var counter = new RuntimeFrameDropCounter();
		var framePeriod = TimeSpan.FromMilliseconds(20);

		const int warmup = 16_384;
		const int samples = 100_000;
		// Match the other steady-state guards: warm tiered entry points before measuring frame allocations.
		for (var index = 0; index < warmup; index++)
			counter.Observe(TimeSpan.FromTicks(index * framePeriod.Ticks), framePeriod);

		var start = GC.GetAllocatedBytesForCurrentThread();
		for (var index = warmup; index < warmup + samples; index++)
			counter.Observe(TimeSpan.FromTicks(index * framePeriod.Ticks), framePeriod);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

		Assert.Equal(0, allocated);
		Assert.NotNull(counter.OutputFramesPerSecond);
		Assert.Equal(50, counter.OutputFramesPerSecond.Value, 6);
		Console.WriteLine($"PROGRAM CADENCE samples={samples} allocated={allocated} warmup={warmup}");
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


    [Fact]
    public void Pipeline_stage_scopes_allocate_zero_and_keep_a_bounded_window_after_warmup()
    {
        var probe = new RuntimeTimingQualificationProbe(new TimingQualificationThresholds(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(15), 2048));
        void Observe(ulong sequence)
        {
            using var scope = probe.MeasureStage(RuntimePipelineTimingStage.ServiceBoundaryAttempt, sequence);
            probe.TryObserveStage(RuntimePipelineTimingStage.SchedulerStartLateness, sequence, TimeSpan.Zero);
        }
        // Warm scope disposal, both record/overwrite branches and tiered entry points.
        for (ulong index = 0; index < 16_384; index++) Observe(index);
        probe.ResetPipelineSamples();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (ulong index = 0; index < 100_000; index++) Observe(index);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert.Equal(0, allocated);
        Assert.True(elapsed < TimeSpan.FromSeconds(15));
        var snapshot = probe.PipelineSnapshot();
        Assert.Equal(200_000UL, snapshot.RecordedSamples);
        Assert.Equal(2048, snapshot.Samples.Count);
        Assert.Equal(197_952UL, snapshot.OverwrittenSamples);
        Console.WriteLine($"PIPELINE TELEMETRY samples=200000 allocated={allocated} elapsedMs={elapsed.TotalMilliseconds:0.###} retained=2048");
    }

    [Fact]
    public void Cuda_timing_records_allocate_zero_after_warmup_without_hardware()
    {
        var collector = new CudaGpuTimingCollector(2048);
        var record = typeof(CudaGpuTimingCollector).GetMethod("Record",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<CudaGpuTimingOperation, nuint, TimeSpan, CudaGpuTimingOperation?>>(collector);
        // Warm both recording and overwrite branches, plus the reflection-created delegate/JIT entry point.
        for (var index = 0; index < 16_384; index++)
            record(CudaGpuTimingOperation.ReadbackDeviceToHost, 4096, TimeSpan.Zero, null);
        collector.Reset();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100_000; index++)
            record(CudaGpuTimingOperation.ReadbackDeviceToHost, 4096, TimeSpan.Zero, null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert.Equal(0, allocated);
        Assert.Equal(97_952UL, collector.OverwrittenSampleCount);
        Assert.Equal(2048, collector.Snapshot().Count);
        Console.WriteLine($"CUDA TELEMETRY SOFTWARE samples=100000 allocated={allocated} elapsedMs={elapsed.TotalMilliseconds:0.###}");
    }
}
