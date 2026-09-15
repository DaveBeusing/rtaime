<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Durable Persistence & Production Journal

## Purpose

rtaime V1 keeps durability in three independent lanes:

1. management persistence,
2. Production Journal,
3. media recording.

This implementation covers the first two. Media recording remains owned by `rtaime.Recording`.

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

AP-15 persists the evidence needed by later recovery work:

```text
latest valid checkpoint
+
subsequent causal Production Journal
```

It does not implement automatic process restart, production reconstruction, replay orchestration or exact live continuation after power loss. Those remain recovery/supervision responsibilities.

## Failure semantics

Management persistence failure may impair durable management mutation, but it must not redefine already committed Runtime execution.

Journal pressure or journal-storage failure must be observable and must not synchronously block Program execution.

Checkpoint pressure or checkpoint-storage failure must be observable and must not roll back an authoritative state that has already crossed the Runtime commit boundary.

## Evidence expectations

Acceptance evidence includes:

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
