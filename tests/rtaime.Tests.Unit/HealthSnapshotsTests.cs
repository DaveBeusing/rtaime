// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class HealthSnapshotsTests
{
	[Theory]
	[InlineData("PASS", SubsystemHealthState.Healthy)]
	[InlineData("WARNING", SubsystemHealthState.Warning)]
	[InlineData("DEGRADED", SubsystemHealthState.Degraded)]
	[InlineData("RECOVERING", SubsystemHealthState.Recovering)]
	[InlineData("FAIL", SubsystemHealthState.Failed)]
	[InlineData("UNVERIFIED", SubsystemHealthState.Unknown)]
	[InlineData("unexpected", SubsystemHealthState.Unknown)]
	public void Health_state_mapping_is_explicit(string evidence, SubsystemHealthState expected) =>
		Assert.Equal(expected, HealthStateMapping.FromEvidence(evidence));

	[Fact]
	public void Multiple_simultaneous_warnings_remain_independent_attention_states()
	{
		var states = new[]
		{
			SubsystemHealthState.Warning,
			SubsystemHealthState.Degraded,
			SubsystemHealthState.Healthy,
			SubsystemHealthState.Unknown
		};

		var attention = states
			.Where(HealthSnapshotAnalysis.RequiresAttention)
			.OrderByDescending(HealthSnapshotAnalysis.Severity)
			.ToArray();

		Assert.Equal(
			[SubsystemHealthState.Degraded, SubsystemHealthState.Warning],
			attention);
	}

	[Fact]
	public void Failed_recovering_healthy_retains_state_history_and_last_success()
	{
		var start = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
		var failed = HealthSnapshotHistory.Retain(
			Snapshot(SubsystemHealthState.Failed, start),
			null,
			start);

		var recoveringAt = start.AddSeconds(2);
		var recovering = HealthSnapshotHistory.Retain(
			Snapshot(SubsystemHealthState.Recovering, recoveringAt),
			failed,
			recoveringAt);

		var healthyAt = recoveringAt.AddSeconds(3);
		var healthy = HealthSnapshotHistory.Retain(
			Snapshot(SubsystemHealthState.Healthy, healthyAt),
			recovering,
			healthyAt);

		Assert.Equal(start, failed.StatusSince);
		Assert.Equal(recoveringAt, recovering.StatusSince);
		Assert.Equal(healthyAt, healthy.StatusSince);
		Assert.Equal(healthyAt, healthy.LastSuccessfulCheck);
	}

	[Fact]
	public void Same_state_keeps_status_since_while_fast_telemetry_updates_advance_last_success()
	{
		var start = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
		SubsystemHealthSnapshot? current = null;

		for (var index = 0; index < 500; index++)
		{
			var observedAt = start.AddMilliseconds(index * 10);
			current = HealthSnapshotHistory.Retain(
				Snapshot(SubsystemHealthState.Healthy, observedAt, $"sample {index}"),
				current,
				observedAt);
		}

		Assert.NotNull(current);
		Assert.Equal(start, current.StatusSince);
		Assert.Equal(start.AddMilliseconds(4990), current.LastSuccessfulCheck);
		Assert.Equal("sample 499", current.Detail);
	}

	[Fact]
	public void Provider_outage_maps_to_failed_without_changing_unknown_semantics()
	{
		Assert.Equal(SubsystemHealthState.Failed, HealthStateMapping.FromEvidence("FAIL"));
		Assert.Equal(SubsystemHealthState.Unknown, HealthStateMapping.FromEvidence("UNVERIFIED"));
		Assert.True(HealthSnapshotAnalysis.RequiresAttention(SubsystemHealthState.Failed));
		Assert.False(HealthSnapshotAnalysis.RequiresAttention(SubsystemHealthState.Unknown));
	}

	[Fact]
	public void Last_successful_check_survives_later_failure()
	{
		var start = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
		var healthy = HealthSnapshotHistory.Retain(
			Snapshot(SubsystemHealthState.Healthy, start),
			null,
			start);
		var failedAt = start.AddSeconds(4);
		var failed = HealthSnapshotHistory.Retain(
			Snapshot(SubsystemHealthState.Failed, failedAt),
			healthy,
			failedAt);

		Assert.Equal(failedAt, failed.StatusSince);
		Assert.Equal(start, failed.LastSuccessfulCheck);
	}

	private static SubsystemHealthSnapshot Snapshot(
		SubsystemHealthState state,
		DateTimeOffset observedAt,
		string detail = "detail") =>
		new(
			"runtime",
			"Runtime Service",
			"Lifecycle",
			state,
			detail,
			observedAt,
			null,
			[new HealthMetricSnapshot("Health", state.ToString())],
			"No recovery action is required.",
			$"state={state}");
}
