using rtaime.AI;
using rtaime.AI.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Unit;

public sealed class AIResultUsePolicyTests
{
    [Fact]
    public void Stale_result_uses_fallback()
    {
        var execution = SuccessfulExecution(Duration.FromTimeSpan(TimeSpan.FromMilliseconds(250)), confidence: 0.95);
        var decision = AIResultUsePolicy.Evaluate(
            execution,
            execution.Metadata!.SourceFrameId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            minimumConfidence: 0.9);

        Assert.False(decision.Usable);
        Assert.Equal("ai.result.stale", decision.Failure!.Value.Code);
    }

    [Fact]
    public void Low_confidence_result_uses_fallback()
    {
        var execution = SuccessfulExecution(Duration.Zero, confidence: 0.6);
        var decision = AIResultUsePolicy.Evaluate(
            execution,
            execution.Metadata!.SourceFrameId,
            Duration.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
            minimumConfidence: 0.9);

        Assert.False(decision.Usable);
        Assert.Equal("ai.result.confidence_low", decision.Failure!.Value.Code);
    }

    private static GovernedInferenceExecutionResult SuccessfulExecution(Duration freshness, double confidence)
    {
        var requestId = InferenceRequestId.New();
        var providerId = InferenceProviderId.New();
        var capabilityId = InferenceCapabilityId.New();
        var frameId = SurfaceId.New();
        var admission = new InferenceAdmissionDecision(requestId, InferenceAdmissionStatus.Admitted, providerId, null);
        var result = new GovernedInferenceResult(
            AIContractVersion.Current,
            requestId,
            InferenceExecutionStatus.Succeeded,
            new[] { new InferenceOutput("mask.semantic", "person") },
            null);
        var metadata = new InferenceResultMetadata(
            InferenceResultId.New(),
            capabilityId,
            frameId,
            InferenceModelId.New(),
            "1.0.0-test",
            providerId,
            UtcTimestamp.UnixEpoch,
            new InferenceProductionTime(0, new Timebase(1, 50)),
            freshness,
            confidence,
            1 - confidence,
            new InferencePayloadDescriptor("SegmentationMask", "application/x-rtaime-mask-descriptor"),
            new InferenceResourceHandle("test.mask", "mask-0"));
        return new GovernedInferenceExecutionResult(AIContractVersion.Current, admission, result, metadata);
    }
}
