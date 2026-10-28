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

public sealed class MediaReferenceClockAdmissionTests
{
	[Fact]
	public async Task Synchronized_route_rejects_unverified_clock_before_any_runtime_commit()
	{
		var (runtime, prepared, sink) = MakeFixture();
		await using (runtime)
		{
			var original = runtime.Snapshot.Runtime;
			runtime.ConfigureReferenceClockRequirement(MediaReferenceClockRequirement.HardwareLocked);
			var refused = runtime.ApplyExecution(prepared, sink);
			Assert.Equal(RuntimePrepareStatus.Rejected, refused.Prepare.Status);
			Assert.Equal("runtime.clock.reference_unverified", refused.Prepare.Failure?.Code);
			Assert.Null(refused.Commit);
			Assert.Equal(original, runtime.Snapshot.Runtime);
			Assert.Null(runtime.ProgramOutputSinkId);
			Assert.Equal(MediaReferenceEvidence.Unverified, runtime.Snapshot.ClockReference!.Evidence);
			runtime.ConfigureReferenceClockRequirement(MediaReferenceClockRequirement.Asynchronous);
			var accepted = runtime.ApplyExecution(prepared, sink);
			Assert.Equal(RuntimeCommitStatus.Committed, accepted.Commit?.Status);
			using var frame = runtime.ProcessNextBoundary();
			Assert.Equal(0UL, frame.SequenceNumber);
		}
	}

	[Fact]
	public async Task Opting_in_after_committed_program_does_not_revoke_unrelated_legacy_output()
	{
		var (runtime, prepared, sink) = MakeFixture();
		await using (runtime)
		{
			Assert.Equal(RuntimeCommitStatus.Committed, runtime.ApplyExecution(prepared, sink).Commit!.Status);
			using (runtime.ProcessNextBoundary()) { }
			runtime.ConfigureReferenceClockRequirement(MediaReferenceClockRequirement.QualifiedHoldover);
			var refused = runtime.ApplyExecution(prepared, sink);
			Assert.Equal(RuntimePrepareStatus.Rejected, refused.Prepare.Status);
			Assert.Null(refused.Commit);
			Assert.Equal(sink, runtime.ProgramOutputSinkId);
			using var second = runtime.ProcessNextBoundary();
			Assert.Equal(1UL, second.SequenceNumber);
			Assert.Equal(sink, runtime.ProgramOutputSinkId);
		}
	}

	[Fact]
	public async Task Runtime_only_accepts_source_matched_provider_clock_facts_not_virtual_hardware_claims()
	{
		var (runtime, _, _) = MakeFixture();
		await using (runtime)
		{
			var provider = new rtaime.Runtime.VirtualMediaReferenceClock();
			var now = DateTimeOffset.UtcNow;
			provider.ObserveSync(TimeSpan.Zero, now, "0011223344556677", 1, 0);
			runtime.ObserveReferenceClock(provider, TimeSpan.FromSeconds(1), now.AddSeconds(1));
			var observation = runtime.Snapshot.ClockReference;
			Assert.NotNull(observation);
			Assert.Equal(MediaReferenceClockSource.Virtual, observation.Source);
			Assert.Equal(MediaReferenceLockState.Locked, observation.LockState);
			Assert.Equal(MediaReferenceEvidence.Simulated, observation.Evidence);
			Assert.Equal(observation.EpochId, runtime.ReferenceClock.EpochId);
			runtime.ConfigureReferenceClockRequirement(MediaReferenceClockRequirement.HardwareLocked);
			Assert.False(MediaReferenceAdmission.Allows(MediaReferenceClockRequirement.HardwareLocked,
				observation, now.AddSeconds(1)));
		}
	}

		[Fact]
	public void Runtime_options_reject_invalid_external_clock_requirement_without_changing_defaults()
	{
		var baseline = RuntimeHostProcessOptions.Load([], _ => null);
		Assert.Equal(MediaReferenceClockRequirement.Asynchronous, baseline.ClockRequirement);
		var checkedOptions = RuntimeHostProcessOptions.Load(["--clock-requirement=hardware-locked"], _ => null);
		Assert.Equal(MediaReferenceClockRequirement.HardwareLocked, checkedOptions.ClockRequirement);
		Assert.Equal(MediaReferenceClockRequirement.QualifiedHoldover,
			RuntimeHostProcessOptions.Load(["--clock-requirement=qualified-holdover"], _ => null).ClockRequirement);
		Assert.Throws<ArgumentException>(() =>
			RuntimeHostProcessOptions.Load(["--clock-requirement=unknown"], _ => null));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			(baseline with { ClockRequirement = (MediaReferenceClockRequirement)255 }).Validate());
	}

	private static (RuntimeProgramExecutionService Runtime,
		PreparedExecutionContract Prepared, MediaSinkId Sink) MakeFixture()
	{
		var sourceA = new ProductionSourceId(Identity.Parse("aa000000-0000-0000-0000-000000000001"));
		var sourceB = new ProductionSourceId(Identity.Parse("aa000000-0000-0000-0000-000000000002"));
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			new ProductionId(Identity.Parse("aa000000-0000-0000-0000-000000000099")),
			"Reference clock admission",
			[
				new ProductionSourceSpecification(sourceA, "A"),
				new ProductionSourceSpecification(sourceB, "B")
			],
			new ProductionRoutingState(sourceA, sourceA),
			initialOutputRoles: [ProductionOutputRoleState.Program(sourceA)]);
		var runtime = new RuntimeProgramExecutionService(
			new MediaSourceId(sourceA.Value), new MediaSourceId(sourceB.Value),
			VideoFormat.Hd1080p50Rgba8, new NullRecordingWriter());
		var initialized = ControlDomainEngine.Initialize(specification);
		if (!initialized.Succeeded)
			throw new InvalidOperationException("Fixture control state failed.");
		var planning = CapabilityPlanningEngine.Plan(specification,
			initialized.State!.Authoritative, new ProviderRegistry(runtime.ProviderDescriptors));
		if (!planning.Succeeded)
			throw new InvalidOperationException("Fixture provider admission failed.");
		var prepared = planning.PreparedExecution!;
		var sink = Assert.Single(prepared.Bindings, b => b.OutputRoleId == "program").MediaSinkId!.Value;
		return (runtime, prepared, sink);
	}

	private sealed class ProviderRegistry(IReadOnlyList<ProviderDescriptor> providers) : IProviderCapabilityRegistry
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
