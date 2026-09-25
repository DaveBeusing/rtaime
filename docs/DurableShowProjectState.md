<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Durable Show Project State

## Decision

ControlHost owns one canonical durable authored show/project model per production.

The persisted management document uses format `rtaime.show-project.v1` and is stored through the existing `SqliteManagementStore`. No second persistence database, production authority, scheduler or Runtime state owner is introduced.

The durable show project owns:

- stable project identity;
- ordered Scene definitions with stable Scene IDs;
- Production CG definition;
- confirmed authored compositing configuration;
- retained bitmap graphics reference and checksum;
- Show Control workspace content and its logical storage version.

Confirmed live production state remains separately owned by the established ControlHost/RuntimeHost authority flow.

## Why one project owner

Scene definitions, Show Control authoring and graphics configuration previously had different durability characteristics:

- Scene definitions were derived from process bootstrap configuration;
- Show Control cue lists and recovery cursor were durable;
- graphics recovery state survived RuntimeHost restart only while the same ControlHost process remained alive.

Keeping those states independent would make a production show only partially recoverable after a full application restart. The show project provides a single authored-state boundary while leaving Runtime confirmation and production checkpoints unchanged.

## Persistence model

The project document is a management document and therefore inherits:

- SQLite transactions;
- optimistic expected-version writes;
- SHA-256 payload checksums;
- WAL and FULL synchronous durability;
- management-store integrity verification.

Show Control keeps a logical subdocument version inside the project. This preserves existing editor conflict semantics even when another part of the project changes and advances the outer management-document version.

Bitmap RGBA payloads are not embedded in project JSON. A bounded bitmap is retained in a sidecar under the production durability root. The project records its stable identity, dimensions, filename and SHA-256 checksum.

The sidecar write order is deliberate:

1. write the new payload to a temporary file;
2. atomically move it to its stable retained filename;
3. atomically replace the project document reference;
4. delete the superseded sidecar only after the new project document is durable.

A failed project update therefore preserves the previously valid durable project. An unreferenced new sidecar is safe to remove and cannot become production truth by itself.

## Startup and recovery ordering

ControlHost startup uses the following ordering:

```text
verify management persistence
-> load/create durable show project
-> reconstruct ProductionSpecification with persisted Scenes
-> load and validate authoritative checkpoint
-> compose Control authority
-> connect/reconcile RuntimeHost
-> re-admit retained bitmap/CG resources when required
-> prepare/reapply authoritative compositing state
-> expose Runtime-confirmed state to clients
```

This ordering is important. A checkpoint containing an `ActiveSceneId` is validated against the persisted Scene catalog that belongs to the show, not against a newly derived process-local Scene set.

When authoritative compositing state is recovered against a fresh RuntimeHost, retained graphics resources are restored before the authoritative execution is prepared and committed. This preserves normal Runtime admission rules and avoids bypassing the existing commit boundary.

## Authored state versus live state

Durable project state is authored recovery input, not proof of live production state.

A Scene stored in the project is not automatically active. A persisted CG definition is not automatically presented as live. A retained bitmap reference is not automatically proof that Runtime has admitted it.

Only the normal ControlHost/RuntimeHost validation and commit paths establish confirmed production truth. Operator/client snapshots continue to project Runtime-confirmed graphics and Control-confirmed authority.

The client additionally receives durable project lifecycle evidence:

- `LOADED` — the project document was loaded;
- `SAVED` — a confirmed authored mutation was durably synchronized;
- `RESTORED` — retained project graphics were reapplied through Runtime recovery;
- `STALE` — live authored state changed but the durable project update failed;
- `RECOVERY_REQUIRED` — a retained resource could not be safely restored;
- `UNAVAILABLE` — durable project state is not configured.

Unsupported or malformed project formats fail ControlHost startup rather than silently creating replacement state.

## Show Control migration

Existing `show-control.workspace` documents are migration input when no show project exists yet.

The first show-project creation copies the legacy Show Control JSON and logical storage version into the new project. Stable cue-list, cue, action and execution identities remain unchanged. Subsequent Show Control writes target the project-owned subdocument.

Unsafe Show Control replay semantics are unchanged. Persisted `Executing` or `Waiting` state still restores as `RecoveryRequired`; project durability does not authorize automatic replay.

## Scene semantics

Scene definitions are persisted with their stable Scene IDs, name, routing and optional versioned compositing declaration.

Rename and reorder operations preserve identity. The current implementation does not infer active Scene identity from Scene similarity. Direct confirmed routing or compositing mutations continue to clear active-Scene exactness according to the governed Scene rules.

## Failure behavior

The implementation fails closed for:

- unsupported show-project format;
- mismatched production identity;
- duplicate or invalid Scene identity;
- Scene references to unavailable production sources;
- invalid compositing schema;
- invalid retained bitmap metadata;
- missing bitmap sidecar during required recovery;
- bitmap byte-length mismatch;
- bitmap SHA-256 mismatch.

A live graphics command can already have crossed the Runtime confirmation boundary before a later durability write fails. In that case ControlHost does not pretend the Runtime mutation was rejected or roll it back unsafely. The durable project is marked `STALE`, the failure is recorded as a persistence observation, and the prior durable document remains intact.

## Non-goals

This decision does not add:

- automatic show execution after load;
- replay of uncertain in-flight Show Control actions;
- a generic scheduler;
- scripting;
- distributed show control;
- external automation protocols;
- NLE project semantics;
- bulk media embedding in the project document;
- Operator-local production truth;
- a second Runtime or production authority.
