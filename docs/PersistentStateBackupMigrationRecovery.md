<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Persistent State Backup, Migration & Recovery Foundation

## Purpose

This document defines the SQLite persistence maintenance primitive introduced before coordinated software/state upgrades.

The persistence layer owns backup, registered forward migration and verified restore. It does not own software discovery, software replacement, host lifecycle or Production Package activation.

## Maintenance flow

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

The current production SQLite stores remain:

```text
management           schema version 1
production-journal   schema version 1
```

No production schema version is incremented merely to exercise migration logic.

Ordinary store initialization first inspects the database identity and component version inside a serialized SQLite transaction. An unsupported version, a missing component identity, or an unrecognized nonempty database is rejected before bootstrap DDL or journal-mode changes. Opening future state with an older binary therefore preserves its schema and data, including tables that the future schema retired.

Only an empty database with no application objects, application ID or user version can receive the initial schema. Its tables, indexes, metadata and initial journal head are created in one transaction; failed initialization rolls back the entire bootstrap. Existing supported stores are validated and opened without replaying bootstrap DDL. Missing required tables require explicit recovery instead of silently creating empty replacement tables. Upgrades continue to require the registered maintenance migration path; both production schema versions remain at 1.

## Legacy Control authority compatibility

Control contract 1.0 authority checkpoints are interpreted compatibly when loaded by the current 1.1 production specification. Recovery preserves production IDs, revision, routes, Scene/output configuration and compositing. The original stored checkpoint is not upgraded in place, and unsupported contracts do not trigger a fresh empty production.

Retain a verified backup before operational repair. A startup timeout with `planning.authority.version_mismatch` at `reconcile-prepare` identifies the contract boundary; increasing the startup timeout or deleting management/journal databases is not the supported repair. Use the compatible recovery build and the normal lifecycle. The next confirmed production mutation persists the current contract at its next authority revision. See [Durable Persistence & Journal](DurablePersistenceAndJournal.md).

The Windows regression fixture `tests/rtaime.Tests.Integration/TestData/LegacyAuthorityV1.json` retains revision 8, Program/Aux routes and legacy CG state. `LegacyAuthorityCheckpointRecoveryTests` verifies real Runtime reconciliation, repeated recovery, byte-for-byte original checkpoint preservation, a later current-contract commit and fail-closed unsupported versions.

## `SqliteStateMaintenance`

The persistence project exposes:

- schema inventory inspection,
- SQLite `PRAGMA integrity_check`,
- SQLite snapshot creation through the backup API,
- SHA-256 backup evidence,
- exact schema-inventory comparison,
- verified restore,
- explicit registered migration steps,
- forward-only single-version transitions,
- transactional migration,
- post-migration verification callbacks,
- automatic snapshot restore after migration or post-verification failure.

The API is not part of a synchronous media hot path.

## Backup semantics

A migration backup is created before schema mutation.

The backup path must differ from the active database and must not already exist. Existing backup evidence is never silently overwritten.

Accepted snapshots bind:

```text
source integrity
source schema inventory
backup integrity
backup schema inventory
backup size
backup SHA-256
```

## Migration semantics

Each migration is explicit:

```text
Component
FromVersion
ToVersion
SQL
```

The engine rejects unknown paths, competing steps, downgrade requests, skipped versions, empty SQL, missing schema metadata and duplicate component metadata.

Only:

```text
N → N+1
```

steps are accepted. A larger target requires a complete registered chain.

Migration SQL and the matching `schema_metadata` advancement run in the same SQLite transaction.

Cancellation during migration or post-migration verification remains a failed operation. Once a verified backup exists, required snapshot compensation uses its own two-minute cooperative recovery deadline, independent of the caller's cancellation token. A verifier that returns after cancelling the operation cannot turn the migration into success. Compensation is awaited before the original cancellation or failure is rethrown.

The recovery deadline is observed by the existing cancellable hashing and SQLite validation boundaries; it does not forcibly interrupt synchronous filesystem calls. If recovery itself times out or fails, both failures and the database/backup locations are reported, and the backup remains available. Keep database users stopped until the intended schema and payload have been verified; cancellation alone is not evidence that restoration completed.

## Recovery semantics

Restore requires explicit acknowledgement that processes using the database have stopped.

Before active replacement, WAL state is checkpointed. The verified backup is staged beside the active database, activated by same-volume file movement and verified again after activation.

If activation or its verification fails, restore attempts to put the previous active file back. The previous file is eligible for deletion only after the replacement passes its integrity, schema and SHA-256 checks. Successful compensation moves the previous file back to the active path and rethrows the original restore failure.

If removal of the failed replacement or movement of the previous file fails, remaining `.restore-previous-*` and `.restore-stage-*` files are retained. An `AggregateException` reports both the restore failure and the recovery failure, together with the active, previous, staged and backup paths. These are the operation's locations; a stage already moved into place will no longer exist at its original path. A snapshot can be older than the pre-restore database, so retaining the snapshot alone does not preserve the latest state.

Cleanup after a verified restore is separate from compensation. A cleanup failure reports that the restored database remains active and identifies the remaining recovery-file locations; it does not undo the verified replacement. If staged-file cleanup fails after successful compensation, both the original restore failure and the cleanup failure are reported.

Keep database users stopped after a recovery failure. Preserve the reported files and any sidecars before manual repair, resolve the underlying I/O or access failure, and verify integrity, schema and the intended data generation before reactivating a retained database. Do not delete recovery files merely because an older snapshot is available or infer successful recovery from the presence of the active path alone.

## Relationship to coordinated update

The software update policy now records:

```text
persistentStateMigration = COORDINATED_ONLY
```

This does not move persistence logic into the software updater. Instead, `Invoke-CoordinatedUpgrade.ps1` explicitly composes the Update Discovery & Rollback software replacement boundary with this maintenance primitive.

Production migration authority comes from the signed target bundle's:

```text
tools/state-upgrade-catalog.json
```

The coordinator performs pre-software snapshots for databases that require migration, activates the software, invokes the new ControlHost maintenance mode for migration and performs coordinated recovery if a later step fails.

Direct software-only rollback is blocked while coordinated recovery evidence exists.

The persistence policy itself continues to state:

```text
softwareUpdateStateOrchestration = EXPLICIT_ORCHESTRATION_REQUIRED
automaticBackgroundMigration = false
```

because state mutation must remain an explicit maintenance action, not a side effect of ordinary host startup.

## Qualification

`PersistentStateRecoveryIntegrationTests` covers:

- verified management database backup and restore,
- post-activation verification failure followed by successful compensation, preserving the exact newer database bytes,
- target-deletion and rollback-rename failures that retain the newer previous database and report both errors and recovery paths,
- failed activation plus failed compensation that retains both the previous database and the unactivated stage,
- cleanup failures that preserve the primary failure or leave a verified restored database active,
- registered transactional forward migration,
- post-migration verification failure with automatic snapshot restore,
- cancellation after a committed migration and while asynchronous verification is pending, restoring the prior schema and payload before reporting cancellation,
- independent recovery deadline and I/O failures, retaining both failures and the verified backup,
- unknown migration rejection before mutation,
- tampered backup rejection without modifying active state,
- mandatory exclusive-access acknowledgement.

Restore failure tests use real SQLite databases and file operations with a narrow internal failure-injection boundary for file movement and deletion. Post-activation verification failures are produced by corrupting only the disposable activated copy. They do not depend on filesystem-watcher timing or platform-specific file-lock races. After the pinned-SDK Release build, run the focused regression selection with:

```powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build -m:1 --filter "FullyQualifiedName~PersistentStateRecoveryIntegrationTests|FullyQualifiedName~StateMaintenanceCliIntegrationTests"
```

`StateMaintenanceCliIntegrationTests` additionally launches the built ControlHost process and qualifies the operational CLI bridge used by coordinated updates.

Packaged release qualification adds a signed, explicitly qualification-only catalog for a disposable `qualification-state.db`. It performs a real `1 -> 2` migration without changing the production management or production-journal schema versions. The packaged failure path then forces a post-migration failure and verifies coordinated software rollback plus state restoration through the same ControlHost maintenance boundary.

## Scope boundary / non-claims

This persistence foundation does not itself implement or claim:

- automatic migration during ordinary host startup,
- database downgrade migrations,
- distributed database backup,
- remote database engines,
- point-in-time recovery,
- long-term backup retention policy,
- encrypted backup storage,
- cloud backup replication,
- automatic Windows service/process stop/start,
- Production Package activation,
- formal CRA conformity.

`UNVERIFIED` remains distinct from `PASS` whenever runtime or operational evidence has not actually been obtained.
