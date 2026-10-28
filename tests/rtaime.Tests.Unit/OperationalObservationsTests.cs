// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class OperationalObservationsTests
{
	private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Active_escalation_recovery_and_stale_are_monotonic_and_source_bound()
	{
		long ticks = 1000;
		var projection = new OperationalObservationProjector(() => ticks, 1000);
		var warning = Snapshot("audio", SubsystemHealthState.Warning);
		var first = Assert.Single(projection.Update([warning], Start));
		Assert.Equal(1, first.FormatVersion);
		Assert.Equal("health:audio", first.Key);
		Assert.Equal(OperationalObservationCondition.Active, first.Condition);
		Assert.Equal(OperationalObservationSeverity.Warning, first.Severity);
		Assert.Equal("Control-confirmed audio", first.EvidenceSource);
		Assert.Null(first.CorrelationId);
		Assert.Equal(TimeSpan.Zero, first.Duration);

		ticks = 2500;
		Assert.Empty(projection.Update([warning with { Detail = "new meter" }], Start.AddSeconds(2)));
		Assert.Equal(TimeSpan.FromMilliseconds(1500), Assert.Single(projection.Current).Duration);

		ticks = 3000;
		var escalation = Assert.Single(projection.Update([Snapshot("audio", SubsystemHealthState.Failed)], Start.AddSeconds(3)));
		Assert.Equal(OperationalObservationSeverity.Error, escalation.Severity);
		Assert.Equal(Start, escalation.SinceUtc);

		ticks = 4000;
		var cleared = Assert.Single(projection.Update([Snapshot("audio", SubsystemHealthState.Healthy)], Start.AddSeconds(4)));
		Assert.Equal(OperationalObservationCondition.Clear, cleared.Condition);
		Assert.Equal(Start.AddSeconds(4), cleared.LastGoodProjectedUtc);
		Assert.Equal(TimeSpan.Zero, cleared.Duration);

		ticks = 5000;
		var lost = Assert.Single(projection.Update([], Start.AddSeconds(5)));
		Assert.Equal(OperationalObservationCondition.Stale, lost.Condition);
		Assert.Equal("UNAVAILABLE", lost.EvidenceAvailability);
		Assert.Equal(cleared.LastGoodProjectedUtc, lost.LastGoodProjectedUtc);
		Assert.Empty(projection.Update([], Start.AddSeconds(6)));
	}

	[Fact]
	public void Provider_identity_is_recorded_only_from_source_published_metadata()
	{
		var projector = new OperationalObservationProjector(() => 20, 1000);
		var withProvider = Snapshot("recording", SubsystemHealthState.Failed) with
		{
			Metrics = [new HealthMetricSnapshot("Provider", "native-recorder")]
		};
		projector.Update([withProvider], Start);
		Assert.Equal("native-recorder", Assert.Single(projector.Current).OwningProviderId);
		var unsafeProvider = withProvider with
		{
			Metrics = [new HealthMetricSnapshot("Provider", "https://user:password@example.invalid")]
		};
		projector.Update([unsafeProvider], Start.AddMilliseconds(500));
		Assert.Null(Assert.Single(projector.Current).OwningProviderId);

		var unmeasured = Snapshot("ptp", SubsystemHealthState.Unknown);
		projector.Update([withProvider, unmeasured], Start.AddSeconds(1));
		Assert.Null(projector.Current.Single(item => item.ComponentId == "ptp").OwningProviderId);
		Assert.Equal(OperationalObservationCondition.Stale,
			projector.Current.Single(item => item.ComponentId == "ptp").Condition);
	}

		[Theory]
	[InlineData("STALE")]
	[InlineData("UNAVAILABLE")]
	[InlineData("UNVERIFIED")]
	[InlineData("UNKNOWN")]
	public void Untrusted_evidence_never_becomes_active_or_clear(string evidence)
	{
		var projection = new OperationalObservationProjector(() => 0, 1000);
		var stale = Snapshot("runtime", SubsystemHealthState.Healthy) with
		{
			Metrics = [new HealthMetricSnapshot("Evidence status", evidence)]
		};
		Assert.Empty(projection.Update([stale], Start));
		var observed = Assert.Single(projection.Current);
		Assert.Equal(OperationalObservationCondition.Stale, observed.Condition);
		Assert.Equal(evidence, observed.EvidenceAvailability);
		Assert.Null(observed.CorrelationId);
	}

	[Fact]
	public void Burst_coalesces_repeated_states_and_capacity_stays_bounded()
	{
		long ticks = 100;
		var projection = new OperationalObservationProjector(() => ticks, 1000);
		var fault = Snapshot("output", SubsystemHealthState.Degraded);
		Assert.Single(projection.Update([fault], Start));
		for (var index = 0; index < 5000; index++)
		{
			ticks++;
			Assert.Empty(projection.Update([fault with { Detail = $"sample-{index}" }], Start.AddMilliseconds(index)));
		}
		Assert.Single(projection.Current);
		Assert.Equal("sample-4999", projection.Current[0].Detail);
		Assert.Equal(1, projection.Current[0].FormatVersion);
		var oversized = Enumerable.Range(0, 100)
			.Select(index => Snapshot($"source-{index}", SubsystemHealthState.Failed))
			.ToArray();
		projection.Update(oversized, Start.AddSeconds(60));
		Assert.Equal(OperationalObservationProjector.Capacity, projection.Current.Count);
		Assert.All(projection.Current, observation => Assert.Equal(OperationalObservationCondition.Active, observation.Condition));
	}

	private static SubsystemHealthSnapshot Snapshot(string id, SubsystemHealthState state) =>
		new(
			id,
			id,
			"Operations",
			state,
			"measured evidence",
			Start,
			null,
			[new HealthMetricSnapshot("Health", state.ToString())],
			"Review the owning workflow.",
			"No raw media data.",
			ProgramImpact: SubsystemProgramImpact.Unknown,
			EvidenceSource: "Control-confirmed audio");
}
