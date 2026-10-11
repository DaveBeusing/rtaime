// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Windows.Input;
using rtaime.Client;
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
	[InlineData("CONNECTED", true, 10, "DEGRADED")]
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
