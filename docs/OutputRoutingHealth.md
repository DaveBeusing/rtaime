<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Output Routing, Health and Performance

## Purpose

The OUTPUTS workspace is the production-facing view for Program routing, output state and currently available performance evidence. It is a presentation layer over existing Operator state and does not create another routing, health, recording, streaming or telemetry authority.

## Output model

Four presentation roles are represented in the compact production workspace:

- **PROGRAM** is a governed output role whose authoritative source is synchronized with the existing Program transition path. Runtime/provider evidence confirms the committed target, format, timing and health.
- **PREVIEW** reflects the authoritative Control Preview routing snapshot for operator monitoring and switching; it is not promoted to a physical output role merely because it is displayed in OUTPUTS.
- **AUX** is a governed output role with its own stable role identity, authoritative source, provider/target selection, lifecycle and Runtime/provider evidence. The V1 reference configuration initializes Aux from Input B and allows deterministic source changes through the governed output-role command path.
- **CLEAN FEED** reflects the existing local Windows presentation managed by `ProgramOutputController`. It displays the Runtime-derived Program monitoring image and is not a physical Program output path.

Program and Aux therefore share the governed output-role state model, while Preview and Clean Feed retain their correct monitoring/presentation semantics. The full OUTPUTS workspace and the compact Edit presentation consume this same projection; neither creates another routing model.

The workspace also shows the existing Engine, Control, Runtime, Media and GPU-provider health states as a compact system-health surface. The selected-output details expose target, assigned source, resolution, frame rate, pixel format, color-space evidence, recording state and streaming evidence. A field is shown as `UNAVAILABLE` when no authoritative value exists.

## Routing

The workspace does not define a routing engine. Program continues to move through the existing Preview-to-Program CUT/DISSOLVE and governed Scene paths; the generic output-role command is deliberately rejected for Program so transition semantics cannot be bypassed. Aux source changes use the governed `RouteOutputRoleCommand` path through Operator/Client, ControlHost planning and Runtime commit.

ControlHost owns output-role configuration authority. RuntimeHost executes the prepared bindings and the selected provider supplies output evidence. Invalid source identities, duplicate/unsupported roles and unsupported V1 provider/format/timing policies are rejected before execution. V1 currently supports deterministic `auto` provider admission with `production` format and timing policies; future provider selectors or policies must become explicit supported contract behavior before they are accepted.

When production mutations cannot execute, the workspace enters `SAFE READ-ONLY`. The reason is derived from existing connection, stale-state, Program-safety and Runtime-readiness state. No local override bypasses the shared mutation gate.

## Health semantics

Governed Program/Aux output health is projected from Runtime/provider evidence and reconciled against authoritative Control configuration:

- `PASS` is presented as `HEALTHY` only when Runtime confirms the configured role/source and provider output evidence is healthy.
- `UNVERIFIED` is presented as `WARNING` while committed provider evidence is missing, stale or unavailable.
- `FAIL` is presented as `FAULTED` for Runtime/provider failure or when Runtime output evidence does not match authoritative Control configuration.
- failure evidence retains the structured error code/reason so the operator can diagnose the affected role without inferring success from configuration alone.

The clean Program monitor reuses the existing `ProgramOutputController.Health` state. `LIVE` is presented as healthy, `ERROR` as faulted, and states without confirmed healthy/faulted evidence remain warning-level. The existing monitoring panel remains available in OUTPUTS so display selection, start, stop and fullscreen controls are preserved. The workspace defines no independent warning thresholds.

Technical detail from the existing projections remains visible with the affected output so an operator does not need a log file to identify first-level output failures.

## Performance evidence

The current Runtime health contract provides:

- core render time and frame budget;
- measured Program output FPS;
- dropped-frame count;
- GPU utilization when available;
- VRAM evidence when available;
- configured video format.

Runtime performance snapshots provide bounded measured CPU utilization and system-memory usage/capacity on the qualified Windows platform. NVIDIA GPU utilization and VRAM usage are published through driver-provided NVML telemetry when available; unsupported or unavailable GPU telemetry remains explicitly `UNVERIFIED`. The Render metric is the core Runtime render/composite duration, while full pipeline timing remains owned by Runtime timing qualification. The engineering target is at or below 3 ms Runtime render latency, with 5 ms retained as the hardware P95 qualification ceiling. Values at or below 3 ms are healthy, values above the engineering target are warnings, and values beyond the active frame budget are faulted. Measured Output FPS is derived from the existing Program scheduler-boundary cadence using a constant-space smoothed observation and is transported in the same Runtime performance snapshot. Disk, network and temperature telemetry remain `UNAVAILABLE`; the configured frame rate remains context and is not substituted for measured Output FPS.

Color space is likewise `UNAVAILABLE` until it is published by an authoritative output contract. Recording state reuses the existing recording projection. Streaming remains `UNAVAILABLE` while no authoritative streaming provider/state exists; the generalized output-role model is an extension point, not evidence that a streaming protocol is implemented.

The Operator does not probe Performance Counters, driver utilities or hardware APIs to fill missing values. The compact System Status surface may render Disk, Network and Temperature as thin metric bars, but unavailable evidence remains visually empty rather than being converted into synthetic telemetry.

## Historical metrics

Mini histories are presentation-only ring buffers with a maximum of 48 samples. Samples are accepted only when the existing health observation changes. No additional polling timer is introduced, and unavailable values are not converted into zero-value samples.

## Shared visual contract

Output, health and performance presentation now uses one reusable Operator control family across EDIT, LIVE, OUTPUTS and COMPOSITING:

- `RtaimeOutputRow` is a fixed 54 px flat row with an 8 px evidence dot, 56×32 monitoring thumbnail, output/source identity, target plus format context and the route state on the right.
- `RtaimeHealthRow` is a fixed 29 px flat subsystem row with an 8 px evidence dot, subsystem label and textual state. Rows are not wrapped in individual cards.
- `RtaimeMetricBar` keeps a 4 px track and has explicit `HasValue` semantics. Missing telemetry hides the value indicator rather than presenting a synthetic zero.
- `RtaimeMetricDial` reuses the bounded metric-ring renderer while preserving the same explicit no-value behavior.
- `RtaimeSparkline` draws only retained real samples on the flat dark performance background with a 1 px trace.
- `RtaimeAlertRow` provides the same text-plus-evidence-dot language for LIVE alerts.

EDIT and LIVE reuse the compact `OutputRoutingHealthControl` rather than maintaining separate output-row implementations. OUTPUTS uses the same row controls in its full diagnostic surface. COMPOSITING reuses the metric dial and sparkline for its performance panel.

The 1920×1080 reference shell remains unchanged: Top 60 px, navigation 92 px, media 400 px, center 1070 px, inspector 340 px, upper workspace 700 px and timeline 320 px. The LIVE output region is 270 px at reference size, but its rendered host height follows the responsive shell. The 27 px header/footer and four 54 px row-density targets remain reference control geometry; when the host is shorter, the output list scrolls inside the available region instead of forcing or clipping the surrounding workspace.

No presentation component increases the Runtime health observation cadence. Metric histories remain bounded to the existing 48 samples and are updated only from the existing health observation stream.

## Lifecycle

`OutputRoutingHealthViewModel` subscribes only to the existing `OperatorViewModel` and `ProgramOutputController` presentation state. Governed Aux rows are derived from the synchronized output-role evidence already carried by `OperatorViewModel`; the view model does not probe providers or create production truth. `MainWindow` owns and disposes the projection together with the other Operator presentation components.

Output-role configuration is persisted with the authoritative Control checkpoint. On ControlHost restart the configured Program/Aux roles are reconstructed, validated against the current production specification and reconciled with Runtime. Legacy checkpoints without the optional role payload recover through the specification defaults, preserving the existing V1 checkpoint format while remaining fail-closed for invalid recovered role state.
