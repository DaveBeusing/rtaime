<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Build, Publish & Test

## Purpose

This document is the canonical developer entry point for building, publishing and testing **rtaime — Real Time AI Media Engine**.

**Production-grade real-time AI media platform.**

The repository's primary managed solution is `rtaime.slnx`.

Repository automation and qualification scripts are source-controlled under `build/`. The repository intentionally has no root `tools/` source directory. A `tools/` directory exists only inside generated offline release bundles, where the release builder copies a controlled allowlist of scripts and policy files from their canonical `build/...` source paths.

## Prerequisites

The repository pins the .NET SDK in `global.json`:

```text
.NET SDK 10.0.401
```

The V1 reference development platform is Windows x64. The WPF Operator targets `net10.0-windows`; platform-neutral libraries and non-UI hosts, including the optional `rtaime.IntegrationHost`, target `net10.0`.

Verify the SDK:

```powershell
dotnet --info
dotnet --list-sdks
```

## Restore

From the repository root:

```powershell
dotnet restore rtaime.slnx
```

## Standard builds

The canonical local developer build entry point is:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
```

Debug:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Debug
```

The developer build performs three bounded steps:

1. it stops stale **repository-local** `rtaime`, Operator, ControlHost, RuntimeHost, AIHost and optional IntegrationHost processes that would lock the current checkout's `bin/` outputs;
2. it restores the solution unless `-NoRestore` is supplied;
3. it builds the complete `rtaime.slnx` in the requested configuration.

Process cleanup is path-scoped to executables below the current repository root. Installed product/service processes outside the checkout are never selected by this cleanup path. The AppHost is stopped first so it cannot supervise or restart repository-local child hosts while the build output is being replaced.

For an already restored solution:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release -NoRestore
```

`-SkipProcessCleanup` is available only for development scenarios that deliberately build into outputs that are not used by a running repository-local instance.

A raw `dotnet build` remains valid when no repository development lifecycle is running:

```powershell
dotnet restore rtaime.slnx
dotnet build rtaime.slnx --configuration Release --no-restore
```

On Windows, a running .NET executable locks assemblies loaded from its output directory. Building the same configuration while AppHost, Operator or a supervised host is still running can therefore produce `MSB3026`, followed by `MSB3027`/`MSB3021` after the copy retry limit is exhausted. Use the canonical developer build entry point instead of manually killing arbitrary system processes.

The normal build is framework-dependent and preserves the multi-process V1 topology behind one product entry point:

```text
rtaime.exe / AppHost
├── ControlHost
│   ├── RuntimeHost
│   └── AIHost
└── Operator
```

After a complete solution build, the AppHost can also be started directly from its normal build output. In repository development mode it resolves the sibling ControlHost, RuntimeHost, AIHost and Operator outputs from the same build configuration; installed bundles continue to resolve only their packaged product payload.

Debug example:

```powershell
./src/Hosts/rtaime.AppHost/bin/Debug/net10.0/rtaime.exe
```

Release example:

```powershell
./src/Hosts/rtaime.AppHost/bin/Release/net10.0/rtaime.exe
```

Build the complete `rtaime.slnx` before using this direct development startup path.

On Windows, the canonical AppHost is a `WinExe`/WINDOWS_GUI executable. Normal Interactive and Showcase startup therefore opens no AppHost console window; the branded Operator startup experience is the first visible product surface. Use `--show-console` when an explicit diagnostic console is wanted. `HeadlessEngine` also requests console semantics unless both stdout and stderr are already redirected. Windows-service startup remains non-interactive.

For automation that needs deterministic terminal output, redirect stdout/stderr or invoke the process with `--show-console` and an explicit wait primitive. Bootstrap failures are also persisted to `apphost-startup.log` below the selected AppHost work root, so an early GUI-start failure does not depend on terminal visibility.

## Single-file Windows publish

### Architecture boundary

rtaime V1 is intentionally a multi-process system. The canonical `rtaime.exe` AppHost may be published as one self-contained executable, but ControlHost, RuntimeHost, AIHost and Operator remain separate process artifacts. Single-file publishing does not create a monolithic runtime.

A future all-in-one process would be a separate architecture decision and is not implied by the commands below.

### Canonical rtaime.exe AppHost

```powershell
dotnet publish src/Hosts/rtaime.AppHost/rtaime.AppHost.csproj `
	-c Release -r win-x64 --self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:DebugType=None -p:DebugSymbols=false `
	-o artifacts/publish/rtaime-win-x64
```

The AppHost resolves the installed product payload, starts or adopts ControlHost, waits for qualified readiness, and starts Operator only for profiles that require it.

Required Gates qualify the generated Windows bootstrap with:

```powershell
./build/operations/Test-WindowsApplicationBootstrap.ps1 -Configuration Release -QualifySingleFile
```

The qualification inspects the built and self-contained single-file `rtaime.exe` PE subsystem, verifies redirected startup diagnostics, exercises an explicit `--show-console` failure path, and verifies a HeadlessEngine failure path without changing lifecycle authority.

### Operator-only single-file EXE

Use this when the Control/Runtime/AI lifecycle is already available separately.

The Operator currently resolves Demo Production assets as files relative to `AppContext.BaseDirectory`. The command therefore uses `IncludeAllContentForSelfExtract=true` so those bundled assets are extracted before startup. Microsoft documents this property as a compatibility mode rather than the preferred general single-file model. Treat this as a developer/showcase publish option until the Operator asset-access path or release qualification is updated.

Reference: https://learn.microsoft.com/dotnet/core/deploying/single-file/overview

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

The resulting distribution is intended to be started as `rtaime.Operator.exe`. Content required by the Operator is included in the single-file bundle and may be extracted by the .NET single-file host at runtime.

### Full internal host set as self-contained single-file executables

Publish the four internal service/client executables when a fully self-contained distribution is required:

```powershell
dotnet publish src/Hosts/rtaime.ControlHost/rtaime.ControlHost.csproj `
	-c Release -r win-x64 --self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:DebugType=None -p:DebugSymbols=false `
	-o artifacts/publish/rtaime.ControlHost-win-x64

dotnet publish src/Hosts/rtaime.RuntimeHost/rtaime.RuntimeHost.csproj `
	-c Release -r win-x64 --self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:DebugType=None -p:DebugSymbols=false `
	-o artifacts/publish/rtaime.RuntimeHost-win-x64

dotnet publish src/Hosts/rtaime.AIHost/rtaime.AIHost.csproj `
	-c Release -r win-x64 --self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:DebugType=None -p:DebugSymbols=false `
	-o artifacts/publish/rtaime.AIHost-win-x64

dotnet publish src/Hosts/rtaime.Operator/rtaime.Operator.csproj `
	-c Release -r win-x64 --self-contained true `
	-p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true `
	-p:IncludeAllContentForSelfExtract=true `
	-p:DebugType=None -p:DebugSymbols=false `
	-o artifacts/publish/rtaime.Operator-win-x64
```

### Release qualification boundary

These commands are developer/distribution build options. The authoritative V1 release pipeline currently produces and qualifies the framework-dependent offline bundle documented in [ReleasePackagingAndOfflineDeployment.md](ReleasePackagingAndOfflineDeployment.md).

A self-contained/single-file artifact must not be represented as an officially qualified release artifact until the release pipeline, integrity inventory, offline preflight and release evidence explicitly cover that artifact shape.

## Test runs

Build Release first when using `--no-build`:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
```

### Complete solution test run

This matches the serialized managed test shape used by the authoritative release pipeline:

```powershell
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

### Fast correctness tests

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build
dotnet test tests/rtaime.Tests.Contracts/rtaime.Tests.Contracts.csproj --configuration Release --no-build
dotnet test tests/rtaime.Tests.Architecture/rtaime.Tests.Architecture.csproj --configuration Release --no-build
```

### Integration and behavior

```powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build -m:1
dotnet test tests/rtaime.Tests.Behavioral/rtaime.Tests.Behavioral.csproj --configuration Release --no-build -m:1
```

### Failure qualification

```powershell
dotnet test tests/rtaime.Tests.Failure/rtaime.Tests.Failure.csproj --configuration Release --no-build -m:1
```

### Performance suite

```powershell
dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj --configuration Release --no-build -m:1
```

A passing performance test project is managed evidence only. It does not replace physical reference-platform latency, GPU, media-I/O or long-soak qualification.

### List or filter tests

List tests in a project:

```powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --list-tests
```

Run a targeted test class/name:

```powershell
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj `
	--configuration Release `
	--no-build `
	--filter "FullyQualifiedName~ProductionIpcIntegrationTests"
```

## Repository quality gates

The version-controlled required gates are:

```text
CI
Quality
Security
Packaged E2E
Provider Smoke
```

Local `dotnet build` and `dotnet test` runs are necessary developer validation, but they do not replace packaged release qualification, security checks, provider smoke evidence or physical hardware qualification.

See:

- [RepositoryGovernance.md](RepositoryGovernance.md)
- [ReleasePipelineAndChannels.md](ReleasePipelineAndChannels.md)
- [ReleaseEvidence.md](ReleaseEvidence.md)
- [QualificationEvidenceProvenance.md](QualificationEvidenceProvenance.md)

## Product Showcase

The qualified Product Showcase remains the preferred full-stack demonstration path:

```text
Start-rtaime-Showcase.cmd
```

See [Product Showcase Scenario](InvestorDemoScenario.md) for the deterministic demonstration flow and acceptance boundary.


## Optional IntegrationHost

`rtaime.IntegrationHost` is built with the primary solution and packaged as an optional host, but AppHost does not start it and ControlHost readiness does not depend on it. Configure and start it separately with `--config=<path>` or `RTAIME_INTEGRATION_CONFIG`. See [ProductionIntegrationGateway.md](ProductionIntegrationGateway.md).

## Rational cadence and backpressure regressions

Target the timing arithmetic, unchanged probe evidence, bounded network wakeups and recording saturation with:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --filter "FullyQualifiedName~RationalFrameScheduleTests|FullyQualifiedName~TimingQualificationTests|FullyQualifiedName~NetworkOutputFoundationTests|FullyQualifiedName~RecordingFoundationTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --filter "FullyQualifiedName~RuntimeHealthPerformanceHudIntegrationTests|FullyQualifiedName~DeterministicRuntimeBoundaryTests|FullyQualifiedName~OperatorMonitoringPlaneTests|FullyQualifiedName~ProgramFrameMemoryOwnershipTests|FullyQualifiedName~RecordingRuntimeIntegrationTests"
dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj --configuration Release --filter "FullyQualifiedName~RuntimePerformanceStatusBarPerformanceTests"
```

Cadence tests simulate approximately 24 hours at 50/1 and 60000/1001, with every deadline checked against independent rational arithmetic. Early/repeated wakes, long overruns, recovery, explicit resync/format epochs, clock regression and overflow have deterministic coverage. The targeted allocation guard requires zero allocation over 100,000 warmed schedule/probe/drop observations and retains a broad 15-second software regression limit.

These targeted commands supplement the complete Release build, existing CUT/DISSOLVE, output health, audio alignment, recording, quality policies and five Required Gates. Passing software tests does not qualify physical GPU, external clocks, device latency or physical presentation.

## Viewport and startup capture regressions

The real Operator XAML qualification includes scroll measurement, hidden monitor views and normal/maximized windows with 150% physical target sizing. GPU controls keep layout in logical WPF units; only texture allocation converts to physical pixels. A render target does not supply its own viewport's desired size.

Startup capture qualification checks that Healthy is published after qualified evidence and the Production Readiness stage are available. Fixture teardown owns and joins every host run before copying retained evidence or removing temporary data; active capture leases are not diagnostic artifacts. Repeated recovery qualification also runs rapid preview/take actions and requires a successful ControlHost durability drain.

The unit suite exercises bounded large MXF KLV/local-set inputs and SRT cancellation/failed encoder initialization with resource-release checks. These checks supplement all five Required Gates and the packaged managed lifecycle qualification, including preserved legacy 1.0 checkpoint content. They do not qualify physical presentation or external hardware.


## Program pipeline measurements

Run `TimingQualificationTests`, `RuntimePerformanceStatusBarPerformanceTests`, `GpuCompositorPerformanceProfileTests`, `ProgramPipelineMeasurementBaselineTests` and `DeterministicRuntimeBoundaryTests` for bounded telemetry, diagnostic-contention behavior, warmed allocation overhead, both-format layer/load matrices and unchanged Program pixels/ownership. Required Gates runs the profile subset with detailed console output and preserves source-bound JSON measurements.

For retained local software reports, set `RTAIME_PIPELINE_BASELINE_EVIDENCE` to an output directory, `RTAIME_SOURCE_COMMIT` and `RTAIME_BUILD_COMMIT` to the same exact built SHA, and `RTAIME_BASELINE_SDK` to the verified `10.0.401` SDK. Without an output destination the tests only print measurements. See [Renderer qualification](RendererQualificationAndSoakTesting.md#program-pipeline-measurement-baseline) for scope, warmup, bounded-window semantics and the physical evidence boundary.

## Runtime consumer isolation regressions

After the canonical Release build, run:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter "FullyQualifiedName~ReplayCaptureIsolationTests|FullyQualifiedName~NetworkOutputFoundationTests|FullyQualifiedName~RecordingFoundationTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~RuntimeConsumerIsolationTests"
```

Required Gates runs these scopes before wider qualification, retains source-bound mixed-consumer timing/ownership reports and still executes the full solution and packaged lifecycle, including unchanged legacy 1.0 checkpoint qualification. The evidence environment is scoped to the targeted mixed-consumer step so isolated release fixtures do not inherit an unrelated source identity. Native transport/GPU/output and long-soak claims require reference hardware evidence.


## Lifecycle fault qualification

After the canonical Release build, targeted GPU lifecycle/resource tests and IPC fault/resource-baseline tests run before wider CI qualification:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter "FullyQualifiedName~GpuLifecycleRecoveryTests|FullyQualifiedName~GpuBackendResourceSemanticsTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~ControlTransportFaultQualificationTests|FullyQualifiedName~HostIpcSessionLifecycleIntegrationTests|FullyQualifiedName~OperatorMonitoringPlaneTests"
```

The existing binding/recovery step also runs actual child-process crash/restart, repeated owned-process shutdown, durable show recovery and old 1.0 checkpoint regressions. All five Required Gates remain mandatory on the exact PR head, including the authoritative packaged release/lifecycle path. The [lifecycle fault matrix](ProcessRecoveryAndSupervision.md#lifecycle-fault-qualification-matrix) distinguishes software counters and fault injection from unavailable physical GPU/driver/soak evidence.


## Sustained mixed-load qualification

After the canonical Release build, Required Gates run bounded histogram/resource regressions, evidence-verifier negative fixtures, the short staged mixed-load smoke and managed renderer cycle/pressure regressions. The short smoke reuses the existing integration and factored layer/consumer matrices; it emits `SHORT_SOFTWARE_PASS` and never qualifies long soak.

For an opt-in Windows x64 software run, use:

```powershell
./build/qualification/Invoke-SustainedMixedLoadQualification.ps1 -Profile 8h
```

The `24h` and `72h` profiles require their complete elapsed duration. Each invocation creates a unique evidence directory; manifests bind source/build, pinned SDK, workload hash, summary and TRX hashes. `-NoBuild` is for already-built outputs from the same source only.

Physical reference renderer profiles `soak8h`, `soak24h` and `soak72h` reuse the existing approved-machine runner, with explicit total duration over both formats. See [Renderer Qualification](RendererQualificationAndSoakTesting.md#sustained-profile-duration-and-bounded-statistics) and [Timing/Soak Qualification](TimingReferenceLatencySoakQualification.md#bounded-sustained-load-evidence) for scope, metrics, resource budgets and unavailable-evidence status.

## Evidence-bound readiness index

See [EvidenceBoundReleaseReadiness.md](EvidenceBoundReleaseReadiness.md) for exact-source Required Gates correlation, the retained candidate evidence inventory, individual long-profile scopes and the NO-GO operational handoff. The workflow collects this report after the five required jobs; missing physical, production-trust and approved support evidence remains UNVERIFIED. Run `build/release/Test-ReleaseReadinessRegression.ps1` for stale/pending/cancelled evidence and no-false-Stable regressions.

## Dock workspace qualification

Run `dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter FullyQualifiedName~OperatorDockLayoutTests` after the canonical Release build. The existing real-STA `OperatorXamlStartupTests` also exercises shared panel identity, tabs, owned windows, focus, compact layout and main-window teardown. Retain physical mixed-DPI hot-unplug qualification separately; hosted geometry tests do not substitute for it.

## Precision Inspector regressions

```powershell
dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter FullyQualifiedName~InspectorPrecisionTests
```

The existing OperatorXamlStartupTests qualification also exercises the live themed numeric field and invalid-input Apply gate. Physical mixed-DPI interaction remains UNVERIFIED until reference-platform execution.

## Timeline and cue ergonomics regressions

```powershell
dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter FullyQualifiedName~TimelineErgonomicsTests
```

The real OperatorXamlStartupTests qualification also checks the themed timeline's focused edit-cursor key, local undo and unchanged confirmed transport. Required source-bound visual, lifecycle, failure and packaged checks remain in place. Physical mixed-DPI behavior remains UNVERIFIED until reference-platform execution.

## Media Library Smart Bin and hover regressions

After the canonical pinned-SDK Release build:

```powershell
dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter FullyQualifiedName~MediaLibraryBrowsingTests
```

The required CI job runs these focused metadata, persistence-fault, large-catalogue and hover-cancellation checks before the full suite. The existing real-STA OperatorXamlStartupTests also scrolls 4096 Grid/List entries and checks custom-control keyboard focus and bounded recycling. See [MediaLibraryAssetBrowser.md](MediaLibraryAssetBrowser.md) for the explicit unsupported scrubbing gate. Physical mixed-DPI, screen-reader interaction and hardware render/IPC latency remain UNVERIFIED. CI, Quality, Security, Provider Smoke and Packaged E2E must be checked at the exact PR head; existing source-bound evidence requirements are unchanged.
