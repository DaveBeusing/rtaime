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
- governed Scene activation;
- timeline, IN/OUT, markers and Cue points;
- graphics and compositing, including Production CG text rendering;
- Audio Follow Video and deterministic generated audio diagnostics;
- failure-isolated Program Recording with a provider-neutral profile catalog and a qualified Windows MP4 H.264/AAC software-interoperability path;
- governed Program/Aux output roles plus monitoring, bounded SRT output, external-runtime NDI output, and governed NDI source discovery/input;
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
| Media | bounded A/B local-file decks (one or two decoder owners; physical SSD concurrency unverified), Media Pool, governed NDI source discovery/adoption and input, transport, timeline, IN/OUT, markers and Cue points |
| Switching | authoritative Preview/Program, CUT, DISSOLVE and governed Scene activation |
| Compositing | RuntimeHost-owned bitmap overlays, dynamic Production CG lower thirds, layers and compositing workspace |
| Audio | Audio Follow Video, explicit breakaway, bounded multi-source/bus mixing, evidence-aware mixer routing and draft gain controls, deterministic crossfade/ducking, Runtime metering and generated test signals |
| Recording | provider-neutral recording profile catalog, qualified Windows MP4 H.264/AAC software interoperability, and bounded encoded Program replay/clip production |
| Output | governed Program/Aux output roles, Clean Program monitoring, output health, bounded SRT output and external-runtime NDI High Bandwidth software output |
| AI | provider-evidence capability browser, descriptor tests, explicit reference-effect approval and bounded inference fallback |
| Health | CPU/GPU/memory telemetry, Runtime performance, evidence-aware active incidents, safe diagnostic copy/export and health center |
| Recovery | process supervision, Runtime resynchronization and safe session recovery |
| External control | optional ControlHost gRPC/TLS boundary with authentication, RBAC, bounded requests and resynchronization; local Named Pipe remains the default |
| Production integrations | optional IntegrationHost gateway for OSC, Windows MIDI, virtual GPIO/GPI reference control and authenticated Companion-style HTTP/WebSocket feedback |
| Delivery | release pipeline, offline packaging, update/rollback foundations and qualification evidence |

The source-controlled product version is currently **0.1.0-dev** in the **DEV** release stage.

Hardware and production-readiness claims remain limited to evidence explicitly recorded by the qualification system.

Production-support policy and Stable-readiness governance are source-controlled in [Production Support and Stable Readiness](docs/ProductionSupport.md), with the generated [Support and Compatibility Matrix](docs/SupportCompatibilityMatrix.md) and [Production Deployment Baseline](docs/ProductionDeploymentBaseline.md). The current DEV source remains Stable-readiness `UNVERIFIED`; the readiness verifier does not promote release stage.

The Media Library provides bounded local Smart Bins for existing duration, availability and readiness metadata, with cancelable reuse of monitoring thumbnails. Independent hover scrubbing is explicitly unsupported; filters and hover do not open or seek production media. See [Media Library & Asset Browser](docs/MediaLibraryAssetBrowser.md).

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

Normal local Interactive startup defaults to EphemeralLocal. On Windows, `rtaime.exe` is built as a GUI-subsystem application, so normal Interactive and Showcase launches do not create or flash an AppHost console before the branded Operator startup surface. When rtaime.exe starts the local engine, closing Operator shuts down that owned lifecycle so development binaries are not left locked by background hosts.

Use `--show-console` for an explicit diagnostic console. HeadlessEngine requests console semantics automatically, redirected stdout/stderr remain usable for automation, and early bootstrap failures are persisted to `apphost-startup.log` below the selected AppHost work root.

PersistentEngine is used by the supported Windows-service production path. ExternalManaged desktop startup adopts an existing engine without lifecycle authority.

See [Application Startup](docs/ApplicationStartup.md) and [Windows Production Lifecycle](docs/WindowsProductionLifecycle.md).

## Single-file AppHost publish

The AppHost can be published as a self-contained Windows single-file executable:

~~~powershell
dotnet publish src/Hosts/rtaime.AppHost/rtaime.AppHost.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false --output artifacts/publish/rtaime-win-x64
~~~

This does not collapse the V1 architecture into one process. ControlHost, RuntimeHost, AIHost and Operator remain separate executable artifacts internally.

Single-file publishing is a development/distribution option until the authoritative release evidence explicitly qualifies that artifact shape. Required Gates nevertheless exercise the self-contained AppHost publish and verify that its `rtaime.exe` retains the Windows GUI subsystem and bootstrap diagnostic behavior.

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
- [Secure External Control](docs/ExternalControlDeployment.md)
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


NDI discovery and input reuse the shared external-runtime NDI provider boundary. Discovery is observational until explicit ControlHost adoption, input execution remains Runtime-owned, and real peer/network interoperability remains UNVERIFIED. See [NDI Discovery and Input](docs/NdiDiscoveryAndInput.md).

Replay retains bounded encoded Program history and materializes normal-speed selections as ordinary Media Library clips that reuse Media Deck and normal Preview/Program routing. See [Replay and Clip Production](docs/ReplayClipProduction.md).


Recording profiles are Runtime-confirmed capabilities rather than UI codec presets. The current default remains `mp4-h264-aac`; the catalog also exposes the managed `mov-2vuy-pcm` QuickTime profile with uncompressed 8-bit 4:2:2 video and PCM16 audio. MXF, proprietary professional codecs and hardware-encoder claims still require separate concrete providers and qualification. See [Recording Profile Catalog](docs/RecordingProfileCatalog.md).

Portable production setup templates add reviewed scene/cue definitions through the canonical show project without executing them. See [Production Show Templates](docs/ProductionShowTemplates.md) for dependency binding, preview and compatibility limits.

### Operator docking

Optional workspace docking adds tab groups, resizable splits and detachable existing panels with safe monitor/DPI restoration. Built-in workspace layouts remain the default. See [Operator workspaces](docs/OperatorWorkspaces.md#optional-docking-and-detached-panels) for keyboard controls and recovery.

The [Context Inspector](docs/ContextInspector.md) provides precise numeric draft editing, compatible attribute copy/paste and explicit authoritative Apply.

[Timeline editing](docs/LayeredTimeline.md#draft-cursor-snapping-and-reversible-editing) adds explicit draft/confirmed positions, prioritized snapping and bounded local authoring undo without replaying production operations.

## Operator command palette

Ctrl+K or **Commands** opens searchable commands with visible availability reasons and protected production activation. Versioned shortcut profiles show the active gesture and focus area, isolate editing from command execution, and support strict validated import/export, collision checks and reset without modifying production data. See [Commands and shortcut profiles](docs/OperatorCommandsAndShortcuts.md).

## Show checks and activity

The navigation rail and Commands expose [Show checks and activity](docs/ShowPreflightAndActivity.md): read-only preflight evidence, a bounded correlated request history and consolidated notification acknowledgement/review. Missing or stale evidence stays UNKNOWN; ControlHost retains launch and Program authority.

The [Operator visual qualification](docs/OperatorVisualQualification.md#combined-workspace-regression-evidence)
retains combined workspace geometry, review images and bounded Windows UI measurements.
[QoL interaction qualification](docs/OperatorVisualQualification.md#qol-interaction-qualification)
adds preference-window focus transitions, context-menu guards and rendering subscription
balance through teardown. Injected DPI and pointer arithmetic do not establish physical
mixed-monitor or touch qualification.

Recent items, stable-ID favorites and contextual help are available through Commands or its context menu. History is opt-in and local; opening references only inspects existing items. See [Operator commands and shortcuts](docs/OperatorCommandsAndShortcuts.md#recent-items-favorites-and-inline-help) for privacy, limits, help search and unavailable-item recovery.

The Inspector supports grouped local draft undo/redo and compatible typed attribute transfer for graphics/processing, audio gain and cue names. APPLY/RENAME remain explicit; confirmed live operations are excluded. See [Inspector draft transactions](docs/OperatorCommandsAndShortcuts.md#inspector-draft-undo-and-typed-attribute-transfer).

The [Media Library](docs/MediaLibraryAssetBrowser.md) supports composable typed search and reviewed, bounded import/remove batches with per-item confirmation, partial failure and stop/retry handling through the existing Client path. Confirmed production operations remain outside local undo.

[Workspace layout templates](docs/OperatorWorkspaces.md#named-workspace-layout-templates) add named portable presentation presets, deterministic Focus/Cinema restore and retained detached views. They reuse the original Operator models and monitoring and do not change production state.

Show checks now separates Program/AUX availability and recording target evidence, shows submission provenance and explicit response limits, groups notifications by severity, and offers bounded reminder pauses plus the existing support exporter. See [Show checks and activity](docs/ShowPreflightAndActivity.md).

The Media Library adds freely named/renamed local Smart Bins with validated saved expressions and session hover controls that pause under current Runtime pressure. Hover and keyboard focus share existing thumbnails; independent position scrubbing remains unavailable. See [Media Library](docs/MediaLibraryAssetBrowser.md).

Local MP4 playback aligns native AAC packets with video sample windows to prevent
long-playback audio runahead and early exhaustion. The [local playback baseline](docs/ReferenceMediaRegression.md#long-local-file-playback-baseline)
retains actual-host timing/resource evidence; physical storage/GPU qualification remains separate.

Local MP4 playback now uses one bounded decode worker per Runtime session, qualified
preroll and generation-fenced PTS admission. Program cadence performs no local-file
decode/seek work. See [local media ownership and underrun policy](docs/LocalMediaFileSource.md#asynchronous-decode-ownership-and-preroll);
hosted software regression does not establish physical SSD/GPU qualification.

Local input uses independent bounded MP4/QuickTime probes before Windows decoding.
Restricted MOV/H.264 with AAC or PCM16 and per-host HEVC Main/Main10 outcomes are
covered by the [format qualification matrix](docs/LocalMediaFileSource.md#container-probes-and-format-qualification).
Decoder availability, real software decoding and physical qualification remain
separate; ProRes/DNx/MXF input and HDR conversion are unsupported.

Explicit [asset preparation](docs/MediaLibraryAssetBrowser.md#explicit-asset-preparation)
verifies selected files on a bounded non-Program worker. Availability, representative
conditional success and resident deck readiness remain distinct; preparation never
routes Program or changes saved production.

## Decoder capability and dependency boundary

See the [source-derived input compatibility inventory](docs/LocalMediaDecoderCapabilities.md)
for exact sample-entry/profile/audio checks and qualification gaps, and the
[proposed professional decoder ADR](docs/ProfessionalDecoderDecision.md)
for backend alternatives and approval requirements. ProRes, DNxHD/DNxHR, MXF input
and a production FFmpeg dependency remain outside approved scope. Existing test-only
FFmpeg generation and MXF recording evidence do not authorize input support.
