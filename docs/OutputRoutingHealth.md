<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Output Routing, Health and Performance

## Purpose

The OUTPUTS workspace is the production-facing view for Program routing, output state and currently available performance evidence. It is a presentation layer over existing Operator state and does not create another routing, health, recording, streaming or telemetry authority.

## Output model

Four presentation roles are represented in the compact production workspace:

- **PROGRAM** reflects the authoritative Runtime Program state and confirmed Program source.
- **PREVIEW** reflects the authoritative Control Preview routing snapshot.
- **AUX** is an explicit placeholder only. The current V1 contracts expose no governed Aux output role, so its target, source and format remain `UNAVAILABLE` with `UNVERIFIED` evidence.
- **CLEAN FEED** reflects the existing local Windows presentation managed by `ProgramOutputController`. It displays the Runtime-derived Program monitoring image and is not a physical Program output path.

The full OUTPUTS workspace and the compact Edit presentation consume this same projection; neither creates another routing model.

The workspace also shows the existing Engine, Control, Runtime, Media and GPU-provider health states as a compact system-health surface. The selected-output details expose target, assigned source, resolution, frame rate, pixel format, color-space evidence, recording state and streaming evidence. A field is shown as `UNAVAILABLE` when no authoritative value exists.

## Routing

The workspace does not define a routing engine. The only exposed Program-routing mutation is Preview to Program through the existing `OperatorViewModel.CutCommand`.

When that command cannot execute, the workspace enters `SAFE READ-ONLY`. The reason is derived from existing connection, stale-state, Program-safety and Runtime-readiness state. No local override bypasses the shared mutation gate.

## Health semantics

Runtime output health reuses the existing health projection:

- `PASS` is presented as `HEALTHY`.
- `UNVERIFIED` is presented as `WARNING`.
- `FAIL` is presented as `FAULTED`.

The clean Program monitor reuses the existing `ProgramOutputController.Health` state. `LIVE` is presented as healthy, `ERROR` as faulted, and states without confirmed healthy/faulted evidence remain warning-level. The existing monitoring panel remains available in OUTPUTS so display selection, start, stop and fullscreen controls are preserved. The workspace defines no independent warning thresholds.

Technical detail from the existing projections remains visible with the affected output so an operator does not need a log file to identify first-level output failures.

## Performance evidence

The current Runtime health contract provides:

- frame processing time and frame budget;
- dropped-frame count;
- GPU utilization when available;
- VRAM evidence when available;
- configured video format.

The current contract does not provide authoritative CPU utilization, system memory utilization, disk telemetry, network telemetry, temperature telemetry or measured Output FPS. Those metrics therefore remain `UNAVAILABLE`. The configured frame rate is shown as context but is not presented as measured Output FPS.

Color space is likewise `UNAVAILABLE` until it is published by an authoritative output contract. Recording state reuses the existing recording projection. Streaming remains `UNAVAILABLE` while no authoritative streaming state exists.

The Operator does not probe Performance Counters, driver utilities or hardware APIs to fill missing values. The compact System Status surface may render Disk, Network and Temperature as thin metric bars, but unavailable evidence remains visually empty rather than being converted into synthetic telemetry.

## Historical metrics

Mini histories are presentation-only ring buffers with a maximum of 48 samples. Samples are accepted only when the existing health observation changes. No additional polling timer is introduced, and unavailable values are not converted into zero-value samples.

## Lifecycle

`OutputRoutingHealthViewModel` subscribes only to the existing `OperatorViewModel` and `ProgramOutputController` presentation state. `MainWindow` owns and disposes the projection together with the other Operator presentation components.
