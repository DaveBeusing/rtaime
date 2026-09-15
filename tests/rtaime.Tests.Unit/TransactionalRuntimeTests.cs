using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Unit;

public sealed class TransactionalRuntimeTests
{
    [Fact]
    public void Prepare_success_keeps_runtime_uncommitted()
    {
        var reservations = new FakeReservationManager();
        var runtime = CreateRuntime(reservations);
        var prepared = CreatePreparedExecution(1, 1);

        var result = runtime.Prepare(prepared);

        Assert.Equal(RuntimePrepareStatus.Prepared, result.Status);
        Assert.NotNull(result.ReservationId);
        Assert.Null(result.Failure);
        Assert.Null(runtime.ActiveExecution);
        Assert.Equal(RuntimeExecutionStatus.Prepared, runtime.State.Status);
        Assert.Equal(Revision.Initial, runtime.State.ExecutionRevision);
    }

    [Fact]
    public void Prepare_failure_leaves_runtime_idle()
    {
        var reservations = new FakeReservationManager { RejectNextReservation = true };
        var runtime = CreateRuntime(reservations);

        var result = runtime.Prepare(CreatePreparedExecution(1, 1));

        Assert.Equal(RuntimePrepareStatus.Rejected, result.Status);
        Assert.Equal("test.reservation.rejected", result.Failure?.Code);
        Assert.Null(runtime.ActiveExecution);
        Assert.Empty(runtime.PreparedExecutionIds);
        Assert.Equal(RuntimeExecutionStatus.Idle, runtime.State.Status);
        Assert.Equal(Revision.Initial, runtime.State.ExecutionRevision);
    }

    [Fact]
    public void Commit_success_activates_execution_revision()
    {
        var runtime = CreateRuntime(new FakeReservationManager());
        var prepared = CreatePreparedExecution(4, 1);
        var prepare = runtime.Prepare(prepared);

        var commit = runtime.Commit(CreateCommitRequest(prepared, prepare, Revision.Initial));

        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);
        Assert.Equal(new Revision(1), commit.ExecutionRevision);
        Assert.NotNull(commit.ExecutionInstanceId);
        Assert.Null(commit.Failure);
        Assert.NotNull(runtime.ActiveExecution);
        Assert.Equal(prepared.PreparedExecutionId, runtime.ActiveExecution!.PreparedExecution.PreparedExecutionId);
        Assert.Equal(commit.ExecutionInstanceId, runtime.ActiveExecution.ExecutionInstanceId);
        Assert.Equal(RuntimeExecutionStatus.Committed, runtime.State.Status);
        Assert.Equal(new Revision(1), runtime.State.ExecutionRevision);
    }

    [Fact]
    public void Invalid_commit_does_not_consume_prepared_execution()
    {
        var runtime = CreateRuntime(new FakeReservationManager());
        var prepared = CreatePreparedExecution(1, 1);
        var prepare = runtime.Prepare(prepared);
        var wrongReservation = Id(9999);

        var rejected = runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            prepared.PreparedExecutionId,
            wrongReservation,
            Revision.Initial));

        Assert.Equal(RuntimeCommitStatus.Rejected, rejected.Status);
        Assert.Equal("runtime.commit.reservation_mismatch", rejected.Failure?.Code);
        Assert.Equal(RuntimeExecutionStatus.Prepared, runtime.State.Status);
        Assert.Single(runtime.PreparedExecutionIds);

        var committed = runtime.Commit(CreateCommitRequest(prepared, prepare, Revision.Initial));
        Assert.Equal(RuntimeCommitStatus.Committed, committed.Status);
    }

    [Fact]
    public void Duplicate_commit_is_rejected_without_changing_active_execution()
    {
        var runtime = CreateRuntime(new FakeReservationManager());
        var prepared = CreatePreparedExecution(1, 1);
        var prepare = runtime.Prepare(prepared);
        var request = CreateCommitRequest(prepared, prepare, Revision.Initial);
        var first = runtime.Commit(request);
        var activeBeforeDuplicate = runtime.ActiveExecution;

        var duplicate = runtime.Commit(request);

        Assert.Equal(RuntimeCommitStatus.Committed, first.Status);
        Assert.Equal(RuntimeCommitStatus.Rejected, duplicate.Status);
        Assert.Equal("runtime.commit.already_consumed", duplicate.Failure?.Code);
        Assert.Equal(activeBeforeDuplicate, runtime.ActiveExecution);
        Assert.Equal(new Revision(1), runtime.State.ExecutionRevision);
    }

    [Fact]
    public void Stale_prepared_contract_is_rejected_after_newer_commit()
    {
        var runtime = CreateRuntime(new FakeReservationManager());
        var stale = CreatePreparedExecution(1, 1);
        var newer = CreatePreparedExecution(2, 2);
        var stalePrepare = runtime.Prepare(stale);
        var newerPrepare = runtime.Prepare(newer);

        var newerCommit = runtime.Commit(CreateCommitRequest(newer, newerPrepare, Revision.Initial));
        var activeAfterNewerCommit = runtime.ActiveExecution;
        var staleCommit = runtime.Commit(CreateCommitRequest(stale, stalePrepare, new Revision(1)));

        Assert.Equal(RuntimeCommitStatus.Committed, newerCommit.Status);
        Assert.Equal(RuntimeCommitStatus.Rejected, staleCommit.Status);
        Assert.Equal("runtime.prepare.stale_authority_revision", staleCommit.Failure?.Code);
        Assert.Equal(activeAfterNewerCommit, runtime.ActiveExecution);
        Assert.Equal(new Revision(1), runtime.State.ExecutionRevision);
    }

    [Fact]
    public void Abort_releases_reservation_and_prevents_commit()
    {
        var reservations = new FakeReservationManager();
        var runtime = CreateRuntime(reservations);
        var prepared = CreatePreparedExecution(1, 1);
        var prepare = runtime.Prepare(prepared);
        var reservationId = AssertReservation(prepare);

        var abort = runtime.Abort(prepared.PreparedExecutionId, reservationId);
        var commitAfterAbort = runtime.Commit(CreateCommitRequest(prepared, prepare, Revision.Initial));

        Assert.Equal(RuntimeAbortStatus.Aborted, abort.Status);
        Assert.Contains(reservationId, reservations.ReleasedReservations);
        Assert.Empty(runtime.PreparedExecutionIds);
        Assert.Equal(RuntimeExecutionStatus.Idle, runtime.State.Status);
        Assert.Equal(RuntimeCommitStatus.Rejected, commitAfterAbort.Status);
        Assert.Equal("runtime.commit.already_consumed", commitAfterAbort.Failure?.Code);
    }

    [Fact]
    public void Previous_execution_survives_failed_prepare()
    {
        var reservations = new FakeReservationManager();
        var runtime = CreateRuntime(reservations);
        var first = CreatePreparedExecution(1, 1);
        var firstPrepare = runtime.Prepare(first);
        var firstCommit = runtime.Commit(CreateCommitRequest(first, firstPrepare, Revision.Initial));
        var activeBeforeFailure = runtime.ActiveExecution;
        reservations.RejectNextReservation = true;

        var failedPrepare = runtime.Prepare(CreatePreparedExecution(2, 2));

        Assert.Equal(RuntimeCommitStatus.Committed, firstCommit.Status);
        Assert.Equal(RuntimePrepareStatus.Rejected, failedPrepare.Status);
        Assert.Equal(activeBeforeFailure, runtime.ActiveExecution);
        Assert.Equal(RuntimeExecutionStatus.Committed, runtime.State.Status);
        Assert.Equal(new Revision(1), runtime.State.ExecutionRevision);
    }

    [Fact]
    public void Execution_revision_transitions_are_monotonic()
    {
        var runtime = CreateRuntime(new FakeReservationManager());
        var first = CreatePreparedExecution(1, 1);
        var firstPrepare = runtime.Prepare(first);
        var firstCommit = runtime.Commit(CreateCommitRequest(first, firstPrepare, Revision.Initial));

        var second = CreatePreparedExecution(2, 2);
        var secondPrepare = runtime.Prepare(second);
        var secondCommit = runtime.Commit(CreateCommitRequest(second, secondPrepare, firstCommit.ExecutionRevision));

        Assert.Equal(new Revision(1), firstCommit.ExecutionRevision);
        Assert.Equal(new Revision(2), secondCommit.ExecutionRevision);
        Assert.Equal(new Revision(2), runtime.State.ExecutionRevision);
        Assert.NotEqual(firstCommit.ExecutionInstanceId, secondCommit.ExecutionInstanceId);
    }

    [Fact]
    public void Prepare_does_not_change_active_execution()
    {
        var runtime = CreateRuntime(new FakeReservationManager());
        var first = CreatePreparedExecution(1, 1);
        var firstPrepare = runtime.Prepare(first);
        runtime.Commit(CreateCommitRequest(first, firstPrepare, Revision.Initial));
        var activeBeforePrepare = runtime.ActiveExecution;

        var secondPrepare = runtime.Prepare(CreatePreparedExecution(2, 2));

        Assert.Equal(RuntimePrepareStatus.Prepared, secondPrepare.Status);
        Assert.Equal(activeBeforePrepare, runtime.ActiveExecution);
        Assert.Equal(RuntimeExecutionStatus.Committed, runtime.State.Status);
        Assert.Equal(new Revision(1), runtime.State.ExecutionRevision);
        Assert.Single(runtime.PreparedExecutionIds);
    }

    private static TransactionalRuntime CreateRuntime(FakeReservationManager reservations) =>
        new(reservations, new FakeClock());

    private static RuntimeCommitRequest CreateCommitRequest(
        PreparedExecutionContract prepared,
        RuntimePrepareResult prepare,
        Revision expectedRevision) =>
        new(
            RuntimeContractVersion.Current,
            prepared.PreparedExecutionId,
            AssertReservation(prepare),
            expectedRevision);

    private static Identity AssertReservation(RuntimePrepareResult prepare)
    {
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);
        Assert.True(prepare.ReservationId.HasValue);
        return prepare.ReservationId!.Value;
    }

    private static PreparedExecutionContract CreatePreparedExecution(ulong authorityRevision, int variant)
    {
        var providerId = new ProviderId(Id(100));
        var resource = new ProviderResourceDescriptor(
            new ProviderResourceId(Id(2000 + variant)),
            providerId,
            "media.route",
            1,
            true);

        var binding = new PreparedExecutionBinding(
            Id(3000 + variant),
            new CapabilityId(Id(4000 + variant)),
            resource,
            new MediaSourceId(Id(5000 + variant)),
            new MediaSinkId(Id(6000 + variant)));

        return new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            new PreparedExecutionId(Id(7000 + variant)),
            new AuthoritySnapshotReference(Id(8000), new Revision(authorityRevision)),
            new Generation(authorityRevision),
            new[] { binding });
    }

    private static Identity Id(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        return new Identity(new Guid(bytes));
    }

    private sealed class FakeClock : IRuntimeClock
    {
        private long _milliseconds;

        public UtcTimestamp GetUtcNow() =>
            UtcTimestamp.FromUnixTimeMilliseconds(_milliseconds++);
    }

    private sealed class FakeReservationManager : IRuntimeResourceReservationManager
    {
        private int _reservationSequence = 9000;

        public bool RejectNextReservation { get; set; }
        public List<Identity> ReleasedReservations { get; } = new();

        public RuntimeResourceReservationResult Reserve(PreparedExecutionContract preparedExecution)
        {
            if (RejectNextReservation)
            {
                RejectNextReservation = false;
                return RuntimeResourceReservationResult.Rejected(
                    new Failure("test.reservation.rejected", "Reservation rejected by test manager."));
            }

            return RuntimeResourceReservationResult.Reserved(Id(++_reservationSequence));
        }

        public RuntimeResourceReleaseResult Release(Identity reservationId)
        {
            ReleasedReservations.Add(reservationId);
            return RuntimeResourceReleaseResult.Released();
        }
    }
}
