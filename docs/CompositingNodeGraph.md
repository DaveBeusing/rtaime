<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Compositing Node Graph

## Purpose

The COMPOSITING workspace presents the observable production path as a compact, read-only-first node graph. It is an Operator projection over existing source, routing, graphics, Runtime health, monitoring and recording state; it is not a second execution or routing authority.

## Reference composition

The global production shell is preserved. At the 1920×1080 reference viewport, Media Library uses the 400 px reference left region, Inspector uses the 340 px reference right region and Timeline uses the 320 px reference lower region. Their rendered allocations continue to follow the shared responsive shell rather than forcing those reference pixels.

Inside the 1070×700 reference center surface, COMPOSITING uses the reference **64 / 6 / 36** horizontal split. The graph/preview split remains proportional while the Preview reference height is rendered from the current viewport:

- approximately 64% for the node graph;
- 6 px gap;
- approximately 36% for the Preview and System & Performance stack.

The right stack reuses the established Preview monitor at 390 px high, followed by a 6 px gap and the compact System & Performance panel. No second Preview monitor transport, decoder or timeline is created.

## Node graph visual contract

The graph follows the compositing mockup palette and density:

- graph background: `#0A141C`;
- minor grid: `#13222C`;
- major grid: `#1B303C`;
- node radius: 4 px;
- node padding: 10 px;
- neutral connections: 2 px;
- active connections: cyan;
- selected node: cyan outline;
- no large node shadows.

Category identity is shown by a narrow node accent rather than coloring every node outline. Inputs are presented on the left and outputs on the right.

The current product data supports these mockup-facing roles:

- **Media Input** — current source and graphics-source projections;
- **Output Router** — current Preview / Program routing;
- **Transform** — the existing graphics X / Y / Scale transform;
- **Merge** — the current GPU composite stage;
- **Output** — Preview and Program output projections;
- **Recorder** — the current Program recorder.

Processing/Enhance, Color Grade and Keying are not rendered because the current projection does not expose authoritative nodes for those roles.

## Stable topology and interaction

The graph uses stable node identities. Ordinary status refreshes update the existing node view models in place and do not reset node positions. Source collection or topology changes may invoke deterministic Auto Layout.

SELECT projects the chosen node into the existing Inspector. PAN, mouse-wheel zoom, FIT, 100% reset and AUTO LAYOUT remain presentation-only operations. Middle-mouse panning remains available independently of the active interaction mode.

Arbitrary source/routing topology rewiring is not exposed by the current contracts and the graph still provides no decorative REWIRE action. The toolbar identifies the **CONFIRMED LAYER STACK** and adds only bounded layer operations that are backed by the existing authority path:

- move the selected active layer one position lower or higher;
- toggle visibility for bitmap graphics or Production CG;
- decrease or increase opacity for bitmap graphics or Production CG.

Layer selection remains presentation-only. A button press sends an explicit client command through ControlHost to RuntimeHost, displays an applying state, and refreshes the graph only from the confirmed Runtime snapshot. Legacy visual/test-layer state remains governed by its existing command rather than being silently migrated into the new controls.

## Preview

The right-side Preview reuses the same `PreviewViewer` and monitor chrome used elsewhere in the Operator. It consumes the existing monitoring image, source identity, format, timecode, Media Deck transport, cue navigation, fit/maximize and fullscreen paths.

The compositing workspace does not create another monitoring or playback path.

## System & Performance

The System & Performance panel is a view over the existing `OutputRoutingHealthViewModel` and Operator lifecycle evidence.

CPU, GPU and system Memory use the own `RtaimeMetricDial` presentation built on the shared metric-ring renderer. A dial draws a value arc only when a real numeric sample exists. CPU and system Memory now use Runtime-published Windows measurements; GPU uses Runtime-published NVML utilization when a qualified NVIDIA driver exposes it. Missing measurements remain `UNAVAILABLE / UNVERIFIED` rather than being synthesized.

VRAM and Render Time reuse their Runtime-derived values. The small history graph uses only the bounded existing metric history. The Operator panel adds no timer, hardware query, Performance Counter, NVML call or other telemetry probe; hardware sampling is owned and cached by RuntimeHost.

This is **real telemetry only**: missing data remains visibly unavailable rather than being converted into zero, healthy or estimated values.

## Inspector integration

Selecting a graph node uses the existing shared Inspector path. Runtime layer nodes expose their stable identity, confirmed order, visibility, opacity and transform in the graph projection. Inspector data remains a projection only; the bounded layer commands live in the COMPOSITING toolbar and do not create another editor-state model.

## Authority boundary

`CompositingGraphProjector` is a deterministic Client-side projection over already observed state. It does not own Runtime execution, routing, media processing, graphics composition or recording.

`CompositingGraphViewModel` consumes the existing `OperatorViewModel` projection and updates existing node instances by stable identity. No graph action introduces a second production-state store.

## Verification

`build/quality/Test-OperatorUiPolicy.ps1` verifies:

- the 64 / 6 / 36 center split;
- exact graph background/grid colors through the shared semantic tokens;
- absence of large node shadows;
- 4 px node radius and 10 px padding;
- neutral 2 px connections and cyan active connections;
- cyan selected-node outline and category accent separation;
- left input / right output presentation;
- only currently backed mockup role labels;
- own rtaime controls with no directly visible stock WPF controls;
- existing pan/zoom/Fit/Auto Layout behavior;
- bounded layer order, visibility and opacity controls through the client boundary;
- pending-versus-confirmed layer mutation feedback;
- stable node positions across status refreshes;
- shared Inspector selection;
- reuse of the established Preview monitor and the Preview / 6 px gap / System & Performance right stack;
- real-only CPU/GPU/Memory evidence, VRAM/Render values and bounded GPU history;
- no second telemetry poller or host-level authority dependency.
