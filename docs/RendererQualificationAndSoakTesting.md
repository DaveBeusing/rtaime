# Renderer Qualification and Long-Run Stability Testing

## Purpose

Renderer qualification extends the existing CUDA reference-hardware path with reproducible production-path evidence for long-tail frame timing, compositor correctness, CUDA/D3D11 monitoring, bounded recording, resource pressure, reconnect behavior and cleanup.

A green hosted CI run is **not** physical renderer qualification. Physical status remains **UNVERIFIED** until the dedicated Windows NVIDIA self-hosted workflow executes the exact source SHA and retains its evidence artifact.

## Relationship to existing qualification

This qualification reuses existing product and evidence paths rather than creating another renderer or timing authority:

- `CudaGpuProcessingBackend` remains the hardware compositor.
- `V1RuntimeHostService` remains the single Program execution/render path.
- the existing Program readback remains the host payload for recording and other established host consumers.
- the existing CUDA/D3D11 shared-resource monitoring export remains the GPU presentation path.
- the existing CUDA reference qualification remains a prerequisite.
- professional Media I/O, reference/genlock, physical end-to-end latency and A/V synchronization remain owned by their dedicated reference-platform workflows.

No stable contract changes and no CUDA/D3D pointer transport are introduced.

## Qualification profiles

The canonical profile is `qualification/renderer/renderer-qualification.json`.

| Profile | Runtime duration per format | Warm-up | Compositor samples/case | Purpose |
| --- | ---: | ---: | ---: | --- |
| `smoke` | 30 s | 12 | 20 | deterministic hardware sanity |
| `stress` | 300 s | 30 | 60 | medium resource/backpressure/reconnect stress |
| `soak` | 3600 s | 60 | 120 | long-tail stability and lifecycle evidence |

Every profile executes 1080p50 RGBA8 and 1080p59.94 RGBA8. The direct CUDA compositor matrix covers CUT and 50% DISSOLVE with **0 / 1 / 2 / 4 / 8** ordered layers. Full-frame SHA-256 output is compared with the managed semantic reference, while surface lifetime and CUDA/D3D11 monitoring export are also required.

## Exact qualification manifest

`New-RendererQualificationManifest.ps1` captures the exact 40-character source SHA, selected profile and profile SHA-256, Windows version/build/architecture, pinned .NET SDK 10.0.401, NVIDIA device name/ordinal/driver/PCI identity/VRAM, workflow run/attempt/runner identity and the 90-day retention policy.

The manifest fails closed for source mismatch, unexpected GPU identity, missing `nvidia-smi`, wrong SDK or malformed source identity.

## Production-path workload

For each V1 format the harness constructs the normal RuntimeHost production path with an injected real `CudaGpuProcessingBackend`. There is no managed fallback.

It exercises committed capability-planned Program execution, static plus bitmap graphics, CUDA composite/readback, a governed four-boundary DISSOLVE, CUDA/D3D11 Program monitoring, bounded Program recording, controlled recording backpressure/recovery, monitoring disconnect/reconnect, Runtime Program output history and final cleanup.

Professional physical output is not substituted by the Runtime virtual output. AJA/SDI, external reference/genlock, physical scan-out, physical A/V synchronization and end-to-end signal latency remain **UNVERIFIED** here.

## Retained metrics

The evidence retains Program-boundary P50/P95/P99/maximum, format frame budget and deadline violations; sequence discontinuities, duplicate frames, missing frames and Runtime dropped-frame evidence; CUDA HtoD, launch, context-sync, event-kernel, DtoH and monitoring-export timings; CPU/GPU utilization and VRAM percentiles when available; upload/readback/copy/reuse counters; readback and shared-resource pressure; recording backpressure/recovery; monitoring capture/drop/export data; bounded GPU fault observations; and final GPU/readback/shared-resource ownership.

Correctness, lifetime, continuity, required monitoring export or cleanup violations fail the run.

## P99 and baseline policy

P99 is mandatory evidence for both V1 formats. Current P99 is retained even when no approved historical baseline exists.

No arbitrary new P99 pass/fail limit is invented. Regression status is `PASS` only with a supplied previous PASSED artifact from the same GPU and profile that remains within the configured threshold; `UNVERIFIED` when no approved comparable baseline is supplied; and `FAIL` when comparison exceeds the policy.

The current comparison guard allows at most a 20% P99 regression versus the supplied approved baseline. This is a regression guard, not external broadcast certification. Existing strict CUDA qualification separately retains its own synchronous Composite + Readback limits.

## Controlled fault and pressure matrix

Destructive physical GPU reset/TDR or forced device removal is not performed and remains **UNVERIFIED**.

The self-hosted run also executes deterministic software evidence:

- `CLIP_SEEK`: confirmed media seek/state reconciliation.
- `RENDERER_BACKEND_RECOVERY`: injected CUDA-classified start/composite/readback failure behavior.
- `RESOURCE_PRESSURE`: readback-pool exhaustion and bounded reusable-upload eviction.
- `INTEROP_FAILURE_RECOVERY`: monitoring export/release failure isolation and recovery.
- `RESIZE_DPI_CHURN`: bounded monitoring target churn.
- `MONITORING_TRANSPORT_RECONNECT`: transport reconnect and shared-resource replacement.
- `PROCESS_RESTART`: RuntimeHost restart and authoritative revision reapplication.

These scenarios do not imply that a physical NVIDIA reset, Windows TDR, cable failure, AJA failure or external-reference loss occurred.

## Running on the reference NVIDIA machine

The dedicated `.github/workflows/cuda-reference-qualification.yml` remains manual `workflow_dispatch` and requires the `self-hosted`, `windows`, `x64`, `rtaime-cuda-reference` runner labels. It executes strict CUDA reference qualification first and then the selected renderer profile.

Equivalent approved-machine invocation:

```powershell
./build/qualification/Invoke-RendererReferenceQualification.ps1 -Profile smoke -ExpectedDeviceName "<expected NVIDIA device name substring>" -DeviceOrdinal 0
```

Use `stress` or `soak` for the corresponding duration. `-BaselineEvidencePath` optionally supplies a prior PASSED same-GPU/same-profile renderer artifact; without it, baseline comparison remains `UNVERIFIED`.

## Retained artifacts

Renderer artifacts are retained for 90 days and include `manifest.json`, `renderer-reference.json`, `software-scenarios.json`, `baseline-comparison.json` and per-scenario logs. Existing CUDA evidence and its source-bound binding remain separate immutable artifacts.

## Evidence state

Until the dedicated workflow executes on approved NVIDIA hardware for the exact source SHA, the following remain **UNVERIFIED**: physical CUDA/D3D11 production-path results; long-duration renderer soak; physical GPU/VRAM P50/P95/P99; physical monitoring-export timing; professional Media I/O/AJA; external reference/genlock; physical end-to-end latency and A/V synchronization; destructive device-reset/TDR behavior.

A missing hardware run is never represented as a pass.
