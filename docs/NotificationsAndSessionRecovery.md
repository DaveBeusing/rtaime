<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Notifications And Session Recovery

## Purpose

rtaime presents operational events through one centralized notification path and restores only safe Operator presentation state after an unexpected session end.

The notification layer is presentation-only. It does not become a production authority, does not advance a production revision and does not bypass Runtime readiness or existing mutation gates.

## Notification model

The shared notification model defines these categories:

- Information
- Success
- Warning
- Error
- Recovery

Notifications also carry a condition:

- Informational
- Resolved
- Active
- Fatal

Every operator-facing notification can identify what happened, the affected component, the expected operator action and the production impact. Technical details are retained separately and are revealed only on demand.

Transient notifications use the global toast host. Persistent Active or Fatal notifications remain visible in the Live Alerts & Notifications surface until their condition is replaced or dismissed.

Repeated identical notifications with the same key are deduplicated. The current item retains an occurrence count instead of creating an unbounded list of duplicate messages.

## Error translation

Operator and recording errors are translated into production-facing messages before presentation. Raw failure detail remains available as technical detail but is not used as the primary operator message.

Runtime recovery uses one keyed notification transition:

```text
Error -> Recovery -> Success
```

The same notification therefore changes condition instead of leaving stale recovery messages behind.

## Session marker

The Operator stores session evidence below the local rtaime application-data directory.

At session start the marker is written with:

```text
CleanShutdown = false
```

A normal completed shutdown changes the marker to:

```text
CleanShutdown = true
```

A fatal Dispatcher failure deliberately does not mark the session clean.

If the next start observes a prior dirty marker, rtaime reports that the previous session ended unexpectedly and restores the last safe workspace selection.

Corrupt or unsupported session evidence is ignored and replaced with a safe current-session marker.

## Safe state boundary

The recovery contract persists only:

```text
SelectedWorkspace
```

The existing Operator layout store remains responsible for presentation geometry and already normalizes invalid or corrupt layout data.

Session recovery does not persist or reactivate:

- Program or Preview routing
- output start state
- recording state
- graphics on-air state
- AI enable state
- transport play state
- Runtime resources
- any other production mutation

After recovery, outputs and other production actions remain governed by fresh authoritative Runtime state and explicit operator commands.

## UI behavior

Transient messages appear in the global toast host without blocking operation.

Persistent problems appear in Live Alerts & Notifications with:

- affected component
- condition
- operator-facing title and detail
- production impact
- operator action
- optional expandable technical detail
- deduplicated occurrence count

Existing Engine, Runtime and Media health rows remain authoritative health projections and are not replaced by notification state.

## Verification

Unit coverage validates:

- transient toast lifetime
- deduplicated repeated warnings
- persistent errors
- Error to Recovery to Success replacement
- unexpected previous process termination
- clean shutdown
- corrupt session state
- safe-state persistence containing only the workspace selection

The safety contract is intentionally narrow: restoring presentation must never imply restoring production execution.
