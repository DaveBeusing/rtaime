// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class OperatorSystemLifecycleProjectionTests
{
	[Fact]
	public void Disconnected_state_is_STARTING_until_authoritative_snapshot_exists()
	{
		var result = Evaluate(HealthyHealth(), connected: false, stale: false);
		Assert.Equal(OperatorLifecycleStates.Starting, result.State);
		Assert.Equal(OperatorProgramSafetyStates.Unknown, result.ProgramSafety);
		Assert.False(result.MainUiReady);
	}

	[Fact]
	public void Stale_state_is_RECOVERING_and_blocks_mutations()
	{
		var result = Evaluate(HealthyHealth(), connected: false, stale: true);
		Assert.Equal(OperatorLifecycleStates.Recovering, result.State);
		Assert.Equal(OperatorProgramSafetyStates.Blocked, result.ProgramSafety);
		Assert.Equal("Control", result.AffectedComponent);
		Assert.False(result.MainUiReady);
	}

	[Fact]
	public void Qualified_core_evidence_is_HEALTHY_and_safe()
	{
		var result = Evaluate(HealthyHealth(), connected: true, stale: false);
		Assert.Equal(OperatorLifecycleStates.Healthy, result.State);
		Assert.Equal(OperatorProgramSafetyStates.Safe, result.ProgramSafety);
		Assert.True(result.MainUiReady);
	}

	[Fact]
	public void AI_only_failure_is_DEGRADED_without_marking_core_program_unsafe()
	{
		var ai = new OperatorAIShowcaseDescriptor(true, "Person Segmentation Highlight", "UNAVAILABLE", "UNVERIFIED", TimeSpan.Zero, 0, null, null, null, false, null, DateTimeOffset.UtcNow);
		var result = OperatorSystemLifecycleProjection.Evaluate(HealthyHealth(), "READY", ai, connected: true, stale: false);
		Assert.Equal(OperatorLifecycleStates.Degraded, result.State);
		Assert.Equal(OperatorProgramSafetyStates.Safe, result.ProgramSafety);
		Assert.Equal("AI", result.AffectedComponent);
		Assert.True(result.MainUiReady);
	}

	[Fact]
	public void Runtime_failure_is_DEGRADED_and_blocks_program_mutations()
	{
		var health = HealthyHealth() with
		{
			Engine = new OperatorHealthMetricDescriptor(OperatorHealthStates.Fail, "Runtime failed."),
			Runtime = new OperatorHealthMetricDescriptor(OperatorHealthStates.Fail, "RuntimeHost is unavailable.")
		};
		var result = Evaluate(health, connected: true, stale: false);
		Assert.Equal(OperatorLifecycleStates.Degraded, result.State);
		Assert.Equal(OperatorProgramSafetyStates.Blocked, result.ProgramSafety);
		Assert.Equal("Runtime", result.AffectedComponent);
		Assert.False(result.MainUiReady);
	}

	[Fact]
	public void Unknown_required_evidence_is_DEGRADED_and_never_false_green()
	{
		var health = HealthyHealth() with
		{
			Engine = new OperatorHealthMetricDescriptor(OperatorHealthStates.Unverified, "GPU qualification evidence is unavailable."),
			GpuProvider = new OperatorHealthMetricDescriptor(OperatorHealthStates.Unverified, "GPU provider is not qualified.")
		};
		var result = Evaluate(health, connected: true, stale: false);
		Assert.Equal(OperatorLifecycleStates.Degraded, result.State);
		Assert.Equal(OperatorProgramSafetyStates.Caution, result.ProgramSafety);
		Assert.Equal("GPU / Provider", result.AffectedComponent);
		Assert.True(result.MainUiReady);
	}

	[Fact]
	public void Explicit_recovery_exhaustion_is_FAILED_and_never_inferred_from_missing_metrics()
	{
		var result = OperatorSystemLifecycleProjection.Evaluate(
			HealthyHealth(), "READY", OperatorAIShowcaseDescriptor.Unavailable, connected: true, stale: false,
			recoveryExhausted: true, terminalFailureDetail: "RuntimeHost restart budget exhausted.");
		Assert.Equal(OperatorLifecycleStates.Failed, result.State);
		Assert.Equal(OperatorProgramSafetyStates.Blocked, result.ProgramSafety);
		Assert.Contains("restart budget exhausted", result.Detail, StringComparison.OrdinalIgnoreCase);
	}

	private static OperatorSystemLifecycleSnapshot Evaluate(OperatorHealthDescriptor health, bool connected, bool stale) =>
		OperatorSystemLifecycleProjection.Evaluate(health, "READY", OperatorAIShowcaseDescriptor.Unavailable, connected, stale);

	private static OperatorHealthDescriptor HealthyHealth()
	{
		var pass = new OperatorHealthMetricDescriptor(OperatorHealthStates.Pass, "Healthy.");
		return new OperatorHealthDescriptor(
			pass, pass, pass, pass, pass, pass, "1920x1080",
			TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(20), 0,
			TimeSpan.FromMinutes(1), "UNVERIFIED", "UNVERIFIED", DateTimeOffset.UtcNow);
	}
}
