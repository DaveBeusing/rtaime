// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Runtime;

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
}
