// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.Tests.Unit;

public sealed class RuntimeReadinessServiceTests
{
	[Fact]
	public void Global_states_cover_initializing_ready_degraded_not_ready_recovering_and_failed()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);

		Assert.Equal(RuntimeReadinessState.Initializing, service.Current.State);

		service.Observe(Observation(Healthy(now)));
		Assert.Equal(RuntimeReadinessState.Ready, service.Current.State);
		Assert.True(service.Current.IsProductionReady);

		service.Observe(Observation(
			Healthy(now),
			ai: new OperatorAIShowcaseDescriptor(
				true,
				"Person Segmentation Highlight",
				"FAILED",
				"GPU",
				TimeSpan.Zero,
				0,
				null,
				null,
				null,
				false,
				null,
				now)));
		Assert.Equal(RuntimeReadinessState.Degraded, service.Current.State);
		Assert.True(service.Current.IsProductionReady);

		service.Observe(Observation(
			Healthy(now) with
			{
				Engine = Fail("Runtime failed."),
				Runtime = Fail("Runtime failed.")
			},
			runtimeStatus: "DEGRADED"));
		Assert.Equal(RuntimeReadinessState.NotReady, service.Current.State);
		Assert.False(service.Current.IsProductionReady);

		service.Observe(Observation(Healthy(now), connected: false, stale: true));
		Assert.Equal(RuntimeReadinessState.Recovering, service.Current.State);

		service.Observe(Observation(
			Healthy(now),
			recoveryExhausted: true,
			terminalFailureDetail: "Recovery limit reached."));
		Assert.Equal(RuntimeReadinessState.Failed, service.Current.State);
	}

	[Fact]
	public void Multiple_degradation_reasons_are_retained_together()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);
		var health = Healthy(now) with
		{
			Engine = Unverified("Engine evidence is incomplete."),
			Media = Unverified("Media signal is recovering."),
			Provider = Unverified("Provider refresh is delayed.")
		};

		service.Observe(Observation(health));

		Assert.Equal(RuntimeReadinessState.Degraded, service.Current.State);
		Assert.True(service.Current.IsProductionReady);
		Assert.Contains(service.Current.Reasons, reason => reason.Code == "media.unverified");
		Assert.Contains(service.Current.Reasons, reason => reason.Code == "provider.unverified");
		Assert.All(service.Current.Reasons, reason => Assert.False(reason.IsBlocking));
	}

	[Fact]
	public void Recovered_required_subsystem_returns_automatically_to_ready()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);

		service.Observe(Observation(Healthy(now) with
		{
			Engine = Fail("Media unavailable."),
			Media = Fail("Program input lost.")
		}));
		Assert.Equal(RuntimeReadinessState.NotReady, service.Current.State);

		now += TimeSpan.FromMilliseconds(100);
		service.Observe(Observation(Healthy(now)));

		Assert.Equal(RuntimeReadinessState.Ready, service.Current.State);
		Assert.True(service.Current.IsProductionReady);
	}

	[Fact]
	public void Verified_performance_survives_transient_retained_runtime_observation()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);

		service.Observe(Observation(Healthy(now)));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);

		now += TimeSpan.FromMilliseconds(500);
		var retained = Healthy(now - TimeSpan.FromMilliseconds(500)) with
		{
			Engine = Unverified("Runtime is operating with retained evidence."),
			Runtime = Unverified("RuntimeHost refresh missed; recent runtime observations are being retained temporarily.")
		};
		service.Observe(Observation(retained, runtimeStatus: "DEGRADED"));

		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);
		Assert.Equal(RuntimeReadinessState.Degraded, service.Current.State);
		Assert.Contains(service.Current.Reasons, reason => reason.Code == "runtime.observation.retained");
	}

	[Fact]
	public void Media_failure_blocks_production_without_destroying_valid_render_verification()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);

		service.Observe(Observation(Healthy(now)));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);

		now += TimeSpan.FromMilliseconds(100);
		service.Observe(Observation(Healthy(now) with
		{
			Engine = Fail("Media input failed."),
			Media = Fail("Program input lost.")
		}));

		Assert.Equal(RuntimeReadinessState.NotReady, service.Current.State);
		Assert.False(service.Current.IsProductionReady);
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);
	}

	[Fact]
	public void Performance_verification_expires_after_validity_window()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now, TimeSpan.FromSeconds(2));
		var health = Healthy(now);

		service.Observe(Observation(health));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);

		now += TimeSpan.FromSeconds(3);
		service.Observe(Observation(health, runtimeStatus: "DEGRADED"));

		Assert.Equal(RuntimePerformanceVerificationState.Invalidated, service.Current.Performance.State);
		Assert.Equal(RuntimePerformanceInvalidationReason.MeasurementExpired, service.Current.Performance.InvalidationReason);
		Assert.Contains(service.Current.Reasons, reason => reason.Code == "performance.unverified");
	}

	[Fact]
	public void Explicit_invalidation_requires_a_newer_measurement_before_reverification()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);
		var health = Healthy(now);

		service.Observe(Observation(health));
		service.InvalidatePerformance("Output pipeline was explicitly recreated.");

		Assert.Equal(RuntimePerformanceVerificationState.Invalidated, service.Current.Performance.State);
		Assert.Equal(RuntimePerformanceInvalidationReason.Explicit, service.Current.Performance.InvalidationReason);

		service.Observe(Observation(health));
		Assert.Equal(RuntimePerformanceVerificationState.Invalidated, service.Current.Performance.State);

		now += TimeSpan.FromMilliseconds(100);
		service.Observe(Observation(Healthy(now)));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);
		Assert.Equal(RuntimeReadinessState.Ready, service.Current.State);
	}

	[Fact]
	public void Runtime_fault_requires_a_newer_measurement_before_reverification()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);
		var healthy = Healthy(now);

		service.Observe(Observation(healthy));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);

		service.Observe(Observation(healthy with
		{
			Engine = Fail("Runtime execution failed."),
			Runtime = Fail("Runtime execution failed.")
		}, runtimeStatus: "DEGRADED"));
		Assert.Equal(RuntimePerformanceInvalidationReason.RuntimeFault, service.Current.Performance.InvalidationReason);

		service.Observe(Observation(healthy));
		Assert.Equal(RuntimePerformanceVerificationState.Invalidated, service.Current.Performance.State);

		now += TimeSpan.FromMilliseconds(100);
		service.Observe(Observation(Healthy(now)));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);
	}

	[Fact]
	public void Hardware_and_pipeline_changes_invalidate_previous_verification()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);

		service.Observe(Observation(Healthy(now, cpu: "CPU A", gpu: "GPU A", format: "1920x1080 60 BGRA8")));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);

		now += TimeSpan.FromMilliseconds(100);
		service.Observe(Observation(Healthy(now, cpu: "CPU A", gpu: "GPU B", format: "1920x1080 60 BGRA8")));
		Assert.Equal(RuntimePerformanceInvalidationReason.HardwareChanged, service.Current.Performance.InvalidationReason);

		now += TimeSpan.FromMilliseconds(100);
		service.Observe(Observation(Healthy(now, cpu: "CPU A", gpu: "GPU B", format: "1920x1080 60 BGRA8")));
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);

		now += TimeSpan.FromMilliseconds(100);
		service.Observe(Observation(Healthy(now, cpu: "CPU A", gpu: "GPU B", format: "3840x2160 60 BGRA8")));
		Assert.Equal(RuntimePerformanceInvalidationReason.PipelineChanged, service.Current.Performance.InvalidationReason);
	}

	[Fact]
	public void Rebinding_subscribers_does_not_reset_current_verification()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);
		var firstSubscriberEvents = 0;
		var secondSubscriberEvents = 0;
		EventHandler<RuntimeReadinessChangedEventArgs> first = (_, _) => firstSubscriberEvents++;
		EventHandler<RuntimeReadinessChangedEventArgs> second = (_, _) => secondSubscriberEvents++;

		service.Changed += first;
		service.Observe(Observation(Healthy(now)));
		var firstSubscriberEventsAtUnsubscribe = firstSubscriberEvents;
		service.Changed -= first;
		service.Changed += second;

		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);
		Assert.Equal(RuntimeReadinessState.Ready, service.Current.State);

		now += TimeSpan.FromMilliseconds(250);
		service.Observe(Observation(Healthy(now)));

		Assert.True(firstSubscriberEventsAtUnsubscribe >= 1);
		Assert.Equal(firstSubscriberEventsAtUnsubscribe, firstSubscriberEvents);
		Assert.True(secondSubscriberEvents >= 1);
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);
	}

	[Fact]
	public void Parallel_state_observations_are_thread_safe()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		using var service = new RuntimeReadinessService(() => now);
		var events = 0;
		service.Changed += (_, _) => Interlocked.Increment(ref events);
		var observation = Observation(Healthy(now));

		Parallel.For(0, 64, _ => service.Observe(observation));

		Assert.Equal(RuntimeReadinessState.Ready, service.Current.State);
		Assert.Equal(RuntimePerformanceVerificationState.Verified, service.Current.Performance.State);
		Assert.True(events >= 1);
	}

	[Fact]
	public void Disposed_service_rejects_late_state_updates()
	{
		var now = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
		var service = new RuntimeReadinessService(() => now);
		service.Dispose();

		Assert.Throws<ObjectDisposedException>(() => service.Observe(Observation(Healthy(now))));
		Assert.Throws<ObjectDisposedException>(() => service.InvalidatePerformance("Late invalidation."));
	}

	private static RuntimeReadinessObservation Observation(
		OperatorHealthDescriptor health,
		string runtimeStatus = "READY",
		OperatorAIShowcaseDescriptor? ai = null,
		bool connected = true,
		bool stale = false,
		bool recoveryExhausted = false,
		string? terminalFailureDetail = null) =>
		new(
			health,
			runtimeStatus,
			ai ?? OperatorAIShowcaseDescriptor.Unavailable,
			connected,
			stale,
			recoveryExhausted,
			terminalFailureDetail);

	private static OperatorHealthDescriptor Healthy(
		DateTimeOffset observedAt,
		string cpu = "CPU A",
		string gpu = "GPU A",
		string format = "1920x1080 60 BGRA8") =>
		new(
			Pass("Engine healthy."),
			Pass("Control healthy."),
			Pass("Runtime healthy."),
			Pass("Media healthy."),
			Pass("Providers healthy."),
			Pass("GPU provider healthy."),
			format,
			TimeSpan.FromMilliseconds(2),
			TimeSpan.FromMilliseconds(16.67),
			0,
			TimeSpan.FromMinutes(5),
			"20%",
			"2 GiB / 24 GiB",
			observedAt,
			cpu,
			"10%",
			"30% · 10 GiB / 32 GiB",
			gpu);

	private static OperatorHealthMetricDescriptor Pass(string detail) =>
		new(OperatorHealthStates.Pass, detail);

	private static OperatorHealthMetricDescriptor Fail(string detail) =>
		new(OperatorHealthStates.Fail, detail);

	private static OperatorHealthMetricDescriptor Unverified(string detail) =>
		new(OperatorHealthStates.Unverified, detail);
}
