# Engine and Renderer Baseline Audit

## Status

- Audit source: master at a4bdec571f745012777dc119265f7dc711cf3181
- Audit date: 2026-10-08
- Technology baseline: .NET SDK 10.0.401, C#/.NET 10, Windows x64 reference platform
- Behavioral change: none
- Physical CUDA / D3D11 / Media I/O evidence for this exact source: UNVERIFIED
- Exact-master Required Gates evidence: UNVERIFIED. The available commit-status and commit-workflow surfaces expose no run attached to this merge commit. Earlier PR-head success is intentionally not promoted to evidence for this later SHA.

This document is the source-level baseline for the engine/render path before optimization. It separates verified facts from source risks, hypotheses and unavailable hardware evidence.

## Evidence classes

- VERIFIED: directly established by current source, tests, configuration or bounded software evidence.
- SOURCE RISK: code structure can plausibly impose cost, but the cost is not yet measured on reference hardware.
- HYPOTHESIS: plausible bottleneck requiring instrumentation before remediation.
- UNVERIFIED: requires physical/reference-hardware evidence that is not present for the exact source SHA.

No source risk or hypothesis in this document is a performance defect claim.

## Architecture constraints to preserve

Future remediation must preserve one RuntimeHost-owned Program cadence and committed execution boundary; ControlHost production authority; RuntimeHost execution ownership; one compositor/renderer path; provider-neutral contracts; bounded GPU surfaces, host readback buffers, monitoring resources and recording queues; deterministic Program-frame ownership; fail-closed qualification; and the C#/.NET-first baseline.

## Current Program frame path

| Stage | Current implementation | Copy / synchronization | Evidence |
| --- | --- | --- | --- |
| Cadence | RuntimeHostProcess.RunMediaLoopAsync owns the single epoch-based rational schedule | No overlapping Program boundary; lateness becomes timing evidence instead of an unbounded frame queue | VERIFIED |
| Media admission | Local Media Deck / Media I/O input is admitted before the Program boundary | Physical Media I/O copies the current capture lease into Runtime working memory | VERIFIED |
| Boundary capture | V1RuntimeHostService captures committed execution, Program binding, inputs, layers, monitoring state and audio inputs | Boundary execution, capture and short metadata synchronization are separated | VERIFIED |
| GPU upload | CudaGpuProcessingBackend.Allocate | Backend-wide gate; synchronous cuMemcpyHtoD_v2 per allocated/materialized RGBA surface | VERIFIED |
| Composite | CudaGpuProcessingBackend.Composite | Backend-wide gate is held across cuLaunchKernel followed by cuCtxSynchronize | VERIFIED source structure; physical cost UNVERIFIED |
| Monitoring GPU export | TryExportMonitoringResource -> CudaD3D11MonitoringInterop.TryExport | Backend-wide gate; CUDA/D3D11 map, cuMemcpy2D_v2, unmap and cuCtxSynchronize | VERIFIED source structure; physical cost UNVERIFIED |
| Program readback | GpuProcessingProvider.RentReadback -> CUDA ReadbackInto | Backend-wide gate; synchronous cuMemcpyDtoH_v2 into bounded reusable host memory | VERIFIED |
| Physical Program output | Media I/O borrows/pins the existing Program readback for synchronous submit | No second documented full-frame managed copy | VERIFIED |
| Monitoring | Runtime monitoring retains the Program readback lease as needed; GPU presentation may retain a shared resource | CPU fallback reuses Program readback; GPU monitoring uses D3D11 shared resource | VERIFIED |
| Recording | Recording retains an explicit Program-video lease for asynchronous writer consumption | Bounded queue and deterministic release; no second GPU readback | VERIFIED |
| Replay | Replay consumes committed post-composite Program pixels/audio | Bounded retention; no second renderer or production authority | VERIFIED |
| Publication | Final Runtime state is published under the short metadata gate | Publication follows successful boundary processing | VERIFIED |

## Static bandwidth baseline

RGBA8 1920x1080 is 8,294,400 bytes per full frame.

The mandatory Program device-to-host readback therefore represents, before protocol or driver overhead:

- 1080p50: about 414.7 MB/s of device-to-host payload.
- 1080p59.94: about 497.2 MB/s of device-to-host payload.

These are arithmetic payload rates, not measured PCIe throughput, latency or utilization.

Frame budgets are:

- 1080p50: 20.000 ms.
- 1080p59.94 (60000/1001): about 16.683 ms.

Existing project guidance uses a 3 ms engineering target for core Runtime render/composite latency. CUDA reference qualification currently enforces P95 <= 5 ms and maximum <= 10 ms for synchronous composite/readback qualification cases.

## Existing measurement surfaces

### Runtime timing

RuntimeTimingQualificationProbe records bounded scheduler interval jitter and complete committed-boundary processing duration. RuntimeFrameDropCounter keeps constant-space cadence/drop evidence without a per-frame history.

This is suitable for complete boundary duration, deadline/jitter evidence, dropped cadence/output evidence and long-running bounded observation. It is not a GPU kernel timer.

### Runtime performance snapshot

Runtime diagnostics already expose frame budget, most recently observed core render/composite duration, output FPS, cumulative dropped-frame evidence, CPU/RAM telemetry, and NVIDIA GPU/VRAM management telemetry when available. Management telemetry is sampled outside the Program hot path.

### GPU provider tests

GpuProcessingPerformanceTests currently provide:

- 1080p50 and 1080p59.94 managed-reference regression coverage;
- reusable readback allocation guard;
- layer-count coverage at 0/1/2/4/8;
- provider duration versus CPU wall-clock reporting for a single multi-layer operation;
- active-surface lifetime checks.

This is software regression evidence, not reference CUDA timing evidence.

### CUDA reference qualification

CudaReferenceHardwareQualification currently provides:

- 1080p50 and 1080p59.94;
- CUT A, CUT B, 50% DISSOLVE and one DISSOLVE-with-layer case;
- warm-up plus measured steady-state samples;
- P50, P95 and maximum wall-clock duration;
- pixel correctness;
- surface lifetime correctness;
- device identity and memory evidence;
- exact-source evidence binding through the self-hosted CUDA workflow.

Its duration is synchronous Composite + Program Readback wall time. It must not be described as GPU kernel elapsed time.

## Prioritized findings

### 1. CUDA composite uses a full context barrier inside the backend lock

Class: SOURCE RISK

Location: src/Providers/rtaime.Provider.Gpu/CudaGpuProcessingBackend.cs, Composite.

The CUDA path launches composite_rgba and immediately executes cuCtxSynchronize while holding the backend-wide gate.

Potential consequences are CPU waiting for all work on the context, inability for the same backend instance to overlap other operations, and lock hold time expanding with GPU or driver stalls.

Not proven: that this is the dominant V1 bottleneck, that asynchronous streams would improve the complete boundary, or that removing the barrier is safe.

Required evidence before change: CPU wall duration around launch/sync, CUDA-event elapsed time around the kernel, backend lock wait/hold duration and complete boundary timing on the reference GPU.

### 2. One backend-wide lock serializes the relevant CUDA operations

Class: SOURCE RISK

Allocate, Composite, TryExportMonitoringResource, ReadbackInto, Release, startup and shutdown use the same gate.

This is structurally simple and safe but creates a possible serialization point between source upload, composite, monitoring export, Program readback and surface release.

Not proven: lock contention. The current single-boundary Runtime design may mean the lock is largely uncontended. Instrument contention before changing synchronization.

### 3. Program device-to-host readback is a mandatory synchronous copy in the current V1 path

Class: VERIFIED architectural cost / SOURCE RISK for performance

The bounded pool removes recurring full-frame managed allocation but does not remove the per-frame device-to-host transfer.

The same Program lease is reused by physical output, monitoring CPU fallback and recording, which avoids duplicate readbacks and is a positive baseline property.

Hypothesis to test: at 1080p59.94 the readback and associated driver synchronization may consume a meaningful portion of the 16.683 ms frame budget.

### 4. GPU monitoring export adds a GPU-side copy and explicit context synchronization when sampled

Class: SOURCE RISK

CudaD3D11MonitoringInterop.TryExport performs CUDA/D3D11 registration, map, cuMemcpy2D_v2, unmap, cuCtxSynchronize, unregister and D3D flush before publishing the shared handle.

It avoids a second host readback. Its physical cost and sampling-frequency impact remain UNVERIFIED.

### 5. CUDA qualification does not cover the full layer scaling matrix

Class: VERIFIED coverage gap

Managed-reference performance regression covers 0/1/2/4/8 layers. Physical CUDA qualification covers no layer or one layer only.

Required hardware baseline matrix:

- formats: 1080p50 and 1080p59.94;
- transitions: CUT and DISSOLVE;
- layers: 0, 1, 2, 4, 8;
- warm-up and steady state;
- simultaneous Program output, monitoring and recording where the reference environment supports them;
- bounded fault/recovery cases.

Do not infer 2/4/8-layer CUDA scaling from managed-reference results.

### 6. P99 is absent from the existing CUDA qualification report

Class: VERIFIED coverage gap

The report captures P50, P95 and maximum. Deadline-sensitive baseline analysis should additionally record P99 while retaining maximum/outlier evidence.

No P99 pass/fail budget should be added until enough reference-hardware samples establish a stable expectation.

### 7. CPU wall time and GPU elapsed time are not independently measured

Class: VERIFIED instrumentation gap

The existing CUDA qualification deliberately measures synchronous end-to-end Composite + Readback wall time. That is useful production-boundary evidence but cannot separate CPU/lock overhead, driver overhead, kernel execution, synchronization wait, device-to-host copy and monitoring-export cost.

Any CUDA optimization decision must first add GPU-event timing or equivalent device-side timestamps while preserving wall-clock measurement.

### 8. Combined full-pipeline physical baseline is incomplete for the exact source

Class: UNVERIFIED

Software CI validates architecture and bounded-resource behavior. Dedicated self-hosted workflows exist for CUDA and timing/reference qualification, but the audited SHA has no attached physical evidence proving the complete simultaneous workload.

Software CI or managed-reference performance must not be represented as physical GPU qualification.

## Required measurement plan

### Configuration matrix

| Dimension | Values |
| --- | --- |
| Format | 1080p50, 1080p59.94 |
| Transition | CUT, DISSOLVE |
| Active layers | 0, 1, 2, 4, 8 |
| Monitoring | off, normal sampled GPU/CPU fallback path as applicable |
| Recording | off, active qualified profile |
| Program output | virtual baseline, physical qualified output |
| Phases | warm-up, steady state, bounded fault/recovery |
| Evidence | P50, P95, P99, maximum and exact sample count |

### Per-boundary metrics

Record without unbounded histories or hot-loop logging:

- scheduler interval and jitter;
- complete Runtime boundary wall time;
- materialization/upload CPU wall time;
- backend lock wait time and hold time;
- composite CPU wall time;
- CUDA kernel GPU elapsed time;
- context synchronization wait time;
- Program readback CPU wall time and byte count;
- monitoring export wall time and GPU copy elapsed time;
- output submission duration/backpressure;
- recording queue depth and accepted/dropped/failure counters;
- monitoring queue/subscriber drop counters;
- active GPU surfaces;
- readback pool allocated/active/available buffers;
- active shared monitoring leases;
- benchmark-process per-frame managed allocation delta;
- CUDA/driver errors;
- shutdown resource counts returning to zero.

Never combine CPU wall time and GPU elapsed time under one label. Complete Runtime boundary wall time remains the deadline-qualification signal.

## Reproduction on software CI or a developer machine

Use the repository-pinned SDK.

~~~powershell
dotnet --version
dotnet restore rtaime.slnx
dotnet build rtaime.slnx --configuration Release --no-restore

dotnet test tests/rtaime.Tests.Performance/rtaime.Tests.Performance.csproj --configuration Release --no-build --filter "FullyQualifiedName~GpuProcessingPerformanceTests"

dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~ProgramFrameMemoryOwnershipTests|FullyQualifiedName~Timing"
~~~

Run Required Gates unchanged for PR validation. Software runs establish regression and architecture evidence only.

## Reproduction on the CUDA reference runner

Use the existing self-hosted CUDA reference hardware qualification workflow against the exact candidate SHA, or invoke the repository script on the qualified runner:

~~~powershell
./build/qualification/Invoke-CudaReferenceQualification.ps1 -ExpectedDeviceName "<approved reference GPU name>" -DeviceOrdinal 0 -SampleIterations 100 -EvidencePath "artifacts/qualification/cuda-reference.json"
~~~

Bind resulting evidence to the exact source using the existing qualification evidence binding scripts/workflow. A run from another SHA is not evidence for the candidate.

For physical cadence, Media I/O, external reference, A/V sync, end-to-end latency and long-duration stability, use the existing Timing reference latency and soak qualification workflow.

## Baseline budgets

| Signal | Baseline / gate | Status |
| --- | --- | --- |
| 1080p50 frame budget | 20.000 ms | VERIFIED definition |
| 1080p59.94 frame budget | about 16.683 ms | VERIFIED definition |
| Core Runtime render engineering target | <= 3 ms | Existing project target |
| Existing CUDA synchronous Composite + Readback P95 | <= 5 ms | Existing gate; exact-SHA result UNVERIFIED |
| Existing CUDA synchronous Composite + Readback maximum | <= 10 ms | Existing gate; exact-SHA result UNVERIFIED |
| GPU kernel P50/P95/P99 | No isolated baseline | UNVERIFIED |
| Program readback P50/P95/P99 | No isolated baseline | UNVERIFIED |
| Backend lock wait/hold P50/P95/P99 | No measured baseline | UNVERIFIED |
| Monitoring export P50/P95/P99 | No isolated baseline | UNVERIFIED |
| Full output + monitoring + recording workload | No attached exact-SHA physical baseline | UNVERIFIED |

## Remediation order after evidence

1. Add measurement separation first: preserve wall-clock timing and add bounded GPU-event timing plus backend lock wait/hold timing in qualification-only instrumentation.
2. Expand CUDA workload qualification to 0/1/2/4/8 layers, CUT/DISSOLVE, P99 and combined output/monitoring/recording configurations.
3. Only if measured, reduce the composite-wide context barrier using explicit stream/event dependencies while preserving deterministic frame publication.
4. Only if measured, narrow backend synchronization without introducing concurrent Program boundaries or unsafe resource lifetime.
5. Only if measured, reduce mandatory host readback for consumers that can remain GPU-resident while keeping one authoritative Program frame and provider-neutral contracts.
6. Only if measured, amortize monitoring interop registration/export work or retain safe reusable interop resources across sampled frames.
7. Re-run the exact same matrix after every remediation and reject changes that improve a local microbenchmark while degrading complete-boundary P95/P99, failure behavior or resource bounds.

## Rollback considerations

This audit changes no runtime behavior and requires no product rollback.

Subsequent optimization work should keep each synchronization/copy optimization independently revertible, retain the synchronous path until replacement evidence is complete, avoid combining lock restructuring/CUDA stream changes/Program ownership changes in one commit, and preserve current failure-closed resource counters as regression oracles.

## Qualification matrix at audit completion

| Area | Software evidence | Physical evidence for exact SHA |
| --- | --- | --- |
| Single Runtime cadence | PASS by source/policy coverage | UNVERIFIED |
| Bounded Program readback pool | PASS by source/tests | UNVERIFIED under sustained physical load |
| Surface release to baseline | PASS by software tests | UNVERIFIED on reference CUDA for exact SHA |
| Managed 0/1/2/4/8-layer scaling regression | PASS as software evidence | Not CUDA proof |
| CUDA 0/1-layer qualification capability | Present | UNVERIFIED for exact SHA |
| CUDA 2/4/8-layer qualification | Coverage missing | UNVERIFIED |
| CPU wall vs GPU elapsed separation | Coverage missing | UNVERIFIED |
| P99 capture | Coverage missing | UNVERIFIED |
| D3D11/CUDA monitoring interop | Contract/lifetime/policy coverage present | UNVERIFIED |
| Program output + monitoring + recording combined load | Architecture coverage present | UNVERIFIED |
| Shutdown resource counts | Software regression coverage present | UNVERIFIED under reference-hardware fault/soak |

## Conclusion

The current architecture already avoids several common failure modes: one Program cadence, one compositor, bounded queues/pools, reusable Program readback memory, deterministic lease ownership and no duplicate Program host readback for monitoring/recording.

The highest-value next step is not a renderer rewrite. It is reference-hardware instrumentation that separates kernel time, synchronization wait, readback time and backend lock time while preserving complete-boundary timing.

The two strongest source-level optimization candidates are the synchronous CUDA context barrier and the backend-wide lock, but neither is a measured bottleneck yet.
