using System.Diagnostics;
using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Performance;

public sealed class GovernedAIPerformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Virtual_program_frames_with_lower_rate_reference_inference_remain_bounded_and_leak_free(bool use5994)
    {
        const ulong frameCount = 10_000;
        const ulong inferenceInterval = 5;
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var virtualMedia = new VirtualMediaReferenceProvider(MediaSourceId.New(), MediaSourceId.New(), format);
        var now = new UtcTimestamp(new DateTimeOffset(2026, 9, 15, 18, 30, 0, TimeSpan.Zero));
        var ai = new GovernedInferenceRuntime(
            new IInferenceProvider[] { new ManagedReferencePersonSegmentationProvider() },
            InferenceRuntimeLimits.ReferenceV1,
            new FixedClock(now));

        var inferenceCount = 0UL;
        var stopwatch = Stopwatch.StartNew();
        for (ulong sequence = 0; sequence < frameCount; sequence++)
        {
            var frame = virtualMedia.SourceA.GenerateFrame(sequence);
            Assert.Equal(sequence, frame.Timing.SequenceNumber);

            if (sequence % inferenceInterval != 0)
                continue;

            var result = await ai.ExecuteAsync(Request(frame, now));
            Assert.Equal(InferenceExecutionStatus.Succeeded, result.Result.Status);
            inferenceCount++;
        }
        stopwatch.Stop();

        Assert.Equal(2_000UL, inferenceCount);
        Assert.Equal(inferenceCount, ai.Snapshot.Completed);
        Assert.Equal(0u, ai.Snapshot.ActiveRequests);
        Assert.Equal(0u, ai.Snapshot.ReservedComputeUnits);
        Assert.Equal(0UL, ai.Snapshot.ReservedVramBytes);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Combined virtual media + managed reference AI workload took {stopwatch.Elapsed}.");
    }

    private static GovernedInferenceExecutionRequest Request(FrameDescriptor frame, UtcTimestamp now)
    {
        var request = new GovernedInferenceRequest(
            AIContractVersion.Current,
            InferenceRequestId.New(),
            ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId,
            frame,
            new UtcTimestamp(now.Value.AddMinutes(1)),
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

    private sealed class FixedClock : IAIClock
    {
        public FixedClock(UtcTimestamp value) => Value = value;
        public UtcTimestamp Value { get; }
        public UtcTimestamp GetUtcNow() => Value;
    }
}
