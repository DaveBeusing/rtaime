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
- Control-confirmed Program audio and recording snapshots;
- Control-confirmed Program/Aux network transport observations using the existing Outputs lifecycle and successful-send freshness rules;
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
- Program Audio;
- Recording;
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

## Active incidents and evidence limits

The existing attention list is the active-incident view. It keeps one row per subsystem, sorted by severity, then impact and component name. Each selection exposes component, current evidence, evidence source, impact and a recommended safe next action. No separate incident authority, notification feed or historical telemetry database is created.

Impact is conservative. A Clean Program or independent monitoring fault is **MONITORING ONLY**. An isolated recording failure is **RECORDING ONLY**; an active output transport fault is **OUTPUT TRANSPORT ONLY**. Neither establishes that Runtime Program stopped. Aggregated Runtime/provider/media failures and invalidated performance qualification cannot establish Program unavailability or a hardware cause. Their Program impact stays **UNKNOWN**. Audio clipping, underrun and explicit audio errors can indicate degraded Program audio; silence or mute intent remains unknown.

Control disconnection, stale Control state or unverified Runtime observation marks retained Control-dependent evidence **UNKNOWN**, even when a retained meter or recording value appears healthy. The independent monitoring incident remains visible and the Control incident describes reconnect. Current evidence is shown with its own provenance; coincident drops, A/V offsets, GPU memory and transport faults are not presented as a causal diagnosis. Cumulative dropped samples are labeled as cumulative.

**STATE FIRST PROJECTED** is the start of the current displayed subsystem state. **LAST PROJECTED** is the latest Health aggregation time, not a new device observation, physical failure timestamp or successful-send time. State changes reset the first projection time. Source timestamps, where available, remain in the measurements. No new hardware, network-peer, physical output, soak or mixed-monitor DPI qualification is claimed.

## Diagnostic actions

**COPY INCIDENT SUMMARY** copies only the selected metadata fields. Each value uses the shared bounded redactor independently, so a long evidence detail cannot truncate away the safe next action. Technical details, media frames and audio buffers are excluded. Clipboard failure is visible in the view.

**EXPORT SUPPORT BUNDLE** reuses the System/startup exporter and destination workflow. The existing 256 MiB source-log limit, atomic publication, cancellation and redaction apply. Health metadata adds impact, evidence source and last projection time; this is an additive change to the existing support metadata, not an IPC or durable schema migration. Private-key material is rejected before diagnostic text truncation.

Synchronization remains the only executable recovery operation in Health, and it rechecks the existing command's availability at execution. The existing Client command owns transport authorization, deadlines and resynchronization. All other suggestions lead the operator to existing owning workflows; the view never restarts a Program-critical component.

## Bounded delivery and regression coverage

Health retains current rows and state timestamps only. A slow UI has at most one queued update and one pending current snapshot; subsequent updates replace that snapshot. Collection reconciliation preserves row identity and selection. Closing the view removes provider, readiness and synchronization-command subscriptions and prevents queued delivery from changing it.

Transient event history remains owned by the existing keyed notifications and structured logs. Health is a current-state projection: intermediate projections may be coalesced and are not a lossless event journal.

Deterministic regressions cover simultaneous monitoring/audio/recording failures, 5,000 delayed UI updates, selection retention, stale evidence, reconnect, recovery gating, clipboard failure/redaction, transport send freshness, export metadata/cancellation and notification deduplication. Surface checks cover named custom keyboard controls and scrollable incident evidence. Existing shell geometry/DPI tests continue to apply; physical multi-monitor interaction remains separately UNVERIFIED.

## Show checks and activity

Open **Show checks** in the navigation rail or search **Show preflight** in Commands (Ctrl+K). The [shared review view](ShowPreflightAndActivity.md) combines advisory asset/cue/output/audio/AI/durable-state observations with recent correlated requests and the existing notifications. Review links lead to existing workspaces. Health incidents, recovery and support bundles remain in their established services; acknowledgement does not change production health or permit show launch.
