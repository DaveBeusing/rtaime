// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed partial class AudioMixerMutationTests
{
	[Fact]
	public async Task Pending_source_commit_keeps_confirmed_routing_and_preserves_other_source_and_bus_drafts()
	{
		var transport = new AudioTransport();
		await using var owner = new OperatorViewModel(new OperatorControlClient(transport), runtimeReadiness: new ReadyService());
		owner.SynchronizeCommand.Execute(null);
		Assert.True(owner.IsConnected);
		var source = owner.AudioMixer.Sources[0];
		var other = owner.AudioMixer.Sources[1];
		var bus = owner.AudioMixer.Buses[0];
		source.Gain = 0.5;
		other.Gain = 0.75;
		bus.MasterGain = 0.25;
		transport.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var commit = owner.AudioMixer.ApplySourceAsync(source);
		Assert.Equal("PENDING", source.DraftState);
		Assert.False(source.CanEditDraft);
		Assert.False(other.ApplyCommand.CanExecute(null));
		Assert.Equal(1, transport.Configuration.Sources[0].Gain);
		transport.Hold.SetResult();
		Assert.True(await commit);
		Assert.Equal("CONFIRMED", source.DraftState);
		Assert.Equal(0.5, transport.Configuration.Sources[0].Gain);
		Assert.Equal(2UL, transport.Configuration.Revision);
		Assert.Equal(0.75, other.Gain);
		Assert.Equal(0.25, bus.MasterGain);
		Assert.True(other.HasDraftChanges);
		Assert.True(bus.HasDraftChanges);
		Assert.Equal(1, transport.Configuration.Sources[1].Gain);
		Assert.Equal(1, transport.Configuration.Buses[0].MasterGain);
	}

	[Fact]
	public async Task Rejection_keeps_local_draft_and_confirmed_mute_routing_until_explicit_discard()
	{
		var transport = new AudioTransport { Reject = true };
		await using var owner = new OperatorViewModel(new OperatorControlClient(transport), runtimeReadiness: new ReadyService());
		owner.SynchronizeCommand.Execute(null);
		var source = owner.AudioMixer.Sources[0];
		source.Gain = 2;
		source.FollowRoutedSource = false;
		Assert.False(await owner.AudioMixer.ApplySourceAsync(source));
		Assert.Equal("REJECTED", source.DraftState);
		Assert.Equal(2, source.Gain);
		Assert.Equal("ROUTED", source.ContributionLabel);
		Assert.Equal(1UL, transport.Configuration.Revision);
		Assert.False(transport.Configuration.Sources[0].Muted);
		Assert.Equal(1, transport.MutationCalls);
		Assert.True(transport.SnapshotCalls >= 2);
		source.DiscardDraftCommand.Execute(null);
		Assert.Equal(1, source.Gain);
		Assert.False(source.HasDraftChanges);
	}

	[Fact]
	public async Task Lost_control_connection_disables_mutations_and_marks_retained_meter_evidence_stale()
	{
		var transport = new AudioTransport();
		await using var owner = new OperatorViewModel(new OperatorControlClient(transport), runtimeReadiness: new ReadyService());
		owner.SynchronizeCommand.Execute(null);
		var source = owner.AudioMixer.Sources[0];
		transport.FailSnapshots = true;
		owner.SynchronizeCommand.Execute(null);
		Assert.True(owner.IsStale);
		Assert.False(source.ToggleMuteCommand.CanExecute(null));
		Assert.False(source.HasMeterEvidence);
		Assert.Equal("STALE", owner.AudioMixer.Availability);
		Assert.False(await owner.AudioMixer.ToggleSourceMuteAsync(source));
		Assert.Equal(0, transport.MutationCalls);
		transport.FailSnapshots = false;
		owner.SynchronizeCommand.Execute(null);
		Assert.False(owner.IsStale);
		Assert.True(source.HasMeterEvidence);
		Assert.True(source.ToggleMuteCommand.CanExecute(null));
	}

	private sealed class AudioTransport : IOperatorControlTransport
	{
		private readonly MediaSourceId[] _sources = [new(Identity.New()), new(Identity.New())];
		private readonly ProductionId _production = ProductionId.New();
		internal AudioProductionConfiguration Configuration { get; private set; }
		internal TaskCompletionSource? Hold { get; set; }
		internal bool Reject { get; set; }
		internal bool FailSnapshots { get; set; }
		internal int MutationCalls { get; private set; }
		internal int SnapshotCalls { get; private set; }

		internal AudioTransport() => Configuration = new AudioProductionConfiguration(1,
			new[] { new AudioProductionBusConfiguration(AudioBusId.Program, 1, false) },
			_sources.Select(id => new AudioProductionSourceConfiguration(id, 1, false, true, new[] { AudioBusId.Program })).ToArray());

		private OperatorAudioProductionDescriptor Evidence() => new(Configuration, 0.2, 0.1, 0.2, false, 0, 1, 0, true, null, 1, 0,
			new[] { new OperatorAudioProductionBusDescriptor("program", Configuration.Buses[0].MasterGain,
				Configuration.Buses[0].Muted, 0.2, 0.1, 0.2, false, 0, 1, 0) });

		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			SnapshotCalls++;
			if (FailSnapshots)
				return ValueTask.FromException<OperatorStatusSnapshot>(new TimeoutException("Control unavailable."));
			var id = new ProductionSourceId(_sources[0].Value);
			return ValueTask.FromResult(new OperatorStatusSnapshot(
				new AuthoritativeProductionState(ControlContractVersion.Current, _production, new Revision(1), new ProductionRoutingState(id, id)),
				_sources.Select((source, index) => new OperatorSourceDescriptor(source.ToString(), $"Camera {index + 1}")).ToArray(),
				"READY", "LOCKED", "HEALTHY", "OFF", "IDLE", false, 0.2,
				audioInputs: Configuration.Sources.Select(source => new OperatorAudioInputDescriptor(source.SourceId.ToString(),
					"stream", source.Gain, source.Muted, 0.2, 0.1, 0.2, false, "HEALTHY")).ToArray(),
				audioProgram: new OperatorAudioProgramDescriptor(id.ToString(), "stream", 1, false, 0.2, 0.1, 0.2, false, "HEALTHY"),
				audioProduction: Evidence()));
		}

		public async ValueTask<OperatorAudioProductionDescriptor> SetAudioProductionAsync(
			AudioProductionConfiguration configuration, ulong expectedRevision, CancellationToken cancellationToken = default)
		{
			MutationCalls++;
			if (Hold is not null)
				await Hold.Task.WaitAsync(cancellationToken);
			if (Reject)
				throw new InvalidOperationException("control.audio.production.revision_conflict");
			Assert.Equal(Configuration.Revision, expectedRevision);
			Configuration = configuration;
			return Evidence();
		}

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(SelectPreviewCommand command, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
		public ValueTask<OperatorMutationResponse> CutProgramAsync(CutProgramCommand command, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(DissolveProgramCommand command, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<OperatorMutationResponse>(new NotSupportedException());
	}

	private sealed class ReadyService : IRuntimeReadinessService
	{
		public RuntimeReadinessSnapshot Current { get; } = new(RuntimeReadinessState.Ready, DateTimeOffset.UnixEpoch,
			DateTimeOffset.UnixEpoch, Array.Empty<ReadinessReason>(), true, RuntimePerformanceVerificationSnapshot.Unverified);
		public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed { add { } remove { } }
		public void Observe(RuntimeReadinessObservation observation) { }
		public void InvalidatePerformance(string detail) { }
	}
}
