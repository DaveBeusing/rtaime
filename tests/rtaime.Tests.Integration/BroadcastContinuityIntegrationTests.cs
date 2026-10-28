// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class BroadcastContinuityIntegrationTests
{
	[Fact]
	public async Task Committed_program_tracks_only_complete_frame_and_audio_and_never_claims_hardware_fallback()
	{
		await using var test = CreateFixture();
		var (prepared, program) = test.Plan();
		Assert.Equal(RuntimeCommitStatus.Committed, test.Runtime.ApplyExecution(prepared, program).Commit!.Status);
		var pending = Assert.IsType<BroadcastContinuitySnapshot>(test.Runtime.GetContinuityObservation("program"));
		Assert.Equal(BroadcastContinuityPhase.AwaitingFirstBoundary, pending.Phase);
		Assert.Null(pending.LastCompleteFrameSequence);
		Assert.False(pending.HasQualifiedFallbackDelivery);
		using (var result = test.Runtime.ProcessNextBoundary())
		{
			var complete = Assert.IsType<BroadcastContinuitySnapshot>(test.Runtime.GetContinuityObservation("program"));
			Assert.Equal(BroadcastContinuityPhase.ObservedComplete, complete.Phase);
			Assert.Equal(result.SequenceNumber, complete.LastCompleteFrameSequence);
			Assert.NotNull(complete.LastCompleteAudioSamplePosition);
			Assert.Equal(prepared.AuthoritySnapshot.Revision.Value, complete.AuthorityRevision);
			Assert.Equal(prepared.AuthoritySnapshot.StateId.ToString(), complete.AuthorityStateId);
		}
		var role = Assert.Single(test.Runtime.Snapshot.OutputRoles!, x => x.RoleId == "program");
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, role.HealthState);
		Assert.Contains("CONTINUITY SOFTWARE OBSERVED", role.Evidence, StringComparison.Ordinal);
		Assert.Contains("physical scan-out UNVERIFIED", role.Evidence, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Program_input_loss_latches_an_incident_and_recovery_does_not_silently_rearm()
	{
		await using var test = CreateFixture();
		var (prepared, program) = test.Plan();
		Assert.Equal(RuntimeCommitStatus.Committed, test.Runtime.ApplyExecution(prepared, program).Commit!.Status);
		using (test.Runtime.ProcessNextBoundary()) { }
		var first = test.Runtime.GetContinuityObservation("program")!;
		Assert.Equal(BroadcastContinuityPhase.ObservedComplete, first.Phase);
		var authoritativeSource = Assert.Single(prepared.Bindings, x => x.OutputRoleId == "program").MediaSourceId!.Value;
		var programFrameCount = test.Runtime.ProgramFrames.Count;
		test.Runtime.SetInputSignalState(authoritativeSource, RuntimeInputSignalState.Lost);
		using (test.Runtime.ProcessNextBoundary()) { }
		var failed = test.Runtime.GetContinuityObservation("program")!;
		Assert.Equal(BroadcastContinuityPhase.Failed, failed.Phase);
		Assert.True(failed.RequiresManualRearm);
		Assert.Equal("runtime.continuity.input_lost", failed.IncidentCode);
		Assert.Equal(first.LastCompleteFrameSequence, failed.LastCompleteFrameSequence);
		Assert.Equal(first.LastCompleteAudioSamplePosition, failed.LastCompleteAudioSamplePosition);
		Assert.Single(test.Runtime.RecentContinuityIncidents);
		Assert.Equal(RuntimeOutputRoleHealthState.Faulted,
			Assert.Single(test.Runtime.Snapshot.OutputRoles!, x => x.RoleId == "program").HealthState);
		Assert.Equal(programFrameCount + 1, test.Runtime.ProgramFrames.Count);
		// An existing black-fallback frame does not become a NO-BLACK continuity PASS.
		Assert.Equal(authoritativeSource, test.Runtime.ProgramFrames[^1].Frame.SourceId);
		test.Runtime.SetInputSignalState(authoritativeSource, RuntimeInputSignalState.Valid);
		using (test.Runtime.ProcessNextBoundary()) { }
		var recovered = test.Runtime.GetContinuityObservation("program")!;
		Assert.Equal(BroadcastContinuityPhase.RecoveredAwaitingRearm, recovered.Phase);
		Assert.Equal(failed.IncidentGeneration, recovered.IncidentGeneration);
		Assert.Equal(RuntimeOutputRoleHealthState.Unverified,
			Assert.Single(test.Runtime.Snapshot.OutputRoles!, x => x.RoleId == "program").HealthState);
		Assert.Contains("RECOVERED AWAITING RE-ARM",
			Assert.Single(test.Runtime.Snapshot.OutputRoles!, x => x.RoleId == "program").Evidence,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Lost_preview_or_aux_input_is_isolated_from_committed_program_authority()
	{
		await using var test = CreateFixture(includeAux: true);
		var (prepared, program) = test.Plan();
		Assert.Equal(RuntimeCommitStatus.Committed, test.Runtime.ApplyExecution(prepared, program).Commit!.Status);
		using (test.Runtime.ProcessNextBoundary()) { }
		var initialProgram = test.Runtime.GetContinuityObservation("program")!;
		var auxSource = Assert.Single(prepared.Bindings, x => x.OutputRoleId == "aux").MediaSourceId!.Value;
		test.Runtime.SetInputSignalState(auxSource, RuntimeInputSignalState.Lost);
		using (test.Runtime.ProcessNextBoundary()) { }

		Assert.Equal(BroadcastContinuityPhase.ObservedComplete,
			test.Runtime.GetContinuityObservation("program")!.Phase);
		Assert.Equal(BroadcastContinuityPhase.Failed,
			test.Runtime.GetContinuityObservation("aux")!.Phase);
		Assert.Equal("runtime.continuity.input_lost",
			test.Runtime.GetContinuityObservation("aux")!.IncidentCode);
		Assert.Equal(initialProgram.SourceId,
			test.Runtime.GetContinuityObservation("program")!.SourceId);
		var roles = test.Runtime.Snapshot.OutputRoles!;
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, Assert.Single(roles, x => x.RoleId == "program").HealthState);
		Assert.Equal(RuntimeOutputRoleHealthState.Faulted, Assert.Single(roles, x => x.RoleId == "aux").HealthState);
	}

	[Fact]
	public void Failed_boundaries_are_idempotent_and_incident_history_is_bounded_by_role_and_revision()
	{
		using var test = CreateFixture();
		var (plan, program) = test.Plan();
		var tracker = new RuntimeBroadcastContinuityTracker();
		tracker.ConfirmCommittedExecution(plan, 1, program);
		tracker.ObserveComplete("program", 7, 8000);
		tracker.ObserveFault("program", "test.decoder.unavailable", "Decoder recovery required.");
		tracker.ObserveFault("program", "test.decoder.unavailable", "Decoder recovery required.");
		Assert.Equal(1UL, tracker.ForRole("program")!.IncidentGeneration);
		Assert.Single(tracker.RecentIncidents);
		tracker.ObserveComplete("program", 8, 9000);
		Assert.Equal(BroadcastContinuityPhase.RecoveredAwaitingRearm, tracker.ForRole("program")!.Phase);
		Assert.False(tracker.CanReportHealthy("program"));
		// An unrelated revision with unchanged Program source/sink must not erase it.
		var sameBinding = new PreparedExecutionContract(
			RuntimeContractVersion.Current,
			PreparedExecutionId.New(),
			new AuthoritySnapshotReference(plan.AuthoritySnapshot.StateId, plan.AuthoritySnapshot.Revision.Next()),
			plan.PlanGeneration.Next(), plan.Bindings);
		tracker.ConfirmCommittedExecution(sameBinding, 2, program);
		Assert.True(tracker.ForRole("program")!.RequiresManualRearm);
		Assert.Equal(1UL, tracker.ForRole("program")!.IncidentGeneration);
		for (var i = 0; i < 45; i++)
		{
			tracker.ObserveFault("program", "test.provider." + i, "Fault injection only.");
			tracker.ObserveComplete("program", (ulong)(9 + i), (ulong)(10000 + i));
		}
		Assert.Equal(RuntimeBroadcastContinuityTracker.HistoryLimit, tracker.RecentIncidents.Count);
		Assert.Equal(BroadcastContinuityPhase.RecoveredAwaitingRearm, tracker.ForRole("program")!.Phase);
	}

	[Fact]
	public void Video_or_pcm_position_regression_is_an_incident_not_a_runtime_observer_exception()
	{
		using var test = CreateFixture();
		var (plan, sink) = test.Plan();
		var tracker = new RuntimeBroadcastContinuityTracker();
		tracker.ConfirmCommittedExecution(plan, 1, sink);
		tracker.ObserveComplete("program", 12, 12_000);
		tracker.ObserveComplete("program", 11, 13_000);
		var video = tracker.ForRole("program")!;
		Assert.Equal(BroadcastContinuityPhase.Failed, video.Phase);
		Assert.Equal("runtime.continuity.video_regression", video.IncidentCode);
		Assert.Equal(12UL, video.LastCompleteFrameSequence);
		tracker.ObserveComplete("program", 13, 13_000);
		Assert.Equal(BroadcastContinuityPhase.RecoveredAwaitingRearm,
			tracker.ForRole("program")!.Phase);
		tracker.ObserveComplete("program", 14, 12_999);
		var audio = tracker.ForRole("program")!;
		Assert.Equal(BroadcastContinuityPhase.Failed, audio.Phase);
		Assert.Equal("runtime.continuity.audio_regression", audio.IncidentCode);
		Assert.Equal(13UL, audio.LastCompleteFrameSequence);
		Assert.Equal(13_000UL, audio.LastCompleteAudioSamplePosition);
		Assert.Equal(2, tracker.RecentIncidents.Count);
	}

	[Fact]
	public void New_control_committed_source_or_sink_revokes_old_last_complete_evidence()
	{
		using var test = CreateFixture();
		var (plan, sink) = test.Plan();
		var tracker = new RuntimeBroadcastContinuityTracker();
		tracker.ConfirmCommittedExecution(plan, 1, sink);
		tracker.ObserveComplete("program", 5, 4000);
		tracker.ObserveFault("program", "test.output.stale", "Old output source is unavailable.");
		var newSource = MediaSourceId.New();
		var other = plan.Bindings.Select(binding => binding.OutputRoleId == "program"
			? new PreparedExecutionBinding(
				binding.LogicalNodeId, binding.CapabilityId, binding.Resource,
				newSource, binding.MediaSinkId, binding.OutputRoleId, binding.AudioBusId)
			: binding).ToArray();
		var newPlan = new PreparedExecutionContract(
			RuntimeContractVersion.Current, PreparedExecutionId.New(),
			new AuthoritySnapshotReference(plan.AuthoritySnapshot.StateId, plan.AuthoritySnapshot.Revision.Next()),
			plan.PlanGeneration.Next(), other);
		tracker.ConfirmCommittedExecution(newPlan, 2, sink);
		var observation = tracker.ForRole("program")!;
		Assert.Equal(BroadcastContinuityPhase.AwaitingFirstBoundary, observation.Phase);
		Assert.Null(observation.LastCompleteFrameSequence);
		Assert.Null(observation.LastCompleteAudioSamplePosition);
		Assert.Equal(newSource.ToString(), observation.SourceId);
		Assert.True(tracker.RecentIncidents.Count > 0);
	}

	private static Fixture CreateFixture(bool includeAux = false) => new(includeAux);

	private sealed class Fixture : IDisposable, IAsyncDisposable
	{
		private static readonly ProductionSourceId A = new(Identity.Parse("91000000-0000-0000-0000-00000000000a"));
		private static readonly ProductionSourceId B = new(Identity.Parse("91000000-0000-0000-0000-00000000000b"));
		private readonly ProductionSpecification _specification;
		public RuntimeProgramExecutionService Runtime { get; }

		public Fixture(bool includeAux = false)
		{
			_specification = new ProductionSpecification(
				ControlContractVersion.Current,
				new ProductionId(Identity.Parse("91000000-0000-0000-0000-000000000001")),
				"Broadcast continuity reference",
				[new ProductionSourceSpecification(A, "Input A"), new ProductionSourceSpecification(B, "Input B")],
				new ProductionRoutingState(A, A),
				initialOutputRoles: includeAux
					? [ProductionOutputRoleState.Program(A), ProductionOutputRoleState.Aux(B)]
					: [ProductionOutputRoleState.Program(A)]);
			Runtime = new RuntimeProgramExecutionService(
				new MediaSourceId(A.Value), new MediaSourceId(B.Value),
				VideoFormat.Hd1080p50Rgba8, new NullRecordingWriter());
		}

		public (PreparedExecutionContract Plan, MediaSinkId ProgramSink) Plan()
		{
			var initialized = ControlDomainEngine.Initialize(_specification);
			Assert.True(initialized.Succeeded);
			var planning = CapabilityPlanningEngine.Plan(
				_specification, initialized.State!.Authoritative,
				new Registry(Runtime.ProviderDescriptors));
			Assert.True(planning.Succeeded);
			var plan = planning.PreparedExecution!;
			return (plan, Assert.Single(plan.Bindings, x => x.OutputRoleId == "program").MediaSinkId!.Value);
		}

		public void Dispose() => Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
		public ValueTask DisposeAsync() => Runtime.DisposeAsync();
	}

	private sealed class Registry(IReadOnlyList<ProviderDescriptor> providers) : IProviderCapabilityRegistry
	{
		public IReadOnlyList<ProviderDescriptor> GetProviders() => providers;
	}

	private sealed class NullRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
