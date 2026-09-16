<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Persistent State Backup, Migration & Recovery Foundation

## Purpose

This package establishes the first explicit recovery boundary for SQLite-backed persistent state.

It complements the software replacement and rollback mechanics introduced previously, but does not merge software deployment and persistent-state mutation into one implicit operation.

The foundation covers:

```text
persistent SQLite database
    ↓
integrity + schema inventory
    ↓
verified snapshot backup
    ↓
registered forward-only migration chain
    ↓
transactional schema/data mutation
    ↓
post-migration integrity / semantic verification
    ↓
PASS
```

On migration or post-migration verification failure:

```text
failure
    ↓
verify recorded backup evidence
    ↓
restore snapshot
    ↓
verify restored SQLite state
    ↓
rethrow original migration failure
```

A successful recovery does not rewrite the failed migration into a PASS.

## Existing production schemas

This package does not invent a new persisted product schema solely to exercise migration logic.

At implementation time the existing SQLite stores remain:

```text
management           schema version 1
production-journal   schema version 1
```

No production schema version is incremented by this package.

The integration qualification uses an isolated test-only schema to prove the migration engine.

## `SqliteStateMaintenance`

The persistence project now owns the maintenance boundary because SQLite implementation details belong to `rtaime.Persistence`.

The API supports:

- schema inventory inspection,
- SQLite `PRAGMA integrity_check`,
- online SQLite snapshot creation through the native SQLite backup API exposed by `Microsoft.Data.Sqlite`,
- SHA-256 backup evidence,
- exact schema-inventory comparison,
- verified restore,
- explicit registered migration steps,
- forward-only single-version transitions,
- one transaction for the selected migration chain,
- post-migration verification callback,
- automatic snapshot restore after migration or post-verification failure.

The API is not placed on a media hot path.

## Backup semantics

A backup is created before any schema mutation.

The backup path must be different from the active database and must not already exist. Existing backup evidence is never silently overwritten.

Before the snapshot is accepted the implementation verifies:

```text
source integrity
source schema inventory
backup integrity
backup schema inventory
backup file size
backup SHA-256
```

The snapshot records:

- source database path,
- backup path,
- backup size,
- SHA-256,
- schema component/version inventory,
- creation time.

## Migration semantics

Migrations are explicit values with:

```text
Component
FromVersion
ToVersion
SQL
```

The engine rejects:

- unknown migration paths,
- multiple competing migrations from the same version,
- downgrade requests,
- skipped versions,
- empty migration payloads,
- missing schema metadata,
- invalid or duplicate schema component metadata.

Only one-version forward steps are accepted:

```text
N → N+1
```

A larger target version therefore requires a complete registered chain.

The migration SQL and matching `schema_metadata` update execute in the same SQLite transaction.

## Post-migration verification

After commit, the engine always repeats SQLite integrity and schema inspection.

Callers may additionally supply a semantic post-migration verifier. This is intended for checks that cannot be expressed by SQLite structural integrity alone, for example application-level invariants or compatibility probes.

If that verifier fails, the migration result remains a failure and the pre-migration snapshot is restored.

## Recovery semantics

Restore requires explicit acknowledgement that all processes using the database have stopped.

Before replacement the active database WAL is checkpointed. The backup is copied into a sibling staging file and verified before activation.

Activation is performed by same-volume file replacement semantics:

```text
verified backup
    ↓
restore-stage
    ↓
active database → temporary previous slot
    ↓
restore-stage → active database
    ↓
verify schema + SHA-256
```

If activation verification fails, the previous active database is restored.

The temporary previous slot is not a long-term backup policy; it exists only to make restore activation fail-safe.

## Exclusive-access boundary

Migration and restore require explicit:

```text
acknowledgeExclusiveAccess = true
```

This foundation does not discover, stop or restart ControlHost, RuntimeHost, AIHost or third-party processes automatically.

Process/service orchestration remains an operational deployment concern.

## Relationship to software update

The existing managed software updater still records:

```text
persistentStateMigration = NOT_IMPLEMENTED
```

That remains intentionally true.

This package provides the persistence-side primitive required for a future coordinator, but it does not silently change the AP-23 update sequence to mutate state.

The policy therefore states:

```text
softwareUpdateStateOrchestration = EXPLICIT_ORCHESTRATION_REQUIRED
automaticBackgroundMigration = false
```

A future package may compose:

```text
verified software candidate
+ explicit persistent-state migration plan
+ backup evidence
+ process quiescence
+ software replacement
+ state migration
+ application verification
+ coordinated recovery
```

That orchestration must preserve which subsystem owns each failure and rollback decision.

## Qualification

`PersistentStateRecoveryIntegrationTests` covers:

- verified management database backup and restore,
- registered transactional forward migration,
- backup retention for migration evidence,
- post-migration verification failure with automatic snapshot restore,
- unknown migration rejection before backup/mutation,
- tampered backup rejection without modifying active state,
- mandatory exclusive-access acknowledgement.

The `Quality` gate validates the version-controlled state-maintenance policy and verifies that the required implementation and qualification cases remain wired into the repository.

## Scope boundary / non-claims

This package does not implement or claim:

- a new production database schema version,
- automatic schema migration during ordinary host startup,
- automatic migration as part of AP-23 software update,
- database downgrade migrations,
- distributed database backup,
- remote database engines,
- point-in-time recovery,
- long-term backup retention policy,
- encrypted backup storage,
- cloud backup replication,
- Windows service/process stop/start orchestration,
- Production Package activation,
- formal CRA conformity.

The initial implementation is specifically scoped to the current local SQLite persistence baseline.

`UNVERIFIED` remains distinct from `PASS` until repository CI executes the new integration and policy checks successfully.
