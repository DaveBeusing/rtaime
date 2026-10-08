<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>
# CUDA Reference Hardware Qualification

## Purpose

CUDA Reference Hardware Qualification qualifies the existing NVIDIA CUDA Driver API backend on one explicitly selected Windows x64 NVIDIA professional GPU. The presence of `CudaGpuProcessingBackend` or a green standard CI run is not hardware qualification evidence.

The qualification is deliberately evidence-driven. A candidate remains `UNVERIFIED` until the dedicated self-hosted reference runner executes the full profile and produces a `PASSED` evidence document.

## Current qualification state

`UNVERIFIED` until a physical reference-hardware run of `.github/workflows/cuda-reference-qualification.yml` completes successfully and its immutable `cuda-reference-qualification-<run>-<attempt>` artifact is retained with the release evidence.

Standard GitHub-hosted Required Gates compile and structurally verify the qualification path, but they do not satisfy CUDA Reference Hardware Qualification hardware evidence.

## Qualification profile

The runner requires an explicit expected GPU name substring and CUDA device ordinal. It fails closed when:

- Windows x64 is not used;
- the CUDA Driver API is unavailable;
- the requested CUDA ordinal is absent;
- the detected device name does not match the declared reference target;
- the backend is not `NvidiaCuda` and hardware accelerated;
- any functional, lifetime or timing case fails;
- evidence is missing, malformed, not schema `1.1`, or does not report `PASSED`.

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

This validates provider-visible allocation/release lifetime. CUDA Reference Hardware Qualification does not claim vendor-driver VRAM accounting beyond the device memory metadata reported by CUDA capability detection.

## Timing evidence

Qualification continues to measure synchronous `Composite + Readback`, matching the current V1 boundary behavior. It now also enables a bounded qualification-only CUDA timing collector so CPU driver-call wall time and GPU kernel elapsed time are not conflated.

For every case the report records:

- synchronous Composite + Readback P50, P95, P99 and maximum elapsed milliseconds;
- `UploadHostToDevice` CPU wall time around `cuMemcpyHtoD_v2`;
- `KernelLaunch` CPU wall time around `cuLaunchKernel`;
- `ContextSynchronize` CPU wait time around `cuCtxSynchronize`;
- `KernelGpuElapsed` from CUDA events recorded around the kernel on the default stream;
- `ReadbackDeviceToHost` CPU wall time around `cuMemcpyDtoH_v2`.

The CUDA-event elapsed value and the CPU `ContextSynchronize` wait overlap in meaning and must not be summed. The former is device-side kernel elapsed evidence; the latter is how long the CPU waits for context completion.

P95 must be at or below 5 ms and maximum synchronous Composite + Readback latency must be at or below 10 ms. P99 is retained as tail evidence but has no independent pass/fail ceiling yet; the existing maximum ceiling is stricter while reference-hardware history is established.

The collector is not enabled by RuntimeHost production construction. No CUDA event, extra wait, queue, stream or scheduling policy is introduced into the default production path solely for diagnostics.

The engineering target for the qualified production path is approximately 3 ms core render latency, with 5 ms treated as the hard P95 qualification ceiling. These limits qualify the current synchronous compositor/readback path; media I/O, scheduling and true end-to-end signal latency remain separate timing domains.

## Running locally

On the intended Windows x64 reference machine:

```powershell
./build/qualification/Invoke-CudaReferenceQualification.ps1 `
	-ExpectedDeviceName "<expected NVIDIA device name substring>" `
	-DeviceOrdinal 0 `
	-SampleIterations 30
```

Evidence is written to `artifacts/qualification/cuda-reference.json` by default. The script exits unsuccessfully unless the evidence status is exactly `PASSED`, all eight cases pass pixel/surface-lifetime/timing checks, P50/P95/P99/maximum ordering is valid, and every case contains the required HtoD, launch, synchronization, GPU-kernel and DtoH timing metrics.

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

CUDA Reference Hardware Qualification qualifies the V1 CUDA compositor backend and its current synchronous readback behavior. It does not introduce multi-GPU scheduling, external media I/O, GPUDirect, vendor video SDK integration or a new production authority path. Those remain outside this work package or are addressed by later media-I/O work packages.
