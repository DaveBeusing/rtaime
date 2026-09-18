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

The same AppHost lifecycle implementation is used for local interactive startup and the persistent Windows-service engine path. RuntimeHost and AIHost supervision remains owned by ControlHost.

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

The AppHost starts or adopts the engine lifecycle, waits for qualified readiness and then opens Operator.

### Showcase

```powershell
./rtaime.exe --profile=Showcase
```

Showcase uses `EphemeralLocal` lifecycle ownership by default. When the showcase ends, AppHost stops the lifecycle only when it was started by that AppHost instance.

The packaged showcase launcher remains available for deterministic qualification and compatibility.

### HeadlessEngine

```powershell
./rtaime.exe --profile=HeadlessEngine
```

HeadlessEngine starts or adopts the service topology without launching Operator and remains attached to lifecycle observation until explicitly stopped.

The supported Windows-service path runs this profile with `PersistentEngine` ownership.

## Explicit lifecycle ownership

Lifecycle ownership is never inferred from process parent/child relationships.

### EphemeralLocal

```powershell
./rtaime.exe --profile=Interactive --ownership=EphemeralLocal
```

The AppHost may start the engine for the current local session and stops only the ControlHost lifecycle that it owns when the session ends.

`--disposable` remains a convenience switch for this mode and requires `EphemeralLocal`.

### PersistentEngine

```powershell
./rtaime.exe --profile=Interactive --ownership=PersistentEngine
```

This is the default ownership for normal Interactive startup.

Closing Operator does not stop an owned persistent engine. Engine lifetime is independent from Operator lifetime.

For Windows service hosting:

```text
--windows-service
--profile=HeadlessEngine
--ownership=PersistentEngine
```

Service Control Manager stop is an explicit persistent-engine stop and is routed through the existing graceful ControlHost lifecycle.

### ExternalManaged

```powershell
./rtaime.exe --profile=Interactive --ownership=ExternalManaged
```

ExternalManaged is adopt-only. AppHost waits for an externally managed engine to reach qualified readiness and never starts or terminates that engine.

By default it uses the same deterministic service work root as the Windows persistent engine:

```text
%ProgramData%\rtaime\service\<instance>
```

This allows an Operator session to connect to an already-running service without requiring a custom work-root argument.

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

ControlHost only reaches Ready/Healthy after its existing Runtime authority initialization/reconciliation path succeeds. AppHost therefore treats ControlHost readiness as reconciliation evidence rather than implementing a second reconciliation path.

A running process or Windows service state alone is never treated as engine readiness.

## Adoption and ControlHost replacement

A lifecycle is adopted only when the same readiness rules used for a newly started lifecycle pass.

An adopted ControlHost is never re-parented and AppHost does not claim ownership of it.

While Operator is running, AppHost continues to evaluate qualified readiness rather than pinning UI lifetime to the original ControlHost process identity. If ControlHost is replaced, new readiness evidence is adopted and the Operator/Client SDK performs its existing full authoritative snapshot resynchronization.

Stale intent is not replayed automatically.

## Ownership and shutdown

AppHost records process ownership only for a ControlHost it starts itself.

`EphemeralLocal` may stop that owned lifecycle when the interactive session ends.

`PersistentEngine` interactive Operator exit does not stop the engine. In Windows service mode, Service Control Manager stop cancels the headless lifecycle and requests graceful owned ControlHost shutdown.

`ExternalManaged` never stops the adopted engine.

RuntimeHost and AIHost shutdown/recovery remain consequences of ControlHost-owned supervision; AppHost never stops those child processes directly during normal lifecycle operation.

## Failure and recovery observation

RuntimeHost or AIHost degradation causes ControlHost readiness evidence to disappear while child supervision performs its bounded recovery behavior. AppHost exposes this as:

```text
Healthy
→ Degraded
→ Recovering
→ Healthy
```

If readiness does not recover within the configured recovery window, the application lifecycle becomes `Failed`.

For an interactive client, ControlHost replacement can recover through new qualified readiness and full snapshot resynchronization.

For the persistent Windows-service engine, top-level ControlHost loss is a service failure. The service process exits unsuccessfully and Windows Service Control Manager recovery policy may restart the persistent engine lifecycle. The restarted lifecycle must establish fresh qualified readiness.

The application lifecycle states remain observational and never become a second production authority:

```text
Stopped
Starting
Healthy
Degraded
Recovering
Failed
Stopping
```

## Administrative lifecycle paths

The existing `Invoke-ManagedHostLifecycle.ps1` remains the explicit local/deployment lifecycle controller for non-service managed operation.

Persistent Windows production operation uses:

```text
Invoke-WindowsServiceLifecycle.ps1
```

Both paths preserve the same authority model:

```text
ControlHost owns RuntimeHost/AIHost supervision.
```

The Windows-service path reuses `UnifiedApplicationHost`; it does not implement a third host lifecycle.

## Configuration

Supported application arguments include:

```text
--profile=Interactive|Showcase|HeadlessEngine
--ownership=EphemeralLocal|PersistentEngine|ExternalManaged
--install-root=<path>
--state-root=<path>
--work-root=<path>
--instance-id=<id>
--windows-service
--disposable
--no-ai
```

The default requires AI readiness. `--no-ai` is an explicit reduced startup configuration and does not change the default qualified release topology.

`--windows-service` requires `HeadlessEngine` and `PersistentEngine`.

## Windows production lifecycle

Service installation, automatic boot start, Service Control Manager recovery, deterministic state/work roots, service-managed update/rollback and qualification boundaries are documented in [Windows Production Lifecycle](WindowsProductionLifecycle.md).

Real Windows reboot behavior is not inferred from managed CI. Reference-platform boot/recovery results remain `UNVERIFIED` until executed and captured on the designated environment.

## Scope boundary

Application startup does not implement:

- distributed orchestration;
- multi-node failover;
- a second RuntimeHost/AIHost supervisor;
- new Control/Runtime authority paths;
- remote fleet management;
- automatic failover to another machine;
- media or AI business-logic changes.

## Operator startup presentation

The product entry point qualifies ControlHost, RuntimeHost and the required AIHost endpoint before it launches the interactive Operator. The Operator then performs an ordinary Client SDK full-snapshot synchronization before exposing its production workspace as interactive.

During that synchronization the Operator shows a lightweight startup surface driven only by real connection, health and lifecycle evidence. There are no fake progress timers and no optimistic completion. The surface reports Control, Runtime, AI and Operator state and remains present until the first authoritative snapshot proves the UI is ready.

Direct launches of `rtaime.Operator` use the same behavior: the bounded management refresh keeps attempting the normal full-snapshot path until ControlHost is available. Once initial readiness has completed, the startup surface is latched off; later outages are presented as in-place recovery rather than hiding the production workspace.
