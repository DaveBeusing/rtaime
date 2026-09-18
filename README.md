<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# rtaime

> **Production-grade real-time AI media platform.**

**rtaime** is pronounced **“realtime”** and expands to **Real Time AI Media Engine**.

rtaime is a production-shaped real-time media platform that combines deterministic control and runtime execution with video, audio, graphics, recording, GPU processing and governed inference.

This repository is the controlled development source for rtaime V1.

## Current V1 scope

The repository contains the V1 production-shaped architecture and funding-showcase path, including:

- transactional Control → Runtime execution;
- Preview/Program operator workflow with CUT and DISSOLVE;
- local media file source, transport, timeline, IN/OUT and Cue points;
- Media Deck operator workflow;
- graphics and overlay composition;
- Audio Follow Video and operator metering/control;
- clean Program Output;
- Program Recording;
- governed AI showcase integration;
- Runtime/Provider health and monitoring;
- unified `rtaime.exe` startup with managed ControlHost/RuntimeHost/AIHost lifecycle;
- release, offline packaging and qualification evidence.

Hardware and production-readiness claims remain limited to evidence explicitly recorded by the qualification system.

## Requirements

The repository pins:

```text
.NET SDK 10.0.401
```

The primary managed solution is:

```text
rtaime.slnx
```

The V1 reference development platform is Windows x64. The WPF Operator targets `net10.0-windows`.

## Build

Restore and build Release:

```powershell
dotnet restore rtaime.slnx
dotnet build rtaime.slnx --configuration Release --no-restore
```

Debug build:

```powershell
dotnet build rtaime.slnx --configuration Debug
```

Run the Debug AppHost directly after the complete solution build:

```powershell
./src/Hosts/rtaime.AppHost/bin/Debug/net10.0/rtaime.exe
```

Repository development startup resolves the sibling host outputs from the same build configuration. Installed/offline bundles continue to use their packaged product payload.

Repository automation scripts live under `build/`; the repository does not use a root `tools/` source directory. For the full build/publish matrix, see [Build, Publish & Test](docs/BuildAndTest.md).

## Application startup and single-file EXE

The canonical product entry point is:

```text
rtaime.exe
```

`rtaime.exe` is the thin AppHost. It starts or adopts the qualified ControlHost lifecycle, waits for engine readiness and then opens Operator for the `Interactive` profile. It does **not** merge the service hosts into one process:

```text
rtaime.exe / AppHost
├── starts or adopts ControlHost
│   ├── supervises RuntimeHost
│   └── supervises AIHost
└── starts Operator after qualified readiness
```

Supported startup profiles are `Interactive`, `Showcase` and `HeadlessEngine`.

Lifecycle ownership is explicit:

```text
EphemeralLocal
PersistentEngine
ExternalManaged
```

The supported persistent Windows production path runs the same AppHost lifecycle under Windows Service Control Manager. Closing or crashing Operator does not stop a persistent engine; `ExternalManaged` desktop startup adopts the running engine without lifecycle authority.

Service installation and operation from an installed bundle:

```powershell
./tools/Invoke-WindowsServiceLifecycle.ps1 -Action Install -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime
./tools/Invoke-WindowsServiceLifecycle.ps1 -Action Start -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime
./tools/Invoke-WindowsServiceLifecycle.ps1 -Action Qualify -InstallPath C:\rtaime -StateRoot C:\ProgramData\rtaime
```

The AppHost itself can be published as a self-contained single-file Windows executable:

```powershell
dotnet publish src/Hosts/rtaime.AppHost/rtaime.AppHost.csproj `
	--configuration Release `
	--runtime win-x64 `
	--self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:DebugType=None `
	-p:DebugSymbols=false `
	--output artifacts/publish/rtaime-win-x64
```

ControlHost, RuntimeHost, AIHost and Operator remain separate executable artifacts internally. A single monolithic process is not the V1 architecture. See [Application Startup](docs/ApplicationStartup.md) and [Build, Publish & Test](docs/BuildAndTest.md).

## Tests

Build Release first, then run the complete managed test suite:

```powershell
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

Fast architectural/core validation:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj --configuration Release --no-build
dotnet test tests/rtaime.Tests.Architecture/rtaime.Tests.Architecture.csproj --configuration Release --no-build
```

Integration:

```powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build -m:1
```

Behavioral, failure and performance suites are documented in [Build, Publish & Test](docs/BuildAndTest.md).

## Funding showcase

A verified installed Windows release exposes the one-click showcase entry point:

```text
Start-rtaime-Showcase.cmd
```

The launcher starts the existing managed ControlHost lifecycle, opens the Operator against the qualified endpoints, and stops only the lifecycle it started when the Operator closes.

The deterministic demonstration flow and acceptance boundaries are documented in [Investor Demo Scenario](docs/InvestorDemoScenario.md).

## Documentation

Start here:

- [Product Identity](docs/ProductIdentity.md) — naming, pronunciation, acronym and slogan.
- [Build, Publish & Test](docs/BuildAndTest.md) — developer build matrix, single-file publishing and test runs.
- [Application Startup](docs/ApplicationStartup.md) — canonical `rtaime.exe` startup, profiles, readiness and ownership semantics.
- [Windows Production Lifecycle](docs/WindowsProductionLifecycle.md) — persistent service operation, boot/recovery, maintenance and qualification boundaries.
- [Bootstrap Architecture](docs/BootstrapArchitecture.md) — solution structure and architecture guardrails.
- [Repository Governance](docs/RepositoryGovernance.md) — required gates and integration policy.
- [V1 End-to-End Proof](docs/V1EndToEndProof.md) — production-shaped end-to-end execution proof.
- [Release Evidence](docs/ReleaseEvidence.md) — evidence model and qualification boundaries.
- [Release Pipeline & Channels](docs/ReleasePipelineAndChannels.md) — QUALIFICATION/PREVIEW/STABLE pipeline.
- [Release Packaging & Offline Deployment](docs/ReleasePackagingAndOfflineDeployment.md) — verified Windows deployment bundle.
- [Investor Demo Scenario](docs/InvestorDemoScenario.md) — funding-showcase flow and acceptance.
- [Security Policy](SECURITY.md) — vulnerability reporting and security boundary.

## Product identity

Canonical usage:

```text
Name:        rtaime
Pronounced:  realtime
Expansion:   Real Time AI Media Engine
Slogan:      Production-grade real-time AI media platform.
```

See [Product Identity](docs/ProductIdentity.md) for the complete naming rules.
