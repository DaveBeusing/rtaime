using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Integration;

public sealed class GovernedAIHostIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Virtual_video_frame_executes_through_AIHost_person_segmentation_path(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var provider = new VirtualMediaReferenceProvider(MediaSourceId.New(), MediaSourceId.New(), format);
        var frame = provider.SourceA.GenerateFrame(7);
        var host = AIHostService.CreateManagedReference();
        var request = CreateRequest(frame, provider.Timing.FrameTimebase);

        var result = await host.ExecuteAsync(request);

        Assert.Equal(AIAvailabilityState.Ready, host.Snapshot.State);
        Assert.Equal(InferenceExecutionStatus.Succeeded, result.Result.Status);
        Assert.NotNull(result.Metadata);
        Assert.Equal(frame.Surface.SurfaceId, result.Metadata.SourceFrameId);
        Assert.Equal(request.Context.ProductionTime, result.Metadata.ProductionTime);
        Assert.Equal(ManagedReferencePersonSegmentationProvider.ReferenceProviderId, result.Metadata.ProviderId);
        Assert.Equal(ManagedReferencePersonSegmentationProvider.ReferenceModelId, result.Metadata.ModelId);
        Assert.Equal("SegmentationMask", result.Metadata.PayloadDescriptor.Kind);
        Assert.Equal("reference.segmentation.mask", result.Metadata.ResourceHandle.Kind);
        Assert.Contains(result.Result.Outputs, output => output.Name == "mask.semantic" && output.Value == "person");
        Assert.Equal(0u, host.Snapshot.ActiveRequests);
    }

    [Fact]
    public async Task Same_frame_and_request_identity_produce_deterministic_reference_mask_handle()
    {
        var sourceA = new MediaSourceId(Identity.Parse("c1000000-0000-0000-0000-000000000001"));
        var sourceB = new MediaSourceId(Identity.Parse("c1000000-0000-0000-0000-000000000002"));
        var provider = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
        var frame = provider.SourceA.GenerateFrame(11);
        var requestId = new InferenceRequestId(Identity.Parse("c2000000-0000-0000-0000-000000000001"));

        var first = await AIHostService.CreateManagedReference().ExecuteAsync(
            CreateRequest(frame, provider.Timing.FrameTimebase, requestId));
        var second = await AIHostService.CreateManagedReference().ExecuteAsync(
            CreateRequest(frame, provider.Timing.FrameTimebase, requestId));

        Assert.NotNull(first.Metadata);
        Assert.NotNull(second.Metadata);
        Assert.Equal(first.Metadata.ResultId, second.Metadata.ResultId);
        Assert.Equal(first.Metadata.ResourceHandle, second.Metadata.ResourceHandle);
    }

    private static GovernedInferenceExecutionRequest CreateRequest(
        FrameDescriptor frame,
        Timebase timebase,
        InferenceRequestId? requestId = null)
    {
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            requestId ?? InferenceRequestId.New(),
            ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
            frame,
            new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(10)),
            Array.Empty<InferenceParameter>());

        return new GovernedInferenceExecutionRequest(
            AIContractVersion.Current,
            request,
            new InferenceRequestContext(
                Identity.New(),
                new InferenceProductionTime(frame.Timing.PresentationTimestamp, timebase),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
                new InferenceResourceHandle("surface.descriptor", frame.Surface.SurfaceId.ToString())));
    }
}
