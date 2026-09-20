// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows.Input;
using rtaime.Client;
using rtaime.Operator;

namespace rtaime.Tests.Unit;

public sealed class HealthCenterViewModelTests
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
	public void Multiple_simultaneous_warnings_are_grouped_without_collapsing_subsystems()
	{
		var provider = new FakeHealthSnapshotProvider(
		[
			Snapshot("cpu", "CPU", SubsystemHealthState.Warning),
			Snapshot("media", "Media Engine", SubsystemHealthState.Degraded),
			Snapshot("runtime", "Runtime Service", SubsystemHealthState.Healthy)
		]);
		var readiness = new FakeRuntimeReadinessService();
		using var center = new HealthCenterViewModel(provider, readiness, new FakeCommand(), new InlineSynchronizationContext());

		Assert.Equal(3, center.Subsystems.Count);
		Assert.Equal(2, center.Attention.Count);
		Assert.Contains(center.Attention, item => item.Id == "cpu");
		Assert.Contains(center.Attention, item => item.Id == "media");
		Assert.Equal(0, center.FailedCount);
	}

	[Fact]
	public void Failed_recovering_healthy_transition_updates_same_subsystem()
	{
		var provider = new FakeHealthSnapshotProvider([Snapshot("runtime", "Runtime Service", SubsystemHealthState.Failed)]);
		var readiness = new FakeRuntimeReadinessService();
		using var center = new HealthCenterViewModel(provider, readiness, new FakeCommand(), new InlineSynchronizationContext());

		Assert.Equal("FAILED", center.Subsystems.Single().State);
		provider.Publish([Snapshot("runtime", "Runtime Service", SubsystemHealthState.Recovering)]);
		Assert.Equal("RECOVERING", center.Subsystems.Single().State);
		provider.Publish([Snapshot("runtime", "Runtime Service", SubsystemHealthState.Healthy)]);

		Assert.Equal("HEALTHY", center.Subsystems.Single().State);
		Assert.Empty(center.Attention);
		Assert.Equal(1, center.HealthyCount);
	}

	[Fact]
	public void Fast_telemetry_update_stream_coalesces_to_latest_snapshot_without_growth()
	{
		var provider = new FakeHealthSnapshotProvider([Snapshot("cpu", "CPU", SubsystemHealthState.Healthy, "sample 0")]);
		var readiness = new FakeRuntimeReadinessService();
		using var center = new HealthCenterViewModel(provider, readiness, new FakeCommand(), new InlineSynchronizationContext());

		for (var index = 1; index <= 500; index++)
		{
			provider.Publish(
			[
				Snapshot("cpu", "CPU", SubsystemHealthState.Healthy, $"sample {index}")
			]);
		}

		Assert.Single(center.Subsystems);
		Assert.Equal("sample 500", center.Subsystems.Single().Detail);
		Assert.Single(center.Overview);
	}

	[Fact]
	public void Provider_outage_is_visible_as_failed_without_changing_health_into_readiness()
	{
		var provider = new FakeHealthSnapshotProvider(
		[
			Snapshot("providers", "Processing Providers", SubsystemHealthState.Failed, "GPU provider unavailable.")
		]);
		var readiness = new FakeRuntimeReadinessService();
		using var center = new HealthCenterViewModel(provider, readiness, new FakeCommand(), new InlineSynchronizationContext());

		Assert.Equal("FAILED", center.Subsystems.Single().State);
		Assert.Equal(1, center.FailedCount);
		Assert.Equal("INITIALIZING", center.OverallState);
		Assert.Equal("PRODUCTION BLOCKED", center.ProductionReadiness);
	}

	[Fact]
	public void Unknown_health_state_remains_unknown_and_is_not_invented_as_failure()
	{
		var provider = new FakeHealthSnapshotProvider(
		[
			Snapshot("decoder", "Decoder", SubsystemHealthState.Unknown, "No media loaded.")
		]);
		var readiness = new FakeRuntimeReadinessService();
		using var center = new HealthCenterViewModel(provider, readiness, new FakeCommand(), new InlineSynchronizationContext());

		Assert.Equal("UNKNOWN", center.Subsystems.Single().State);
		Assert.Equal(0, center.FailedCount);
		Assert.Empty(center.Attention);
	}

	[Fact]
	public void Open_close_unsubscribes_from_health_and_readiness_events()
	{
		var provider = new FakeHealthSnapshotProvider([Snapshot("cpu", "CPU", SubsystemHealthState.Healthy)]);
		var readiness = new FakeRuntimeReadinessService();
		using var @operator = new OperatorViewModel();
		var center = new HealthCenterViewModel(provider, readiness, @operator, new InlineSynchronizationContext());

		Assert.Equal(1, provider.SubscriberCount);
		Assert.Equal(1, readiness.SubscriberCount);

		center.Dispose();

		Assert.Equal(0, provider.SubscriberCount);
		Assert.Equal(0, readiness.SubscriberCount);
	}

	private static SubsystemHealthSnapshot Snapshot(
		string id,
		string displayName,
		SubsystemHealthState state,
		string detail = "detail")
	{
		var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
		return new SubsystemHealthSnapshot(
			id,
			displayName,
			"Test",
			state,
			detail,
			now,
			state == SubsystemHealthState.Healthy ? now : null,
			[new HealthMetricSnapshot("Metric", "1")],
			"No recovery action is required.",
			$"{id}:{state}");
	}

	private sealed class FakeHealthSnapshotProvider : IHealthSnapshotProvider
	{
		private EventHandler<HealthSnapshotChangedEventArgs>? _changed;
		private IReadOnlyList<SubsystemHealthSnapshot> _current;

		public FakeHealthSnapshotProvider(IReadOnlyList<SubsystemHealthSnapshot> current) =>
			_current = current;

		public int SubscriberCount { get; private set; }

		public event EventHandler<HealthSnapshotChangedEventArgs>? Changed
		{
			add
			{
				_changed += value;
				SubscriberCount++;
			}
			remove
			{
				_changed -= value;
				SubscriberCount--;
			}
		}

		public IReadOnlyList<SubsystemHealthSnapshot> GetCurrent() => _current;

		public void Publish(IReadOnlyList<SubsystemHealthSnapshot> current)
		{
			_current = current;
			_changed?.Invoke(this, new HealthSnapshotChangedEventArgs(current));
		}
	}

	private sealed class FakeRuntimeReadinessService : IRuntimeReadinessService
	{
		private EventHandler<RuntimeReadinessChangedEventArgs>? _changed;

		public RuntimeReadinessSnapshot Current { get; private set; } =
			RuntimeReadinessSnapshot.Initial(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));

		public int SubscriberCount { get; private set; }

		public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed
		{
			add
			{
				_changed += value;
				SubscriberCount++;
			}
			remove
			{
				_changed -= value;
				SubscriberCount--;
			}
		}

		public void Observe(RuntimeReadinessObservation observation)
		{
		}

		public void InvalidatePerformance(string detail)
		{
		}
	}

	private sealed class FakeCommand : ICommand
	{
		public event EventHandler? CanExecuteChanged;
		public bool CanExecute(object? parameter) => false;
		public void Execute(object? parameter)
		{
		}
	}

	private sealed class InlineSynchronizationContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state) => d(state);
	}
}
