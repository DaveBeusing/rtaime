// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class UiStatePresentationTests
{
	private static readonly DateTimeOffset Now = new(2026, 9, 20, 15, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Empty_content_is_distinct_from_loading()
	{
		var loading = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.Initializing, false, Reason("control.initializing", true)),
			false,
			"No media loaded",
			"Import media to begin.");

		var empty = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.Ready, true),
			false,
			"No media loaded",
			"Import media to begin.");

		Assert.Equal(UiStateKind.Loading, loading.State);
		Assert.True(loading.IsBlocking);
		Assert.Equal(UiStateKind.Empty, empty.State);
		Assert.False(empty.IsBlocking);
		Assert.True(empty.IsProductionReady);
	}

	[Fact]
	public void Loading_to_ready_preserves_production_readiness()
	{
		var loading = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.Initializing, false, Reason("control.initializing", true)),
			true,
			"Empty",
			"Empty.");

		var ready = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.Ready, true),
			true,
			"Empty",
			"Empty.");

		Assert.Equal(UiStateKind.Loading, loading.State);
		Assert.Equal(UiStateKind.Ready, ready.State);
		Assert.True(ready.IsProductionReady);
		Assert.False(ready.IsVisible);
	}

	[Fact]
	public void Loading_to_error_is_explicit()
	{
		var failed = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.Failed, false, Reason("runtime.recovery.exhausted", true, "Recovery limit reached.")),
			true,
			"Empty",
			"Empty.");

		Assert.Equal(UiStateKind.Error, failed.State);
		Assert.Equal("Recovery limit reached.", failed.Detail);
		Assert.False(failed.IsProductionReady);
	}

	[Fact]
	public void Offline_recovering_ready_transition_is_distinct()
	{
		var offline = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.NotReady, false, Reason("runtime.failed", true)),
			true,
			"Empty",
			"Empty.");
		var recovering = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.Recovering, false, Reason("control.recovering", true)),
			true,
			"Empty",
			"Empty.");
		var ready = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.Ready, true),
			true,
			"Empty",
			"Empty.");

		Assert.Equal(UiStateKind.Offline, offline.State);
		Assert.Equal(UiStateKind.Recovering, recovering.State);
		Assert.Equal(UiStateKind.Ready, ready.State);
	}

	[Fact]
	public void Required_production_failure_does_not_fake_readiness()
	{
		var unavailable = UiStatePresentationFactory.FromRuntime(
			Snapshot(RuntimeReadinessState.NotReady, false, Reason("media.failed", true)),
			true,
			"Empty",
			"Empty.");

		Assert.Equal(UiStateKind.Unavailable, unavailable.State);
		Assert.False(unavailable.IsProductionReady);
		Assert.True(unavailable.IsBlocking);
	}

	[Theory]
	[InlineData("Operator", StartupDependencyClass.Critical)]
	[InlineData("Configuration", StartupDependencyClass.Critical)]
	[InlineData("Control", StartupDependencyClass.RequiredForProduction)]
	[InlineData("Runtime", StartupDependencyClass.RequiredForProduction)]
	[InlineData("Media", StartupDependencyClass.RequiredForProduction)]
	[InlineData("GPU / Provider", StartupDependencyClass.RequiredForProduction)]
	[InlineData("AI", StartupDependencyClass.Optional)]
	[InlineData("Telemetry decoration", StartupDependencyClass.Optional)]
	public void Startup_dependencies_have_explicit_classification(string component, StartupDependencyClass expected)
	{
		Assert.Equal(expected, UiStatePresentationFactory.ClassifyDependency(component));
	}

	[Fact]
	public void Required_ai_profile_promotes_ai_to_production_dependency()
	{
		Assert.Equal(
			StartupDependencyClass.RequiredForProduction,
			UiStatePresentationFactory.ClassifyDependency("AI", requireAI: true));
	}

	private static RuntimeReadinessSnapshot Snapshot(
		RuntimeReadinessState state,
		bool productionReady,
		params ReadinessReason[] reasons) =>
		new(
			state,
			Now,
			Now,
			reasons,
			productionReady,
			RuntimePerformanceVerificationSnapshot.Unverified);

	private static ReadinessReason Reason(
		string code,
		bool blocking,
		string detail = "State detail.") =>
		new(
			code,
			code.Split('.')[0],
			blocking ? ReadinessReasonSeverity.Critical : ReadinessReasonSeverity.Warning,
			detail,
			blocking);
}
