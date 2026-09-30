# Monitoring Visual and Performance Qualification

## Purpose

This document is the consolidated qualification contract for rtaime's Operator monitoring and inspection subsystem. It maps existing and targeted automated evidence across presentation geometry, DPI, color, frame identity, inspection, scopes, comparison, recovery, bounded resources, allocation behavior and Program isolation.

Monitoring remains derived, non-authoritative state. A monitoring failure, fallback, reconnect or graphics-device failure must not change Runtime Program, routing, recording or physical output authority.

## Evidence classes

Qualification evidence is classified as:

- **Semantic** — deterministic model, geometry, color, identity or compatibility behavior.
- **Visual structure** — source/layout structure that protects one scaling surface, overlay alignment and Clean Program separation.
- **Performance** — software-measured allocation, cadence, result-size or bounded-resource evidence.
- **Failure/recovery** — loss, replacement, reconnect, fallback and cleanup behavior.
- **Physical** — reference GPU/driver/display evidence that cannot be established by software-only CI.

Software-only CI must never promote physical evidence from `UNVERIFIED` to `PASS`.

## Qualification matrix

| Area | Automated evidence | Evidence class | Software status |
| --- | --- | --- | --- |
| Fit / Fill / Pixel Perfect / zoom / pan | `MediaPresentationGeometryTests`, `MediaViewportVisualQualityQualificationTests`, `MonitoringVisualPerformanceQualificationTests` | Semantic / visual structure | Qualified |
| Physical-pixel mapping | `MediaViewportVisualQualityQualificationTests`, `MediaPixelInspectionTests` | Semantic | Qualified |
| DPI 100 / 125 / 150 / 200 percent | `MediaViewportVisualQualityQualificationTests`, `MonitoringVisualPerformanceQualificationTests` | Semantic | Qualified |
| GPU / WPF fallback geometry parity | `MonitoringVisualPerformanceQualificationTests` | Semantic | Qualified |
| Single scaling surface / sharpness structure | `MediaViewportVisualQualityQualificationTests`, Operator monitoring policy | Visual structure | Qualified |
| Pixel grid / ROI source alignment | `MediaPixelInspectionTests`, `GpuChannelAndRoiInspectionTests` | Semantic / visual structure | Qualified |
| Presentation color transform | `MonitoringDisplayTransformTests` | Semantic | Qualified |
| Channel / scope / compare color basis | `GpuChannelAndRoiInspectionTests`, `GpuScopesAndFrameComparisonTests` | Semantic | Qualified |
| Unknown color metadata behavior | `MonitoringDisplayTransformTests`, `MediaScopeAnalysisTests` | Semantic | Qualified |
| GPU frame generation ordering | `OperatorGpuMonitoringFrameTests` | Semantic / recovery | Qualified |
| Preview follows confirmed route | `OperatorMonitoringPlaneTests`, Operator monitoring policy | Semantic | Qualified |
| Program is actual post-composite observation | `OperatorMonitoringPlaneTests`, Program-frame ownership policy | Semantic | Qualified |
| Exact 1x1 inspection | `MediaPixelInspectionTests` | Semantic | Qualified |
| GPU channel isolation | `GpuChannelAndRoiInspectionTests` | Semantic | Qualified |
| ROI geometry / bounded statistics | `MediaPixelInspectionTests`, `GpuChannelAndRoiInspectionTests` | Semantic / performance | Qualified |
| Histogram / waveform / RGB parade / vectorscope | `MediaScopeAnalysisTests`, `GpuScopesAndFrameComparisonTests` | Semantic / performance | Qualified |
| Split / wipe / Difference | `GpuScopesAndFrameComparisonTests`, `MediaScopeAnalysisTests` | Semantic | Qualified |
| Shared-resource replacement / cleanup | `OperatorMonitoringPlaneTests`, `MonitoringSustainedQualificationTests` | Failure/recovery / performance | Qualified |
| Named-pipe disconnect / reconnect | `MonitoringSustainedQualificationTests` | Failure/recovery | Qualified |
| Graphics open / draw failure fallback | `MediaViewportVisualQualityQualificationTests`, GPU inspection/scope policy tests | Failure/recovery | Qualified structurally |
| Sustained resource churn | `MonitoringSustainedQualificationTests` | Performance / recovery | Qualified in software |
| Steady-state geometry allocation | `MonitoringVisualPerformanceQualificationTests` | Performance | Qualified in software |
| UI analysis invalidation bounds | ROI/scope/diagnostics 200 ms policy checks | Performance | Qualified structurally |
| Monitoring queue pressure | `OperatorMonitoringPlaneTests` | Performance / recovery | Qualified |
| Program memory ownership / isolation | `ProgramFrameMemoryOwnershipTests`, Program-frame ownership policy | Semantic / performance | Qualified |
| Clean Program overlay separation | Operator monitoring tests and policy | Visual structure / architecture | Qualified |
| Physical D3D11/CUDA draw duration | Dedicated reference-platform evidence | Physical | UNVERIFIED by software CI |
| Physical monitor latency / scan-out | Dedicated reference-platform evidence | Physical | UNVERIFIED by software CI |
| Driver-specific device-loss recovery timing | Dedicated reference-platform evidence | Physical | UNVERIFIED by software CI |

## Software performance envelope

The software qualification envelope is intentionally deterministic and hardware-neutral:

- Scope analysis cadence is at most 5 Hz: one update window every 200 ms.
- Scope analysis samples no more than 262,144 source pixels per update.
- GPU scope result transfer is fixed at 21,504 `uint` values / 86,016 bytes.
- ROI analysis cadence is at most 5 Hz and samples no more than 262,144 source pixels.
- ROI analysis transfers only 16 `uint` values / 64 bytes of reduced numeric evidence.
- Monitoring diagnostics HUD refresh is bounded to a 200 ms cadence.
- Preview plus Program shared monitoring ownership is bounded to two published shared resources per active monitoring tap.
- The GPU provider permits at most four active monitoring leases: the current published pair plus one complete replacement pair. This fixed headroom is required so replacement does not fail merely because the previous pair is still published.
- Replaced shared monitoring resources must be released; after replacement settles the active provider count returns to two, and disconnect returns it to zero.
- Geometry and target-stabilization qualification uses allocation measurements after warmup and rejects material steady-state managed growth.
- Monitoring subscriptions are bounded and latest-wins under pressure rather than blocking Program.

Measured GPU shader execution duration is diagnostic evidence only. It is not a physical CUDA or reference-platform PASS unless the dedicated physical qualification path records that evidence.

## Visual and DPI contract

The WPF fallback and GPU presentation paths derive their destination from the same physical viewport size and the same `MediaPresentationGeometry` semantics.

The qualified representative DPI set is 100%, 125%, 150% and 200%. Additional existing cases may remain in the viewport matrix. Pixel Perfect means one source pixel maps to one physical destination pixel; no surrounding `Viewbox` or second presentation resample is permitted.

The checkerboard, pixel grid and ROI overlay are projected from the same presentation width, height, offsets and source dimensions as the media surface. They are presentation-only.

## Frame identity and temporal correctness

GPU monitoring ordering is scoped to a provider epoch:

1. a newer sequence in the same generation may replace the current resource;
2. a newer generation may replace an older generation;
3. stale generations and stale sequences may not replace the current resource;
4. a provider-instance change establishes a new presentation epoch after provider restart.

Program observations are accepted only as Program stream observations. Preview GPU comparison uses only the source matching the confirmed Preview route.

The monitoring hub and transport are loss-tolerant. Queue pressure may discard older monitoring observations, but must not delay Runtime Program execution.

## Failure and recovery

The qualification suite protects the following behavior:

- shared-handle open failure leaves deterministic CPU/WPF fallback eligible;
- device/surface unload releases opened D3D resources;
- a newer valid resource can retry the GPU path;
- scope and ROI compute failure does not become Program failure;
- named-pipe disconnect causes the client to retry independently from control transport;
- a restarted monitoring server can be observed by the same monitoring client;
- shared monitoring resources return to baseline after subscriber disconnect and tap disposal.

Software tests establish lifecycle semantics. Exact driver/device-loss timing remains physical evidence.

## Sustained qualification

The default CI soak repeatedly replaces Preview and Program shared monitoring resources and verifies that active resources remain bounded and return to zero after disconnect.

A longer local mode is enabled with:

`RTAIME_MONITORING_QUALIFICATION_SOAK_LONG=1`

The long mode increases replacement iterations without changing acceptance criteria.

## Production isolation

Qualification must continue to prove:

- no management/control IPC bulk-pixel transport;
- no raw CUDA/vendor pointer leakage in stable monitoring contracts;
- no second decoder or Program renderer for monitoring;
- no new full-resolution CPU readback solely for monitoring;
- Clean Program has no inspection, scope or comparison overlays;
- monitoring state cannot mutate Runtime Program, routing or recording.

## Evidence boundary

Repository tests prove deterministic software behavior and bounded software resources. They do not prove physical display sharpness, GPU driver scheduling, CUDA/D3D11 execution latency, monitor scan-out latency or long-duration reference-hardware stability.

Those items remain **UNVERIFIED** until dedicated reference-platform evidence is produced by the existing physical qualification process.
