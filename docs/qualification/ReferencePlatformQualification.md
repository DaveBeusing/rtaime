<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Reference Platform Qualification

**Work package:** Reference Platform Qualification  
**Profile:** `rtaime-v1-reference-platform`

## Purpose

Reference Platform Qualification turns the existing V1 software and physical qualification mechanisms into one explicit reference-platform result. It does not add product features, create a new Production Authority or replace the existing physical qualification workflows.

The qualification answers two separate questions:

1. Does the V1 production workload execute correctly against the declared software reference-platform profile?
2. Does exact source-bound physical evidence exist for every mandatory reference-platform hardware requirement?

The distinction is intentional. GitHub-hosted CI can prove the first question, but cannot manufacture the second.

> `UNVERIFIED` is not `PASS`.

A complete reference-platform result is `PASS` only when all mandatory software scenarios and all mandatory physical requirements are either `PASS` or explicitly `NOT_APPLICABLE`. The current V1 physical requirements are mandatory, so absent hardware evidence produces an overall `UNVERIFIED` result rather than a false pass.

## Architecture

Qualification remains an outer tooling concern:

```text
Core
	→ Contracts
	→ Implementations
	→ Hosts / Clients
	→ Qualification tooling
```

Production subsystems do not reference Reference Platform Qualification code. The runner consumes existing test surfaces and the existing source-bound physical evidence bindings.

The authoritative machine-readable profile is:

```text
qualification/reference-platform/reference-platform.json
```

The profile declares:

- Windows reference-platform family;
- x64 architecture;
- pinned .NET SDK `10.0.401`;
- V1 development formats `1080p50` and `1080p59.94`;
- ControlHost, RuntimeHost, AIHost and Operator as required host surfaces;
- software qualification scenarios Q01-Q10;
- the five mandatory physical qualification requirements and their existing evidence-binding types.

The profile deliberately describes qualified platform classes and capabilities instead of machine serial numbers.

## Qualification status model

Reference Platform Qualification uses exactly these statuses:

| Status | Meaning |
| --- | --- |
| `PASS` | The declared requirement has valid evidence. |
| `FAIL` | The requirement was exercised or validated and failed. |
| `NOT_APPLICABLE` | The profile explicitly permits the requirement to be inapplicable. |
| `UNVERIFIED` | Required evidence is absent or cannot be trusted for the exact source candidate. |

Aggregation is fail-closed:

1. any mandatory `FAIL` makes the aggregate `FAIL`;
2. otherwise any mandatory `UNVERIFIED` makes the aggregate `UNVERIFIED`;
3. otherwise mandatory requirements must be `PASS` or `NOT_APPLICABLE` for aggregate `PASS`.

The verifier independently recomputes software, hardware and overall status, so editing the result JSON cannot turn missing evidence into `PASS`.

## Software scenarios

Reference Platform Qualification reuses existing integration evidence instead of creating a parallel test stack.

| ID | Scenario | Existing evidence |
| --- | --- | --- |
| Q01 | Platform Bootstrap | executable ControlHost, RuntimeHost and AIHost lifecycle tests |
| Q02 | 1080p50 End-to-End | V1 end-to-end proof |
| Q03 | 1080p59.94 End-to-End | V1 end-to-end proof with fractional frame-rate timebase |
| Q04 | CUT Qualification | V1 Preview-to-Program CUT evidence |
| Q05 | DISSOLVE Qualification | V1 deterministic DISSOLVE evidence |
| Q06 | Graphics / RGBA Source | static and dynamic RGBA layer evidence |
| Q07 | Audio Follow Video | V1 AFV transition and fallback evidence |
| Q08 | Governed AI / Inference | governed Person Segmentation and unavailable-provider fallback evidence |
| Q09 | Controlled Fault | recording storage exhaustion with committed Program continuity |
| Q10 | Restart / Recovery Sanity | real headless Operator process kill/restart and authoritative resynchronization |

Q02-Q08 intentionally share one V1 end-to-end execution evidence group. The runner executes that mapped test once and references the same retained log from each semantic qualification scenario. This keeps CI runtime bounded while preserving explicit scenario-level reporting.

Q10 exercises an actual Operator operating-system process restart. Broader RuntimeHost and ControlHost supervision/recovery evidence remains in the existing Required Gates and is not duplicated inside Reference Platform Qualification.

## Physical requirements

Reference Platform Qualification consumes the source-bound evidence model introduced before this work package. It does not weaken or bypass it.

| Requirement | Existing qualification type | Expected binding |
| --- | --- | --- |
| `REFERENCE_GPU` | `CUDA_REFERENCE` | `cuda-reference.binding.json` |
| `PROFESSIONAL_MEDIA_IO` | `MEDIA_IO_REFERENCE` | `media-io-reference.binding.json` |
| `GENLOCK` | `TIMING_REFERENCE_SOAK` | `timing-reference-soak.binding.json` |
| `PHYSICAL_END_TO_END_LATENCY` | `TIMING_REFERENCE_SOAK` | `timing-reference-soak.binding.json` |
| `LONG_SOAK` | `TIMING_REFERENCE_SOAK` | `timing-reference-soak.binding.json` |

A physical requirement becomes `PASS` only when `Test-QualificationEvidenceBinding.ps1` accepts the binding for:

- the expected qualification type;
- the exact source commit being qualified;
- the qualification-specific payload checks;
- the previously established payload hash/provenance chain.

A missing binding is `UNVERIFIED`. A supplied binding that fails source, type, schema, payload or integrity verification is `FAIL`.

Normal GitHub-hosted Required Gates therefore produce a useful software qualification result whose full platform status is normally `UNVERIFIED` until artifacts from the dedicated self-hosted physical workflows are supplied for that exact source commit.

## Platform environment evidence

Before scenario acceptance the runner captures `environment.json` and validates:

- Windows operating-system family;
- x64 process architecture;
- exact .NET SDK `10.0.401`.

Environment evidence also records OS/runtime descriptions, processor count, capture time and source commit.

This is an environment identity check, not a performance claim. Reference Platform Qualification introduces no arbitrary performance threshold. GPU deadlines, physical latency and long-soak acceptance remain owned by their existing measured physical qualification mechanisms.

## Running the qualification

From the repository root:

```powershell
./build/qualification/Invoke-ReferencePlatformQualification.ps1
```

When Release binaries already exist, such as inside Required Gates:

```powershell
./build/qualification/Invoke-ReferencePlatformQualification.ps1 -NoBuild
```

To make absence of any mandatory physical evidence a failing exit condition:

```powershell
./build/qualification/Invoke-ReferencePlatformQualification.ps1 -RequirePass
```

`-RequirePass` is appropriate only in an environment expected to possess all exact source-bound physical qualification bindings. It is intentionally not the default GitHub-hosted CI behavior.

## Evidence output

The runner writes under:

```text
artifacts/qualification/reference-platform/
```

Outputs include:

```text
environment.json
qualification-result.json
qualification-summary.md
logs/
	software-*.log
```

`qualification-result.json` is the machine-readable truth for the Reference Platform Qualification run. `qualification-summary.md` is a human-readable projection. `Test-ReferencePlatformQualificationResult.ps1` independently validates the machine-readable artifact.

Because `artifacts/*` is ignored by Git, generated qualification evidence is retained as CI/release evidence rather than committed as source.

## CI behavior

Required Gates execute the CI-safe Reference Platform Qualification runner after the normal Release build/test and process-recovery checks. The generated reference-platform evidence directory is uploaded as a workflow artifact.

The Quality job executes `Test-ReferencePlatformQualificationPolicy.ps1`, which protects:

- the fixed Q01-Q10 profile shape;
- the two V1 development formats;
- required host/platform identity;
- software-to-existing-test mappings;
- physical-to-existing-binding mappings;
- Required Gates integration;
- fail-closed aggregation, including a regression that proves mandatory `UNVERIFIED` hardware cannot be represented as overall `PASS`.

## Evidence interpretation

Typical GitHub-hosted result after all software scenarios pass and no physical bindings are supplied:

```text
Software: PASS
Hardware: UNVERIFIED
Overall: UNVERIFIED
```

A self-hosted reference qualification with all valid exact source-bound physical bindings may produce:

```text
Software: PASS
Hardware: PASS
Overall: PASS
```

If a software scenario fails, or a supplied physical binding fails validation:

```text
Overall: FAIL
```

The runner exits non-zero for `FAIL`, preserving CI failure semantics while allowing a normal CI run to retain truthful `UNVERIFIED` hardware state.

## Scope boundary

Reference Platform Qualification does not claim:

- certification of arbitrary customer hardware;
- qualification of unbound or stale physical evidence;
- 4K/UHD or HDR capability;
- Linux runtime qualification;
- multi-GPU scheduling;
- professional codec/container qualification beyond existing evidence;
- security/compliance acceptance outside their existing release evidence domains;
- performance guarantees beyond thresholds already owned by dedicated measured qualification mechanisms.

Reference Platform Qualification is reference-platform qualification evidence, not a general product certification statement.

## Completion criterion

Reference Platform Qualification is complete when the profile, runner, verifier, policy regression, documentation and CI retention path are present and Required Gates pass. Full **physical reference-platform PASS** remains evidence-dependent: it is achieved only by supplying successful exact source-bound CUDA, professional Media I/O and timing/reference/latency/soak qualification bindings for the candidate commit.
