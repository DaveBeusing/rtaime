<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Health Center

## Purpose

The Health Center is the Operator's central diagnostic workspace for understanding the current health of production-relevant rtaime subsystems. It is a presentation and aggregation surface only; it does not replace subsystem ownership, Runtime health authority or the global Runtime Readiness service.

Health and readiness remain separate concepts. A subsystem can be `Healthy`, `Warning`, `Degraded`, `Recovering`, `Failed` or `Unknown`. `RuntimeReadinessService` remains responsible for deciding whether the current combination of authoritative observations is production-ready.

## Data sources

`OperatorHealthSnapshotProvider` implements `IHealthSnapshotProvider` and projects existing evidence from:

- the authoritative Operator/Control health snapshot for Control, Runtime, Media, providers, CPU, system memory, GPU, VRAM and frame timing;
- the Media Deck for local decoder/session state and codec details;
- the existing compositing graph projection for compositing-node health;
- the existing Program Output controller for clean-output presentation health;
- the independent Runtime monitoring plane for diagnostic monitoring health.

No additional hardware probing, decoder, media transport, host transport or polling loop is introduced by the Health Center.

## Update model

Health snapshots are event-driven. Runtime hardware/performance data is sampled only when the existing authoritative health observation completes (`HealthObserved`) or centralized global-readiness evidence changes.

Monitoring image/frame updates are deliberately excluded from Health Center refresh triggers. Only monitoring state/detail changes participate. Compositing pan/zoom interaction also does not trigger health recomputation.

This keeps high-frequency media and render paths decoupled from WPF layout and diagnostic rendering.

## Workspace

`HEALTH` is a dedicated Operator workspace.

The overview shows:

- overall Runtime Readiness;
- CPU;
- Memory;
- GPU and VRAM;
- Media Engine;
- Compositing;
- Output;
- Frame Timing / Performance.

The subsystem list additionally exposes Decoder, Control Service, Runtime Service, Processing Providers and Monitoring Plane.

Selecting a subsystem shows its current state, state-change time, most recent successful health check, key measurements, operator-readable detail, recovery status and optional technical detail.

Warnings, degradation, recovery and failures remain grouped without collapsing independent subsystem reasons into one UI-only global state.

## Recovery

Recovery actions are shown only when an existing safe operation can be reused.

The current Health Center exposes manual `SYNCHRONIZE` only for a disconnected Control state when the existing Operator synchronization command is executable. Stale Control state keeps automatic full-snapshot recovery visible but does not expose a competing manual action.

Runtime, Media, Provider, Decoder and Output recovery continue to follow their owning services/workflows; the Health Center does not invent restart or retry semantics.

## State retention

`StatusSince` is retained while a subsystem remains in the same health state. `LastSuccessfulCheck` advances when a subsystem is observed as `Healthy` and remains available while a later warning, degradation or failure is displayed.

Opening, closing or rebinding the Health Center does not alter Runtime Readiness or subsystem authority.

## Performance verification

Frame Timing consumes the centralized Runtime performance-verification snapshot. A UI refresh cannot reset a verified result. Hardware, pipeline, validity-window and Runtime faults continue to use the invalidation rules defined by `RuntimeReadinessService`.

The Health Center never performs synchronous hardware discovery from the UI thread.
