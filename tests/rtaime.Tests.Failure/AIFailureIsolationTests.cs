using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Failure;

public sealed class AIFailureIsolationTests
{
    [Fact]
    public async Task Inference_provider_failure_degrades_intelligence_without_changing_committed_program()
    {
        var sourceA = new MediaSourceId(Identity.Parse("d1000000-0000-0000-0000-000000000001"));
        var sourceB = new MediaSourceId(Identity.Parse("d1000000-0000-0000-0000-000000000002"));
        var sink = new MediaSinkId(Identity.Parse("d1000000-0000-0000-0000-000000000003"));
        var virtualMedia = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
        var runtime = CommitProgram(sourceA, sink, virtualMedia);
        var committedBeforeFailure = runtime.ActiveExecution!;

        var failingProvider = new ManagedReferencePersonSegmentationProvider(
            forcedFailure: new Failure("ai.provider.reference.model_failure", "Simulated model execution failure."));
        var ai = new GovernedInferenceRuntime(
            new IInferenceProvider[] { failingProvider },
            InferenceRuntimeLimits.ReferenceV1);
        var frame0 = virtualMedia.SourceA.GenerateFrame(0);

        var aiResult = await ai.ExecuteAsync(Request(frame0));
        var decision = AIResultUsePolicy.Evaluate(
            aiResult,
            frame0.Surface.SurfaceId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            minimumConfidence: 0.9);

        Assert.Equal(InferenceExecutionStatus.Failed, aiResult.Result.Status);
        Assert.False(decision.Usable);
        Assert.Equal(AIAvailabilityState.Degraded, ai.Snapshot.State);
        Assert.Equal(0u, ai.Snapshot.ActiveRequests);
        Assert.Equal(0u, ai.Snapshot.ReservedComputeUnits);
        Assert.Equal(0UL, ai.Snapshot.ReservedVramBytes);

        Assert.Same(committedBeforeFailure, runtime.ActiveExecution);
        Assert.Equal(committedBeforeFailure.ExecutionRevision, runtime.State.ExecutionRevision);
        Assert.Equal(RuntimeExecutionStatus.Committed, runtime.State.Status);

        var frame1 = virtualMedia.SourceA.GenerateFrame(1);
        Assert.Equal(sourceA, frame1.SourceId);
        Assert.Equal(1UL, frame1.Timing.SequenceNumber);
        Assert.Same(committedBeforeFailure, runtime.ActiveExecution);
    }

    private static TransactionalRuntime CommitProgram(
        MediaSourceId source,
        MediaSinkId sink,
        VirtualMediaReferenceProvider provider)
    {
        var capability = provider.Descriptor.Capabilities.Single(item => item.Kind == "media.route");
        var prepared = new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(1)),
            Generation.Initial,
            new[]
            {
                new PreparedExecutionBinding(
                    Identity.New(),
                    capability.CapabilityId,
                    provider.Descriptor.Resources[0],
                    source,
                    sink)
            });

        var runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());
        var prepare = runtime.Prepare(prepared);
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);
        var commit = runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            prepared.PreparedExecutionId,
            prepare.ReservationId!.Value,
            Revision.Initial));
        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);
        return runtime;
    }

    private static GovernedInferenceExecutionRequest Request(FrameDescriptor frame)
    {
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
            frame,
            new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(10)),
            Array.Empty<InferenceParameter>());
        return new GovernedInferenceExecutionRequest(
            AIContractVersion.Current,
            request,
            new InferenceRequestContext(
                Identity.New(),
                new InferenceProductionTime(frame.Timing.PresentationTimestamp, frame.Timing.Timebase),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
                new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));
    }
}
