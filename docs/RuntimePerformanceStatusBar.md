<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Runtime Performance Status Bar

## Purpose

The Operator Production Shell exposes one permanent compact Runtime performance bar. It presents current production telemetry without introducing another telemetry authority, hardware probe, render-thread measurement or UI polling loop.

## Metrics

The status bar contains ordered metric slots for:

- CPU utilization;
- GPU utilization;
- system RAM usage;
- GPU VRAM usage;
- Runtime frame/render time;
- measured Program output frames per second;
- cumulative dropped-frame evidence.

The slot list is projected by `RuntimePerformanceStatusBarViewModel` and can be extended without changing the status-bar control layout.

## Data Sources

All values reuse existing Runtime and Control observations.

CPU, RAM, GPU and VRAM originate in `SystemHardwareTelemetry`. Hardware sampling remains cached at the Runtime management boundary. The Operator never performs local hardware discovery.

Frame processing time and the active frame budget originate in the existing `V1RuntimePerformanceSnapshot`.

Dropped-frame evidence continues to use `RuntimeFrameDropCounter`, combining cumulative skipped rational scheduler slots with cumulative native-output backpressure/rejection evidence.

Measured Output FPS is derived from the same Program scheduler-boundary timestamps already used by `RuntimeFrameDropCounter`. The estimator retains only the previous boundary timestamp and one exponentially smoothed FPS value. It allocates no per-frame history and performs no I/O.

## Update Cadence

The Runtime hot path records only constant-space scalar observations.

The Operator status bar does not own a timer. It refreshes when a completed `HealthObserved` snapshot becomes visible to the Operator. Connection/stale changes may additionally mark the last values as retained and unverified.

This produces the intended presentation cadence from the existing management synchronization loop while hardware values remain bounded by their own Runtime sampling cache. No WPF render loop or independent polling task is created.

## State And Thresholds

The status bar reuses `OutputRoutingHealthViewModel` performance evidence and status mapping.

- CPU, RAM, GPU and VRAM use existing Runtime/provider evidence rather than new utilization thresholds.
- Frame time reuses the established Runtime render-time policy: at or below 3 ms is healthy, above 3 ms is warning, and beyond the active frame budget is faulted.
- Output FPS uses authoritative Runtime health evidence; it is not compared to a second UI-owned timing threshold.
- Dropped frames remain cumulative evidence. A non-zero count is displayed as a warning.
- Missing measurements remain explicitly `UNAVAILABLE` or `UNVERIFIED`.

Health and Production Readiness remain separate concepts.

## UI Behavior

The Runtime status bar is permanently visible at the bottom of the Production Shell. CPU/GPU/RAM/frame metrics are no longer duplicated in the title bar.

Healthy values remain visually quiet. Warning and fault values use the existing warning/error design tokens. Every slot exposes its evidence detail as a tooltip.

When synchronization is stale or disconnected, the most recent values may remain visible for operator context but are marked as retained/unverified instead of being reset to zero.

## Performance Boundary

The implementation must not:

- query CPU/GPU hardware from the UI thread;
- allocate per-frame history in the Runtime Program loop;
- introduce locks beyond the existing Runtime performance-observation assignment;
- add a UI polling timer;
- perform file or network I/O from Program cadence measurement.

The allocation smoke test verifies that repeated cadence observations allocate no per-frame managed history after warmup.

## Deadline and consumer diagnostics

The status bar retains its existing aggregate dropped count and scheduler-observed FPS. It does not claim physical output acceptance or physical display cadence. Schedule start lateness and internal Program completion lateness are separately available in Runtime support diagnostics and do not add duplicate drops.

Runtime support diagnostics also expose recording and network queue depth, capacity, high-water mark and current saturation status. Network drops and rejections remain per-role evidence; recording drops remain recorder evidence. They are not summed across consumers into the status bar, because several consumers can discard the same Program sequence independently.

Current saturation clears when the queue drains; the high-water mark remains for the session (recording resets it on a new recording session). A historic nonzero dropped count is distinct from current backpressure.
