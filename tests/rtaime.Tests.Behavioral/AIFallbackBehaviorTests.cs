using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Behavioral;

public sealed class AIFallbackBehaviorTests
{
    [Fact]
    public async Task Person_segmentation_unavailable_falls_back_while_clean_program_frames_continue()
    {
        var virtualMedia = new VirtualMediaReferenceProvider(
            MediaSourceId.New(),
            MediaSourceId.New(),
            VideoFormat.Hd1080p50Rgba8);
        var firstFrame = virtualMedia.SourceA.GenerateFrame(0);

        var readyHost = AIHostService.CreateManagedReference();
        var activeResult = await readyHost.ExecuteAsync(Request(firstFrame));
        var activeDecision = AIResultUsePolicy.Evaluate(
            activeResult,
            firstFrame.Surface.SurfaceId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            minimumConfidence: 0.9);
        Assert.True(activeDecision.Usable);

        var unavailableProvider = new ManagedReferencePersonSegmentationProvider(InferenceProviderState.Unavailable);
        var unavailableHost = new AIHostService(new GovernedInferenceRuntime(
            new IInferenceProvider[] { unavailableProvider },
            InferenceRuntimeLimits.ReferenceV1));
        var unavailableResult = await unavailableHost.ExecuteAsync(Request(firstFrame));
        var fallback = AIResultUsePolicy.Evaluate(
            unavailableResult,
            firstFrame.Surface.SurfaceId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            minimumConfidence: 0.9);

        Assert.False(fallback.Usable);
        Assert.Equal(AIAvailabilityState.Unavailable, unavailableHost.Snapshot.State);
        Assert.Equal(InferenceExecutionStatus.Unavailable, unavailableResult.Result.Status);

        var nextProgramFrame = virtualMedia.SourceA.GenerateFrame(1);
        Assert.Equal(firstFrame.SourceId, nextProgramFrame.SourceId);
        Assert.Equal(1UL, nextProgramFrame.Timing.SequenceNumber);
        Assert.NotNull(nextProgramFrame.Surface.Handle);
        Assert.Equal(0u, unavailableHost.Snapshot.ActiveRequests);
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
