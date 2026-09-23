using System.Collections.ObjectModel;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Runtime.Contracts;

public static class RuntimeContractVersion
{
    public static CompatibilityVersion Current { get; } = new(1, 0);

    public static bool IsSupported(CompatibilityVersion version) => version == Current;

    public static void EnsureSupported(CompatibilityVersion version)
    {
        if (!IsSupported(version))
            throw new NotSupportedException($"Unsupported Runtime contract version '{version}'. Supported version is '{Current}'.");
    }
}

public readonly record struct PreparedExecutionId
{
    public PreparedExecutionId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Prepared execution identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static PreparedExecutionId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public readonly record struct ExecutionInstanceId
{
    public ExecutionInstanceId(Identity value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("Execution instance identity must not be empty.", nameof(value));
        Value = value;
    }

    public Identity Value { get; }
    public static ExecutionInstanceId New() => new(Identity.New());
    public override string ToString() => Value.ToString();
}

public sealed record AuthoritySnapshotReference
{
    public AuthoritySnapshotReference(Identity stateId, Revision revision)
    {
        if (stateId.IsEmpty)
            throw new ArgumentException("Authority state identity must not be empty.", nameof(stateId));

        StateId = stateId;
        Revision = revision;
    }

    public Identity StateId { get; }
    public Revision Revision { get; }
}

public sealed record PreparedExecutionBinding
{
    public PreparedExecutionBinding(
        Identity logicalNodeId,
        CapabilityId capabilityId,
        ProviderResourceDescriptor resource,
        MediaSourceId? mediaSourceId,
        MediaSinkId? mediaSinkId,
        string? outputRoleId = null)
    {
        if (logicalNodeId.IsEmpty)
            throw new ArgumentException("Logical node identity must not be empty.", nameof(logicalNodeId));

        LogicalNodeId = logicalNodeId;
        CapabilityId = capabilityId;
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        MediaSourceId = mediaSourceId;
        MediaSinkId = mediaSinkId;
        OutputRoleId = string.IsNullOrWhiteSpace(outputRoleId) ? null : outputRoleId.Trim().ToLowerInvariant();
    }

    public Identity LogicalNodeId { get; }
    public CapabilityId CapabilityId { get; }
    public ProviderResourceDescriptor Resource { get; }
    public MediaSourceId? MediaSourceId { get; }
    public MediaSinkId? MediaSinkId { get; }
    public string? OutputRoleId { get; }
}

public enum RuntimeOutputRoleLifecycleState
{
    Inactive = 1,
    Active = 2,
    Faulted = 3
}

public enum RuntimeOutputRoleHealthState
{
    Unverified = 1,
    Healthy = 2,
    Faulted = 3
}

public sealed record RuntimeOutputRoleSnapshot
{
    public RuntimeOutputRoleSnapshot(
        string roleId,
        string roleKind,
        MediaSourceId sourceId,
        MediaSinkId targetId,
        VideoFormat format,
        Timebase timing,
        ProviderId providerId,
        RuntimeOutputRoleLifecycleState lifecycleState,
        bool authoritativeActive,
        RuntimeOutputRoleHealthState healthState,
        string evidence,
        Failure? error = null)
    {
        if (string.IsNullOrWhiteSpace(roleId))
            throw new ArgumentException("Output role identity is required.", nameof(roleId));
        if (string.IsNullOrWhiteSpace(roleKind))
            throw new ArgumentException("Output role kind is required.", nameof(roleKind));
        if (!Enum.IsDefined(typeof(RuntimeOutputRoleLifecycleState), lifecycleState))
            throw new ArgumentOutOfRangeException(nameof(lifecycleState));
        if (!Enum.IsDefined(typeof(RuntimeOutputRoleHealthState), healthState))
            throw new ArgumentOutOfRangeException(nameof(healthState));
        if (string.IsNullOrWhiteSpace(evidence))
            throw new ArgumentException("Output role evidence is required.", nameof(evidence));
        if (healthState == RuntimeOutputRoleHealthState.Faulted && error is null)
            throw new ArgumentException("Faulted output roles require an error reason.", nameof(error));
        if (healthState != RuntimeOutputRoleHealthState.Faulted && error is not null)
            throw new ArgumentException("Only faulted output roles may carry an error reason.", nameof(error));

        RoleId = roleId.Trim().ToLowerInvariant();
        RoleKind = roleKind.Trim().ToUpperInvariant();
        SourceId = sourceId;
        TargetId = targetId;
        Format = format;
        Timing = timing;
        ProviderId = providerId;
        LifecycleState = lifecycleState;
        AuthoritativeActive = authoritativeActive;
        HealthState = healthState;
        Evidence = evidence.Trim();
        Error = error;
    }

    public string RoleId { get; }
    public string RoleKind { get; }
    public MediaSourceId SourceId { get; }
    public MediaSinkId TargetId { get; }
    public VideoFormat Format { get; }
    public Timebase Timing { get; }
    public ProviderId ProviderId { get; }
    public RuntimeOutputRoleLifecycleState LifecycleState { get; }
    public bool AuthoritativeActive { get; }
    public RuntimeOutputRoleHealthState HealthState { get; }
    public string Evidence { get; }
    public Failure? Error { get; }
}

public sealed class PreparedExecutionContract
{
    private readonly ReadOnlyCollection<PreparedExecutionBinding> _bindings;

    public PreparedExecutionContract(
        CompatibilityVersion version,
        PreparedExecutionId preparedExecutionId,
        AuthoritySnapshotReference authoritySnapshot,
        Generation planGeneration,
        IReadOnlyList<PreparedExecutionBinding> bindings)
    {
        RuntimeContractVersion.EnsureSupported(version);
        if (bindings is null)
            throw new ArgumentNullException(nameof(bindings));
        if (bindings.Any(binding => binding is null))
            throw new ArgumentException("Prepared execution bindings must not contain null values.", nameof(bindings));

        Version = version;
        PreparedExecutionId = preparedExecutionId;
        AuthoritySnapshot = authoritySnapshot ?? throw new ArgumentNullException(nameof(authoritySnapshot));
        PlanGeneration = planGeneration;
        _bindings = Array.AsReadOnly(bindings.ToArray());
    }

    public CompatibilityVersion Version { get; }
    public PreparedExecutionId PreparedExecutionId { get; }
    public AuthoritySnapshotReference AuthoritySnapshot { get; }
    public Generation PlanGeneration { get; }
    public IReadOnlyList<PreparedExecutionBinding> Bindings => _bindings;
}

public enum RuntimePrepareStatus
{
    Prepared = 1,
    Rejected = 2
}

public sealed record RuntimePrepareResult
{
    public RuntimePrepareResult(
        CompatibilityVersion version,
        PreparedExecutionId preparedExecutionId,
        RuntimePrepareStatus status,
        Identity? reservationId,
        Failure? failure)
    {
        RuntimeContractVersion.EnsureSupported(version);
        if (!Enum.IsDefined(typeof(RuntimePrepareStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status), "Runtime prepare status must be a defined contract value.");
        if (status == RuntimePrepareStatus.Prepared && (reservationId is null || reservationId.Value.IsEmpty))
            throw new ArgumentException("Prepared results require a reservation identity.", nameof(reservationId));
        if (status == RuntimePrepareStatus.Prepared && failure is not null)
            throw new ArgumentException("Prepared results must not carry a failure.", nameof(failure));
        if (status == RuntimePrepareStatus.Rejected && failure is null)
            throw new ArgumentException("Rejected prepare results require a failure.", nameof(failure));

        Version = version;
        PreparedExecutionId = preparedExecutionId;
        Status = status;
        ReservationId = reservationId;
        Failure = failure;
    }

    public CompatibilityVersion Version { get; }
    public PreparedExecutionId PreparedExecutionId { get; }
    public RuntimePrepareStatus Status { get; }
    public Identity? ReservationId { get; }
    public Failure? Failure { get; }
}

public sealed record RuntimeCommitRequest
{
    public RuntimeCommitRequest(
        CompatibilityVersion version,
        PreparedExecutionId preparedExecutionId,
        Identity reservationId,
        Revision expectedExecutionRevision)
    {
        RuntimeContractVersion.EnsureSupported(version);
        if (reservationId.IsEmpty)
            throw new ArgumentException("Reservation identity must not be empty.", nameof(reservationId));

        Version = version;
        PreparedExecutionId = preparedExecutionId;
        ReservationId = reservationId;
        ExpectedExecutionRevision = expectedExecutionRevision;
    }

    public CompatibilityVersion Version { get; }
    public PreparedExecutionId PreparedExecutionId { get; }
    public Identity ReservationId { get; }
    public Revision ExpectedExecutionRevision { get; }
}

public enum RuntimeCommitStatus
{
    Committed = 1,
    Rejected = 2
}

public sealed record RuntimeCommitResult
{
    public RuntimeCommitResult(
        CompatibilityVersion version,
        RuntimeCommitStatus status,
        ExecutionInstanceId? executionInstanceId,
        Revision executionRevision,
        Failure? failure)
    {
        RuntimeContractVersion.EnsureSupported(version);
        if (!Enum.IsDefined(typeof(RuntimeCommitStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status), "Runtime commit status must be a defined contract value.");
        if (status == RuntimeCommitStatus.Committed && executionInstanceId is null)
            throw new ArgumentException("Committed results require an execution instance identity.", nameof(executionInstanceId));
        if (status == RuntimeCommitStatus.Committed && failure is not null)
            throw new ArgumentException("Committed results must not carry a failure.", nameof(failure));
        if (status == RuntimeCommitStatus.Rejected && failure is null)
            throw new ArgumentException("Rejected commit results require a failure.", nameof(failure));

        Version = version;
        Status = status;
        ExecutionInstanceId = executionInstanceId;
        ExecutionRevision = executionRevision;
        Failure = failure;
    }

    public CompatibilityVersion Version { get; }
    public RuntimeCommitStatus Status { get; }
    public ExecutionInstanceId? ExecutionInstanceId { get; }
    public Revision ExecutionRevision { get; }
    public Failure? Failure { get; }
}

public enum RuntimeExecutionStatus
{
    Idle = 1,
    Prepared = 2,
    Committed = 3,
    Faulted = 4
}

public sealed record RuntimeExecutionState
{
    public RuntimeExecutionState(
        CompatibilityVersion version,
        ExecutionInstanceId? activeExecutionId,
        Revision executionRevision,
        RuntimeExecutionStatus status,
        Failure? failure)
    {
        RuntimeContractVersion.EnsureSupported(version);
        if (!Enum.IsDefined(typeof(RuntimeExecutionStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status), "Runtime execution status must be a defined contract value.");
        if (status == RuntimeExecutionStatus.Committed && activeExecutionId is null)
            throw new ArgumentException("Committed execution state requires an active execution identity.", nameof(activeExecutionId));
        if (status == RuntimeExecutionStatus.Faulted && failure is null)
            throw new ArgumentException("Faulted execution state requires a failure.", nameof(failure));

        Version = version;
        ActiveExecutionId = activeExecutionId;
        ExecutionRevision = executionRevision;
        Status = status;
        Failure = failure;
    }

    public CompatibilityVersion Version { get; }
    public ExecutionInstanceId? ActiveExecutionId { get; }
    public Revision ExecutionRevision { get; }
    public RuntimeExecutionStatus Status { get; }
    public Failure? Failure { get; }
}

public sealed record RuntimeObservation
{
    public RuntimeObservation(
        CompatibilityVersion version,
        Revision executionRevision,
        UtcTimestamp observedAt,
        string code,
        Failure? failure)
    {
        RuntimeContractVersion.EnsureSupported(version);
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Runtime observation code is required.", nameof(code));

        Version = version;
        ExecutionRevision = executionRevision;
        ObservedAt = observedAt;
        Code = code.Trim();
        Failure = failure;
    }

    public CompatibilityVersion Version { get; }
    public Revision ExecutionRevision { get; }
    public UtcTimestamp ObservedAt { get; }
    public string Code { get; }
    public Failure? Failure { get; }
}
