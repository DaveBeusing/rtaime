using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.AI.Contracts;

public static class AIContractVersion
{
    public static CompatibilityVersion Current { get; } = new(1, 0);

    public static bool IsSupported(CompatibilityVersion version) => version == Current;

    public static void EnsureSupported(CompatibilityVersion version)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"Unsupported AI contract version '{version}'. Supported version is '{Current}'.");
    }
}

public readonly record struct InferenceCapabilityId
{
    public InferenceCapabilityId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Inference capability identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static InferenceCapabilityId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct InferenceRequestId
{
    public InferenceRequestId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Inference request identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static InferenceRequestId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public sealed record InferenceCapabilityDescriptor
{
    public InferenceCapabilityDescriptor(
        CompatibilityVersion version,
        InferenceCapabilityId capabilityId,
        string kind,
        bool acceptsVideoFrames)
    {
        AIContractVersion.EnsureSupported(version);
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Inference capability kind is required.", nameof(kind));

        Version = version;
        CapabilityId = capabilityId;
        Kind = kind.Trim();
        AcceptsVideoFrames = acceptsVideoFrames;
    }

    public CompatibilityVersion Version { get; }
    public InferenceCapabilityId CapabilityId { get; }
    public string Kind { get; }
    public bool AcceptsVideoFrames { get; }
}

public sealed record InferenceParameter
{
    public InferenceParameter(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Inference parameter name is required.", nameof(name));

        Name = name.Trim();
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Name { get; }
    public string Value { get; }
}

public sealed class GovernedInferenceRequest
{
    private readonly ReadOnlyCollection<InferenceParameter> _parameters;

    public GovernedInferenceRequest(
        CompatibilityVersion version,
        InferenceRequestId requestId,
        InferenceCapabilityId capabilityId,
        FrameDescriptor? inputFrame,
        UtcTimestamp deadline,
        IReadOnlyList<InferenceParameter> parameters)
    {
        AIContractVersion.EnsureSupported(version);
        if (parameters is null)
            throw new ArgumentNullException(nameof(parameters));
        if (parameters.Any(parameter => parameter is null))
            throw new ArgumentException("Inference parameters must not contain null values.", nameof(parameters));

        var snapshot = parameters.OrderBy(parameter => parameter.Name, StringComparer.Ordinal).ToArray();
        if (snapshot.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Inference parameter names must be unique.", nameof(parameters));

        Version = version;
        RequestId = requestId;
        CapabilityId = capabilityId;
        InputFrame = inputFrame;
        Deadline = deadline;
        _parameters = Array.AsReadOnly(snapshot);
    }

    public CompatibilityVersion Version { get; }
    public InferenceRequestId RequestId { get; }
    public InferenceCapabilityId CapabilityId { get; }
    public FrameDescriptor? InputFrame { get; }
    public UtcTimestamp Deadline { get; }
    public IReadOnlyList<InferenceParameter> Parameters => _parameters;
}

public enum InferenceExecutionStatus
{
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4,
    Unavailable = 5
}

public sealed record InferenceOutput
{
    public InferenceOutput(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Inference output name is required.", nameof(name));

        Name = name.Trim();
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Name { get; }
    public string Value { get; }
}

public sealed class GovernedInferenceResult
{
    private readonly ReadOnlyCollection<InferenceOutput> _outputs;

    public GovernedInferenceResult(
        CompatibilityVersion version,
        InferenceRequestId requestId,
        InferenceExecutionStatus status,
        IReadOnlyList<InferenceOutput> outputs,
        Failure? failure)
    {
        AIContractVersion.EnsureSupported(version);
        if (!Enum.IsDefined(typeof(InferenceExecutionStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status), "Inference execution status must be a defined contract value.");
        if (outputs is null)
            throw new ArgumentNullException(nameof(outputs));
        if (outputs.Any(output => output is null))
            throw new ArgumentException("Inference outputs must not contain null values.", nameof(outputs));
        if (status == InferenceExecutionStatus.Succeeded && failure is not null)
            throw new ArgumentException("Successful inference results must not carry a failure.", nameof(failure));
        if (status != InferenceExecutionStatus.Succeeded && failure is null)
            throw new ArgumentException("Non-successful inference results require a failure.", nameof(failure));

        var snapshot = outputs.OrderBy(output => output.Name, StringComparer.Ordinal).ToArray();
        if (snapshot.Select(output => output.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Inference output names must be unique.", nameof(outputs));

        Version = version;
        RequestId = requestId;
        Status = status;
        _outputs = Array.AsReadOnly(snapshot);
        Failure = failure;
    }

    public CompatibilityVersion Version { get; }
    public InferenceRequestId RequestId { get; }
    public InferenceExecutionStatus Status { get; }
    public IReadOnlyList<InferenceOutput> Outputs => _outputs;
    public Failure? Failure { get; }
}

public sealed record AIObservation
{
    public AIObservation(
        CompatibilityVersion version,
        InferenceRequestId requestId,
        UtcTimestamp observedAt,
        InferenceExecutionStatus status,
        Failure? failure)
    {
        AIContractVersion.EnsureSupported(version);
        if (!Enum.IsDefined(typeof(InferenceExecutionStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status), "Inference execution status must be a defined contract value.");

        Version = version;
        RequestId = requestId;
        ObservedAt = observedAt;
        Status = status;
        Failure = failure;
    }

    public CompatibilityVersion Version { get; }
    public InferenceRequestId RequestId { get; }
    public UtcTimestamp ObservedAt { get; }
    public InferenceExecutionStatus Status { get; }
    public Failure? Failure { get; }
}
