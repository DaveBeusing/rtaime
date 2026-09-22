// Copyright (c) 2026 Dave Beusing
// david.beusing@gmail.com

using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class OutputRoleRuntimeFailureTests
{
	[Fact]
	public async Task Aux_output_failure_is_role_scoped_and_Program_continues()
	{
		var productionId = new ProductionId(Identity.Parse("92000000-0000-0000-0000-000000000001"));
		var sourceA = new ProductionSourceId(Identity.Parse("92000000-0000-0000-0000-00000000000a"));
		var sourceB = new ProductionSourceId(Identity.Parse("92000000-0000-0000-0000-00000000000b"));
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			productionId,
			"Output Role Failure Isolation",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Input A"),
				new ProductionSourceSpecification(sourceB, "Input B")
			},
			new ProductionRoutingState(sourceA, sourceA),
			initialOutputRoles: new[]
			{
				ProductionOutputRoleState.Program(sourceA),
				ProductionOutputRoleState.Aux(sourceB)
			});

		await using var runtime = new V1RuntimeHostService(
			new MediaSourceId(sourceA.Value),
			new MediaSourceId(sourceB.Value),
			VideoFormat.Hd1080p50Rgba8,
			new NullRecordingWriter());
		var initialized = ControlDomainEngine.Initialize(specification);
		Assert.True(initialized.Succeeded);
		var planning = CapabilityPlanningEngine.Plan(
			specification,
			initialized.State!.Authoritative,
			new ProviderRegistry(runtime.ProviderDescriptors));
		Assert.True(planning.Succeeded);

		var prepared = planning.PreparedExecution!;
		var programBinding = Assert.Single(prepared.Bindings, binding => binding.OutputRoleId == "program");
		var auxBinding = Assert.Single(prepared.Bindings, binding => binding.OutputRoleId == "aux");
		Assert.NotNull(programBinding.MediaSinkId);
		Assert.NotNull(auxBinding.MediaSinkId);

		var initialApply = runtime.ApplyExecution(prepared, programBinding.MediaSinkId!.Value);
		Assert.Equal(RuntimeCommitStatus.Committed, initialApply.Commit!.Status);
		runtime.ProcessNextBoundary();
		var healthyAux = Assert.Single(runtime.Snapshot.OutputRoles!, role => role.RoleId == "aux");
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, healthyAux.HealthState);
		Assert.True(healthyAux.AuthoritativeActive);
		Assert.Null(healthyAux.Error);

		var programFramesBeforeFailure = runtime.ProgramFrames.Count;
		var unknownAuxSource = MediaSourceId.New();
		var brokenBindings = prepared.Bindings
			.Select(binding => binding.OutputRoleId == "aux"
				? new PreparedExecutionBinding(
					binding.LogicalNodeId,
					binding.CapabilityId,
					binding.Resource,
					unknownAuxSource,
					binding.MediaSinkId,
					binding.OutputRoleId)
				: binding)
			.ToArray();
		var brokenPrepared = new PreparedExecutionContract(
			RuntimeContractVersion.Current,
			PreparedExecutionId.New(),
			new AuthoritySnapshotReference(prepared.AuthoritySnapshot.StateId, prepared.AuthoritySnapshot.Revision.Next()),
			prepared.PlanGeneration.Next(),
			brokenBindings);

		var brokenApply = runtime.ApplyExecution(brokenPrepared, programBinding.MediaSinkId.Value);
		Assert.Equal(RuntimeCommitStatus.Committed, brokenApply.Commit!.Status);
		runtime.ProcessNextBoundary();

		var faultedAux = Assert.Single(runtime.Snapshot.OutputRoles!, role => role.RoleId == "aux");
		Assert.Equal(unknownAuxSource, faultedAux.SourceId);
		Assert.Equal(RuntimeOutputRoleLifecycleState.Faulted, faultedAux.LifecycleState);
		Assert.Equal(RuntimeOutputRoleHealthState.Faulted, faultedAux.HealthState);
		Assert.True(faultedAux.AuthoritativeActive);
		Assert.Equal("runtime.output.aux_source_unavailable", faultedAux.Error?.Code);
		Assert.Contains("did not produce a frame", faultedAux.Error?.Message, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(programFramesBeforeFailure + 1, runtime.ProgramFrames.Count);
		Assert.Equal(sourceA.Value, runtime.ProgramFrames[^1].Frame.SourceId.Value);
		Assert.Contains(runtime.Observations, observation => observation == "runtime.output.aux.failed:runtime.output.aux_source_unavailable");
	}

	private sealed class ProviderRegistry : IProviderCapabilityRegistry
	{
		private readonly ProviderDescriptor[] _providers;

		public ProviderRegistry(IReadOnlyList<ProviderDescriptor> providers) =>
			_providers = providers.ToArray();

		public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
	}

	private sealed class NullRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) =>
			ValueTask.CompletedTask;

		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) =>
			ValueTask.CompletedTask;

		public ValueTask FinalizeAsync(CancellationToken cancellationToken) =>
			ValueTask.CompletedTask;

		public ValueTask AbortAsync(CancellationToken cancellationToken) =>
			ValueTask.CompletedTask;
	}
}
