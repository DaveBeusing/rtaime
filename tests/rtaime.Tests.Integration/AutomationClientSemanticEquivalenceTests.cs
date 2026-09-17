// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class AutomationClientSemanticEquivalenceTests
{
	[Fact]
	public async Task Headless_clients_use_the_same_authoritative_revision_and_reconnect_semantics()
	{
		var productionId = new ProductionId(Identity.Parse("7a000000-0000-0000-0000-000000000001"));
		var sourceA = new ProductionSourceId(Identity.Parse("7a000000-0000-0000-0000-00000000000a"));
		var sourceB = new ProductionSourceId(Identity.Parse("7a000000-0000-0000-0000-00000000000b"));
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			productionId,
			"V1 Automation Equivalence",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Input 1"),
				new ProductionSourceSpecification(sourceB, "Input 2")
			},
			new ProductionRoutingState(sourceA, sourceA));

		await using var runtime = new V1RuntimeHostService(
			new MediaSourceId(sourceA.Value),
			new MediaSourceId(sourceB.Value),
			VideoFormat.Hd1080p50Rgba8,
			new NoopRecordingWriter());
		var control = new ControlHostService(specification, runtime.ProviderDescriptors);
		var transport = new HeadlessControlTransport(control, runtime, specification);
		transport.Initialize();

		var automation = new OperatorControlClient(transport);
		var competingClient = new OperatorControlClient(transport);
		var automationInitial = await automation.SynchronizeAsync();
		var competingInitial = await competingClient.SynchronizeAsync();
		Assert.Equal(automationInitial.Production.Revision, competingInitial.Production.Revision);

		var preview = await automation.SelectPreviewAsync(sourceB.ToString());
		Assert.True(preview.Accepted, preview.Failure?.ToString());
		Assert.Equal(sourceB, automation.Snapshot!.Production.Routing.PreviewSourceId);
		Assert.Equal(sourceA, automation.Snapshot.Production.Routing.ProgramSourceId);

		var stale = await competingClient.CutAsync(sourceB.ToString());
		Assert.False(stale.Accepted);
		Assert.NotNull(stale.Failure);
		Assert.Equal(competingInitial.Production.Revision, competingClient.Snapshot!.Production.Revision);
		Assert.Equal(control.State.Revision, stale.State.Revision);

		competingClient.Disconnect();
		Assert.False(competingClient.Connected);
		var resynchronized = await competingClient.SynchronizeAsync();
		Assert.Equal(control.State.Revision, resynchronized.Production.Revision);
		Assert.Equal(sourceB, resynchronized.Production.Routing.PreviewSourceId);

		var cut = await competingClient.CutPreviewAsync();
		Assert.True(cut.Accepted, cut.Failure?.ToString());
		Assert.Equal(sourceB, control.State.Routing.ProgramSourceId);

		automation.Disconnect();
		var afterCut = await automation.SynchronizeAsync();
		Assert.Equal(control.State.Revision, afterCut.Production.Revision);
		Assert.Equal(sourceB, afterCut.Production.Routing.ProgramSourceId);

		var previewBack = await automation.SelectPreviewAsync(sourceA.ToString());
		Assert.True(previewBack.Accepted, previewBack.Failure?.ToString());
		var dissolve = await automation.DissolvePreviewAsync(3);
		Assert.True(dissolve.Accepted, dissolve.Failure?.ToString());
		Assert.Equal(sourceA, control.State.Routing.ProgramSourceId);
		Assert.Equal(control.State.Revision, automation.Snapshot!.Production.Revision);
	}

	private sealed class HeadlessControlTransport : IOperatorControlTransport
	{
		private readonly ControlHostService _control;
		private readonly V1RuntimeHostService _runtime;
		private readonly ProductionSpecification _specification;

		public HeadlessControlTransport(
			ControlHostService control,
			V1RuntimeHostService runtime,
			ProductionSpecification specification)
		{
			_control = control;
			_runtime = runtime;
			_specification = specification;
		}

		public void Initialize()
		{
			var initialized = Apply(_control.Initialize());
			if (!initialized.Accepted)
				throw new InvalidOperationException(initialized.Failure?.Message ?? "Initial Runtime commit failed.");
		}

		public ValueTask<OperatorStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var runtime = _runtime.Snapshot;
			var sources = _specification.Sources
				.Select(source => new OperatorSourceDescriptor(source.SourceId.ToString(), source.Name))
				.ToArray();
			return ValueTask.FromResult(new OperatorStatusSnapshot(
				_control.State,
				sources,
				runtime.Runtime.Status.ToString(),
				runtime.TimingHealth.ToString(),
				"Valid",
				"Optional",
				runtime.Recording.State.ToString(),
				runtime.VisualLayerMode != V1VisualLayerMode.Disabled,
				runtime.Audio.LastPeakLevel));
		}

		public ValueTask<OperatorMutationResponse> SelectPreviewAsync(
			SelectPreviewCommand command,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(Apply(_control.SelectPreview(command)));
		}

		public ValueTask<OperatorMutationResponse> CutProgramAsync(
			CutProgramCommand command,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(Apply(_control.CutProgram(command)));
		}

		public ValueTask<OperatorMutationResponse> DissolveProgramAsync(
			DissolveProgramCommand command,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return ValueTask.FromResult(Apply(_control.DissolveProgram(command)));
		}

		private OperatorMutationResponse Apply(ControlHostOperationResult staged)
		{
			if (!staged.Accepted || staged.Execution is null)
			{
				return new OperatorMutationResponse(
					false,
					staged.State,
					staged.Failure ?? new Failure("control.operation.rejected", "Control operation was rejected."));
			}

			var execution = staged.Execution;
			var runtime = _runtime.ApplyExecution(
				execution.PreparedExecution,
				execution.ProgramSinkId,
				execution.ProgramTransition);
			var runtimeCommit = runtime.Commit ?? new RuntimeCommitResult(
				RuntimeContractVersion.Current,
				RuntimeCommitStatus.Rejected,
				null,
				_runtime.Snapshot.Runtime.ExecutionRevision,
				runtime.Prepare.Failure ?? new Failure("runtime.prepare.rejected", "Runtime prepare was rejected."));
			var confirmed = _control.ConfirmRuntimeCommit(execution.PreparedExecution.PreparedExecutionId, runtimeCommit);
			if (!confirmed.Committed || confirmed.State is null)
			{
				return new OperatorMutationResponse(
					false,
					confirmed.State ?? staged.State,
					confirmed.Failure ?? new Failure("control.commit.rejected", "Cross-host commit was rejected."));
			}

			return new OperatorMutationResponse(true, confirmed.State, null);
		}
	}

	private sealed class NoopRecordingWriter : IProgramRecordingWriter
	{
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
		public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
	}
}
