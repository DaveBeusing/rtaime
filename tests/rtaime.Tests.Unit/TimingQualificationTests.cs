// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Runtime;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class TimingQualificationTests
{
	private static readonly TimingQualificationThresholds Thresholds = new(
		TimeSpan.FromMilliseconds(20),
		TimeSpan.FromMilliseconds(1),
		TimeSpan.FromMilliseconds(15),
		8);

	[Fact]
	public void No_boundary_evidence_remains_recovering()
	{
		var probe = new RuntimeTimingQualificationProbe(Thresholds);

		var snapshot = probe.Snapshot(TimeSpan.FromSeconds(10));

		Assert.Equal(TimingQualificationState.Recovering, snapshot.State);
		Assert.Equal(0UL, snapshot.TotalBoundaries);
	}

	[Fact]
	public void Perfect_cadence_remains_healthy()
	{
		var probe = new RuntimeTimingQualificationProbe(Thresholds);

		for (ulong sequence = 0; sequence < 6; sequence++)
			probe.RecordBoundary(sequence, TimeSpan.FromMilliseconds(sequence * 20), TimeSpan.FromMilliseconds(4));

		var snapshot = probe.Snapshot(TimeSpan.FromMilliseconds(105));
		Assert.Equal(TimingQualificationState.Healthy, snapshot.State);
		Assert.Equal(6UL, snapshot.TotalBoundaries);
		Assert.Equal(0UL, snapshot.SequenceDiscontinuities);
		Assert.Equal(0UL, snapshot.JitterViolations);
		Assert.Equal(0UL, snapshot.ProcessingViolations);
	}

	[Fact]
	public void Single_budget_violation_degrades_and_repeated_violations_become_unstable()
	{
		var probe = new RuntimeTimingQualificationProbe(Thresholds);
		probe.RecordBoundary(0, TimeSpan.Zero, TimeSpan.FromMilliseconds(4));
		var degraded = probe.RecordBoundary(1, TimeSpan.FromMilliseconds(23), TimeSpan.FromMilliseconds(4));
		Assert.Equal(TimingQualificationState.Degraded, degraded.State);

		probe.RecordBoundary(2, TimeSpan.FromMilliseconds(46), TimeSpan.FromMilliseconds(18));
		var unstable = probe.RecordBoundary(3, TimeSpan.FromMilliseconds(69), TimeSpan.FromMilliseconds(18));
		Assert.Equal(TimingQualificationState.Unstable, unstable.State);
		Assert.True(unstable.JitterViolations >= 3);
		Assert.True(unstable.ProcessingViolations >= 2);
	}

	[Fact]
	public void Sequence_gap_is_immediately_unstable()
	{
		var probe = new RuntimeTimingQualificationProbe(Thresholds);
		probe.RecordBoundary(10, TimeSpan.Zero, TimeSpan.FromMilliseconds(2));
		var snapshot = probe.RecordBoundary(12, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2));

		Assert.Equal(TimingQualificationState.Unstable, snapshot.State);
		Assert.Equal(1UL, snapshot.SequenceDiscontinuities);
		Assert.False(snapshot.Samples[^1].SequenceContinuous);
	}

	[Fact]
	public void Missing_boundaries_beyond_three_frame_periods_are_lost()
	{
		var probe = new RuntimeTimingQualificationProbe(Thresholds);
		probe.RecordBoundary(0, TimeSpan.Zero, TimeSpan.FromMilliseconds(2));

		var snapshot = probe.Snapshot(TimeSpan.FromMilliseconds(61));

		Assert.Equal(TimingQualificationState.Lost, snapshot.State);
	}

	[Fact]
	public void Retained_samples_are_bounded_and_chronological()
	{
		var probe = new RuntimeTimingQualificationProbe(Thresholds);
		for (ulong sequence = 0; sequence < 12; sequence++)
			probe.RecordBoundary(sequence, TimeSpan.FromMilliseconds(sequence * 20), TimeSpan.FromMilliseconds(2));

		var snapshot = probe.Snapshot(TimeSpan.FromMilliseconds(225));
		Assert.Equal(12UL, snapshot.TotalBoundaries);
		Assert.Equal(8, snapshot.Samples.Count);
		Assert.Equal(4UL, snapshot.Samples[0].SequenceNumber);
		Assert.Equal(11UL, snapshot.Samples[^1].SequenceNumber);
	}

	[Fact]
	public void Non_monotonic_observation_is_rejected()
	{
		var probe = new RuntimeTimingQualificationProbe(Thresholds);
		probe.RecordBoundary(0, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(2));

		Assert.Throws<ArgumentException>(() =>
			probe.RecordBoundary(1, TimeSpan.FromMilliseconds(19), TimeSpan.FromMilliseconds(2)));
	}
	[Fact]
	public void Scalar_hot_path_preserves_full_probe_evidence_and_health()
	{
		var scalar = new RuntimeTimingQualificationProbe(Thresholds);
		var full = new RuntimeTimingQualificationProbe(Thresholds);
		for (ulong sequence = 0; sequence < 20; sequence++)
		{
			var at = TimeSpan.FromMilliseconds(sequence * 21);
			var duration = TimeSpan.FromMilliseconds(sequence % 4 == 0 ? 18 : 4);
			Assert.Equal(full.RecordBoundary(sequence, at, duration).State,
				scalar.ObserveBoundary(sequence, at, duration));
		}
		var observed = scalar.Snapshot(TimeSpan.FromMilliseconds(399));
		var expected = full.Snapshot(TimeSpan.FromMilliseconds(399));
		Assert.Equal(expected.TotalBoundaries, observed.TotalBoundaries);
		Assert.Equal(expected.JitterViolations, observed.JitterViolations);
		Assert.Equal(expected.ProcessingViolations, observed.ProcessingViolations);
		Assert.Equal(expected.MaximumObservedProcessingDuration, observed.MaximumObservedProcessingDuration);
		Assert.Equal(expected.Samples, observed.Samples);
	}


    [Fact]
    public void Pipeline_window_reports_exact_nearest_rank_percentiles_and_overwrites()
    {
        var probe = new RuntimeTimingQualificationProbe(Thresholds);
        for (ulong index = 0; index < 12; index++)
            Assert.True(probe.TryObserveStage(RuntimePipelineTimingStage.Readback, index, TimeSpan.FromMilliseconds(index)));
        var snapshot = probe.PipelineSnapshot();
        Assert.Equal(12UL, snapshot.RecordedSamples);
        Assert.Equal(4UL, snapshot.OverwrittenSamples);
        Assert.Equal(0UL, snapshot.ContentionDroppedSamples);
        Assert.Equal(Enumerable.Range(4, 8).Select(i => (ulong)i), snapshot.Samples.Select(sample => sample.SequenceNumber));
        var stage = Assert.Single(snapshot.Stages);
        Assert.Equal("CPU_WALL", stage.MeasurementKind);
        Assert.Equal(8, stage.RetainedSamples);
        Assert.Equal(7, stage.P50Milliseconds);
        Assert.Equal(11, stage.P95Milliseconds);
        Assert.Equal(11, stage.P99Milliseconds);
        Assert.Equal(11, stage.MaximumMilliseconds);
        // Pipeline measurements are observational and must not change readiness health.
        Assert.Equal(TimingQualificationState.Recovering, probe.Snapshot(TimeSpan.Zero).State);
        probe.ResetPipelineSamples();
        Assert.Empty(probe.PipelineSnapshot().Samples);
        Assert.Equal(0UL, probe.PipelineSnapshot().RecordedSamples);
    }

    [Fact]
    public async Task Pipeline_writer_drops_and_counts_samples_instead_of_waiting_for_snapshot()
    {
        var probe = new RuntimeTimingQualificationProbe(Thresholds);
        var gate = typeof(RuntimeTimingQualificationProbe).GetField("_pipelineGate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(probe)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = Task.Run(() => { lock (gate) { held.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(3)));
            var writer = Task.Run(() => probe.TryObserveStage(RuntimePipelineTimingStage.Readback, 1, TimeSpan.Zero));
            Assert.False(await writer.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { release.Set(); await reader; }
        Assert.Equal(1UL, probe.PipelineSnapshot().ContentionDroppedSamples);
        Assert.Empty(probe.PipelineSnapshot().Samples);
    }

    [Fact]
    public void Pipeline_rejects_invalid_measurements_and_labels_lateness_separately()
    {
        var probe = new RuntimeTimingQualificationProbe(Thresholds);
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.TryObserveStage((RuntimePipelineTimingStage)999, 0, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => probe.TryObserveStage(RuntimePipelineTimingStage.Readback, 0, TimeSpan.FromTicks(-1)));
        probe.TryObserveStage(RuntimePipelineTimingStage.SchedulerStartLateness, 0, TimeSpan.Zero);
        probe.TryObserveStage(RuntimePipelineTimingStage.PresentationLateness, 0, TimeSpan.Zero);
        Assert.Equal(new[] { "SCHEDULER_LATENESS", "PRESENTATION_LATENESS" }, probe.PipelineSnapshot().Stages.Select(stage => stage.MeasurementKind));
    }

    [Fact]
    public async Task Cuda_timing_writer_is_nonblocking_and_preserves_operation_scope()
    {
        var collector = new CudaGpuTimingCollector(64);
        var method = typeof(CudaGpuTimingCollector).GetMethod("Record",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var record = method.CreateDelegate<Action<CudaGpuTimingOperation, nuint, TimeSpan, CudaGpuTimingOperation?>>(collector);
        for (var index = 0; index < 70; index++)
            record(CudaGpuTimingOperation.BackendLockWait, 0, TimeSpan.FromMilliseconds(index), CudaGpuTimingOperation.KernelLaunch);
        Assert.Equal(6UL, collector.OverwrittenSampleCount);
        Assert.Equal(64, collector.Snapshot().Count);
        Assert.All(collector.Snapshot(), sample => Assert.Equal(CudaGpuTimingOperation.KernelLaunch, sample.Scope));
        var gate = typeof(CudaGpuTimingCollector).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(collector)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = Task.Run(() => { lock (gate) { held.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(3)));
            await Task.Run(() => record(CudaGpuTimingOperation.ReadbackDeviceToHost, 0, TimeSpan.Zero, null))
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { release.Set(); await reader; }
        Assert.Equal(1UL, collector.ContentionDroppedSamples);
        collector.Reset();
        Assert.Empty(collector.Snapshot());
        Assert.Equal(0UL, collector.ContentionDroppedSamples);
    }
}
