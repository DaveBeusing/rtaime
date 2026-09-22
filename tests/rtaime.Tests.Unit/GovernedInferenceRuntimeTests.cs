using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;

namespace rtaime.Tests.Unit;

public sealed class GovernedInferenceRuntimeTests
{
    private static readonly UtcTimestamp Now = new(new DateTimeOffset(2026, 9, 15, 18, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Reference_provider_advertises_person_segmentation_and_controlled_model_package()
    {
        var provider = new ManagedReferencePersonSegmentationProvider();
        var runtime = Runtime(provider);

        var capability = Assert.Single(runtime.Capabilities);
        Assert.Equal(ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId, capability.CapabilityId);
        Assert.Equal("ai.person-segmentation", capability.Kind);
        Assert.True(capability.AcceptsVideoFrames);

        var descriptor = Assert.Single(runtime.Providers);
        var model = Assert.Single(descriptor.Models);
        Assert.Equal(ManagedReferencePersonSegmentationProvider.ReferenceModelId, model.ModelId);
        Assert.Equal(capability.CapabilityId, model.CapabilityId);
        Assert.Equal(AIAvailabilityState.Ready, runtime.Snapshot.State);
    }

    [Fact]
    public async Task Successful_reference_inference_is_normalized_with_frame_model_provider_and_timing_provenance()
    {
        var provider = new ManagedReferencePersonSegmentationProvider();
        var runtime = Runtime(provider);
        var request = Request(1, Now.Value.AddSeconds(1));

        var result = await runtime.ExecuteAsync(request);

        Assert.Equal(InferenceAdmissionStatus.Admitted, result.Admission.Status);
        Assert.Equal(InferenceExecutionStatus.Succeeded, result.Result.Status);
        Assert.NotNull(result.Metadata);
        Assert.Equal(request.Request.InputFrame!.Surface.SurfaceId, result.Metadata.SourceFrameId);
        Assert.Equal(ManagedReferencePersonSegmentationProvider.ReferenceModelId, result.Metadata.ModelId);
        Assert.Equal(ManagedReferencePersonSegmentationProvider.ReferenceProviderId, result.Metadata.ProviderId);
        Assert.Equal(request.Context.ProductionTime, result.Metadata.ProductionTime);
        Assert.Equal("SegmentationMask", result.Metadata.PayloadDescriptor.Kind);
        Assert.Equal("reference.segmentation.mask", result.Metadata.ResourceHandle.Kind);
        Assert.Equal(0.95, result.Metadata.Confidence, 6);
        Assert.Equal(1UL, runtime.Snapshot.Completed);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
        Assert.Contains(runtime.Observations, observation => observation.Code == "ai.inference.completed");
    }

    [Fact]
    public async Task Expired_deadline_is_rejected_before_provider_execution()
    {
        var runtime = Runtime(new ManagedReferencePersonSegmentationProvider());
        var request = Request(2, Now.Value.AddMilliseconds(-1));

        var result = await runtime.ExecuteAsync(request);

        Assert.Equal(InferenceAdmissionStatus.Rejected, result.Admission.Status);
        Assert.Equal(InferenceExecutionStatus.TimedOut, result.Result.Status);
        Assert.Equal("ai.inference.deadline_expired", result.Result.Failure!.Value.Code);
        Assert.Equal(1UL, runtime.Snapshot.Rejected);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
    }

    [Fact]
    public async Task Provider_execution_timeout_releases_admitted_resources()
    {
        var provider = new BlockingProvider();
        var runtime = Runtime(provider);
        var request = Request(3, Now.Value.AddMilliseconds(25), provider.CapabilityId);

        var result = await runtime.ExecuteAsync(request);

        Assert.Equal(InferenceAdmissionStatus.Admitted, result.Admission.Status);
        Assert.Equal(InferenceExecutionStatus.TimedOut, result.Result.Status);
        Assert.Equal("ai.inference.timeout", result.Result.Failure!.Value.Code);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
        Assert.Equal(0u, runtime.Snapshot.ReservedComputeUnits);
        Assert.Equal(0UL, runtime.Snapshot.ReservedVramBytes);
    }

    [Fact]
    public async Task Caller_cancellation_releases_admitted_resources()
    {
        var provider = new ManagedReferencePersonSegmentationProvider(executionDelay: TimeSpan.FromSeconds(2));
        var runtime = Runtime(provider);
        var request = Request(4, Now.Value.AddSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        var result = await runtime.ExecuteAsync(request, cancellation.Token);

        Assert.Equal(InferenceExecutionStatus.Cancelled, result.Result.Status);
        Assert.Equal("ai.inference.cancelled", result.Result.Failure!.Value.Code);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
        Assert.Equal(1UL, runtime.Snapshot.Cancelled);
    }

    [Fact]
    public async Task Unavailable_provider_rejects_admission_and_host_state_is_unavailable()
    {
        var provider = new ManagedReferencePersonSegmentationProvider(InferenceProviderState.Unavailable);
        var runtime = Runtime(provider);

        var result = await runtime.ExecuteAsync(Request(5, Now.Value.AddSeconds(1)));

        Assert.Equal(InferenceAdmissionStatus.Rejected, result.Admission.Status);
        Assert.Equal(InferenceExecutionStatus.Unavailable, result.Result.Status);
        Assert.Equal(AIAvailabilityState.Unavailable, runtime.Snapshot.State);
    }

    [Fact]
    public async Task Degraded_provider_remains_usable_but_host_state_stays_degraded()
    {
        var provider = new ManagedReferencePersonSegmentationProvider(
            InferenceProviderState.Degraded,
            forcedFailure: new Failure("ai.provider.reference.degraded", "Provider is operating in degraded reference mode."));
        var runtime = Runtime(provider);

        var result = await runtime.ExecuteAsync(Request(6, Now.Value.AddSeconds(1)));

        Assert.Equal(InferenceExecutionStatus.Failed, result.Result.Status);
        Assert.Equal(AIAvailabilityState.Degraded, runtime.Snapshot.State);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
    }

    [Fact]
    public async Task Concurrent_request_is_rejected_when_AI_budget_is_saturated_then_capacity_recovers()
    {
        var provider = new BlockingProvider();
        var runtime = new GovernedInferenceRuntime(
            new[] { provider },
            new InferenceRuntimeLimits(10, 64UL * 1024 * 1024, 1),
            new FixedClock(Now));

        var firstTask = runtime.ExecuteAsync(Request(7, Now.Value.AddSeconds(2), provider.CapabilityId)).AsTask();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var second = await runtime.ExecuteAsync(Request(8, Now.Value.AddSeconds(2), provider.CapabilityId));
        Assert.Equal(InferenceAdmissionStatus.Rejected, second.Admission.Status);
        Assert.Equal("ai.inference.resource_budget_unavailable", second.Result.Failure!.Value.Code);
        Assert.Equal(1u, runtime.Snapshot.ActiveRequests);

        provider.Release.SetResult();
        var first = await firstTask;
        Assert.Equal(InferenceExecutionStatus.Succeeded, first.Result.Status);
        Assert.Equal(0u, runtime.Snapshot.ActiveRequests);
    }

    [Fact]
    public async Task Result_policy_falls_back_for_wrong_frame_and_accepts_valid_reference_result()
    {
        var runtime = Runtime(new ManagedReferencePersonSegmentationProvider());
        var request = Request(9, Now.Value.AddSeconds(1));
        var result = await runtime.ExecuteAsync(request);

        var valid = AIResultUsePolicy.Evaluate(
            result,
            request.Request.InputFrame!.Surface.SurfaceId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            0.9);
        var wrongFrame = AIResultUsePolicy.Evaluate(
            result,
            SurfaceId.New(),
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            0.9);

        Assert.True(valid.Usable);
        Assert.False(wrongFrame.Usable);
        Assert.Equal("ai.result.source_mismatch", wrongFrame.Failure!.Value.Code);
    }

    private static GovernedInferenceRuntime Runtime(IInferenceProvider provider) =>
        new(new[] { provider }, InferenceRuntimeLimits.ReferenceV1, new FixedClock(Now));

    private static GovernedInferenceExecutionRequest Request(
        ulong sequence,
        DateTimeOffset deadline,
        InferenceCapabilityId? capabilityId = null)
    {
        var surface = new SurfaceDescriptor(
            new SurfaceId(new Identity(GuidFromSequence(sequence, 1))),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(new Generation(sequence), new Identity(GuidFromSequence(sequence, 2))),
            new OpaqueSurfaceHandle("unit.ai.input", $"surface-{sequence}"));
        var frame = new FrameDescriptor(
            MediaContractVersion.Current,
            new MediaSourceId(new Identity(GuidFromSequence(sequence, 3))),
            surface,
            new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            new InferenceRequestId(new Identity(GuidFromSequence(sequence, 4))),
            capabilityId ?? ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
            frame,
            new UtcTimestamp(deadline),
            Array.Empty<InferenceParameter>());
        return new GovernedInferenceExecutionRequest(
            AIContractVersion.Current,
            request,
            new InferenceRequestContext(
                new Identity(GuidFromSequence(sequence, 5)),
                new InferenceProductionTime(checked((long)sequence), new Timebase(1, 50)),
                new InferenceResourceBudget(10, 64UL * 1024 * 1024, 25),
                new InferenceResourceHandle("surface.descriptor", surface.SurfaceId.ToString())));
    }

    private static Guid GuidFromSequence(ulong sequence, byte suffix)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, sequence + 1);
        bytes[15] = suffix;
        return new Guid(bytes);
    }

    private sealed class FixedClock : IAIClock
    {
        public FixedClock(UtcTimestamp value) => Value = value;
        public UtcTimestamp Value { get; }
        public UtcTimestamp GetUtcNow() => Value;
    }

    private sealed class BlockingProvider : IInferenceProvider
    {
        public BlockingProvider()
        {
            ProviderId = new InferenceProviderId(Identity.Parse("b1000000-0000-0000-0000-000000000001"));
            CapabilityId = new InferenceCapabilityId(Identity.Parse("b2000000-0000-0000-0000-000000000001"));
            ModelId = new InferenceModelId(Identity.Parse("b3000000-0000-0000-0000-000000000001"));
            Descriptor = new InferenceProviderDescriptor(
                AIContractVersion.Current,
                ProviderId,
                "Blocking Test Provider",
                InferenceProviderState.Ready,
                null,
                new[] { new InferenceCapabilityDescriptor(AIContractVersion.Current, CapabilityId, "ai.person-segmentation", true) },
                new[]
                {
                    new InferenceModelPackageDescriptor(
                        ModelId,
                        "1.0.0-test",
                        CapabilityId,
                        "test",
                        "video",
                        "mask",
                        "test",
                        "test",
                        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                        "test")
                });
        }

        public InferenceProviderId ProviderId { get; }
        public InferenceCapabilityId CapabilityId { get; }
        public InferenceModelId ModelId { get; }
        public InferenceProviderDescriptor Descriptor { get; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<InferenceProviderExecutionResult> ExecuteAsync(
            GovernedInferenceExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new InferenceProviderExecutionResult(
                InferenceExecutionStatus.Succeeded,
                new[] { new InferenceOutput("mask.semantic", "person") },
                ModelId,
                "1.0.0-test",
                0.9,
                0.1,
                Duration.Zero,
                new InferencePayloadDescriptor("SegmentationMask", "application/x-test-mask"),
                new InferenceResourceHandle("test.mask", request.Request.RequestId.ToString()),
                null);
        }
    }
}
