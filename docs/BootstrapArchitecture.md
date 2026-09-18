<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Initial Solution Bootstrap and Architecture Guardrails

## Scope

This document records the mechanical repository baseline for the initial rtaime V1 solution bootstrap. It does not replace the binding architecture context, accepted ADRs, V1 product definition, technology baseline, subsystem architecture, project/dependency map, or project reference map.

Change classification: `ARCHITECTURE`.

## Toolchain qualification

The bootstrap pins .NET SDK `10.0.401` in `global.json`.

Selection evidence:

- .NET 10 is the approved V1 target family.
- Microsoft publishes SDK `10.0.401` as the current .NET 10 LTS SDK selected for this bootstrap.
- `rtaime.slnx` is the primary solution format supported by the selected SDK family.

Execution qualification status at this commit:

```text
Windows restore/build/test: UNVERIFIED
```

Managed restore/build/test validation is executed by the current Required Gates workflow. The historical bootstrap-only workflow has been retired in favor of the repository-wide required-gates and single release-pipeline model. No execution result is recorded as PASS until an actual workflow or equivalent Windows validation run has completed successfully.

Target frameworks are centralized:

```text
RtaimeTargetFramework        = net10.0
RtaimeWindowsTargetFramework = net10.0-windows
```

Platform-neutral libraries and non-UI hosts use `net10.0`. `rtaime.Operator` uses `net10.0-windows` with WPF enabled.

## Solution

Primary entry point:

```text
rtaime.slnx
```

The solution contains exactly 28 managed projects arranged into navigation-only solution folders:

```text
Core
Contracts
Control
Runtime
Media
AI
Durability
Providers
Client
Hosts
Tests
```

No `.vcxproj` is part of this bootstrap. `native/Providers/` is reserved only as a future integration location when a measured or vendor-driven native requirement exists.

## Dependency policy

The approved dependency direction is:

```text
stable semantics inward
implementation outward
composition at Hosts
```

The direct production `ProjectReference` graph is encoded in the project files and validated by `rtaime.Tests.Architecture`.

Key rules include:

- `rtaime.Core` has no rtaime project dependencies.
- Contracts depend only on approved inward semantics.
- Control does not reference Runtime/Media/AI implementations, Persistence, Operator, or concrete providers.
- Runtime does not reference Control, Persistence, Operator, or concrete providers.
- Operator reaches production only through `rtaime.Client`.
- Production projects cannot reference tests.
- Hosts cannot reference other hosts.
- Core/contracts remain package-neutral in the bootstrap.
- the initial production project set has no package references.
- vendor/runtime package boundaries are checked for SQLite, inference runtimes, CUDA/NVIDIA families.
- known vendor/runtime source tokens are rejected in Media/AI contracts.
- Core, contracts, and Runtime cannot opt into WPF/WindowsDesktop in the bootstrap.

## Build-time enforcement

`Directory.Build.targets` supplies fast MSBuild guardrails for high-value invalid references, package neutrality, host boundaries, and Operator platform consistency.

These checks complement, rather than replace, the architecture test suite.

## Architecture tests

`rtaime.Tests.Architecture` validates:

- exact 28-project managed project set;
- exact solution membership;
- absence of `.vcxproj`;
- approved production project-reference graph;
- production-to-test prohibition;
- Core/contract boundaries;
- Control and Runtime implementation boundaries;
- Operator-through-Client boundary;
- host-to-host prohibition;
- Core/contract package neutrality;
- initial production package neutrality;
- vendor package boundaries;
- direct assembly references in Core/contracts;
- known vendor type/token leakage into Media/AI contracts;
- WPF/WindowsDesktop neutrality for Core, contracts, and Runtime.

Negative fixtures prove that at least these invalid edges are rejected:

```text
Production -> Test
Contract   -> Provider implementation
Operator   -> Runtime implementation
Host       -> Host
```

Unknown managed projects under `src/` or `tests/` fail architecture validation closed until explicitly classified.

## Package management

Central Package Management is enabled by `Directory.Packages.props`.

Only test-runner packages are present in the bootstrap:

```text
Microsoft.NET.Test.Sdk
xunit
xunit.runner.visualstudio
```

No production vendor package is introduced by this work package.

## Local verification

The canonical developer build, single-file publish and test commands are maintained in [BuildAndTest.md](BuildAndTest.md).

The baseline managed verification remains:

```powershell
dotnet --info
dotnet restore rtaime.slnx
dotnet build rtaime.slnx --configuration Release --no-restore
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

A successful managed build/test run is not evidence for physical real-time behavior, professional media I/O, GPU qualification, physical inference performance, hardware support or production qualification.
