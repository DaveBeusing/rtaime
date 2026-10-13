// Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.Runtime;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class DeterministicRuntimeBoundaryTests
{
	[Fact]
	public async Task Snapshot_remains_available_while_gpu_composite_is_blocked()
	{
		using var backend = new BlockingCompositeBackend();
		await using var fixture = CreateFixture(backend);
		var initialBinding = Assert.Single(fixture.Prepared.Bindings, binding => binding.OutputRoleId == "program");
		AssertCommitted(fixture.Runtime.ApplyExecution(fixture.Prepared, initialBinding.MediaSinkId!.Value));

		var boundaryTask = Task.Run(() => fixture.Runtime.ProcessNextBoundary());
		await backend.CompositeEntered.WaitAsync(TimeSpan.FromSeconds(3));

		var snapshotTask = Task.Run(() => fixture.Runtime.Snapshot);
		var snapshot = await snapshotTask.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(0UL, snapshot.NextSequenceNumber);
		Assert.True(snapshot.ActiveGpuSurfaces >= 1);
		var program = Assert.Single(snapshot.OutputRoles!, role => role.RoleId == "program");
		Assert.Equal(RuntimeOutputRoleHealthState.Unverified, program.HealthState);

		backend.ReleaseComposite();
		using var boundary = await boundaryTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(0UL, boundary.SequenceNumber);
		var published = fixture.Runtime.Snapshot;
		Assert.Equal(1UL, published.NextSequenceNumber);
		Assert.Equal(0, published.ActiveGpuSurfaces);
		Assert.Equal(
			RuntimeOutputRoleHealthState.Healthy,
			Assert.Single(published.OutputRoles!, role => role.RoleId == "program").HealthState);
	}

	[Fact]
	public async Task Control_mutation_during_heavy_execution_applies_to_the_next_boundary_only()
	{
		using var backend = new BlockingCompositeBackend();
		await using var fixture = CreateFixture(backend);
		var initialBinding = Assert.Single(fixture.Prepared.Bindings, binding => binding.OutputRoleId == "program");
		AssertCommitted(fixture.Runtime.ApplyExecution(fixture.Prepared, initialBinding.MediaSinkId!.Value));

		var boundaryTask = Task.Run(() => fixture.Runtime.ProcessNextBoundary());
		await backend.CompositeEntered.WaitAsync(TimeSpan.FromSeconds(3));

		fixture.Runtime.SetVisualLayerMode(V1VisualLayerMode.Dynamic);
		backend.ReleaseComposite();
		using var first = await boundaryTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(V1VisualLayerMode.Static, first.VisualLayerMode);
		using var second = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(V1VisualLayerMode.Dynamic, second.VisualLayerMode);
	}

	[Fact]
	public async Task Execution_commit_arriving_during_boundary_is_serialized_between_frames()
	{
		using var backend = new BlockingCompositeBackend();
		await using var fixture = CreateFixture(backend);
		var initialBinding = Assert.Single(fixture.Prepared.Bindings, binding => binding.OutputRoleId == "program");
		var sinkA = initialBinding.MediaSinkId!.Value;
		AssertCommitted(fixture.Runtime.ApplyExecution(fixture.Prepared, sinkA));
		var outputA = GetPrivateField<VirtualVideoOutput>(fixture.Runtime, "_programOutput");

		var boundaryTask = Task.Run(() => fixture.Runtime.ProcessNextBoundary());
		await backend.CompositeEntered.WaitAsync(TimeSpan.FromSeconds(3));

		var sinkB = MediaSinkId.New();
		var preparedB = RebindProgram(fixture.Prepared, sinkB);
		var applyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var applyTask = Task.Run(() =>
		{
			applyStarted.TrySetResult(true);
			return fixture.Runtime.ApplyExecution(preparedB, sinkB);
		});
		await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
		await Task.Delay(50);
		Assert.False(applyTask.IsCompleted);

		backend.ReleaseComposite();
		using var first = await boundaryTask.WaitAsync(TimeSpan.FromSeconds(5));
		var applied = await applyTask.WaitAsync(TimeSpan.FromSeconds(5));
		AssertCommitted(applied);

		Assert.Equal(0UL, first.SequenceNumber);
		Assert.Equal(1UL, outputA.TotalFramesWritten);
		Assert.Equal(sinkB, fixture.Runtime.ProgramOutputSinkId);
		Assert.Equal(RuntimeOutputRoleHealthState.Unverified, ProgramRole(fixture.Runtime).HealthState);

		using var second = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(1UL, second.SequenceNumber);
		var outputB = GetPrivateField<VirtualVideoOutput>(fixture.Runtime, "_programOutput");
		Assert.NotSame(outputA, outputB);
		Assert.Equal(sinkB, outputB.SinkId);
		Assert.Equal(1UL, outputB.TotalFramesWritten);
		Assert.Equal(1UL, outputA.TotalFramesWritten);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, ProgramRole(fixture.Runtime).HealthState);
	}


	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Aux_health_retains_completed_evidence_while_the_next_boundary_is_blocked(bool reroute)
	{
		using var backend = new BlockingCompositeBackend();
		await using var fixture = CreateFixture(backend, includeAux: true);
		var programBinding = Assert.Single(fixture.Prepared.Bindings, binding => binding.OutputRoleId == "program");
		AssertCommitted(fixture.Runtime.ApplyExecution(fixture.Prepared, programBinding.MediaSinkId!.Value));
		backend.ReleaseComposite();
		using (fixture.Runtime.ProcessNextBoundary()) { }
		var initial = Assert.Single(fixture.Runtime.Snapshot.OutputRoles!, role => role.RoleId == "aux");
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, initial.HealthState);
		Assert.Contains("frame sequence 0", initial.Evidence, StringComparison.Ordinal);

		var expectedSource = initial.SourceId;
		if (reroute)
		{
			expectedSource = programBinding.MediaSourceId!.Value;
			var bindings = fixture.Prepared.Bindings.Select(binding => binding.OutputRoleId == "aux"
				? new PreparedExecutionBinding(binding.LogicalNodeId, binding.CapabilityId, binding.Resource,
					expectedSource, binding.MediaSinkId, binding.OutputRoleId, binding.AudioBusId,
					binding.ExternalSourceId, binding.SafeSourceIdentity)
				: binding).ToArray();
			var prepared = new PreparedExecutionContract(RuntimeContractVersion.Current, PreparedExecutionId.New(),
				new AuthoritySnapshotReference(fixture.Prepared.AuthoritySnapshot.StateId,
					fixture.Prepared.AuthoritySnapshot.Revision.Next()),
				fixture.Prepared.PlanGeneration.Next(), bindings, fixture.Prepared.CompositingState);
			AssertCommitted(fixture.Runtime.ApplyExecution(prepared, programBinding.MediaSinkId.Value));
			Assert.Equal(RuntimeOutputRoleHealthState.Unverified,
				Assert.Single(fixture.Runtime.Snapshot.OutputRoles!, role => role.RoleId == "aux").HealthState);
		}

		backend.BlockNextComposite();
		var boundaryTask = Task.Run(() => fixture.Runtime.ProcessNextBoundary());
		try
		{
			await backend.CompositeEntered.WaitAsync(TimeSpan.FromSeconds(3));
			var snapshot = await Task.Run(() => fixture.Runtime.Snapshot).WaitAsync(TimeSpan.FromSeconds(2));
			Assert.Equal(1UL, snapshot.NextSequenceNumber);
			// The provider already accepted sequence 1, but Runtime has not published that boundary.
			Assert.Equal(1UL, fixture.Runtime.AuxFrames[^1].Frame.Timing.SequenceNumber);
			var aux = Assert.Single(snapshot.OutputRoles!, role => role.RoleId == "aux");
			Assert.Equal(expectedSource, aux.SourceId);
			Assert.Equal(reroute ? RuntimeOutputRoleHealthState.Unverified : RuntimeOutputRoleHealthState.Healthy,
				aux.HealthState);
			if (!reroute)
				Assert.Contains("frame sequence 0", aux.Evidence, StringComparison.Ordinal);
		}
		finally
		{
			backend.ReleaseComposite();
			using var boundary = await boundaryTask.WaitAsync(TimeSpan.FromSeconds(5));
		}

		var published = Assert.Single(fixture.Runtime.Snapshot.OutputRoles!, role => role.RoleId == "aux");
		Assert.Equal(expectedSource, published.SourceId);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, published.HealthState);
		Assert.True(published.AuthoritativeActive);
		Assert.Contains("frame sequence 1", published.Evidence, StringComparison.Ordinal);
	}


    [Fact]
    public async Task Pipeline_measurement_preserves_pixels_sequence_and_readback_ownership()
    {
        using var backend = new ManagedReferenceGpuBackend();
        await using var fixture = CreateFixture(backend);
        var program = Assert.Single(fixture.Prepared.Bindings, binding => binding.OutputRoleId == "program");
        AssertCommitted(fixture.Runtime.ApplyExecution(fixture.Prepared, program.MediaSinkId!.Value));
        using var baselineBackend = new ManagedReferenceGpuBackend();
        await using var baseline = CreateFixture(baselineBackend);
        var baselineProgram = Assert.Single(baseline.Prepared.Bindings, binding => binding.OutputRoleId == "program");
        AssertCommitted(baseline.Runtime.ApplyExecution(baseline.Prepared, baselineProgram.MediaSinkId!.Value));
        byte[] before;
        using (var boundary = baseline.Runtime.ProcessNextBoundary())
        {
            Assert.Equal(0UL, boundary.SequenceNumber);
            before = boundary.ProgramPixels.ToArray();
        }
        var probe = new RuntimeTimingQualificationProbe(new TimingQualificationThresholds(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(15)));
        fixture.Runtime.EnablePipelineTiming(probe);
        using (var boundary = fixture.Runtime.ProcessNextBoundary())
        {
            Assert.Equal(0UL, boundary.SequenceNumber);
            Assert.Equal(before, boundary.ProgramPixels.ToArray());
            Assert.Equal(1, fixture.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
        }
        Assert.Equal(0, fixture.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal(0, fixture.Runtime.Snapshot.ActiveGpuSurfaces);
        var stages = probe.PipelineSnapshot().Stages.Select(stage => stage.Stage).ToArray();
        Assert.Contains(RuntimePipelineTimingStage.ServiceBoundaryAttempt, stages);
        Assert.Contains(RuntimePipelineTimingStage.ExecutionLockWait, stages);
        Assert.Contains(RuntimePipelineTimingStage.CaptureLockHold, stages);
        Assert.Contains(RuntimePipelineTimingStage.MetadataLockHold, stages);
        Assert.Contains(RuntimePipelineTimingStage.Composite, stages);
        Assert.Contains(RuntimePipelineTimingStage.Readback, stages);
        Assert.Contains(RuntimePipelineTimingStage.ConsumerSubmission, stages);
        // Only the process loop can measure external admission and output completion.
        Assert.DoesNotContain(RuntimePipelineTimingStage.CompleteProgramBoundary, stages);
    }

    [Fact]
    public async Task Failed_boundary_records_attempt_without_success_or_readback_evidence()
    {
        using var backend = new ManagedReferenceGpuBackend();
        await using var fixture = CreateFixture(backend);
        var probe = new RuntimeTimingQualificationProbe(new TimingQualificationThresholds(
            TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(15)));
        fixture.Runtime.EnablePipelineTiming(probe);
        Assert.Throws<InvalidOperationException>(() => fixture.Runtime.ProcessNextBoundary());
        var stages = probe.PipelineSnapshot().Stages.Select(stage => stage.Stage).ToArray();
        Assert.Contains(RuntimePipelineTimingStage.ServiceBoundaryAttempt, stages);
        Assert.DoesNotContain(RuntimePipelineTimingStage.CompleteProgramBoundary, stages);
        Assert.DoesNotContain(RuntimePipelineTimingStage.Readback, stages);
        Assert.Equal(0UL, fixture.Runtime.Snapshot.NextSequenceNumber);
        Assert.Equal(0, fixture.Runtime.Snapshot.ActiveGpuSurfaces);
    }

	private static Fixture CreateFixture(IGpuProcessingBackend backend, bool includeAux = false)
	{
		var sourceA = new ProductionSourceId(Identity.New());
		var sourceB = new ProductionSourceId(Identity.New());
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			new ProductionId(Identity.New()),
			"Deterministic Runtime Boundary",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Input A"),
				new ProductionSourceSpecification(sourceB, "Input B")
			},
			new ProductionRoutingState(sourceA, sourceA),
			initialOutputRoles: includeAux
				? new[] { ProductionOutputRoleState.Program(sourceA), ProductionOutputRoleState.Aux(sourceB) }
				: new[] { ProductionOutputRoleState.Program(sourceA) });

		var runtime = new V1RuntimeHostService(
			new MediaSourceId(sourceA.Value),
			new MediaSourceId(sourceB.Value),
			VideoFormat.Hd1080p50Rgba8,
			new NullRecordingWriter(),
			backend);
		// Force real compositor work so the barriers cannot be bypassed by an identity transition.
		runtime.SetVisualLayerMode(V1VisualLayerMode.Static);
		var initialized = ControlDomainEngine.Initialize(specification);
		Assert.True(initialized.Succeeded);
		var planning = CapabilityPlanningEngine.Plan(
			specification,
			initialized.State!.Authoritative,
			new ProviderRegistry(runtime.ProviderDescriptors));
		Assert.True(planning.Succeeded);
		return new Fixture(runtime, planning.PreparedExecution!);
	}

	private static PreparedExecutionContract RebindProgram(
		PreparedExecutionContract prepared,
		MediaSinkId sink)
	{
		var bindings = prepared.Bindings
			.Select(binding => string.Equals(binding.OutputRoleId, "program", StringComparison.Ordinal)
				? new PreparedExecutionBinding(
					binding.LogicalNodeId,
					binding.CapabilityId,
					binding.Resource,
					binding.MediaSourceId,
					sink,
					binding.OutputRoleId)
				: binding)
			.ToArray();
		return new PreparedExecutionContract(
			RuntimeContractVersion.Current,
			PreparedExecutionId.New(),
			new AuthoritySnapshotReference(
				prepared.AuthoritySnapshot.StateId,
				prepared.AuthoritySnapshot.Revision.Next()),
			prepared.PlanGeneration.Next(),
			bindings,
			prepared.CompositingState);
	}

	private static RuntimeOutputRoleSnapshot ProgramRole(V1RuntimeHostService runtime) =>
		Assert.Single(runtime.Snapshot.OutputRoles!, role => role.RoleId == "program");

	private static void AssertCommitted(RuntimeHostApplyResult result)
	{
		Assert.Equal(RuntimePrepareStatus.Prepared, result.Prepare.Status);
		Assert.Equal(RuntimeCommitStatus.Committed, result.Commit?.Status);
	}

	private static T GetPrivateField<T>(object instance, string fieldName)
		where T : class
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"Private field '{fieldName}' was not found.");
		return Assert.IsType<T>(field.GetValue(instance));
	}

	private sealed record Fixture(
		V1RuntimeHostService Runtime,
		PreparedExecutionContract Prepared) : IAsyncDisposable
	{
		public ValueTask DisposeAsync() => Runtime.DisposeAsync();
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

	private sealed class BlockingCompositeBackend : IGpuProcessingBackend
	{
		private readonly ManagedReferenceGpuBackend _inner = new();
		private TaskCompletionSource<bool> _compositeEntered =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private TaskCompletionSource<bool> _releaseComposite =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task CompositeEntered => _compositeEntered.Task;
		public GpuBackendInfo Info => _inner.Info;
		public SurfaceStorageDomain StorageDomain => _inner.StorageDomain;

		public void BlockNextComposite()
		{
			_compositeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_releaseComposite = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		public void ReleaseComposite() => _releaseComposite.TrySetResult(true);
		public void Start() => _inner.Start();
		public void Stop() => _inner.Stop();
		public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels) =>
			_inner.Allocate(surfaceId, format, rgbaPixels);

		public void Composite(SurfaceId outputSurfaceId, VideoFormat format, GpuCompositeOperation operation)
		{
			_compositeEntered.TrySetResult(true);
			_releaseComposite.Task.GetAwaiter().GetResult();
			_inner.Composite(outputSurfaceId, format, operation);
		}

		public byte[] Readback(SurfaceId surfaceId, VideoFormat format) =>
			_inner.Readback(surfaceId, format);

		public void ReadbackInto(SurfaceId surfaceId, VideoFormat format, Span<byte> destination) =>
			_inner.ReadbackInto(surfaceId, format, destination);

		public void Release(SurfaceId surfaceId) => _inner.Release(surfaceId);
		public void Dispose() => _inner.Dispose();
	}
}
