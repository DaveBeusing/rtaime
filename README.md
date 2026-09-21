<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="center">
	<img src="src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="560" />
</p>

<p align="center">
	<strong>Production-grade real-time AI media platform.</strong>
</p>

<p align="center">
	Real-time media execution, deterministic production control and governed AI in one production-shaped platform.
</p>

# rtaime

**rtaime** is pronounced **“realtime”** and expands to **Real Time AI Media Engine**.

rtaime is built around a simple production principle:

> **AI can participate in Program without owning Program.**

The platform combines video, audio, graphics, recording, outputs, monitoring and governed inference with explicit production authority, transactional Runtime execution, visible health and bounded recovery.

This repository is the controlled development source for rtaime V1.

## Why rtaime

### Trust the Program

Production state has one authority. ControlHost owns authoritative state; RuntimeHost executes prepared production state through an explicit prepare/commit boundary.

Operator UI state does not become production truth merely because it changed visually.

### Operate the whole media path

The V1 path brings together:

- media library and local media playback;
- Preview and Program;
- CUT and DISSOLVE;
- timeline, IN/OUT, markers and Cue points;
- graphics and compositing;
- Audio Follow Video and deterministic generated audio diagnostics;
- Program Recording;
- Program Output and monitoring;
- system health and performance visibility.

### AI without surrendering control

AIHost is governed and optional to production continuity.

Inference results can influence the media path through explicit policy and fallback behavior, but AI does not own Program state or bypass the Control → Runtime authority boundary.

### Operational trust is a feature

rtaime exposes lifecycle and health as part of the operator experience:

- unified application startup;
- real readiness states;
- Control/Runtime/AI supervision;
- CPU, GPU and memory telemetry;
- recovery and resynchronization;
- persistent operational notifications;
- safe Operator session recovery;
- evidence-aware release qualification.

## Current product scope

The current V1 development repository implements production-shaped software paths for:

| Area | Current capability |
| --- | --- |
| Application lifecycle | unified rtaime.exe AppHost, managed ControlHost/RuntimeHost/AIHost lifecycle |
| Operator | full-screen production shell, workspaces, progressive readiness and recovery states |
| Media | local media files, Media Pool, transport, timeline, IN/OUT, markers and Cue points |
| Switching | authoritative Preview/Program, CUT and DISSOLVE |
| Compositing | graphics overlays, layers and compositing workspace |
| Audio | Audio Follow Video, gain/mute/metering seams, deterministic generated test signals |
| Recording | failure-isolated Program Recording |
| Output | Program Output, Clean Program monitoring and output health |
| AI | governed inference showcase with bounded fallback |
| Health | CPU/GPU/memory telemetry, Runtime performance, alerts and health center |
| Recovery | process supervision, Runtime resynchronization and safe session recovery |
| Delivery | release pipeline, offline packaging, update/rollback foundations and qualification evidence |

The source-controlled product version is currently **0.1.0-dev** in the **DEV** release stage.

Hardware and production-readiness claims remain limited to evidence explicitly recorded by the qualification system.

## Architecture

The canonical product entry point is:

~~~text
rtaime.exe / AppHost
├── starts or adopts ControlHost
│   ├── supervises RuntimeHost
│   └── supervises AIHost
└── starts Operator after qualified readiness
~~~

On Windows, the canonical `rtaime.exe` uses the GUI subsystem for normal Interactive and Showcase startup, so no AppHost console window is created or briefly flashed before the branded Operator startup experience. Operational console behavior remains explicit through `--show-console`, `HeadlessEngine`, redirected stdout/stderr and the Windows-service path. Early bootstrap failures are persisted as `apphost-startup.log` under the selected AppHost work root.

The production authority path is intentionally separate from execution:

~~~text
Operator
	↓
Client
	↓
ControlHost
	↓
authoritative production state
	↓
prepare / commit
	↓
RuntimeHost
	↓
media + GPU + audio + output
~~~

The governed AI path remains independently bounded:

~~~text
Program frame
	↓
AIHost / governed inference
	↓
result-use + freshness + fallback policy
	↓
media/compositing path
~~~

AIHost does not become Production Authority.

See [V1 End-to-End Proof](docs/V1EndToEndProof.md) and [Bootstrap Architecture](docs/BootstrapArchitecture.md).

## Product Showcase

The installed qualification bundle exposes the one-click showcase entry point:

~~~text
Start-rtaime-Showcase.cmd
~~~

The Product Showcase exercises the real managed lifecycle and Operator path rather than a parallel demo-only authority model.

A typical story demonstrates:

1. application startup and real lifecycle readiness;
2. media loading and Media Pool operation;
3. Preview preparation;
4. TAKE to Program;
5. DISSOLVE;
6. graphics/compositing;
7. Audio Follow Video;
8. governed AI effect and fallback;
9. Program Recording;
10. Program Output;
11. Runtime health and performance.

The deterministic showcase sequence and acceptance boundaries are documented in [Product Showcase Scenario](docs/InvestorDemoScenario.md).

## Evidence over claims

rtaime deliberately distinguishes:

**implemented** → software exists in the product path

**qualified** → a declared automated or reference-platform qualification has passed

**unverified** → the capability or claim has not yet collected the required evidence

Repository Required Gates are:

- CI
- Quality
- Security
- Provider Smoke
- Packaged E2E

Passing repository gates does not automatically qualify physical GPU, Media I/O, long-soak or reference-platform behavior.

See [Release Evidence](docs/ReleaseEvidence.md), [Qualification Evidence Provenance](docs/QualificationEvidenceProvenance.md) and [Reference Platform Qualification](docs/qualification/ReferencePlatformQualification.md).

## Requirements

The repository pins:

~~~text
.NET SDK 10.0.401
~~~

The primary managed solution is:

~~~text
rtaime.slnx
~~~

The V1 reference development platform is Windows x64. The WPF Operator targets net10.0-windows.

## Build

Canonical local developer build:

~~~powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
~~~

Debug build:

~~~powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Debug
~~~

The developer build stops only repository-local rtaime processes before replacing build outputs, preventing stale AppHost/Operator/host instances from locking bin assemblies. Installed product or Windows-service processes outside the current checkout are not selected.

A raw dotnet build remains valid when no repository development lifecycle is running.

After a complete Debug build:

~~~powershell
./src/Hosts/rtaime.AppHost/bin/Debug/net10.0/rtaime.exe
~~~

Repository automation scripts live under build/. The repository does not use a root tools/ source directory. Generated offline release bundles may contain a controlled tools/ payload.

See [Build, Publish & Test](docs/BuildAndTest.md).

## Application startup

Supported AppHost profiles are:

- Interactive
- Showcase
- HeadlessEngine

Lifecycle ownership is explicit:

- EphemeralLocal
- PersistentEngine
- ExternalManaged

Normal local Interactive startup defaults to EphemeralLocal. When rtaime.exe starts the local engine, closing Operator shuts down that owned lifecycle so development binaries are not left locked by background hosts.

PersistentEngine is used by the supported Windows-service production path. ExternalManaged desktop startup adopts an existing engine without lifecycle authority.

See [Application Startup](docs/ApplicationStartup.md) and [Windows Production Lifecycle](docs/WindowsProductionLifecycle.md).

## Single-file AppHost publish

The AppHost can be published as a self-contained Windows single-file executable:

~~~powershell
dotnet publish src/Hosts/rtaime.AppHost/rtaime.AppHost.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false --output artifacts/publish/rtaime-win-x64
~~~

This does not collapse the V1 architecture into one process. ControlHost, RuntimeHost, AIHost and Operator remain separate executable artifacts internally.

Single-file publishing is a development/distribution option until the authoritative release evidence explicitly qualifies that artifact shape.

## Tests

Build Release first:

~~~powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
~~~

Run the complete managed solution test suite:

~~~powershell
dotnet test rtaime.slnx --configuration Release --no-build -m:1
~~~

Fast architectural/core validation:

~~~powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj --configuration Release --no-build
dotnet test tests/rtaime.Tests.Architecture/rtaime.Tests.Architecture.csproj --configuration Release --no-build
~~~

Integration:

~~~powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build -m:1
~~~

Behavioral, failure and performance suites are documented in [Build, Publish & Test](docs/BuildAndTest.md).

## Product and marketing

The product message is intentionally tied to architectural proof.

Start with:

- [Marketing Strategy](docs/MarketingStrategy.md) — positioning, audiences, go-to-market sequence, messaging and proof rules.
- [Product Identity](docs/ProductIdentity.md) — name, pronunciation, slogan, positioning and voice.
- [Brand Assets](docs/BrandAssets.md) — canonical logo, emblem, app icon, palette and usage.
- [Product Showcase Scenario](docs/InvestorDemoScenario.md) — deterministic product demonstration.
- [Operator UI](docs/OperatorUiV1.md) — production workspace and interaction model.

## Engineering documentation

Core architecture and operation:

- [Bootstrap Architecture](docs/BootstrapArchitecture.md)
- [V1 End-to-End Proof](docs/V1EndToEndProof.md)
- [Application Startup](docs/ApplicationStartup.md)
- [Executable Host Lifecycle](docs/ExecutableHostLifecycle.md)
- [Process Recovery & Supervision](docs/ProcessRecoveryAndSupervision.md)
- [Production IPC / Remote API](docs/ProductionIpcRemoteApi.md)
- [Audio Test Signal Generator](docs/AudioTestSignalGenerator.md)
- [Runtime Readiness](docs/RuntimeReadiness.md)
- [Operator Monitoring Plane](docs/OperatorMonitoringPlane.md)
- [Observability & Diagnostics](docs/ObservabilityDiagnostics.md)

Release and qualification:

- [Build, Publish & Test](docs/BuildAndTest.md)
- [Repository Governance](docs/Governance/RepositoryGovernance.md)
- [Release Pipeline & Channels](docs/ReleasePipelineAndChannels.md)
- [Release Packaging & Offline Deployment](docs/ReleasePackagingAndOfflineDeployment.md)
- [Release Evidence](docs/ReleaseEvidence.md)
- [Qualification Evidence Provenance](docs/QualificationEvidenceProvenance.md)
- [Reference Platform Qualification](docs/qualification/ReferencePlatformQualification.md)

Security:

- [Security Policy](SECURITY.md)
- [Product Security Assessment](docs/ProductSecurityAssessment.md)
- [Product Security Compliance](docs/ProductSecurityCompliance.md)

## Canonical identity

~~~text
Name:        rtaime
Pronounced:  realtime
Expansion:   Real Time AI Media Engine
Slogan:      Production-grade real-time AI media platform.
~~~

The canonical visual assets live in:

~~~text
src/Hosts/rtaime.Operator/Assets/Brand/
~~~

Use the source-controlled brand assets rather than recreating the mark for documentation, presentations or product surfaces.
