<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# CUDA Reference Hardware Qualification

## Purpose

AP-31 qualifies the existing NVIDIA CUDA Driver API backend on one explicitly selected Windows x64 NVIDIA professional GPU. The presence of `CudaGpuProcessingBackend` or a green standard CI run is not hardware qualification evidence.

The qualification is deliberately evidence-driven. A candidate remains `UNVERIFIED` until the dedicated self-hosted reference runner executes the full profile and produces a `PASSED` evidence document.

## Current qualification state

`UNVERIFIED` until a physical reference-hardware run of `.github/workflows/cuda-reference-qualification.yml` completes successfully and its immutable `cuda-reference-qualification-<run>-<attempt>` artifact is retained with the release evidence.

Standard GitHub-hosted Required Gates compile and structurally verify the qualification path, but they do not satisfy AP-31 hardware evidence.

## Qualification profile

The runner requires an explicit expected GPU name substring and CUDA device ordinal. It fails closed when:

- Windows x64 is not used;
- the CUDA Driver API is unavailable;
- the requested CUDA ordinal is absent;
- the detected device name does not match the declared reference target;
- the backend is not `NvidiaCuda` and hardware accelerated;
- any functional, lifetime or timing case fails;
- evidence is missing, malformed, not schema `1.0`, or does not report `PASSED`.

There is no silent fallback from the hardware qualification path to `ManagedReferenceGpuBackend`.

## Functional matrix

Both V1 development formats are exercised:

- 1080p50 RGBA8;
- 1080p59.94 RGBA8.

For each format, four cases run against the actual CUDA backend:

1. CUT to source A;
2. CUT to source B;
3. 50% DISSOLVE (`BlendWeight=128`);
4. 50% DISSOLVE plus an RGBA key layer.

Each case compares the center output pixel with the deterministic V1 blend/composite reference math.

## Surface lifetime evidence

Each case holds exactly three persistent input surfaces: A, B and layer. Every warm-up and measured output surface is disposed immediately after readback. The provider surface count must return to the three-surface baseline after every output. Leaving the case disposes all persistent inputs.

This validates provider-visible allocation/release lifetime. AP-31 does not claim vendor-driver VRAM accounting beyond the device memory metadata reported by CUDA capability detection.

## Timing evidence

Qualification measures synchronous `Composite + Readback`, matching the current V1 boundary behavior rather than kernel launch time alone.

For every case:

- P50, P95 and maximum elapsed milliseconds are recorded;
- P95 must be within one frame period for the tested format;
- maximum latency must be within two frame periods.

These limits are qualification guards, not a claim that all future media I/O, scheduling or end-to-end latency work is complete.

## Running locally

On the intended Windows x64 reference machine:

```powershell
./build/qualification/Invoke-CudaReferenceQualification.ps1 `
	-ExpectedDeviceName "<expected NVIDIA device name substring>" `
	-DeviceOrdinal 0 `
	-SampleIterations 30
```

Evidence is written to `artifacts/qualification/cuda-reference.json` by default. The script exits unsuccessfully unless the evidence status is exactly `PASSED` and all eight cases pass pixel, surface-lifetime and timing checks.

## GitHub workflow

`CUDA reference hardware qualification` is a manual `workflow_dispatch` workflow and requires a self-hosted runner labeled:

- `self-hosted`
- `windows`
- `x64`
- `rtaime-cuda-reference`

The workflow uploads the JSON evidence as an immutable GitHub Actions artifact for the run attempt.

## Evidence semantics

`CudaQualificationStatus` has three states:

- `UNVERIFIED`: no qualifying physical run exists;
- `PASSED`: the declared reference device completed all eight cases within the functional/lifetime/timing requirements;
- `FAILED`: device discovery, identity, execution, correctness, lifetime or timing qualification failed.

`UNVERIFIED` is never equivalent to `PASSED` and must never be represented as such in release evidence.

## Scope boundary

AP-31 qualifies the V1 CUDA compositor backend and its current synchronous readback behavior. It does not introduce multi-GPU scheduling, external media I/O, GPUDirect, vendor video SDK integration or a new production authority path. Those remain outside this work package or are addressed by later media-I/O work packages.
