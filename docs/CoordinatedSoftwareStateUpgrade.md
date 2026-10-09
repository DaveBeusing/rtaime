<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Coordinated Software & State Upgrade Orchestration

## Purpose

Coordinated Software State Upgrade composes the software update/rollback foundation with the SQLite backup/migration/recovery foundation into one fail-closed maintenance transaction.

The package deliberately does not turn updates into an unattended background service.

The operator must first stop all rtaime processes/services that use the software installation or persistent state and explicitly acknowledge quiescence.

## Authoritative upgrade sequence

Production upgrade flow:

```text
verified current installation
→ trusted published update discovery
→ verified target Release Candidate / offline bundle
→ signed state-upgrade catalog from target bundle
→ inspect persistent SQLite state
→ validate complete forward migration chains
→ pre-upgrade snapshots for every database that requires migration
→ atomic software activation
→ resolve target ControlHost maintenance executable
→ transactional state migrations
→ post-migration schema/integrity verification
→ coordinated maintenance receipt
```

The maintenance receipt records `runtimeReadiness = UNVERIFIED` and `processesRemainStopped = true`.

Starting hosts and proving runtime readiness are intentionally outside this package.

## Signed migration authority

Production migration SQL is not accepted from a free-standing operator file.

The production state catalog is:

```text
tools/state-upgrade-catalog.json
```

inside the already verified offline software bundle. Because it is a normal bundle payload, it is covered by the bundle manifest, hash set and release signing chain.

The current Coordinated Software State Upgrade catalog declares:

```text
management           target schema 1
production-journal   target schema 1
```

with no migration steps because Coordinated Software State Upgrade does not change a production persistence schema.

When a future release changes a schema, that release must carry the exact registered `N → N+1` migration chain in its signed catalog.

An external catalog override exists only under explicit `QualificationMode`; production mode rejects it.

## ControlHost state-maintenance mode

The existing `rtaime.ControlHost` executable gains a maintenance-only command mode before normal host startup:

```text
state-maintenance inspect
state-maintenance backup
state-maintenance migrate
state-maintenance restore
```

This avoids loading a .NET 10 product assembly into the PowerShell runtime and preserves the fixed managed-project topology.

Mutation commands continue to require:

```text
--acknowledge-exclusive-access
```

The maintenance mode does not start ControlHost supervision, IPC authority or child hosts.

## Backup-before-activation rule

For every discovered database whose current schema is below the signed target schema, the coordinator creates a verified Persistent State Backup, Migration & Recovery snapshot before software activation.

Database instances are discovered only below the explicitly supplied `StateRoot` and only by signed catalog filenames.

A schema newer than the target is rejected as an unsupported downgrade.

A missing, ambiguous or skipped migration step fails before software activation.

## Software activation

Software replacement continues to use the Update Discovery & Rollback `Invoke-AtomicSoftwareReplacement.ps1` path.

Coordinated Software State Upgrade does not duplicate extraction, bundle trust validation or software filesystem swap logic.

The existing rollback slot remains:

```text
<InstallPath>.rollback
```

## State migration

After the new software tree has been activated, the coordinator resolves the newly active `rtaime.ControlHost.dll` and invokes its state-maintenance mode for each required database migration.

Each database migration still uses the Persistent State Backup, Migration & Recovery transaction and internal migration backup. The pre-activation snapshot is retained separately as coordinated recovery evidence.

After migration, the coordinator inspects the database again and requires the exact signed target schema version.

## Automatic failure recovery

If failure occurs after software activation, all processes are still required to remain stopped.

Recovery order is fixed by policy:

```text
1. software rollback
2. verify restored software
3. restore pre-upgrade SQLite snapshots in reverse order
4. verify restored state schema/integrity
```

If any recovery step fails, the coordinator throws a recovery-incomplete failure and explicitly requires the processes to remain stopped.

A partial recovery is never reported as PASS.

## Coordinated recovery evidence

A state-changing successful upgrade retains evidence below:

```text
<InstallPath>.upgrade-recovery
```

including the pre-upgrade snapshots, migration receipts, failure/recovery evidence and `recovery-lifecycle.json`.

While coordinated recovery evidence exists, direct software-only rollback is blocked. The `Invoke-SoftwareRollback.ps1` bypass switch exists only for the coordinator after it has assumed responsibility for state recovery.

A successful maintenance transaction leaves the lifecycle at `MAINTENANCE_COMPLETE` with runtime readiness still `UNVERIFIED`. The service-managed update wrapper changes that lifecycle to `RUNTIME_QUALIFIED` only after the Windows service has restarted and both Start and Qualify report runtime readiness `PASS`.

Recovery evidence is not deleted implicitly. `Complete-CoordinatedUpgradeRecovery.ps1` requires explicit acknowledgement that the retained rollback slot is being retired, verifies the active and rollback installations, writes a closure receipt below the persistent-state maintenance root, deletes the rollback slot first and only then removes `<InstallPath>.upgrade-recovery`. A fully recovered failed upgrade can be closed only through a separate explicit acknowledgement path.

This prevents the old software tree from being activated independently against a schema that may already have advanced and prevents recovery evidence from disappearing before the maintenance transaction is conclusively closed.

## Compatibility entrypoint

`Invoke-VerifiedUpdate.ps1` remains available for existing automation, but it now requires `StateRoot` and delegates to `Invoke-CoordinatedUpgrade.ps1`.

There is no longer a separate production software-only verified-update path.

## Qualification

Qualification is layered:

1. normal solution build/tests compile the ControlHost maintenance boundary;
2. integration tests qualify verified backup/restore, transactional forward migration, tampered-backup rejection and the real ControlHost maintenance executable;
3. packaging checks require both the unchanged production catalog and the signed qualification-only catalog plus coordinator, rollback guard and recovery-retirement tooling;
4. Packaged E2E installs the generated bundle, creates disposable schema-v1 qualification state, proves missing and ambiguous migration chains fail before activation, executes a real `1 -> 2` migration, restarts the Windows service and requires runtime readiness `PASS`;
5. a second packaged run injects failure after the real migration, requires exact previous software manifest restoration, reverse-order verified state restore, retained original upgrade `FAIL`, recovery `PASS` and a stopped service.

The disposable migration is defined only in `state-upgrade-qualification-catalog.json`, which is itself packaged and signed but can be selected only through `QualificationMode`. The production `state-upgrade-catalog.json` remains at schema v1 for both known stores and carries no migration merely to manufacture evidence.

## Operational boundary

Coordinated Software State Upgrade does not automatically:

- stop ControlHost, RuntimeHost, AIHost or Operator;
- start hosts after maintenance;
- prove live IPC/runtime readiness after maintenance;
- schedule background updates;
- perform Production Package activation;
- perform schema downgrades;
- consume unsigned production migration SQL;
- delete retained coordinated recovery evidence implicitly or before explicit rollback retirement;
- claim Reference Platform validation or certification.

Runtime readiness therefore remains `UNVERIFIED` after a successful coordinated maintenance transaction until a later operational lifecycle step starts and qualifies the product processes.
