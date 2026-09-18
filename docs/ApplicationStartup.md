<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Application Startup

## Purpose

rtaime exposes one canonical product entry point while preserving the V1 multi-process architecture:

```text
rtaime.exe / AppHost
│
├── starts or adopts ControlHost
│   ├── supervises RuntimeHost
│   └── supervises AIHost
│
└── starts Operator after qualified engine readiness
```

The AppHost is lifecycle orchestration only. It is not a production authority and does not own media execution, inference semantics or Runtime state.

## Canonical entry point

Installed release bundles expose:

```text
rtaime.exe
```

The AppHost runtime files are also retained in the internal product payload. ControlHost, RuntimeHost, AIHost and Operator remain separate executable artifacts.

A monolithic all-in-one process is not introduced.

## Startup profiles

### Interactive

```powershell
./rtaime.exe --profile=Interactive
```

Interactive is the default profile.

The AppHost starts or adopts the engine lifecycle, waits for qualified readiness and then opens Operator. Closing Operator does not stop an engine lifecycle merely because AppHost originally started it.

For an explicitly disposable local session:

```powershell
./rtaime.exe --profile=Interactive --disposable
```

Only a lifecycle owned by that AppHost instance may then be stopped.

### Showcase

```powershell
./rtaime.exe --profile=Showcase
```

Showcase starts or adopts the same production-shaped engine topology and opens Operator only after qualified readiness. When the showcase ends, AppHost stops the lifecycle only when that lifecycle was started by the current AppHost.

The existing packaged showcase launcher remains available for deterministic qualification and compatibility.

### HeadlessEngine

```powershell
./rtaime.exe --profile=HeadlessEngine
```

HeadlessEngine starts or adopts the service topology without launching Operator. The AppHost remains attached to lifecycle observation until it is explicitly stopped.

## Readiness contract

Operator launch is gated on positive evidence. AppHost requires:

1. the ControlHost process identity in readiness evidence to be live;
2. ControlHost lifecycle state `READY`;
3. ControlHost health `HEALTHY`;
4. RuntimeHost supervision state `HEALTHY`; when the supervisor owns a RuntimeHost process identity, that process must be live;
5. AIHost supervision state `HEALTHY` when AI is required; when the supervisor owns an AIHost process identity, that process must be live;
6. Control Named Pipe connectivity;
7. Runtime Named Pipe connectivity;
8. AI Named Pipe connectivity when AI is required;
9. endpoint identities matching the selected application instance.

ControlHost only reaches `Ready/Healthy` after its existing Runtime authority initialization/reconciliation path succeeds. AppHost therefore treats ControlHost readiness as reconciliation evidence rather than implementing a second reconciliation path.

A running process alone is never treated as ready.

## Adoption

AppHost first checks its stable application readiness location. It can also consume the readiness path published by the existing managed lifecycle state file.

A lifecycle is adopted only when the same readiness rules used for a newly started lifecycle pass. An adopted ControlHost is never re-parented and AppHost does not claim ownership of it.

## Ownership and shutdown

AppHost records ownership only for a ControlHost process it starts itself.

It may request graceful shutdown only for that owned process. Shutdown uses the existing `RTAIME_HOST_STOP_FILE` sentinel. A timeout may trigger process-tree cleanup for the owned ControlHost lifecycle.

AppHost never stops an adopted lifecycle.

RuntimeHost and AIHost shutdown/recovery remain consequences of ControlHost-owned supervision; AppHost never stops those child processes directly.

## Failure and recovery observation

RuntimeHost or AIHost degradation causes ControlHost readiness evidence to disappear while child supervision performs its bounded recovery behavior. AppHost exposes this as:

```text
Healthy
→ Degraded
→ Recovering
→ Healthy
```

If readiness does not recover within the configured recovery window, the application lifecycle becomes `Failed`.

If ControlHost exits while the application is active, AppHost reports `Failed`. V1 does not introduce an unattended top-level ControlHost restart loop.

The application lifecycle states are observational and never become a second production authority:

```text
Stopped
Starting
Healthy
Degraded
Recovering
Failed
Stopping
```

## Shared lifecycle policy

AppHost consumes the same `host-lifecycle-policy.json` timing and endpoint policy used by the managed lifecycle tooling.

The PowerShell lifecycle controller remains the deployment/administration path for explicit `Start`, `Status`, `Restart` and `Stop` operations. Product startup and administrative lifecycle tooling share the same service ownership model:

```text
ControlHost owns RuntimeHost/AIHost supervision.
```

## Configuration

Supported application arguments include:

```text
--profile=Interactive|Showcase|HeadlessEngine
--install-root=<path>
--state-root=<path>
--work-root=<path>
--instance-id=<id>
--disposable
--no-ai
```

The V1 default requires AI readiness. `--no-ai` is an explicit reduced startup configuration and does not change the default qualified release topology.

## Scope boundary

Application startup does not implement:

- Windows service registration;
- boot-time auto-start;
- distributed orchestration;
- multi-node failover;
- a second RuntimeHost/AIHost supervisor;
- new Control/Runtime authority paths;
- media or AI business-logic changes.



## Operator startup presentation

The product entry point still owns process-level startup and qualifies ControlHost, RuntimeHost and the required AIHost endpoint before it launches the interactive Operator. The Operator then performs an ordinary Client SDK full-snapshot synchronization before exposing its production workspace as interactive.

During that synchronization the Operator shows a lightweight startup surface driven only by real connection, health and lifecycle evidence. There are no fake progress timers and no optimistic completion. The surface reports Control, Runtime, AI and Operator state and remains present until the first authoritative snapshot proves the UI is ready.

Direct launches of `rtaime.Operator` use the same behavior: the bounded management refresh keeps attempting the normal full-snapshot path until ControlHost is available. Once initial readiness has completed, the startup surface is latched off; later outages are presented as in-place recovery rather than hiding the production workspace.
