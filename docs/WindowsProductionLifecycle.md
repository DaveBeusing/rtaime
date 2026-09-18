<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Windows Production Lifecycle

## Purpose

rtaime supports a persistent Windows production-engine lifecycle that is independent from the Operator desktop client.

The production topology remains:

```text
Windows Service Control Manager
	↓
rtaime.exe --windows-service
	↓
ControlHost
	├── RuntimeHost
	└── AIHost

Operator
	↕
ControlHost / Client SDK
```

The Windows service path does not introduce a second RuntimeHost or AIHost supervisor. ControlHost remains the owner of child-host supervision and recovery.

## Lifecycle ownership

The AppHost exposes three explicit ownership policies:

```text
EphemeralLocal
PersistentEngine
ExternalManaged
```

### EphemeralLocal

The AppHost may start a local engine and owns shutdown when the interactive session ends.

This mode is appropriate for disposable development, evaluation and showcase sessions.

### PersistentEngine

The engine lifetime is independent from the Operator lifetime.

Closing or crashing Operator does not stop the engine. In Windows service mode, Service Control Manager stop is the explicit lifecycle-authority stop and requests graceful ControlHost shutdown.

### ExternalManaged

The AppHost is adopt-only.

It waits for an already managed engine to reach qualified readiness and never starts or terminates that lifecycle. This is the correct desktop-client mode when a persistent Windows service owns the engine.

Ownership is never inferred from parent/child process relationships.

## Desktop startup

Default interactive startup remains:

```powershell
./rtaime.exe --profile=Interactive --ownership=PersistentEngine
```

Disposable local startup:

```powershell
./rtaime.exe --profile=Interactive --ownership=EphemeralLocal
```

Connect to an externally managed engine without lifecycle authority:

```powershell
./rtaime.exe --profile=Interactive --ownership=ExternalManaged
```

The Operator can be closed and reopened while a persistent or externally managed engine continues to run.

## Windows service installation

The release bundle carries:

```text
tools/Invoke-WindowsServiceLifecycle.ps1
```

Run service-management commands from an elevated PowerShell session.

Install automatic startup:

```powershell
./tools/Invoke-WindowsServiceLifecycle.ps1 `
	-Action Install `
	-InstallPath C:\rtaime `
	-StateRoot C:\ProgramData\rtaime
```

Start and qualify:

```powershell
./tools/Invoke-WindowsServiceLifecycle.ps1 `
	-Action Start `
	-InstallPath C:\rtaime `
	-StateRoot C:\ProgramData\rtaime

./tools/Invoke-WindowsServiceLifecycle.ps1 `
	-Action Qualify `
	-InstallPath C:\rtaime `
	-StateRoot C:\ProgramData\rtaime
```

Status:

```powershell
./tools/Invoke-WindowsServiceLifecycle.ps1 `
	-Action Status `
	-InstallPath C:\rtaime `
	-StateRoot C:\ProgramData\rtaime
```

Graceful stop:

```powershell
./tools/Invoke-WindowsServiceLifecycle.ps1 `
	-Action Stop `
	-InstallPath C:\rtaime `
	-StateRoot C:\ProgramData\rtaime
```

Uninstall:

```powershell
./tools/Invoke-WindowsServiceLifecycle.ps1 `
	-Action Uninstall `
	-InstallPath C:\rtaime `
	-StateRoot C:\ProgramData\rtaime
```

The default service name is `rtaime-engine`. The registration tool passes that exact SCM service identity to `rtaime.exe --service-name=...`, so the .NET service host and Service Control Manager use the same internal service name.

Installation configures the service for automatic startup by default and configures bounded Service Control Manager recovery actions. The service process runs the canonical `rtaime.exe` in `HeadlessEngine` / `PersistentEngine` mode.

The current registration uses the Windows `LocalSystem` account. This is a privileged deployment identity rather than a least-privilege certification. Target-machine qualification must validate filesystem ACLs, provider/device access and operational access policy; see the repository security policy.

## State, work and diagnostics

Default persistent state root:

```text
C:\ProgramData\rtaime
```

Default service work root:

```text
C:\ProgramData\rtaime\service\default
```

The work root contains the ControlHost readiness and graceful-stop coordination files used by the lifecycle controller.

Windows service hosting uses the standard .NET Windows service integration. Service-host diagnostics are therefore available through the Windows Application event log in addition to rtaime subsystem diagnostics.

The service host has no WPF dependency and does not require an interactive desktop session.

## Readiness

Service `Running` is not sufficient evidence.

`Start` and `Qualify` require:

1. service state `Running`;
2. ControlHost readiness state `READY`;
3. ControlHost health `HEALTHY`;
4. RuntimeHost supervision `HEALTHY`;
5. AIHost supervision `HEALTHY`;
6. live ControlHost, RuntimeHost and AIHost process identities;
7. reachable Control, Runtime and AI named-pipe endpoints;
8. endpoint identity matching the configured instance.

A failed readiness check remains a failure. It is not rewritten to PASS because the Windows service process itself is running.

## Shutdown semantics

A Service Control Manager stop is an explicit engine stop.

The service host cancels the persistent headless lifecycle, which routes shutdown through the existing ControlHost stop-sentinel path. RuntimeHost and AIHost shutdown remain under ControlHost supervision.

The service-management tool does not force-kill a service and does not classify a timeout as graceful success. AppHost writes `apphost-shutdown.json` for an owned shutdown. A normal service stop requires `status = PASS`, `graceful = true` and `forcedTermination = false`.

If ControlHost does not exit within the bounded AppHost shutdown window, emergency process-tree cleanup may still be used to prevent orphaned owned processes, but the evidence is written as `FAIL` with `forcedTermination = true`. The service stop therefore cannot be reported as a graceful PASS.

## Update and maintenance

Production service updates use:

```text
tools/Invoke-ServiceManagedUpdate.ps1
```

The sequence is explicit:

```text
acknowledge Operator/provider quiescence
	↓
stop persistent engine service
	↓
verified coordinated software/state update
	↓
start persistent engine service
	↓
qualified readiness
	↓
Operator may reconnect
```

Example:

```powershell
./tools/Invoke-ServiceManagedUpdate.ps1 `
	-InstallPath C:\rtaime `
	-StateRoot C:\ProgramData\rtaime `
	-Channel PREVIEW `
	-AcknowledgeExternalProcessesStopped
```

The wrapper does not bypass the existing release-trust, state-migration or rollback controls. It delegates software/state change to `Invoke-VerifiedUpdate.ps1`.

If update or post-update readiness fails, the operation remains failed. The wrapper does not automatically restart an uncertain engine state.

## Boot and crash recovery

Automatic service startup provides the supported boot path.

ControlHost continues to supervise RuntimeHost and AIHost. If the persistent AppHost service process fails while its ControlHost remains healthy, the restarted service may explicitly reclaim that lifecycle only from its deterministic service readiness root. This is an ownership-policy decision, not parent/child inference.

If ControlHost is also unavailable, Service Control Manager recovery starts a new persistent lifecycle. In both cases the restarted service must establish qualified readiness; machine reboot is not claimed as frame-identical Program continuity.

## Reference-platform qualification

Repository CI can verify the managed ownership logic, service-host composition, packaging and policy contracts. It does not prove real Windows boot/reboot behavior.

The following therefore remain `UNVERIFIED` until executed on a physical or designated reference Windows platform:

- service installation/removal under the production account policy;
- automatic start after Windows reboot;
- Service Control Manager recovery after forced top-level failure;
- GPU/provider behavior from the non-interactive service session;
- production output behavior across OS reboot.

Reference qualification procedure:

```text
1. Install the signed release to the reference machine.
2. Install rtaime-engine with automatic startup.
3. Start and Qualify; archive status/readiness evidence.
4. Open Operator using ExternalManaged ownership.
5. Close and reopen Operator; verify engine PID continuity and full snapshot resynchronization.
6. Force the top-level service process to fail; verify configured Service Control Manager recovery and new qualified readiness.
7. Reboot Windows; verify automatic service start and new qualified readiness.
8. Execute a service-managed update; verify post-update readiness before reconnecting Operator.
9. Record observed results and keep any unproven item UNVERIFIED.
```

## Non-goals

This lifecycle does not implement Windows clustering, active/active control, multi-node authority election, remote fleet management, automatic failover to another machine or frame-identical continuity across an operating-system restart.
