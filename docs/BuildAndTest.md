<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

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

The V1 reference development platform is Windows x64. The WPF Operator targets `net10.0-windows`; platform-neutral libraries and non-UI hosts target `net10.0`.

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

Debug:

```powershell
dotnet build rtaime.slnx --configuration Debug
```

Release:

```powershell
dotnet restore rtaime.slnx
dotnet build rtaime.slnx --configuration Release --no-restore
```

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
dotnet restore rtaime.slnx
dotnet build rtaime.slnx --configuration Release --no-restore
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

## Funding showcase

The qualified packaged showcase remains the preferred full-stack demonstration path:

```text
Start-rtaime-Showcase.cmd
```

See [InvestorDemoScenario.md](InvestorDemoScenario.md) for the deterministic demonstration flow and acceptance boundary.
