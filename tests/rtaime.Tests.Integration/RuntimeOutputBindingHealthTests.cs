// Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeOutputBindingHealthTests
{
	[Fact]
	public async Task Program_output_rebinds_to_each_committed_sink_and_old_outputs_stop_receiving_frames()
	{
		await using var fixture = CreateFixture(new NullRecordingWriter(), includeAux: true);
		var prepared = fixture.Prepared;
		var initialProgramBinding = Assert.Single(prepared.Bindings, binding => binding.OutputRoleId == "program");
		var auxBinding = Assert.Single(prepared.Bindings, binding => binding.OutputRoleId == "aux");
		var sinkA = initialProgramBinding.MediaSinkId!.Value;
		var auxSink = auxBinding.MediaSinkId!.Value;

		AssertCommitted(fixture.Runtime.ApplyExecution(prepared, sinkA));
		using (fixture.Runtime.ProcessNextBoundary())
		{
		}

		var outputA = GetPrivateField<VirtualVideoOutput>(fixture.Runtime, "_programOutput");
		Assert.Equal(sinkA, outputA.SinkId);
		Assert.Equal(1UL, outputA.TotalFramesWritten);
		Assert.Equal(sinkA, fixture.Runtime.ProgramOutputSinkId);
		Assert.Equal(auxSink, fixture.Runtime.AuxOutputSinkId);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, ProgramRole(fixture.Runtime).HealthState);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, AuxRole(fixture.Runtime).HealthState);

		var sinkB = MediaSinkId.New();
		var preparedB = RebindProgram(prepared, sinkB);
		AssertCommitted(fixture.Runtime.ApplyExecution(preparedB, sinkB));

		Assert.Equal(sinkB, fixture.Runtime.ProgramOutputSinkId);
		var pendingB = ProgramRole(fixture.Runtime);
		Assert.Equal(sinkB, pendingB.TargetId);
		Assert.Equal(RuntimeOutputRoleHealthState.Unverified, pendingB.HealthState);
		Assert.Equal(0, fixture.Runtime.ProgramFrames.Count);

		using (fixture.Runtime.ProcessNextBoundary())
		{
		}

		var outputB = GetPrivateField<VirtualVideoOutput>(fixture.Runtime, "_programOutput");
		Assert.NotSame(outputA, outputB);
		Assert.Equal(sinkB, outputB.SinkId);
		Assert.Equal(1UL, outputB.TotalFramesWritten);
		Assert.Equal(1UL, outputA.TotalFramesWritten);
		var programB = Assert.Single(fixture.Runtime.ProgramFrames);
		Assert.Equal(sinkB, programB.SinkId);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, ProgramRole(fixture.Runtime).HealthState);
		Assert.Equal(auxSink, fixture.Runtime.AuxOutputSinkId);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, AuxRole(fixture.Runtime).HealthState);

		var preparedA2 = RebindProgram(preparedB, sinkA);
		AssertCommitted(fixture.Runtime.ApplyExecution(preparedA2, sinkA));
		Assert.Equal(sinkA, fixture.Runtime.ProgramOutputSinkId);
		Assert.Equal(RuntimeOutputRoleHealthState.Unverified, ProgramRole(fixture.Runtime).HealthState);

		using (fixture.Runtime.ProcessNextBoundary())
		{
		}

		var outputA2 = GetPrivateField<VirtualVideoOutput>(fixture.Runtime, "_programOutput");
		Assert.NotSame(outputA, outputA2);
		Assert.NotSame(outputB, outputA2);
		Assert.Equal(sinkA, outputA2.SinkId);
		Assert.Equal(1UL, outputA2.TotalFramesWritten);
		Assert.Equal(1UL, outputA.TotalFramesWritten);
		Assert.Equal(1UL, outputB.TotalFramesWritten);
		var programA2 = Assert.Single(fixture.Runtime.ProgramFrames);
		Assert.Equal(sinkA, programA2.SinkId);
		var healthyA2 = ProgramRole(fixture.Runtime);
		Assert.Equal(sinkA, healthyA2.TargetId);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, healthyA2.HealthState);
		Assert.Contains(sinkA.ToString(), healthyA2.Evidence, StringComparison.Ordinal);
		Assert.Equal(auxSink, fixture.Runtime.AuxOutputSinkId);
		Assert.Equal(RuntimeOutputRoleHealthState.Healthy, AuxRole(fixture.Runtime).HealthState);
	}

	[Fact]
	public async Task Program_health_faults_when_actual_output_sink_is_stale()
	{
		await using var fixture = CreateFixture(new NullRecordingWriter(), includeAux: false);
		var initialBinding = Assert.Single(fixture.Prepared.Bindings, binding => binding.OutputRoleId == "program");
		var sinkA = initialBinding.MediaSinkId!.Value;
		AssertCommitted(fixture.Runtime.ApplyExecution(fixture.Prepared, sinkA));
		using (fixture.Runtime.ProcessNextBoundary())
		{
		}
		var staleOutput = GetPrivateField<VirtualVideoOutput>(fixture.Runtime, "_programOutput");

		var sinkB = MediaSinkId.New();
		var preparedB = RebindProgram(fixture.Prepared, sinkB);
		AssertCommitted(fixture.Runtime.ApplyExecution(preparedB, sinkB));
		Assert.Equal(sinkB, fixture.Runtime.ProgramOutputSinkId);

		SetPrivateField(fixture.Runtime, "_programOutput", staleOutput);
		var role = ProgramRole(fixture.Runtime);

		Assert.Equal(sinkB, role.TargetId);
		Assert.Equal(RuntimeOutputRoleLifecycleState.Faulted, role.LifecycleState);
		Assert.Equal(RuntimeOutputRoleHealthState.Faulted, role.HealthState);
		Assert.Equal("runtime.output.program_binding_mismatch", role.Error?.Code);
		Assert.Contains(sinkA.ToString(), role.Evidence, StringComparison.Ordinal);
		Assert.Contains(sinkB.ToString(), role.Evidence, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Active_recording_rejects_Program_sink_rebind_and_preserves_current_output_target()
	{
		await using var fixture = CreateFixture(new NullRecordingWriter(), includeAux: false);
		var initialBinding = Assert.Single(fixture.Prepared.Bindings, binding => binding.OutputRoleId == "program");
		var sinkA = initialBinding.MediaSinkId!.Value;
		AssertCommitted(fixture.Runtime.ApplyExecution(fixture.Prepared, sinkA));
		using (fixture.Runtime.ProcessNextBoundary())
		{
		}

		var start = await fixture.Runtime.StartRecordingAsync(RecordingSessionId.New(), RecordingOutputId.New());
		Assert.True(start.Succeeded, start.Failure?.ToString());

		var sinkB = MediaSinkId.New();
		var preparedB = RebindProgram(fixture.Prepared, sinkB);
		var rejected = fixture.Runtime.ApplyExecution(preparedB, sinkB);

		Assert.Equal(RuntimePrepareStatus.Rejected, rejected.Prepare.Status);
		Assert.Equal("runtime.output.program_rebind_recording_active", rejected.Prepare.Failure?.Code);
		Assert.Null(rejected.Commit);
		Assert.Equal(sinkA, fixture.Runtime.ProgramOutputSinkId);
		Assert.Equal(sinkA, ProgramRole(fixture.Runtime).TargetId);

		using (var boundary = fixture.Runtime.ProcessNextBoundary())
		{
			Assert.True(boundary.Recording is { Accepted: true });
			Assert.Equal(sinkA, fixture.Runtime.ProgramFrames[^1].SinkId);
		}

		var stop = await fixture.Runtime.StopRecordingAsync();
		Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
	}

	private static Fixture CreateFixture(IProgramRecordingWriter writer, bool includeAux)
	{
		var productionId = new ProductionId(Identity.New());
		var sourceA = new ProductionSourceId(Identity.New());
		var sourceB = new ProductionSourceId(Identity.New());
		var outputRoles = includeAux
			? new[]
			{
				ProductionOutputRoleState.Program(sourceA),
				ProductionOutputRoleState.Aux(sourceB)
			}
			: new[]
			{
				ProductionOutputRoleState.Program(sourceA)
			};
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			productionId,
			"Runtime Output Binding Health",
			new[]
			{
				new ProductionSourceSpecification(sourceA, "Input A"),
				new ProductionSourceSpecification(sourceB, "Input B")
			},
			new ProductionRoutingState(sourceA, sourceA),
			initialOutputRoles: outputRoles);

		var runtime = new V1RuntimeHostService(
			new MediaSourceId(sourceA.Value),
			new MediaSourceId(sourceB.Value),
			VideoFormat.Hd1080p50Rgba8,
			writer);
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

	private static RuntimeOutputRoleSnapshot AuxRole(V1RuntimeHostService runtime) =>
		Assert.Single(runtime.Snapshot.OutputRoles!, role => role.RoleId == "aux");

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

	private static void SetPrivateField<T>(object instance, string fieldName, T value)
		where T : class
	{
		var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException($"Private field '{fieldName}' was not found.");
		field.SetValue(instance, value);
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
}
