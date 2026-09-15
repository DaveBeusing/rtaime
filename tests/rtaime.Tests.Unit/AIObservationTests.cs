using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;

namespace rtaime.Tests.Unit;

public sealed class AIObservationTests
{
    [Fact]
    public async Task Unavailable_capability_emits_stable_failure_observation()
    {
        var runtime = new GovernedInferenceRuntime(
            Array.Empty<IInferenceProvider>(),
            InferenceRuntimeLimits.ReferenceV1);
        var frame = new FrameDescriptor(
            MediaContractVersion.Current,
            MediaSourceId.New(),
            new SurfaceDescriptor(
                SurfaceId.New(),
                VideoFormat.Hd1080p50Rgba8,
                SurfaceStorageDomain.Shared,
                SurfaceOwnership.SharedLease,
                new SurfaceLifetimeDescriptor(Generation.Initial, Identity.New()),
                new OpaqueSurfaceHandle("unit.ai.observation", "surface")),
            new FrameTiming(0, 0, new Timebase(1, 50)));
        var request = new GovernedInferenceExecutionRequest(
            AIContractVersion.Current,
            new GovernedInferenceRequest(
                AIContractVersion.Current,
                InferenceRequestId.New(),
                ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
                frame,
                new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(10)),
                Array.Empty<InferenceParameter>()),
            new InferenceRequestContext(
                Identity.New(),
                new InferenceProductionTime(0, new Timebase(1, 50)),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
                new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));

        var result = await runtime.ExecuteAsync(request);

        Assert.Equal(InferenceExecutionStatus.Unavailable, result.Result.Status);
        var observation = Assert.Single(runtime.Observations);
        Assert.Equal(request.Request.RequestId, observation.RequestId);
        Assert.Equal("ai.inference.admission_rejected", observation.Code);
        Assert.Equal(InferenceExecutionStatus.Unavailable, observation.Status);
        Assert.Equal("ai.inference.capability_unavailable", observation.Failure!.Value.Code);
        Assert.Equal(AIAvailabilityState.Unavailable, runtime.Snapshot.State);
    }
}
