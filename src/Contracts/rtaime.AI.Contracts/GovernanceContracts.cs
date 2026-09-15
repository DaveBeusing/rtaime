using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.AI.Contracts;

public readonly record struct InferenceResultId
{
    public InferenceResultId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Inference result identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static InferenceResultId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct InferenceModelId
{
    public InferenceModelId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Inference model identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static InferenceModelId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct InferenceProviderId
{
    public InferenceProviderId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Inference provider identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static InferenceProviderId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public sealed record InferenceResourceBudget
{
    public InferenceResourceBudget(uint computeUnits, ulong vramBytes, uint maxInferenceRatePerSecond)
    {
        if (computeUnits == 0)
            throw new ArgumentOutOfRangeException(nameof(computeUnits), "Inference compute units must be greater than zero.");
        if (vramBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(vramBytes), "Inference VRAM budget must be greater than zero.");
        if (maxInferenceRatePerSecond == 0)
            throw new ArgumentOutOfRangeException(nameof(maxInferenceRatePerSecond), "Inference rate budget must be greater than zero.");

        ComputeUnits = computeUnits;
        VramBytes = vramBytes;
        MaxInferenceRatePerSecond = maxInferenceRatePerSecond;
    }

    public uint ComputeUnits { get; }
    public ulong VramBytes { get; }
    public uint MaxInferenceRatePerSecond { get; }
}

public readonly record struct InferenceProductionTime
{
    public InferenceProductionTime(long timestamp, Timebase timebase)
    {
        Timestamp = timestamp;
        Timebase = timebase;
    }

    public long Timestamp { get; }
    public Timebase Timebase { get; }
}

public sealed record InferenceResourceHandle
{
    public InferenceResourceHandle(string kind, string value)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Inference resource handle kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Inference resource handle value is required.", nameof(value));

        Kind = kind.Trim();
        Value = value.Trim();
    }

    public string Kind { get; }
    public string Value { get; }
}

public sealed record InferencePayloadDescriptor
{
    public InferencePayloadDescriptor(string kind, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Inference payload kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(mediaType))
            throw new ArgumentException("Inference payload media type is required.", nameof(mediaType));

        Kind = kind.Trim();
        MediaType = mediaType.Trim();
    }

    public string Kind { get; }
    public string MediaType { get; }
}

public sealed record InferenceRequestContext
{
    public InferenceRequestContext(
        Identity timingDomainId,
        InferenceProductionTime productionTime,
        InferenceResourceBudget resourceBudget,
        InferenceResourceHandle inputResourceHandle)
    {
        if (timingDomainId.IsEmpty)
            throw new ArgumentException("Inference timing domain identity must not be empty.", nameof(timingDomainId));

        TimingDomainId = timingDomainId;
        ProductionTime = productionTime;
        ResourceBudget = resourceBudget ?? throw new ArgumentNullException(nameof(resourceBudget));
        InputResourceHandle = inputResourceHandle ?? throw new ArgumentNullException(nameof(inputResourceHandle));
    }

    public Identity TimingDomainId { get; }
    public InferenceProductionTime ProductionTime { get; }
    public InferenceResourceBudget ResourceBudget { get; }
    public InferenceResourceHandle InputResourceHandle { get; }
}

public sealed record GovernedInferenceExecutionRequest
{
    public GovernedInferenceExecutionRequest(
        CompatibilityVersion version,
        GovernedInferenceRequest request,
        InferenceRequestContext context)
    {
        AIContractVersion.EnsureSupported(version);
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        if (request.Version != version)
            throw new ArgumentException("Inference execution request version must match the embedded request version.", nameof(request));
        if (request.InputFrame is null)
            throw new ArgumentException("V1 governed inference requires an input video frame descriptor.", nameof(request));

        Version = version;
    }

    public CompatibilityVersion Version { get; }
    public GovernedInferenceRequest Request { get; }
    public InferenceRequestContext Context { get; }
}

public enum InferenceAdmissionStatus
{
    Admitted = 1,
    Rejected = 2
}

public sealed record InferenceAdmissionDecision
{
    public InferenceAdmissionDecision(
        InferenceRequestId requestId,
        InferenceAdmissionStatus status,
        InferenceProviderId? providerId,
        Failure? failure)
    {
        if (!Enum.IsDefined(typeof(InferenceAdmissionStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == InferenceAdmissionStatus.Admitted && providerId is null)
            throw new ArgumentException("Admitted inference requires a provider identity.", nameof(providerId));
        if (status == InferenceAdmissionStatus.Admitted && failure is not null)
            throw new ArgumentException("Admitted inference must not carry a failure.", nameof(failure));
        if (status == InferenceAdmissionStatus.Rejected && failure is null)
            throw new ArgumentException("Rejected inference admission requires a failure.", nameof(failure));

        RequestId = requestId;
        Status = status;
        ProviderId = providerId;
        Failure = failure;
    }

    public InferenceRequestId RequestId { get; }
    public InferenceAdmissionStatus Status { get; }
    public InferenceProviderId? ProviderId { get; }
    public Failure? Failure { get; }
    public bool Admitted => Status == InferenceAdmissionStatus.Admitted;
}

public enum InferenceProviderState
{
    Ready = 1,
    Degraded = 2,
    Unavailable = 3
}

public sealed record InferenceModelPackageDescriptor
{
    public InferenceModelPackageDescriptor(
        InferenceModelId modelId,
        string modelVersion,
        InferenceCapabilityId capabilityId,
        string runtimeRequirements,
        string inputContract,
        string outputContract,
        string resourceRequirements,
        string providerCompatibility,
        string artifactHashSha256,
        string provenance)
    {
        if (string.IsNullOrWhiteSpace(modelVersion)) throw new ArgumentException("Model version is required.", nameof(modelVersion));
        if (string.IsNullOrWhiteSpace(runtimeRequirements)) throw new ArgumentException("Runtime requirements are required.", nameof(runtimeRequirements));
        if (string.IsNullOrWhiteSpace(inputContract)) throw new ArgumentException("Input contract is required.", nameof(inputContract));
        if (string.IsNullOrWhiteSpace(outputContract)) throw new ArgumentException("Output contract is required.", nameof(outputContract));
        if (string.IsNullOrWhiteSpace(resourceRequirements)) throw new ArgumentException("Resource requirements are required.", nameof(resourceRequirements));
        if (string.IsNullOrWhiteSpace(providerCompatibility)) throw new ArgumentException("Provider compatibility is required.", nameof(providerCompatibility));
        if (string.IsNullOrWhiteSpace(artifactHashSha256)) throw new ArgumentException("Artifact hash is required.", nameof(artifactHashSha256));
        if (string.IsNullOrWhiteSpace(provenance)) throw new ArgumentException("Model provenance is required.", nameof(provenance));

        ModelId = modelId;
        ModelVersion = modelVersion.Trim();
        CapabilityId = capabilityId;
        RuntimeRequirements = runtimeRequirements.Trim();
        InputContract = inputContract.Trim();
        OutputContract = outputContract.Trim();
        ResourceRequirements = resourceRequirements.Trim();
        ProviderCompatibility = providerCompatibility.Trim();
        ArtifactHashSha256 = artifactHashSha256.Trim().ToLowerInvariant();
        Provenance = provenance.Trim();
    }

    public InferenceModelId ModelId { get; }
    public string ModelVersion { get; }
    public InferenceCapabilityId CapabilityId { get; }
    public string RuntimeRequirements { get; }
    public string InputContract { get; }
    public string OutputContract { get; }
    public string ResourceRequirements { get; }
    public string ProviderCompatibility { get; }
    public string ArtifactHashSha256 { get; }
    public string Provenance { get; }
}

public sealed class InferenceProviderDescriptor
{
    private readonly ReadOnlyCollection<InferenceCapabilityDescriptor> _capabilities;
    private readonly ReadOnlyCollection<InferenceModelPackageDescriptor> _models;

    public InferenceProviderDescriptor(
        CompatibilityVersion version,
        InferenceProviderId providerId,
        string name,
        InferenceProviderState state,
        Failure? failure,
        IReadOnlyList<InferenceCapabilityDescriptor> capabilities,
        IReadOnlyList<InferenceModelPackageDescriptor> models)
    {
        AIContractVersion.EnsureSupported(version);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Inference provider name is required.", nameof(name));
        if (!Enum.IsDefined(typeof(InferenceProviderState), state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (state == InferenceProviderState.Ready && failure is not null)
            throw new ArgumentException("Ready inference providers must not carry a failure.", nameof(failure));
        if (state == InferenceProviderState.Unavailable && failure is null)
            throw new ArgumentException("Unavailable inference providers require a failure.", nameof(failure));
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(models);
        if (capabilities.Any(item => item is null)) throw new ArgumentException("Capabilities must not contain null values.", nameof(capabilities));
        if (models.Any(item => item is null)) throw new ArgumentException("Models must not contain null values.", nameof(models));

        var capabilitySnapshot = capabilities.OrderBy(item => item.CapabilityId.ToString(), StringComparer.Ordinal).ToArray();
        if (capabilitySnapshot.Select(item => item.CapabilityId).Distinct().Count() != capabilitySnapshot.Length)
            throw new ArgumentException("Inference capability identities must be unique within a provider.", nameof(capabilities));
        var modelSnapshot = models.OrderBy(item => item.ModelId.ToString(), StringComparer.Ordinal).ToArray();
        if (modelSnapshot.Select(item => item.ModelId).Distinct().Count() != modelSnapshot.Length)
            throw new ArgumentException("Inference model identities must be unique within a provider.", nameof(models));

        Version = version;
        ProviderId = providerId;
        Name = name.Trim();
        State = state;
        Failure = failure;
        _capabilities = Array.AsReadOnly(capabilitySnapshot);
        _models = Array.AsReadOnly(modelSnapshot);
    }

    public CompatibilityVersion Version { get; }
    public InferenceProviderId ProviderId { get; }
    public string Name { get; }
    public InferenceProviderState State { get; }
    public Failure? Failure { get; }
    public IReadOnlyList<InferenceCapabilityDescriptor> Capabilities => _capabilities;
    public IReadOnlyList<InferenceModelPackageDescriptor> Models => _models;
}

public sealed record InferenceProviderExecutionResult
{
    private readonly ReadOnlyCollection<InferenceOutput> _outputs;

    public InferenceProviderExecutionResult(
        InferenceExecutionStatus status,
        IReadOnlyList<InferenceOutput> outputs,
        InferenceModelId? modelId,
        string? modelVersion,
        double confidence,
        double uncertainty,
        Duration freshness,
        InferencePayloadDescriptor? payloadDescriptor,
        InferenceResourceHandle? resourceHandle,
        Failure? failure)
    {
        if (!Enum.IsDefined(typeof(InferenceExecutionStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Any(item => item is null)) throw new ArgumentException("Provider outputs must not contain null values.", nameof(outputs));
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (uncertainty is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(uncertainty));
        if (freshness.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(freshness));
        if (status == InferenceExecutionStatus.Succeeded)
        {
            if (failure is not null) throw new ArgumentException("Successful provider result must not carry a failure.", nameof(failure));
            if (modelId is null || string.IsNullOrWhiteSpace(modelVersion)) throw new ArgumentException("Successful provider result requires model identity and version.", nameof(modelId));
            if (payloadDescriptor is null || resourceHandle is null) throw new ArgumentException("Successful provider result requires payload descriptor and resource handle.", nameof(payloadDescriptor));
        }
        else if (failure is null)
        {
            throw new ArgumentException("Non-successful provider result requires a failure.", nameof(failure));
        }

        Status = status;
        _outputs = Array.AsReadOnly(outputs.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
        ModelId = modelId;
        ModelVersion = modelVersion?.Trim();
        Confidence = confidence;
        Uncertainty = uncertainty;
        Freshness = freshness;
        PayloadDescriptor = payloadDescriptor;
        ResourceHandle = resourceHandle;
        Failure = failure;
    }

    public InferenceExecutionStatus Status { get; }
    public IReadOnlyList<InferenceOutput> Outputs => _outputs;
    public InferenceModelId? ModelId { get; }
    public string? ModelVersion { get; }
    public double Confidence { get; }
    public double Uncertainty { get; }
    public Duration Freshness { get; }
    public InferencePayloadDescriptor? PayloadDescriptor { get; }
    public InferenceResourceHandle? ResourceHandle { get; }
    public Failure? Failure { get; }
}

public interface IInferenceProvider
{
    InferenceProviderDescriptor Descriptor { get; }

    ValueTask<InferenceProviderExecutionResult> ExecuteAsync(
        GovernedInferenceExecutionRequest request,
        CancellationToken cancellationToken);
}

public sealed record InferenceResultMetadata
{
    public InferenceResultMetadata(
        InferenceResultId resultId,
        InferenceCapabilityId capabilityId,
        SurfaceId sourceFrameId,
        InferenceModelId modelId,
        string modelVersion,
        InferenceProviderId providerId,
        UtcTimestamp observationTime,
        InferenceProductionTime productionTime,
        Duration freshness,
        double confidence,
        double uncertainty,
        InferencePayloadDescriptor payloadDescriptor,
        InferenceResourceHandle resourceHandle)
    {
        if (string.IsNullOrWhiteSpace(modelVersion)) throw new ArgumentException("Result model version is required.", nameof(modelVersion));
        if (freshness.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(freshness));
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));
        if (uncertainty is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(uncertainty));

        ResultId = resultId;
        CapabilityId = capabilityId;
        SourceFrameId = sourceFrameId;
        ModelId = modelId;
        ModelVersion = modelVersion.Trim();
        ProviderId = providerId;
        ObservationTime = observationTime;
        ProductionTime = productionTime;
        Freshness = freshness;
        Confidence = confidence;
        Uncertainty = uncertainty;
        PayloadDescriptor = payloadDescriptor ?? throw new ArgumentNullException(nameof(payloadDescriptor));
        ResourceHandle = resourceHandle ?? throw new ArgumentNullException(nameof(resourceHandle));
    }

    public InferenceResultId ResultId { get; }
    public InferenceCapabilityId CapabilityId { get; }
    public SurfaceId SourceFrameId { get; }
    public InferenceModelId ModelId { get; }
    public string ModelVersion { get; }
    public InferenceProviderId ProviderId { get; }
    public UtcTimestamp ObservationTime { get; }
    public InferenceProductionTime ProductionTime { get; }
    public Duration Freshness { get; }
    public double Confidence { get; }
    public double Uncertainty { get; }
    public InferencePayloadDescriptor PayloadDescriptor { get; }
    public InferenceResourceHandle ResourceHandle { get; }
}

public sealed record GovernedInferenceExecutionResult
{
    public GovernedInferenceExecutionResult(
        CompatibilityVersion version,
        InferenceAdmissionDecision admission,
        GovernedInferenceResult result,
        InferenceResultMetadata? metadata)
    {
        AIContractVersion.EnsureSupported(version);
        Admission = admission ?? throw new ArgumentNullException(nameof(admission));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        if (admission.RequestId != result.RequestId)
            throw new ArgumentException("Admission and inference result must refer to the same request.", nameof(result));
        if (result.Status == InferenceExecutionStatus.Succeeded && metadata is null)
            throw new ArgumentException("Successful governed inference requires normalized result metadata.", nameof(metadata));
        if (result.Status != InferenceExecutionStatus.Succeeded && metadata is not null)
            throw new ArgumentException("Failed governed inference must not expose successful result metadata.", nameof(metadata));

        Version = version;
        Metadata = metadata;
    }

    public CompatibilityVersion Version { get; }
    public InferenceAdmissionDecision Admission { get; }
    public GovernedInferenceResult Result { get; }
    public InferenceResultMetadata? Metadata { get; }
}

public enum AIAvailabilityState
{
    Ready = 1,
    Degraded = 2,
    Unavailable = 3
}
