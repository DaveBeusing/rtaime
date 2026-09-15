using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;

namespace rtaime.Tests.Unit;

public sealed class AIAdmissionPolicyTests
{
    [Fact]
    public async Task Declared_inference_rate_above_host_budget_is_rejected_fail_closed()
    {
        var runtime = new GovernedInferenceRuntime(
            new IInferenceProvider[] { new ManagedReferencePersonSegmentationProvider() },
            new InferenceRuntimeLimits(100, 512UL * 1024 * 1024, 2, maxInferenceRatePerSecond: 30));

        var result = await runtime.ExecuteAsync(Request(maxRate: 31));

        Assert.Equal(InferenceAdmissionStatus.Rejected, result.Admission.Status);
        Assert.Equal(InferenceExecutionStatus.Unavailable, result.Result.Status);
        Assert.Equal("ai.inference.resource_budget_unavailable", result.Result.Failure!.Value.Code);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
    }

    [Fact]
    public async Task Degraded_provider_can_still_serve_inference_while_runtime_reports_degraded()
    {
        var runtime = new GovernedInferenceRuntime(
            new IInferenceProvider[] { new ManagedReferencePersonSegmentationProvider(InferenceProviderState.Degraded) },
            InferenceRuntimeLimits.ReferenceV1);

        var result = await runtime.ExecuteAsync(Request(maxRate: 25));

        Assert.Equal(InferenceAdmissionStatus.Admitted, result.Admission.Status);
        Assert.Equal(InferenceExecutionStatus.Succeeded, result.Result.Status);
        Assert.Equal(AIAvailabilityState.Degraded, runtime.Snapshot.State);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
    }

    private static GovernedInferenceExecutionRequest Request(uint maxRate)
    {
        var frame = new FrameDescriptor(
            MediaContractVersion.Current,
            MediaSourceId.New(),
            new SurfaceDescriptor(
                SurfaceId.New(),
                VideoFormat.Hd1080p50Rgba8,
                SurfaceStorageDomain.Shared,
                SurfaceOwnership.SharedLease,
                new SurfaceLifetimeDescriptor(Generation.Initial, Identity.New()),
                new OpaqueSurfaceHandle("unit.ai.admission", "surface")),
            new FrameTiming(0, 0, new Timebase(1, 50)));
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
                new InferenceProductionTime(0, new Timebase(1, 50)),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, maxRate),
                new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));
    }
}
