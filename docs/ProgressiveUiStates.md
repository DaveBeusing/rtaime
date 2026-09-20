<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Progressive UI States

## Purpose

The Operator presents explicit, stable states while the application lifecycle continues to initialize, recover or operate with optional components unavailable. Presentation state never becomes a second readiness authority.

## Startup dependency classification

The classification follows the current executable lifecycle:

| Class | Components | Behavior |
|---|---|---|
| Critical | Operator bootstrap, configuration | The interactive Operator experience cannot be established without these. |
| Required For Production | ControlHost, RuntimeHost, Media, Provider/GPU provider | The shell may remain visible, but Production Readiness stays Not Ready while blocking evidence is present. |
| Optional | AIHost when the selected profile does not require it, non-authoritative presentation/diagnostic enrichment | Failure does not block the normal interactive shell or core production path. |

When a startup profile explicitly requires AIHost, it is promoted to Required For Production for that lifecycle.

Interactive AppHost startup already launches the Operator startup experience before complete Control/Runtime qualification. The Operator therefore remains the progressive presentation surface while the existing Runtime Readiness service continues to own production qualification.

## Shared presentation states

The shared presentation model defines:

- Loading
- Ready
- Empty
- Offline
- Unavailable
- Error
- Recovering

RuntimeReadinessSnapshot remains authoritative. The presentation factory maps that snapshot plus local content availability into a UI state without changing readiness.

## Workspace behavior

Media Library shows an Empty state when no filtered media is available.

Preview and Program show explicit empty states when no monitoring frame is available.

Inspector shows an Empty state until a media item, timeline item, cue or compositing node is selected.

Timeline shows an Empty state until local media transport is loaded.

Output Routing shows Unavailable when no governed output rows are available.

All placeholders use the shared rtaime state control and preserve their host region dimensions so state changes do not intentionally resize the surrounding workspace.

## Recovery and production safety

Recovering remains distinct from Offline and Error. A Required For Production failure never becomes Ready through presentation logic. Optional failures may leave the shell usable while authoritative production readiness continues to report the actual qualified state.

## Verification

Unit coverage validates:

- Loading to Ready
- Loading to Error
- Offline to Recovering to Ready
- Empty versus Loading
- Required For Production failure without fake readiness
- dependency classification, including profile-required AIHost
