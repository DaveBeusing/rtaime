<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Managed Host Lifecycle, Restart & Readiness Qualification

## Purpose

Managed Host Lifecycle Readiness closes the operational readiness boundary left intentionally open by coordinated software/state upgrade.

Coordinated Software State Upgrade may finish with verified software and persistent state while all production processes remain stopped. Managed Host Lifecycle Readiness provides the explicit transition from that maintenance state to a running and externally qualified service-host set.

The managed service topology remains:

```text
ControlHost
├── RuntimeHost
└── AIHost

Operator = interactive client, not a background managed service
```

ControlHost is the only top-level service process started by the lifecycle controller. Existing `ControlHostChildSupervision` remains authoritative for local RuntimeHost and AIHost launch, adoption, endpoint readiness and bounded restart attempts.

The product-level `rtaime.exe` AppHost sits above this boundary. It may start or adopt ControlHost and launch Operator after qualified readiness, but it does not directly supervise RuntimeHost or AIHost. The PowerShell lifecycle controller remains available for deployment, administration and qualification.

## Lifecycle controller

The offline bundle carries:

```text
tools/Invoke-ManagedHostLifecycle.ps1
tools/host-lifecycle-policy.json
```

Supported actions:

```text
Start
Status
Restart
Stop
```

Example:

```powershell
./tools/Invoke-ManagedHostLifecycle.ps1 `
    -Action Start `
    -InstallPath C:\rtaime `
    -StateRoot C:\ProgramData\rtaime
```

Status:

```powershell
./tools/Invoke-ManagedHostLifecycle.ps1 `
    -Action Status `
    -InstallPath C:\rtaime
```

Restart:

```powershell
./tools/Invoke-ManagedHostLifecycle.ps1 `
    -Action Restart `
    -InstallPath C:\rtaime `
    -StateRoot C:\ProgramData\rtaime
```

Stop:

```powershell
./tools/Invoke-ManagedHostLifecycle.ps1 `
    -Action Stop `
    -InstallPath C:\rtaime
```

## Start sequence

Before process launch the controller verifies the installed SOFTWARE_RELEASE bundle. Production operation requires the trusted production-key path; `QualificationMode` exists only for repository Packaged E2E using TEST_EPHEMERAL release evidence.

The controller resolves exactly one installed assembly for each managed service host and starts ControlHost with explicit environment bindings for:

- Control endpoint,
- Runtime endpoint,
- AI endpoint,
- persistence root,
- RuntimeHost executable,
- AIHost executable,
- managed readiness evidence,
- managed stop signal,
- child supervision timing and restart budget.

ControlHost then owns RuntimeHost and AIHost supervision.

## Readiness contract

`runtimeReadiness = PASS` is permitted only when all of the following are simultaneously true:

1. the top-level ControlHost process is still alive;
2. ControlHost reports lifecycle `Ready` and health `Healthy`;
3. RuntimeHost child supervision is `Healthy`;
4. AIHost child supervision is `Healthy`;
5. RuntimeHost and AIHost owned process identities are live;
6. the Control Named Pipe can be connected to externally;
7. the Runtime Named Pipe can be connected to externally;
8. the AI Named Pipe can be connected to externally.

ControlHost does not publish readiness evidence while it is `Degraded`. Because ControlHost becomes `Ready` only after RuntimeHost authority reconciliation, Control readiness is also evidence that the Control/Runtime relationship has passed its existing reconciliation rules.

The transient readiness document is removed again when ControlHost drops out of Ready/Healthy or begins shutdown. Process identity is recorded in the evidence so a stale file cannot qualify a replacement process.

## Child restart and readiness-recovery semantics

RuntimeHost and AIHost continue to use `LocalProcessSupervisor`.

A child can be adopted when its configured endpoint is already ready, or launched when unavailable. Launched children receive bounded restart attempts with the configured probe interval and restart backoff. The start-attempt counter is lifetime-scoped to the supervisor instance and is not reset when the failure mode changes from process exit to readiness loss.

Owned and adopted children have deliberately different recovery rights:

- an **owned** child must first publish its unique managed readiness file before it can become `Healthy`;
- after an owned child has been healthy, loss of that readiness while its process remains alive enters `ReadinessGrace`;
- the grace duration reuses the existing bounded supervision timing and is at least one probe interval, preventing a single transient file-system observation from forcing a restart;
- if readiness returns within the grace period, the same PID returns to `Healthy` and no additional start attempt is consumed;
- if readiness remains absent, supervision enters `Recovering`, requests the existing managed stop sentinel, waits the existing graceful-stop timeout, and uses process-tree termination only as the existing emergency fallback;
- after the old owned process is cleared, the normal restart backoff and the same remaining `MaxStartAttempts` budget govern replacement;
- the replacement is not healthy merely because a process exists: it must publish explicit readiness and RuntimeHost must complete the existing Control/Runtime reconciliation before ControlHost can publish readiness again.

For an **adopted/external** endpoint, the supervisor owns no process handle and therefore never writes a stop sentinel, never kills the external process and never restarts it. If the endpoint lifetime lease remains present while explicit readiness disappears, supervision becomes non-healthy and continues observation while managed launch remains suppressed. Readiness restoration can return the adopted endpoint to `Healthy` without changing ownership.

The supervisor still avoids continuous Named Pipe probe traffic after an owned child has reached readiness. Owned liveness is observed through the process handle while the managed readiness file remains the explicit health signal.

Managed Host Lifecycle Readiness does not add a second supervisor implementation.

## Graceful stop

Managed processes receive a unique file-sentinel path in:

```text
RTAIME_HOST_STOP_FILE
```

RuntimeHost and AIHost translate that sentinel into their existing cancellation/shutdown path.

ControlHost also observes a top-level sentinel. Its normal shutdown disposes child supervision, and the supervisor first requests graceful child cancellation before using process-tree termination as an emergency fallback.

The policy is explicit:

```text
fallbackKillAllowed = true
fallbackKillIsPass = false
```

Emergency cleanup may therefore prevent orphaned processes, but a forced shutdown is never reclassified as a successful graceful lifecycle operation.

## Restart semantics

A **top-level product restart** remains an explicit operator action:

```text
Stop must PASS
    ↓
old lifecycle state removed
    ↓
Start
    ↓
full readiness qualification repeated
```

There is no implicit unattended top-level ControlHost restart scheduler inside the non-service Managed Host Lifecycle Readiness controller.

This is separate from **owned child recovery** inside ControlHost. RuntimeHost and AIHost may be restarted automatically by their existing bounded supervisor after process exit or persistent post-healthy readiness loss. That automatic child recovery is ownership-limited, uses graceful stop before emergency termination, consumes the existing lifetime start-attempt budget, and cannot restart an adopted external endpoint.

Persistent Windows production operation is a separate outer layer: Windows Service Control Manager may restart the AppHost service process after top-level failure, while ControlHost continues to own bounded RuntimeHost/AIHost child recovery.

## Operational evidence

A successful start creates an operational receipt containing:

- installed product version,
- source commit,
- ControlHost PID,
- RuntimeHost PID,
- AIHost PID,
- all three IPC endpoints,
- `status = PASS`,
- `runtimeReadiness = PASS`,
- `operatorClientManaged = false`,
- `productionPackageActivation = NOT_PERFORMED`.

`Status` re-evaluates live process and Named Pipe state; it does not merely trust the earlier receipt.

`Stop` removes active lifecycle state after process termination and writes whether shutdown was graceful.

## Relationship to coordinated upgrade

Coordinated Software State Upgrade remains responsible for:

```text
software verification
→ state backup
→ software activation
→ state migration
→ migration verification
```

Its receipt correctly ends with `runtimeReadiness = UNVERIFIED` because no host was started there.

Managed Host Lifecycle Readiness is the separate next boundary:

```text
maintenance PASS
→ managed host Start
→ supervision qualification
→ external IPC qualification
→ runtimeReadiness PASS
```

The Coordinated Software State Upgrade receipt is historical evidence and is not rewritten in place.

## Packaged E2E

Required Gates install the generated qualification bundle into a clean temporary target and execute:

```text
Start
→ Status
→ Restart
→ Status
→ Stop
→ Status == STOPPED
```

The qualification uses unique Named Pipe endpoint names and the same lifecycle script shipped in the offline bundle.

## Scope boundary / non-claims

The non-service Managed Host Lifecycle Readiness controller itself does not implement or claim:

- Windows service registration,
- automatic boot-start registration,
- a watchdog external to ControlHost,
- unattended top-level ControlHost restart,
- clustering or multi-node failover,
- remote host orchestration,
- Operator desktop auto-launch from the administrative PowerShell lifecycle controller (product startup is provided separately by `rtaime.exe`),
- Production Package activation,
- formal hardware qualification,
- formal CRA conformity.

Those boundaries remain explicit for this controller rather than being inferred from `runtimeReadiness = PASS`. Persistent Windows service behavior is documented and qualified separately in [Windows Production Lifecycle](WindowsProductionLifecycle.md).
