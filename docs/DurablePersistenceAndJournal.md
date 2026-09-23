<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>
# Durable Persistence & Production Journal

## Purpose

rtaime V1 keeps durability in three independent lanes:

1. management persistence,
2. Production Journal,
3. media recording.

The persistence subsystem covers the first two. Media recording remains owned by `rtaime.Recording`.

The core rule is unchanged: active production truth remains in memory and SQLite must not become a synchronous dependency of the RT-critical media path.

## SQLite boundary

`Microsoft.Data.Sqlite` is referenced only by `rtaime.Persistence`. Core, contracts, Runtime, Media and providers remain SQLite-neutral. No additional persistence project is introduced.

Management persistence and the Production Journal use separate database files and separate APIs even when they live under the same operational data root.

## Management persistence

`SqliteManagementStore` provides transactional storage for management/configuration documents and production checkpoints.

Management documents use:

- `(area, key)` identity,
- monotonic document version,
- optimistic expected-version writes,
- UTC update timestamp,
- JSON payload,
- SHA-256 payload checksum.

Checkpoint records use:

- stable checkpoint identity,
- production identity,
- authoritative revision,
- UTC creation timestamp,
- explicit payload format,
- opaque payload bytes,
- SHA-256 payload checksum.

The current authoritative checkpoint payload includes confirmed Preview/Program routing, the optional `ActiveSceneId`, and the configured governed output-role states (stable role ID/kind, source, provider selector, target, format/timing policy and enabled state). Scene definitions remain part of the production specification rather than duplicated into every checkpoint. Older payloads without `ActiveSceneId` are interpreted as having no confirmed active Scene; older V1 payloads without `OutputRoles` recover the production specification defaults with Program synchronized to the recovered Program route.

Only one checkpoint payload is accepted for a given production/revision pair. Replaying the same payload is idempotent; a different payload for the same revision fails closed.

## Production Journal

`SqliteProductionJournalStore` is a purpose-built append-only path, separate from ordinary management persistence.

Each record persists:

- monotonic ordinal,
- EventId,
- ProductionId,
- authoritative revision,
- UTC timestamp,
- category/code/detail,
- optional CausationId,
- optional failure code/message,
- event checksum,
- previous-entry hash,
- entry hash.

EventId replay with identical content is idempotent. Reuse of an EventId with different content fails closed.

The hash chain plus separately persisted journal head detect modified records, reordering and tail truncation during integrity verification. This is operational integrity evidence; it is not a cryptographic signature or external tamper-proof ledger.

## Bounded asynchronous ingress

`BoundedProductionJournal.TryAppend` never waits for SQLite. A single background worker preserves accepted event order and performs durable appends.

If the ingress capacity is exhausted:

- the new event is dropped,
- the drop counter advances,
- journal health becomes degraded,
- the producer is not synchronously blocked by storage.

A storage write failure advances the failure counter and records a machine-readable journal persistence failure. Explicit `FlushAsync` reports durability failure rather than silently claiming success.

The same pattern is used by `BoundedProductionCheckpointWriter`: authoritative commit does not synchronously wait for checkpoint storage.

## SQLite durability baseline

V1 uses:

- WAL journal mode,
- `synchronous=FULL`,
- foreign keys enabled,
- bounded busy timeout,
- explicit schema version metadata.

Schema v1 reference files are under `schemas/persistence/v1/`.

## Recovery boundary

Durable Persistence & Journal established the durable recovery basis:

```text
latest valid checkpoint
+
causal Production Journal
```

Process Recovery & Supervision activates that basis for ControlHost process recovery. Before a persisted authority snapshot is restored, ControlHost verifies the management SQLite store, checkpoint format and identity/version/revision/source constraints, validates recovered output roles against the current production specification and supported V1 policies, plus Production Journal SQLite/hash-chain integrity. Only then can the recovered authority participate in Runtime reconciliation.

The Production Journal remains evidence and diagnostic history rather than a general event-sourcing replay engine. V1 recovery restores the latest qualified authoritative checkpoint and reconciles the Runtime execution's committed `AuthoritySnapshot` against that Control revision; Runtime-local `ExecutionRevision` is a separate counter and is not used as Control authority. Recovery does not reconstruct arbitrary domain state by replaying every journal record.

Process Recovery & Supervision still does not claim exact live continuation after process crash or power loss, frame-identical Runtime continuation, preservation of in-flight transition phase, or distributed recovery. Those remain outside the qualified durability claim.

Detailed process recovery and supervision semantics are documented in `docs/ProcessRecoveryAndSupervision.md`.

## Failure semantics

Management persistence failure may impair durable management mutation or future ControlHost recovery, but it must not redefine already committed Runtime execution.

Journal pressure or journal-storage failure must be observable and must not synchronously block Program execution.

Checkpoint pressure or checkpoint-storage failure must be observable and must not roll back an authoritative state that has already crossed the Runtime commit boundary.

A malformed or contradictory durable recovery state fails closed instead of silently initializing a fresh revision.

## Evidence expectations

Persistence acceptance evidence includes:

- persistence survives store close/reopen,
- optimistic management version conflicts fail closed,
- checkpoints survive reopen and enforce one payload per production revision,
- journal order survives reopen,
- EventId replay is idempotent,
- conflicting EventId reuse fails closed,
- journal integrity verification validates checksums/hash chain/head,
- bounded ingress remains non-blocking when the store stalls,
- architecture tests prove SQLite remains at the Persistence boundary,
- full managed build/test suite remains green.

Process Recovery & Supervision adds recovery evidence for valid checkpoint restore, Runtime reconciliation without authority revision advancement, recovery conflicts, process replacement and stale client/session behavior.
## Show Control durable execution cursor

Show Control persists cue-list definitions plus only the execution cursor required for safe recovery. Persisted `Executing` or `Waiting` state is never treated as proof that the in-flight action completed; ControlHost restores it as `RecoveryRequired` and requires explicit operator acknowledgement.

Show Control uses the existing management document store with optimistic storage versioning. Structured production-journal entries cover cue-list save/selection, arm, GO, action start/completion, waits, failures, cancellation and recovery acknowledgement. Stable list/cue/action identities provide bounded causation evidence without turning the Production Journal into an execution replay engine.

See `docs/ShowControlCueSequencing.md` for the complete state and recovery semantics.
