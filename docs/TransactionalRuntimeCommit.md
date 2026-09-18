# Transactional Runtime Commit Foundation

## Scope

Transactional Runtime Commit establishes the Runtime-side prepare/commit transaction boundary for a `PreparedExecutionContract`.

Change classification: `ARCHITECTURE`.

The implemented path is:

```text
Prepared Execution Contract
→ Runtime Prepare
→ Resource Reservation
→ Prepared
→ Commit Validation
→ Committed Execution
```

This package does not execute media frames. It establishes the state transition that later runtime/media packages execute.

## Authority boundary

Control remains the production authority.

Runtime does not derive or mutate authoritative production state. It receives a prepared execution produced from an authoritative snapshot and owns only the execution that has actually crossed the commit boundary.

The Runtime contract types remain unchanged in Transactional Runtime Commit.

## Transaction invariants

The implementation enforces the following invariants:

1. `Prepare` never replaces or mutates the active committed execution.
2. A prepared execution becomes active only through a successful `Commit`.
3. Commit requires the exact prepared-execution identity and reservation identity created during prepare.
4. Commit requires an `ExpectedExecutionRevision` matching the current Runtime execution revision.
5. The execution revision advances exactly once per successful commit.
6. Failed prepare, failed commit, and abort do not advance the execution revision.
7. A committed or aborted prepared execution is consumed and cannot be committed again.
8. A prepared execution is revalidated against the currently committed authoritative snapshot at commit time.
9. An older or equal authoritative revision cannot replace a newer committed authoritative revision.
10. An authoritative state identity mismatch is fail-closed.
11. Resource cleanup after a successful replacement cannot roll back the already committed execution.

## Runtime state

`TransactionalRuntime` keeps three distinct categories of state:

```text
Prepared state
    temporary reservation + PreparedExecutionContract

Committed state
    ExecutionInstanceId + ExecutionRevision + PreparedExecutionContract + reservation

Observed state
    RuntimeObservation sequence
```

These are intentionally not collapsed into one mutable object.

### RuntimeExecutionState

If no execution has been committed:

- no prepared transaction → `Idle`
- one or more prepared transactions → `Prepared`

If an execution is already committed, preparing a replacement does not hide or downgrade that committed state. `RuntimeExecutionState` remains `Committed` until a different execution successfully crosses the commit boundary.

## Prepare

Prepare performs runtime-local validation before resource reservation.

It rejects, among other invalid input:

- an empty binding set,
- duplicate logical node identities,
- duplicate resource identities,
- non-reservable resources,
- authoritative state identity mismatches,
- stale authoritative revisions,
- reservation identity conflicts,
- already consumed prepared executions.

A reservation failure returns `RuntimePrepareStatus.Rejected` and leaves the current committed execution untouched.

Repeated prepare of the same still-pending `PreparedExecutionId` is idempotent and returns the existing reservation rather than creating a second reservation.

## Resource reservation abstraction

Runtime depends on:

```text
IRuntimeResourceReservationManager
```

with two operations:

```text
Reserve(PreparedExecutionContract)
Release(Identity reservationId)
```

The abstraction deliberately does not introduce hardware-, GPU-, capture-, or vendor-specific types.

The resource manager returns explicit result objects rather than using exceptions for expected reservation/release rejection. Unexpected exceptions are converted into fail-closed Runtime failures.

Transactional Runtime Commit does not define provider discovery or real hardware reservation. Those remain outside this package.

## Commit boundary

Commit validates all transaction identity and concurrency information while holding the Runtime state boundary.

A commit is rejected when:

- the prepared execution is unknown,
- the prepared execution was already consumed,
- reservation identity does not match,
- expected execution revision is stale,
- the prepared contract has become stale relative to a newer committed authoritative snapshot,
- the execution revision cannot advance.

Only after all checks pass does Runtime create the next committed execution.

The transition is:

```text
ExecutionRevision N
+ valid prepared transaction
→ ExecutionRevision N+1
+ new active ExecutionInstanceId
```

The `ExecutionInstanceId` is deterministically derived from:

- `PreparedExecutionId`,
- the new execution revision.

No random identity generation occurs at the commit boundary.

## Replacement and cleanup

After a successful commit, the previous committed reservation is released.

The state swap occurs before cleanup of the superseded reservation. Therefore cleanup failure cannot revert or partially expose the new committed execution. A cleanup failure is recorded as a `RuntimeObservation` and the successful commit remains authoritative for Runtime execution.

This order is intentional: the visible commit boundary must not depend on post-commit cleanup.

## Abort

A still-pending prepared transaction can be aborted using its prepared-execution and reservation identities.

Successful abort:

- releases its reservation,
- removes the pending prepared transaction,
- marks that prepared execution as consumed,
- does not alter the active committed execution,
- does not advance the execution revision.

If reservation release is rejected, abort is rejected and the prepared transaction remains available for a later retry.

## Observation

Runtime records observations for:

- prepare success,
- prepare rejection,
- commit success,
- commit rejection,
- abort success,
- abort rejection,
- post-commit superseded-resource release failure.

Observation timestamps are supplied through `IRuntimeClock` so tests can use deterministic time without placing clock policy in Core or Contracts.

## Failure containment

The central Transactional Runtime Commit failure rule is:

```text
failure before commit
≠
change to committed execution
```

A failed prepare or invalid commit cannot damage the previous committed execution.

No failure in this package promotes Runtime into production authority. Runtime only decides whether a prepared execution can cross its execution commit boundary.

## Tests

Transactional Runtime Commit unit coverage includes:

- prepare success,
- prepare failure,
- commit success,
- invalid commit,
- duplicate commit,
- stale prepared contract,
- abort,
- previous execution survives failed prepare,
- deterministic monotonic revision transition,
- prepare while committed leaves active execution unchanged.

The full repository architecture, contract, behavioral, failure, integration, performance, and unit suites remain part of CI evidence.

## Out of scope

Transactional Runtime Commit intentionally does not implement:

- media frame processing,
- timing loops,
- Virtual Media execution,
- provider discovery,
- hardware reservation,
- GPU processing,
- capture/output SDKs,
- IPC or host protocols,
- persistence,
- Operator behavior,
- AI execution.

Virtual Media Runtime Slice consumes this transaction boundary for the first Virtual Media runtime vertical slice.
