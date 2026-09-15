using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.Runtime;

public enum RuntimeResourceReservationStatus
{
    Reserved = 1,
    Rejected = 2
}

public sealed record RuntimeResourceReservationResult
{
    private RuntimeResourceReservationResult(
        RuntimeResourceReservationStatus status,
        Identity? reservationId,
        Failure? failure)
    {
        if (!Enum.IsDefined(typeof(RuntimeResourceReservationStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RuntimeResourceReservationStatus.Reserved && (reservationId is null || reservationId.Value.IsEmpty))
            throw new ArgumentException("Successful resource reservation requires a reservation identity.", nameof(reservationId));
        if (status == RuntimeResourceReservationStatus.Reserved && failure is not null)
            throw new ArgumentException("Successful resource reservation must not carry a failure.", nameof(failure));
        if (status == RuntimeResourceReservationStatus.Rejected && failure is null)
            throw new ArgumentException("Rejected resource reservation requires a failure.", nameof(failure));

        Status = status;
        ReservationId = reservationId;
        Failure = failure;
    }

    public RuntimeResourceReservationStatus Status { get; }
    public Identity? ReservationId { get; }
    public Failure? Failure { get; }
    public bool Succeeded => Status == RuntimeResourceReservationStatus.Reserved;

    public static RuntimeResourceReservationResult Reserved(Identity reservationId) =>
        new(RuntimeResourceReservationStatus.Reserved, reservationId, null);

    public static RuntimeResourceReservationResult Rejected(Failure failure) =>
        new(RuntimeResourceReservationStatus.Rejected, null, failure);
}

public enum RuntimeResourceReleaseStatus
{
    Released = 1,
    Rejected = 2
}

public sealed record RuntimeResourceReleaseResult
{
    private RuntimeResourceReleaseResult(RuntimeResourceReleaseStatus status, Failure? failure)
    {
        if (!Enum.IsDefined(typeof(RuntimeResourceReleaseStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RuntimeResourceReleaseStatus.Released && failure is not null)
            throw new ArgumentException("Successful resource release must not carry a failure.", nameof(failure));
        if (status == RuntimeResourceReleaseStatus.Rejected && failure is null)
            throw new ArgumentException("Rejected resource release requires a failure.", nameof(failure));

        Status = status;
        Failure = failure;
    }

    public RuntimeResourceReleaseStatus Status { get; }
    public Failure? Failure { get; }
    public bool Succeeded => Status == RuntimeResourceReleaseStatus.Released;

    public static RuntimeResourceReleaseResult Released() =>
        new(RuntimeResourceReleaseStatus.Released, null);

    public static RuntimeResourceReleaseResult Rejected(Failure failure) =>
        new(RuntimeResourceReleaseStatus.Rejected, failure);
}

public interface IRuntimeResourceReservationManager
{
    RuntimeResourceReservationResult Reserve(PreparedExecutionContract preparedExecution);

    RuntimeResourceReleaseResult Release(Identity reservationId);
}

public interface IRuntimeClock
{
    UtcTimestamp GetUtcNow();
}

public sealed class SystemRuntimeClock : IRuntimeClock
{
    public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UtcNow);
}

public enum RuntimeAbortStatus
{
    Aborted = 1,
    Rejected = 2
}

public sealed record RuntimeAbortResult
{
    private RuntimeAbortResult(
        RuntimeAbortStatus status,
        PreparedExecutionId preparedExecutionId,
        Revision executionRevision,
        Failure? failure)
    {
        if (!Enum.IsDefined(typeof(RuntimeAbortStatus), status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == RuntimeAbortStatus.Aborted && failure is not null)
            throw new ArgumentException("Successful abort must not carry a failure.", nameof(failure));
        if (status == RuntimeAbortStatus.Rejected && failure is null)
            throw new ArgumentException("Rejected abort requires a failure.", nameof(failure));

        Status = status;
        PreparedExecutionId = preparedExecutionId;
        ExecutionRevision = executionRevision;
        Failure = failure;
    }

    public RuntimeAbortStatus Status { get; }
    public PreparedExecutionId PreparedExecutionId { get; }
    public Revision ExecutionRevision { get; }
    public Failure? Failure { get; }
    public bool Succeeded => Status == RuntimeAbortStatus.Aborted;

    internal static RuntimeAbortResult Aborted(PreparedExecutionId preparedExecutionId, Revision executionRevision) =>
        new(RuntimeAbortStatus.Aborted, preparedExecutionId, executionRevision, null);

    internal static RuntimeAbortResult Rejected(
        PreparedExecutionId preparedExecutionId,
        Revision executionRevision,
        Failure failure) =>
        new(RuntimeAbortStatus.Rejected, preparedExecutionId, executionRevision, failure);
}

public sealed record CommittedRuntimeExecution
{
    internal CommittedRuntimeExecution(
        ExecutionInstanceId executionInstanceId,
        Revision executionRevision,
        PreparedExecutionContract preparedExecution,
        Identity reservationId)
    {
        if (reservationId.IsEmpty)
            throw new ArgumentException("Committed execution requires a reservation identity.", nameof(reservationId));

        ExecutionInstanceId = executionInstanceId;
        ExecutionRevision = executionRevision;
        PreparedExecution = preparedExecution ?? throw new ArgumentNullException(nameof(preparedExecution));
        ReservationId = reservationId;
    }

    public ExecutionInstanceId ExecutionInstanceId { get; }
    public Revision ExecutionRevision { get; }
    public PreparedExecutionContract PreparedExecution { get; }
    public Identity ReservationId { get; }
}

public sealed class TransactionalRuntime
{
    private readonly object _gate = new();
    private readonly IRuntimeResourceReservationManager _reservationManager;
    private readonly IRuntimeClock _clock;
    private readonly Dictionary<PreparedExecutionId, PreparedRuntimeExecution> _prepared = new();
    private readonly HashSet<PreparedExecutionId> _consumedPreparedExecutions = new();
    private readonly List<RuntimeObservation> _observations = new();

    private Revision _executionRevision = Revision.Initial;
    private CommittedRuntimeExecution? _activeExecution;

    public TransactionalRuntime(
        IRuntimeResourceReservationManager reservationManager,
        IRuntimeClock? clock = null)
    {
        _reservationManager = reservationManager ?? throw new ArgumentNullException(nameof(reservationManager));
        _clock = clock ?? new SystemRuntimeClock();
    }

    public RuntimeExecutionState State
    {
        get
        {
            lock (_gate)
                return CreateExecutionState();
        }
    }

    public CommittedRuntimeExecution? ActiveExecution
    {
        get
        {
            lock (_gate)
                return _activeExecution;
        }
    }

    public IReadOnlyList<RuntimeObservation> Observations
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<RuntimeObservation>(_observations.ToArray());
        }
    }

    public IReadOnlyList<PreparedExecutionId> PreparedExecutionIds
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<PreparedExecutionId>(
                    _prepared.Keys
                        .OrderBy(id => id.ToString(), StringComparer.Ordinal)
                        .ToArray());
            }
        }
    }

    public RuntimePrepareResult Prepare(PreparedExecutionContract preparedExecution)
    {
        ArgumentNullException.ThrowIfNull(preparedExecution);

        lock (_gate)
        {
            if (_consumedPreparedExecutions.Contains(preparedExecution.PreparedExecutionId))
            {
                return RejectPrepare(
                    preparedExecution.PreparedExecutionId,
                    new Failure(
                        "runtime.prepare.already_consumed",
                        "The prepared execution was already committed or aborted and cannot be prepared again."));
            }

            if (_prepared.TryGetValue(preparedExecution.PreparedExecutionId, out var existing))
            {
                return new RuntimePrepareResult(
                    RuntimeContractVersion.Current,
                    preparedExecution.PreparedExecutionId,
                    RuntimePrepareStatus.Prepared,
                    existing.ReservationId,
                    null);
            }

            var validationFailure = ValidatePreparedExecution(preparedExecution);
            if (validationFailure is not null)
                return RejectPrepare(preparedExecution.PreparedExecutionId, validationFailure.Value);

            RuntimeResourceReservationResult reservationResult;
            try
            {
                reservationResult = _reservationManager.Reserve(preparedExecution)
                    ?? throw new InvalidOperationException("Resource reservation manager returned no result.");
            }
            catch (Exception exception)
            {
                return RejectPrepare(
                    preparedExecution.PreparedExecutionId,
                    new Failure(
                        "runtime.prepare.reservation_exception",
                        $"Resource reservation failed unexpectedly: {exception.GetType().Name}."));
            }

            if (!reservationResult.Succeeded)
            {
                return RejectPrepare(
                    preparedExecution.PreparedExecutionId,
                    reservationResult.Failure ?? new Failure(
                        "runtime.prepare.reservation_rejected",
                        "Resource reservation was rejected."));
            }

            var reservationId = reservationResult.ReservationId!.Value;
            if (ReservationIdentityInUse(reservationId))
            {
                return RejectPrepare(
                    preparedExecution.PreparedExecutionId,
                    new Failure(
                        "runtime.prepare.reservation_identity_conflict",
                        "Resource reservation manager returned an identity that is already in use."));
            }

            _prepared.Add(
                preparedExecution.PreparedExecutionId,
                new PreparedRuntimeExecution(preparedExecution, reservationId));

            Observe("runtime.prepare.prepared", null);

            return new RuntimePrepareResult(
                RuntimeContractVersion.Current,
                preparedExecution.PreparedExecutionId,
                RuntimePrepareStatus.Prepared,
                reservationId,
                null);
        }
    }

    public RuntimeCommitResult Commit(RuntimeCommitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_consumedPreparedExecutions.Contains(request.PreparedExecutionId))
            {
                return RejectCommit(new Failure(
                    "runtime.commit.already_consumed",
                    "The prepared execution was already committed or aborted."));
            }

            if (!_prepared.TryGetValue(request.PreparedExecutionId, out var prepared))
            {
                return RejectCommit(new Failure(
                    "runtime.commit.not_prepared",
                    "No prepared execution exists for the requested identity."));
            }

            if (request.ReservationId != prepared.ReservationId)
            {
                return RejectCommit(new Failure(
                    "runtime.commit.reservation_mismatch",
                    "Commit request reservation identity does not match the prepared execution."));
            }

            if (request.ExpectedExecutionRevision != _executionRevision)
            {
                return RejectCommit(new Failure(
                    "runtime.commit.execution_revision_conflict",
                    $"Expected execution revision '{request.ExpectedExecutionRevision}' does not match current revision '{_executionRevision}'."));
            }

            var staleFailure = ValidateAgainstCommittedAuthority(prepared.PreparedExecution);
            if (staleFailure is not null)
                return RejectCommit(staleFailure.Value);

            Revision nextRevision;
            try
            {
                nextRevision = _executionRevision.Next();
            }
            catch (InvalidOperationException)
            {
                return RejectCommit(new Failure(
                    "runtime.commit.revision_exhausted",
                    "Execution revision cannot advance beyond the current value."));
            }

            var executionInstanceId = new ExecutionInstanceId(RuntimeIdentity.Create(
                "runtime-execution-instance",
                prepared.PreparedExecution.PreparedExecutionId.ToString(),
                nextRevision.ToString()));

            var previousExecution = _activeExecution;
            var committedExecution = new CommittedRuntimeExecution(
                executionInstanceId,
                nextRevision,
                prepared.PreparedExecution,
                prepared.ReservationId);

            _activeExecution = committedExecution;
            _executionRevision = nextRevision;
            _prepared.Remove(request.PreparedExecutionId);
            _consumedPreparedExecutions.Add(request.PreparedExecutionId);

            Observe("runtime.commit.committed", null);

            if (previousExecution is not null && previousExecution.ReservationId != committedExecution.ReservationId)
                ReleaseSupersededReservation(previousExecution.ReservationId);

            return new RuntimeCommitResult(
                RuntimeContractVersion.Current,
                RuntimeCommitStatus.Committed,
                executionInstanceId,
                nextRevision,
                null);
        }
    }

    public RuntimeAbortResult Abort(PreparedExecutionId preparedExecutionId, Identity reservationId)
    {
        if (reservationId.IsEmpty)
            throw new ArgumentException("Abort requires a reservation identity.", nameof(reservationId));

        lock (_gate)
        {
            if (_consumedPreparedExecutions.Contains(preparedExecutionId))
            {
                return RejectAbort(
                    preparedExecutionId,
                    new Failure(
                        "runtime.abort.already_consumed",
                        "The prepared execution was already committed or aborted."));
            }

            if (!_prepared.TryGetValue(preparedExecutionId, out var prepared))
            {
                return RejectAbort(
                    preparedExecutionId,
                    new Failure(
                        "runtime.abort.not_prepared",
                        "No prepared execution exists for the requested identity."));
            }

            if (prepared.ReservationId != reservationId)
            {
                return RejectAbort(
                    preparedExecutionId,
                    new Failure(
                        "runtime.abort.reservation_mismatch",
                        "Abort reservation identity does not match the prepared execution."));
            }

            RuntimeResourceReleaseResult releaseResult;
            try
            {
                releaseResult = _reservationManager.Release(reservationId)
                    ?? throw new InvalidOperationException("Resource reservation manager returned no release result.");
            }
            catch (Exception exception)
            {
                return RejectAbort(
                    preparedExecutionId,
                    new Failure(
                        "runtime.abort.release_exception",
                        $"Resource release failed unexpectedly: {exception.GetType().Name}."));
            }

            if (!releaseResult.Succeeded)
            {
                return RejectAbort(
                    preparedExecutionId,
                    releaseResult.Failure ?? new Failure(
                        "runtime.abort.release_rejected",
                        "Resource release was rejected."));
            }

            _prepared.Remove(preparedExecutionId);
            _consumedPreparedExecutions.Add(preparedExecutionId);
            Observe("runtime.prepare.aborted", null);

            return RuntimeAbortResult.Aborted(preparedExecutionId, _executionRevision);
        }
    }

    private Failure? ValidatePreparedExecution(PreparedExecutionContract preparedExecution)
    {
        if (preparedExecution.Bindings.Count == 0)
        {
            return new Failure(
                "runtime.prepare.bindings_empty",
                "Prepared execution must contain at least one binding.");
        }

        if (preparedExecution.Bindings.Select(binding => binding.LogicalNodeId).Distinct().Count() != preparedExecution.Bindings.Count)
        {
            return new Failure(
                "runtime.prepare.logical_node_duplicate",
                "Prepared execution logical node identities must be unique.");
        }

        if (preparedExecution.Bindings.Select(binding => binding.Resource.ResourceId).Distinct().Count() != preparedExecution.Bindings.Count)
        {
            return new Failure(
                "runtime.prepare.resource_duplicate",
                "Prepared execution resources must be unique within one transaction.");
        }

        if (preparedExecution.Bindings.Any(binding => !binding.Resource.Reservable))
        {
            return new Failure(
                "runtime.prepare.resource_not_reservable",
                "Prepared execution contains a resource that is not reservable.");
        }

        return ValidateAgainstCommittedAuthority(preparedExecution);
    }

    private Failure? ValidateAgainstCommittedAuthority(PreparedExecutionContract preparedExecution)
    {
        if (_activeExecution is null)
            return null;

        var activeAuthority = _activeExecution.PreparedExecution.AuthoritySnapshot;
        var incomingAuthority = preparedExecution.AuthoritySnapshot;

        if (incomingAuthority.StateId != activeAuthority.StateId)
        {
            return new Failure(
                "runtime.prepare.authority_identity_mismatch",
                "Prepared execution belongs to a different authoritative state identity than the committed execution.");
        }

        if (incomingAuthority.Revision.CompareTo(activeAuthority.Revision) <= 0)
        {
            return new Failure(
                "runtime.prepare.stale_authority_revision",
                $"Prepared execution authority revision '{incomingAuthority.Revision}' is not newer than committed authority revision '{activeAuthority.Revision}'.");
        }

        return null;
    }

    private bool ReservationIdentityInUse(Identity reservationId) =>
        (_activeExecution is not null && _activeExecution.ReservationId == reservationId) ||
        _prepared.Values.Any(prepared => prepared.ReservationId == reservationId);

    private RuntimePrepareResult RejectPrepare(PreparedExecutionId preparedExecutionId, Failure failure)
    {
        Observe("runtime.prepare.rejected", failure);
        return new RuntimePrepareResult(
            RuntimeContractVersion.Current,
            preparedExecutionId,
            RuntimePrepareStatus.Rejected,
            null,
            failure);
    }

    private RuntimeCommitResult RejectCommit(Failure failure)
    {
        Observe("runtime.commit.rejected", failure);
        return new RuntimeCommitResult(
            RuntimeContractVersion.Current,
            RuntimeCommitStatus.Rejected,
            null,
            _executionRevision,
            failure);
    }

    private RuntimeAbortResult RejectAbort(PreparedExecutionId preparedExecutionId, Failure failure)
    {
        Observe("runtime.abort.rejected", failure);
        return RuntimeAbortResult.Rejected(preparedExecutionId, _executionRevision, failure);
    }

    private RuntimeExecutionState CreateExecutionState()
    {
        if (_activeExecution is not null)
        {
            return new RuntimeExecutionState(
                RuntimeContractVersion.Current,
                _activeExecution.ExecutionInstanceId,
                _executionRevision,
                RuntimeExecutionStatus.Committed,
                null);
        }

        if (_prepared.Count > 0)
        {
            return new RuntimeExecutionState(
                RuntimeContractVersion.Current,
                null,
                _executionRevision,
                RuntimeExecutionStatus.Prepared,
                null);
        }

        return new RuntimeExecutionState(
            RuntimeContractVersion.Current,
            null,
            _executionRevision,
            RuntimeExecutionStatus.Idle,
            null);
    }

    private void ReleaseSupersededReservation(Identity reservationId)
    {
        RuntimeResourceReleaseResult releaseResult;
        try
        {
            releaseResult = _reservationManager.Release(reservationId)
                ?? throw new InvalidOperationException("Resource reservation manager returned no release result.");
        }
        catch (Exception exception)
        {
            Observe(
                "runtime.resource.release_failed",
                new Failure(
                    "runtime.resource.release_exception",
                    $"Superseded resource release failed unexpectedly: {exception.GetType().Name}."));
            return;
        }

        if (!releaseResult.Succeeded)
        {
            Observe(
                "runtime.resource.release_failed",
                releaseResult.Failure ?? new Failure(
                    "runtime.resource.release_rejected",
                    "Superseded resource release was rejected."));
        }
    }

    private void Observe(string code, Failure? failure) =>
        _observations.Add(new RuntimeObservation(
            RuntimeContractVersion.Current,
            _executionRevision,
            _clock.GetUtcNow(),
            code,
            failure));

    private sealed record PreparedRuntimeExecution(
        PreparedExecutionContract PreparedExecution,
        Identity ReservationId);
}

internal static class RuntimeIdentity
{
    public static Identity Create(string scope, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Runtime identity scope is required.", nameof(scope));
        if (parts is null)
            throw new ArgumentNullException(nameof(parts));

        var canonical = string.Join('\u001f', new[] { scope }.Concat(parts));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var guidText = Convert.ToHexString(hash.AsSpan(0, 16));
        return new Identity(Guid.ParseExact(guidText, "N"));
    }
}
