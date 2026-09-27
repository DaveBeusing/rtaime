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

		fixture.Runtime.SetVisualLayerMode(V1VisualLayerMode.Static);
		backend.ReleaseComposite();
		using var first = await boundaryTask.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(V1VisualLayerMode.Disabled, first.VisualLayerMode);
		using var second = fixture.Runtime.ProcessNextBoundary();
		Assert.Equal(V1VisualLayerMode.Static, second.VisualLayerMode);
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

	private static Fixture CreateFixture(IGpuProcessingBackend backend)
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
			new ProductionRoutingState(sourceA, sourceA));

		var runtime = new V1RuntimeHostService(
			new MediaSourceId(sourceA.Value),
			new MediaSourceId(sourceB.Value),
			VideoFormat.Hd1080p50Rgba8,
			new NullRecordingWriter(),
			backend);
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
		private readonly TaskCompletionSource<bool> _compositeEntered =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource<bool> _releaseComposite =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task CompositeEntered => _compositeEntered.Task;
		public GpuBackendInfo Info => _inner.Info;
		public SurfaceStorageDomain StorageDomain => _inner.StorageDomain;

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
