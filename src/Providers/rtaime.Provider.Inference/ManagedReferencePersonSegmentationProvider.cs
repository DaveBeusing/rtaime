using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using rtaime.AI.Contracts;
using rtaime.Core;

namespace rtaime.Provider.Inference;

/// <summary>
/// Deterministic managed reference provider for architecture and CI evidence.
/// It does not represent production model quality or qualified GPU inference.
/// </summary>
public sealed class ManagedReferencePersonSegmentationProvider : IInferenceProvider
{
    public static readonly InferenceProviderId ReferenceProviderId = new(
        Identity.Parse("a1000000-0000-0000-0000-000000000001"));
    public static readonly InferenceCapabilityId PersonSegmentationCapabilityId = new(
        Identity.Parse("a2000000-0000-0000-0000-000000000001"));
    public static readonly InferenceModelId ReferenceModelId = new(
        Identity.Parse("a3000000-0000-0000-0000-000000000001"));

    private readonly TimeSpan _executionDelay;
    private readonly Failure? _forcedFailure;

    public ManagedReferencePersonSegmentationProvider(
        InferenceProviderState state = InferenceProviderState.Ready,
        TimeSpan? executionDelay = null,
        Failure? forcedFailure = null)
    {
        if (executionDelay is { } delay && delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(executionDelay));
        if (state == InferenceProviderState.Unavailable && forcedFailure is null)
            forcedFailure = new Failure("ai.provider.reference.unavailable", "Reference inference provider is unavailable.");

        _executionDelay = executionDelay ?? TimeSpan.Zero;
        _forcedFailure = forcedFailure;

        var capability = new InferenceCapabilityDescriptor(
            AIContractVersion.Current,
            PersonSegmentationCapabilityId,
            "ai.person-segmentation",
            acceptsVideoFrames: true);
        var model = new InferenceModelPackageDescriptor(
            ReferenceModelId,
            "1.0.0-reference",
            PersonSegmentationCapabilityId,
            "managed-reference-runtime",
            "video-frame-descriptor.v1",
            "segmentation-mask-descriptor.v1",
            "compute=10;vram=67108864;rate<=30",
            "rtaime.managed-reference",
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            "synthetic CI/reference package; not a trained production model");

        Descriptor = new InferenceProviderDescriptor(
            AIContractVersion.Current,
            ReferenceProviderId,
            "Managed Reference Person Segmentation",
            state,
            state == InferenceProviderState.Ready ? null : forcedFailure,
            new[] { capability },
            new[] { model });
    }

    public InferenceProviderDescriptor Descriptor { get; }

    public async ValueTask<InferenceProviderExecutionResult> ExecuteAsync(
        GovernedInferenceExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (Descriptor.State == InferenceProviderState.Unavailable)
        {
            return FailureResult(
                InferenceExecutionStatus.Unavailable,
                Descriptor.Failure ?? new Failure("ai.provider.reference.unavailable", "Reference inference provider is unavailable."));
        }

        if (_executionDelay > TimeSpan.Zero)
            await Task.Delay(_executionDelay, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (_forcedFailure is { } forced)
            return FailureResult(InferenceExecutionStatus.Failed, forced);

        if (request.Request.CapabilityId != PersonSegmentationCapabilityId)
        {
            return FailureResult(
                InferenceExecutionStatus.Unavailable,
                new Failure("ai.provider.reference.capability_unsupported", "Reference provider does not support the requested capability."));
        }

        var frame = request.Request.InputFrame;
        if (frame is null)
        {
            return FailureResult(
                InferenceExecutionStatus.Failed,
                new Failure("ai.provider.reference.input_missing", "Person segmentation requires an input frame descriptor."));
        }

        var handleValue = CreateMaskHandle(
            frame.Surface.SurfaceId.ToString(),
            frame.Timing.SequenceNumber,
            request.Request.RequestId.ToString());

        // The normalized region is deterministic reference metadata used only by the V1 visible-effect proof.
        // It does not represent trained-model accuracy and remains separate from compositor/effect policy.
        var outputs = new[]
        {
            new InferenceOutput("mask.semantic", "person"),
            new InferenceOutput("mask.region.normalized", "0.25,0.10,0.75,0.90"),
            new InferenceOutput("source.sequence", frame.Timing.SequenceNumber.ToString(CultureInfo.InvariantCulture))
        };

        return new InferenceProviderExecutionResult(
            InferenceExecutionStatus.Succeeded,
            outputs,
            ReferenceModelId,
            "1.0.0-reference",
            confidence: 0.95,
            uncertainty: 0.05,
            freshness: Duration.Zero,
            payloadDescriptor: new InferencePayloadDescriptor("SegmentationMask", "application/x-rtaime-mask-descriptor"),
            resourceHandle: new InferenceResourceHandle("reference.segmentation.mask", handleValue),
            failure: null);
    }

    private static InferenceProviderExecutionResult FailureResult(InferenceExecutionStatus status, Failure failure) =>
        new(
            status,
            Array.Empty<InferenceOutput>(),
            null,
            null,
            0,
            1,
            Duration.Zero,
            null,
            null,
            failure);

    private static string CreateMaskHandle(string surfaceId, ulong sequenceNumber, string requestId)
    {
        var canonical = $"reference-person-segmentation\n{surfaceId}\n{sequenceNumber}\n{requestId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
