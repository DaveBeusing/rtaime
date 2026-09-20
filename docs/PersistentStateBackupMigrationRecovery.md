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

## Recovery semantics

Restore requires explicit acknowledgement that processes using the database have stopped.

Before active replacement, WAL state is checkpointed. The verified backup is staged beside the active database, activated by same-volume file movement and verified again after activation.

If activation verification fails, the previous active file is restored.

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
- registered transactional forward migration,
- post-migration verification failure with automatic snapshot restore,
- unknown migration rejection before mutation,
- tampered backup rejection without modifying active state,
- mandatory exclusive-access acknowledgement.

`StateMaintenanceCliIntegrationTests` additionally launches the built ControlHost process and qualifies the operational CLI bridge used by coordinated updates.

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
