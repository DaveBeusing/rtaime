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
- managed ControlHost/RuntimeHost/AIHost lifecycle;
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

Repository automation scripts live under `build/`; the repository does not use a root `tools/` source directory. For the full build/publish matrix, see [Build, Publish & Test](docs/BuildAndTest.md).

## Single-file EXE

rtaime V1 intentionally uses separate executable hosts. Single-file publishing therefore creates a self-contained EXE **per host**, preserving the production topology.

Example: self-contained Windows x64 Operator:

```powershell
dotnet publish src/Hosts/rtaime.Operator/rtaime.Operator.csproj `
	--configuration Release `
	--runtime win-x64 `
	--self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:IncludeAllContentForSelfExtract=true `
	-p:DebugType=None `
	-p:DebugSymbols=false `
	--output artifacts/publish/rtaime.Operator-win-x64
```

The same single-file model is available for `rtaime.ControlHost`, `rtaime.RuntimeHost` and `rtaime.AIHost`. The Operator command additionally uses .NET's compatibility extraction mode because the current Demo Production assets are resolved from files below `AppContext.BaseDirectory`; see `docs/BuildAndTest.md` for the boundary and qualification note.

A single monolithic executable containing all four processes is **not** the current architecture.

The authoritative release bundle remains separately qualified; see [Release Packaging & Offline Deployment](docs/ReleasePackagingAndOfflineDeployment.md).

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
