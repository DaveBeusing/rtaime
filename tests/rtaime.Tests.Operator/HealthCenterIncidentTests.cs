// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows.Input;
using rtaime.Client;
using rtaime.Core;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class HealthCenterIncidentTests
{
	private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Simultaneous_faults_are_sorted_without_promoting_monitoring_or_recording_to_Program_failure()
	{
		var provider = new TestProvider(
			Snapshot("monitoring", SubsystemHealthState.Failed, SubsystemProgramImpact.MonitoringOnly),
			Snapshot("recording", SubsystemHealthState.Failed, SubsystemProgramImpact.RecordingOnly),
			Snapshot("audio", SubsystemHealthState.Degraded, SubsystemProgramImpact.Degraded));
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), new QueuedContext());
		Assert.Equal(3, model.Attention.Count);
		Assert.Equal("PROGRAM DEGRADED", model.Subsystems.Single(item => item.Id == "audio").ProgramImpact);
		Assert.Equal("RECORDING ONLY", model.Subsystems.Single(item => item.Id == "recording").ProgramImpact);
		Assert.Equal("MONITORING ONLY", model.Subsystems.Single(item => item.Id == "monitoring").ProgramImpact);
		Assert.Equal("monitoring", model.Attention[0].Id);
		Assert.False(model.RecoverCommand.CanExecute(null));
		Assert.Equal(RuntimeReadinessState.Initializing, readiness.Current.State);
	}

	[Theory]
	[InlineData("UNKNOWN")]
	[InlineData("STALE")]
	[InlineData("UNAVAILABLE")]
	public void Missing_evidence_stays_in_attention_without_becoming_a_failed_subsystem(string evidence)
	{
		var snapshot = Snapshot("audio", SubsystemHealthState.Unknown, SubsystemProgramImpact.Unknown) with
		{ Metrics = [new("Evidence status", evidence)] };
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(new TestProvider(snapshot), readiness, new TestCommand(), new QueuedContext());
		Assert.Equal(evidence, Assert.Single(model.Attention).EvidenceStatus);
		Assert.Equal("UNKNOWN", model.SelectedSubsystem!.State);
		Assert.Equal(0, model.FailedCount);
		Assert.DoesNotContain("All required production readiness evidence is qualified", model.OverallSummary);
		Assert.False(model.RecoverCommand.CanExecute(null));
	}

	[Fact]
	public void Slow_ui_keeps_one_pending_update_and_preserves_latest_active_faults_and_selection()
	{
		var provider = new TestProvider(Snapshot("control", SubsystemHealthState.Warning, SubsystemProgramImpact.ControlLimited));
		var context = new QueuedContext();
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), context);
		var selected = model.SelectedSubsystem;
		for (var index = 0; index < 5000; index++)
			provider.Publish(Snapshot("control", SubsystemHealthState.Recovering, SubsystemProgramImpact.ControlLimited) with
			{
				Detail = $"sample {index}", LastObservedAt = Start.AddMilliseconds(index)
			});
		Assert.Single(context.Pending);
		context.Drain();
		Assert.Same(selected, model.SelectedSubsystem);
		Assert.Equal("sample 4999", Assert.Single(model.Attention).Detail);
		Assert.Single(model.Subsystems);
		Assert.Empty(context.Pending);

		provider.Publish(Snapshot("control", SubsystemHealthState.Healthy, SubsystemProgramImpact.None));
		context.Drain();
		Assert.Empty(model.Attention);
	}

	[Fact]
	public void Detailed_telemetry_is_opt_in_without_hiding_active_incidents_or_changing_readiness()
	{
		var provider = new TestProvider(
			Snapshot("cpu", SubsystemHealthState.Healthy, SubsystemProgramImpact.None),
			Snapshot("recording", SubsystemHealthState.Failed, SubsystemProgramImpact.RecordingOnly),
			Snapshot("audio", SubsystemHealthState.Unknown, SubsystemProgramImpact.Unknown));
		var context = new QueuedContext();
		var sync = new TestCommand();
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, sync, context);

		Assert.False(model.TelemetryOverviewVisible);
		Assert.Equal("SHOW TELEMETRY", model.TelemetryOverviewAction);
		Assert.Equal(2, model.AttentionCount);
		Assert.Equal(1, model.FailedCount);
		Assert.Equal("recording", model.SelectedSubsystem!.Id);
		Assert.False(model.TechnicalDetailsVisible);
		Assert.True(model.ToggleTelemetryOverviewCommand.CanExecute(null));
		var selected = model.SelectedSubsystem;
		var readinessBefore = readiness.Current;
		model.ToggleTelemetryOverviewCommand.Execute(null);
		Assert.True(model.TelemetryOverviewVisible);
		Assert.Equal("HIDE TELEMETRY", model.TelemetryOverviewAction);
		Assert.Same(selected, model.SelectedSubsystem);
		Assert.Equal(readinessBefore, readiness.Current);
		Assert.Equal(0, sync.Executions);

		model.ToggleTechnicalDetailsCommand.Execute(null);
		Assert.True(model.TechnicalDetailsVisible);
		provider.Publish(
			Snapshot("cpu", SubsystemHealthState.Healthy, SubsystemProgramImpact.None),
			Snapshot("recording", SubsystemHealthState.Degraded, SubsystemProgramImpact.RecordingOnly),
			Snapshot("audio", SubsystemHealthState.Unknown, SubsystemProgramImpact.Unknown));
		context.Drain();
		Assert.Same(selected, model.SelectedSubsystem);
		Assert.True(model.TelemetryOverviewVisible);
		Assert.Equal(2, model.AttentionCount);
		Assert.Equal(0, model.FailedCount);
		Assert.Equal(readinessBefore, readiness.Current);
		model.SelectedSubsystem = model.Subsystems.Single(x => x.Id == "audio");
		Assert.False(model.TechnicalDetailsVisible);
		model.ToggleTelemetryOverviewCommand.Execute(null);
		Assert.False(model.TelemetryOverviewVisible);
		Assert.Equal(0, sync.Executions);
		model.Dispose();
		Assert.False(model.ToggleTelemetryOverviewCommand.CanExecute(null));
		Assert.False(model.ToggleTechnicalDetailsCommand.CanExecute(null));
	}

	[Fact]
	public void Recovery_rechecks_authorization_and_releases_command_subscription()
	{
		var provider = new TestProvider(Snapshot("control", SubsystemHealthState.Failed, SubsystemProgramImpact.ControlLimited) with { CanRecover = true });
		var command = new TestCommand();
		var context = new QueuedContext();
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, command, context);
		Assert.True(model.RecoverCommand.CanExecute(null));
		model.RecoverCommand.Execute(null);
		Assert.Equal(1, command.Executions);

		command.Enabled = false;
		context.Drain();
		Assert.False(model.RecoverCommand.CanExecute(null));
		model.RecoverCommand.Execute(null);
		Assert.Equal(1, command.Executions);

		command.Enabled = true;
		provider.Publish(Snapshot("control", SubsystemHealthState.Recovering, SubsystemProgramImpact.ControlLimited));
		context.Drain();
		Assert.False(model.RecoverCommand.CanExecute(null));
		model.Dispose();
		Assert.Equal(0, command.Subscribers);
		provider.Publish(Snapshot("control", SubsystemHealthState.Healthy, SubsystemProgramImpact.None));
		command.Enabled = false;
		Assert.Empty(context.Pending);
	}

	[Fact]
	public void Pending_delivery_after_dispose_cannot_change_the_closed_view()
	{
		var provider = new TestProvider(Snapshot("monitoring", SubsystemHealthState.Failed, SubsystemProgramImpact.MonitoringOnly));
		var context = new QueuedContext();
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), context);
		provider.Publish(Snapshot("monitoring", SubsystemHealthState.Healthy, SubsystemProgramImpact.None));
		model.Dispose();
		context.Drain();
		Assert.Equal("FAILED", Assert.Single(model.Attention).State);
	}

	[Fact]
	public void Incident_copy_redacts_each_bounded_field_and_retains_safe_next_action()
	{
		var snapshot = Snapshot("recording", SubsystemHealthState.Failed, SubsystemProgramImpact.RecordingOnly) with
		{
			Detail = "token=must-not-leak " + new string('x', 2000),
			TechnicalDetail = "raw payload must not be copied"
		};
		var provider = new TestProvider(snapshot);
		using var readiness = new RuntimeReadinessService(() => Start);
		string? copied = null;
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), new QueuedContext(), text => copied = text);
		model.CopyIncidentSummaryCommand.Execute(null);
		Assert.NotNull(copied);
		Assert.DoesNotContain("must-not-leak", copied, StringComparison.Ordinal);
		Assert.DoesNotContain("raw payload", copied, StringComparison.Ordinal);
		Assert.Contains("Recommended safe next action: Review the owning workspace.", copied, StringComparison.Ordinal);
		Assert.Contains("Evidence source: simulated confirmed snapshot", copied, StringComparison.Ordinal);
		Assert.InRange(copied.Length, 1, 5000);
	}

	[Fact]
	public void Clipboard_failure_is_visible_without_losing_incident_evidence()
	{
		var provider = new TestProvider(Snapshot("audio", SubsystemHealthState.Degraded, SubsystemProgramImpact.Degraded));
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), new QueuedContext(),
			_ => throw new InvalidOperationException("clipboard unavailable"));
		model.CopyIncidentSummaryCommand.Execute(null);
		Assert.Equal("The clipboard is currently unavailable.", model.DiagnosticActionStatus);
		Assert.Single(model.Attention);
	}

	[Theory]
	[InlineData("audio")]
	[InlineData("recording")]
	[InlineData("gpu")]
	[InlineData("transport-program")]
	public void Stale_retained_values_never_establish_current_Program_health(string id)
	{
		var retained = Snapshot(id, SubsystemHealthState.Healthy, SubsystemProgramImpact.None);
		foreach (var evidence in new[] { (false, true, "PASS"), (true, false, "UNVERIFIED") })
		{
			var result = OperatorHealthSnapshotProvider.ApplyEvidenceFreshness(retained, evidence.Item1, evidence.Item2, evidence.Item3);
			Assert.Equal(SubsystemHealthState.Unknown, result.State);
			Assert.Equal(SubsystemProgramImpact.Unknown, result.ProgramImpact);
			Assert.Contains("stale", result.Detail, StringComparison.Ordinal);
		}
		Assert.Equal(retained, OperatorHealthSnapshotProvider.ApplyEvidenceFreshness(retained, true, false, "PASS"));
	}

	[Fact]
	public void Independent_monitoring_fault_remains_visible_during_Control_reconnect()
	{
		var fault = Snapshot("monitoring", SubsystemHealthState.Failed, SubsystemProgramImpact.MonitoringOnly);
		Assert.Equal(fault, OperatorHealthSnapshotProvider.ApplyEvidenceFreshness(fault, false, true, "UNVERIFIED"));
	}

	[Theory]
	[InlineData("CONNECTED", true, 0, "LIVE")]
	[InlineData("CONNECTED", true, 10, "STALE")]
	[InlineData("RECONNECTING", false, 0, "RECONNECTING")]
	[InlineData("STOPPED", false, 0, "OFFLINE")]
	public void Transport_guidance_reuses_existing_send_freshness_and_lifecycle_semantics(
		string lifecycle, bool connected, int ageSeconds, string expected)
	{
		var transport = new OperatorNetworkOutputDescriptor(
			"program", "srt", "SRT", "safe-target", lifecycle, connected, "H264", "AAC",
			48000, 2, "PCM16", 1000000, 128000, 100, 1, 1, 0, 0, 0, 1, 100, 0,
			Start.AddSeconds(-ageSeconds), null);
		Assert.Equal(expected, OutputRoutingHealthViewModel.TransmissionState(transport, true, false, Start));
		Assert.Equal("OFFLINE", OutputRoutingHealthViewModel.TransmissionState(transport, false, true, Start));
	}


	[Fact]
	public void Multi_target_sender_health_does_not_hide_a_failed_or_stale_peer()
	{
		var healthy = new OperatorNetworkOutputDescriptor(
			"program-srt", "srt", "SRT", "srt://example.test:9000/program",
			"CONNECTED", true, "H264", "AACLC", 48000, 2, "FLOAT32",
			12_000_000, 192_000, 120, 50, 50, 0, 0, 0, 100, 30000, 0,
			Start, null);
		var failed = new OperatorNetworkOutputDescriptor(
			"program-ndi", "ndi", "NDI", "ndi://Program",
			"FAULTED", false, "NDIHIGHBANDWIDTH", "FLOAT32", 48000, 2, "FLOAT32",
			0, 0, 0, 50, 25, 1, 5, 2, 25, 18000, 0,
			Start.AddSeconds(-10), new Failure("network.output.reconnect_exhausted", "Retry budget used."));
		var role = new OperatorOutputRoleDescriptor(
			"program", "PROGRAM", "source-id", "sink-id", "provider-id", 1920, 1080,
			"50/1", "RGBA8", "1/50", "ACTIVE", true, "PASS", "Runtime has matching source evidence.",
			null, healthy, "program", [healthy, failed], 25, 9);

		Assert.Equal("FAILED", OutputRoutingHealthViewModel.TransmissionStateForRole(role, true, false, Start));
		Assert.Equal(SubsystemHealthState.Failed,
			OperatorHealthSnapshotProvider.TransportState(role, true, false, Start));
		Assert.Equal(SubsystemHealthState.Unknown,
			OperatorHealthSnapshotProvider.TransportState(role, false, true, Start));
		var targets = OutputRoutingHealthViewModel.ProjectNetworkTargets(role, true, false, Start);
		Assert.Equal(2, targets.Count);
		Assert.Equal("LIVE", targets[0].TransmissionState);
		Assert.Equal("FAILED", targets[1].TransmissionState);
		var measured = OutputRoutingHealthViewModel.ProjectNetworkTargets(role, true, false, Start,
			new Dictionary<string, double?> { ["program:program-srt"] = 1024 });
		Assert.Contains("1 KiB/s", measured[0].ProviderSendEvidence, StringComparison.Ordinal);
		Assert.Contains("rate UNVERIFIED", measured[1].ProviderSendEvidence, StringComparison.Ordinal);
		Assert.Contains("CONTROL REV 25", targets[0].RouteRevision, StringComparison.Ordinal);
		Assert.Contains("RUNTIME EXEC REV 9", targets[0].RouteRevision, StringComparison.Ordinal);
		Assert.Contains("SOURCE source-id", targets[0].RouteRevision, StringComparison.Ordinal);
		Assert.Equal("ROUTE CONFIRMED", targets[0].RouteEvidence);
		Assert.Contains("UNVERIFIED", targets[0].PhysicalEvidence, StringComparison.Ordinal);
		Assert.Contains("UNAVAILABLE", targets[1].ConfiguredCapability, StringComparison.Ordinal);

		// Simulate a current/connected NDI sender whose last successful
		// publication has expired: it must not be counted as LIVE.
		var stale = new OperatorNetworkOutputDescriptor(
			"program-ndi", "ndi", "NDI", "ndi://Program", "CONNECTED", true,
			"NDIHIGHBANDWIDTH", "FLOAT32", 48000, 2, "FLOAT32", 0, 0, 0,
			50, 25, 0, 0, 0, 25, 18000, 0, Start.AddSeconds(-10), null);
		var mixed = new OperatorOutputRoleDescriptor("program", "PROGRAM", "source-id",
			"sink-id", "provider-id", 1920, 1080, "50/1", "RGBA8", "1/50",
			"ACTIVE", true, "PASS", "Confirmed.", null, healthy, "program",
			[healthy, stale], 25, 9);
		Assert.Equal("STALE", OutputRoutingHealthViewModel.TransmissionStateForRole(mixed, true, false, Start));
		Assert.Equal(SubsystemHealthState.Unknown,
			OperatorHealthSnapshotProvider.TransportState(mixed, true, false, Start));
		Assert.Equal("OFFLINE", OutputRoutingHealthViewModel.TransmissionStateForRole(role, false, true, Start));
		Assert.Equal("ROUTE UNVERIFIED",
			OutputRoutingHealthViewModel.ProjectNetworkTargets(role, false, true, Start)[0].RouteEvidence);
		var unconfirmed = new OperatorOutputRoleDescriptor(
			"program", "PROGRAM", "new-source", "new-sink", "provider-id", 1920, 1080,
			"50/1", "RGBA8", "1/50", "ACTIVE", false, "UNVERIFIED",
			"Runtime confirmation for this new route is pending.", null,
			healthy, "program", [healthy], 26, null);
		Assert.Equal("UNKNOWN", OutputRoutingHealthViewModel.TransmissionStateForRole(
			unconfirmed, true, false, Start));
		Assert.Equal("UNKNOWN", Assert.Single(OutputRoutingHealthViewModel.ProjectNetworkTargets(
			unconfirmed, true, false, Start)).TransmissionState);
	}

	[Fact]
	public void Measured_local_sender_rates_require_monotonic_bytes_and_a_bounded_observation_interval()
	{
		Assert.Equal(2048d, OutputRoutingHealthViewModel.CalculateSenderRate(
			1024, 3072, TimeSpan.FromSeconds(1)));
		Assert.Equal(0d, OutputRoutingHealthViewModel.CalculateSenderRate(
			4096, 4096, TimeSpan.FromSeconds(1)));
		Assert.Null(OutputRoutingHealthViewModel.CalculateSenderRate(
			4096, 2048, TimeSpan.FromSeconds(1)));
		Assert.Null(OutputRoutingHealthViewModel.CalculateSenderRate(
			1024, 2048, TimeSpan.FromMilliseconds(100)));
		Assert.Null(OutputRoutingHealthViewModel.CalculateSenderRate(
			1024, 2048, TimeSpan.FromSeconds(20)));
	}

	[Fact]
	public void Active_alarm_acknowledgement_is_shared_and_resolution_requires_healthy_evidence()
	{
		var now = Start;
		var notifications = new NotificationService(() => now);
		var provider = new TestProvider(Snapshot("audio", SubsystemHealthState.Failed, SubsystemProgramImpact.Degraded));
		var context = new QueuedContext();
		using var readiness = new RuntimeReadinessService(() => now);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), context,
			notifications: notifications, clock: () => now);

		var active = Assert.Single(notifications.Current);
		Assert.Equal("health:audio", active.Key);
		Assert.Equal(OperatorNotificationCondition.Active, active.Condition);
		Assert.True(active.Persistent);
		Assert.Equal("ACTIVE", model.SelectedSubsystem!.AlarmLifecycle);
		Assert.True(model.AcknowledgeAlarmCommand.CanExecute(null));

		model.AcknowledgeAlarmCommand.Execute(null);
		context.Drain();
		Assert.NotNull(Assert.Single(notifications.Current).AcknowledgedAtUtc);
		Assert.Equal("ACKNOWLEDGED · STILL ACTIVE", model.SelectedSubsystem.AlarmLifecycle);
		Assert.False(model.AcknowledgeAlarmCommand.CanExecute(null));
		Assert.Equal("FAILED", model.SelectedSubsystem.State);

		for (var index = 0; index < 5000; index++)
			provider.Publish(Snapshot("audio", SubsystemHealthState.Failed, SubsystemProgramImpact.Degraded) with
			{ Detail = $"raw measurement {index}" });
		Assert.Single(context.Pending);
		context.Drain();
		Assert.Equal(1, Assert.Single(notifications.Current).OccurrenceCount);
		Assert.Equal("FAILED", model.SelectedSubsystem.State);

		now = now.AddSeconds(5);
		provider.Publish(Snapshot("audio", SubsystemHealthState.Healthy, SubsystemProgramImpact.None));
		context.Drain();
		Assert.Equal("RESOLVED", model.SelectedSubsystem.AlarmLifecycle);
		Assert.Equal(OperatorNotificationCondition.Resolved, Assert.Single(notifications.Current).Condition);
		Assert.False(model.AcknowledgeAlarmCommand.CanExecute(null));
	}

	[Fact]
	public void Lost_health_evidence_never_clears_an_active_alarm_or_certifies_program()
	{
		var now = Start;
		var notifications = new NotificationService(() => now);
		var provider = new TestProvider(Snapshot("runtime", SubsystemHealthState.Failed, SubsystemProgramImpact.Unknown));
		var context = new QueuedContext();
		using var readiness = new RuntimeReadinessService(() => now);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), context,
			notifications: notifications, clock: () => now);

		now = now.AddSeconds(1);
		provider.Publish(Snapshot("runtime", SubsystemHealthState.Unknown, SubsystemProgramImpact.Unknown) with
		{ Metrics = [new("Evidence status", "STALE")] });
		context.Drain();
		Assert.Equal("STALE / UNVERIFIED", model.SelectedSubsystem!.AlarmLifecycle);
		Assert.Equal("UNKNOWN", model.SelectedSubsystem.State);
		Assert.Equal(OperatorNotificationCondition.Active, Assert.Single(notifications.Current).Condition);
		Assert.Contains("evidence unavailable", Assert.Single(notifications.Current).Title, StringComparison.OrdinalIgnoreCase);
		Assert.False(readiness.Current.IsProductionReady);
	}

	[Fact]
	public void Missing_source_retains_stale_incident_in_health_and_does_not_resolve_notification()
	{
		var now = Start;
		var notifications = new NotificationService(() => now);
		var provider = new TestProvider(Snapshot("transport-program", SubsystemHealthState.Failed, SubsystemProgramImpact.OutputTransportOnly));
		var context = new QueuedContext();
		using var readiness = new RuntimeReadinessService(() => now);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), context,
			notifications: notifications, clock: () => now);

		now = now.AddSeconds(1);
		provider.Publish();
		context.Drain();
		var incident = Assert.Single(model.Attention);
		Assert.Equal("transport-program", incident.Id);
		Assert.Equal("UNKNOWN", incident.State);
		Assert.Equal("UNAVAILABLE", incident.EvidenceStatus);
		Assert.Equal("STALE / UNVERIFIED", incident.AlarmLifecycle);
		Assert.Equal(SubsystemProgramImpact.Unknown, incident.ProgramImpactValue);
		Assert.Equal(OperatorNotificationCondition.Active, Assert.Single(notifications.Current).Condition);
		Assert.Contains("evidence unavailable", Assert.Single(notifications.Current).Title, StringComparison.OrdinalIgnoreCase);
		Assert.False(readiness.Current.IsProductionReady);
	}

	[Fact]
	public void Structured_log_failure_does_not_suppress_persistent_alarm_notification()
	{
		var provider = new TestProvider(Snapshot("audio", SubsystemHealthState.Failed, SubsystemProgramImpact.Degraded));
		var notifications = new NotificationService(() => Start);
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), new QueuedContext(),
			notifications: notifications,
			logAlarmTransition: _ => throw new InvalidOperationException("log endpoint unavailable"),
			clock: () => Start);

		Assert.Equal(OperatorNotificationCondition.Active, Assert.Single(notifications.Current).Condition);
		Assert.Equal("FAILED", model.SelectedSubsystem!.State);
		Assert.True(model.AcknowledgeAlarmCommand.CanExecute(null));
	}

	[Fact]
	public void Telemetry_delivery_failure_is_fail_open_for_health_presentation()
	{
		var provider = new TestProvider(Snapshot("output", SubsystemHealthState.Degraded, SubsystemProgramImpact.MonitoringOnly));
		var notifications = new NotificationService(() => Start);
		notifications.Published += (_, _) => throw new InvalidOperationException("subscriber offline");
		using var readiness = new RuntimeReadinessService(() => Start);
		using var model = new HealthCenterViewModel(provider, readiness, new TestCommand(), new QueuedContext(),
			notifications: notifications, clock: () => Start);
		Assert.Equal("DEGRADED", model.SelectedSubsystem!.State);
		Assert.Equal("ACTIVE", model.SelectedSubsystem.AlarmLifecycle);
		Assert.Equal(RuntimeReadinessState.Initializing, readiness.Current.State);
	}

	private static SubsystemHealthSnapshot Snapshot(string id, SubsystemHealthState state, SubsystemProgramImpact impact) =>
		new(id, id, "Diagnostics", state, "Published evidence.", Start, null, [],
			"Review the owning workspace.", "metadata only", ProgramImpact: impact,
			EvidenceSource: "simulated confirmed snapshot", LastObservedAt: Start);

	private sealed class TestProvider(params SubsystemHealthSnapshot[] initial) : IHealthSnapshotProvider
	{
		private IReadOnlyList<SubsystemHealthSnapshot> _current = initial;
		public event EventHandler<HealthSnapshotChangedEventArgs>? Changed;
		public IReadOnlyList<SubsystemHealthSnapshot> GetCurrent() => _current;
		public void Publish(params SubsystemHealthSnapshot[] snapshots)
		{
			_current = snapshots;
			Changed?.Invoke(this, new HealthSnapshotChangedEventArgs(snapshots));
		}
	}

	private sealed class QueuedContext : SynchronizationContext
	{
		public Queue<Action> Pending { get; } = new();
		public override void Post(SendOrPostCallback d, object? state) => Pending.Enqueue(() => d(state));
		public void Drain()
		{
			while (Pending.TryDequeue(out var action))
				action();
		}
	}

	private sealed class TestCommand : ICommand
	{
		private EventHandler? _changed;
		private bool _enabled = true;
		public int Subscribers { get; private set; }
		public int Executions { get; private set; }
		public bool Enabled
		{
			get => _enabled;
			set { _enabled = value; _changed?.Invoke(this, EventArgs.Empty); }
		}
		public event EventHandler? CanExecuteChanged
		{
			add { _changed += value; Subscribers++; }
			remove { _changed -= value; Subscribers--; }
		}
		public bool CanExecute(object? parameter) => Enabled;
		public void Execute(object? parameter) { if (Enabled) Executions++; }
	}
}
