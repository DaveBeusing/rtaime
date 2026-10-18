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

The routing graph additionally projects an authoritative Aux output role when one is present in the synchronized Control snapshot. Its provider and target identity are observational metadata; a missing source marks the output faulted, absent execution confirmation remains degraded, and an inactive route is not drawn as live. Network SRT/NDI health remains in the established OUTPUTS workspace until the signal graph has a validated dedicated network-output projection. The graph does not infer physical device certification.

The current product data supports these mockup-facing roles:

- **Media Input** — current source and graphics-source projections;
- **Output Router** — current Preview / Program routing;
- **Transform** — one authoritative transform node per confirmed bitmap or Production CG layer, including Position, Scale, Rotation, Anchor/Pivot and Crop;
- **Processing** — zero to four ordered closed-kind processing nodes (Color Grade or Chroma Key) when that authoritative layer owns processing state;
- **Merge** — the current GPU composite stage;
- **Output** — Preview and Program output projections;
- **Recorder** — the current Program recorder.

The authoritative layer chain is projected as `Layer → Transform → [ordered Processing nodes] → Composite`. Every configured node is shown with stable identity and execution order; an empty stack connects Transform directly to Composite. A processing node is never shown merely as a placeholder. Color Grade and the bounded typed Chroma Key baseline are projected from confirmed authoritative state; the graph does not imply arbitrary shader/plugin support or broadcast-keyer certification.

## Stable topology and interaction

If the authoritative Preview or Program source identifier is absent from the current source projection, the routing node displays `MISSING SOURCE` with the affected role and error health. This is read-only diagnostic evidence: it does not synthesize a replacement source, alter routing, or claim a successful Runtime commit.

The graph uses stable node identities. Ordinary status refreshes update the existing node view models in place and do not reset node positions. Only changes to the projected topology invoke deterministic Auto Layout. Source snapshot transactions and thumbnail updates do not rebuild the graph. The graph culls offscreen nodes and connections before creating Canvas visuals; the layer list uses recycling virtualization and supports every layer admitted by the current Runtime contract.

SELECT projects the chosen node into the existing Inspector. PAN, mouse-wheel zoom, FIT, 100% reset and AUTO LAYOUT remain presentation-only operations. Middle-mouse panning remains available independently of the active interaction mode.

Arbitrary source/routing topology rewiring is not exposed by the current contracts and the graph still provides no decorative REWIRE action. The toolbar identifies the **CONFIRMED LAYER STACK** and adds only bounded layer operations that are backed by the existing authority path:

- move the selected active layer one position lower or higher;
- toggle visibility for bitmap graphics or Production CG;
- decrease or increase opacity for bitmap graphics or Production CG.

The LAYERS panel shares the same node identities and selection as the graph. It displays confirmed order, visibility, opacity, content and health. Search and per-layer collapse are local presentation state. Ctrl/Shift selection provides a read-only common/mixed review in the shared Inspector; single-layer editing resumes after selecting one layer. Dragging a layer stages a local insert-before order; APPLY ORDER submits it and UNDO ORDER discards it. Visibility and integral 0..255 opacity fields also remain local until APPLY LAYER; invalid numeric input disables submission. Rejection preserves the draft and its confirmed comparison. REVIEW LAYER selects its existing Inspector entry.

Layer selection remains presentation-only. A toolbar or Inspector mutation sends an explicit client command through ControlHost to RuntimeHost, displays the existing in-flight state and refreshes the graph only from confirmed authoritative state. Bitmap graphics and Production CG layers expose the bounded transform model: normalized Position X/Y, Scale, Rotation in degrees, normalized Anchor/Pivot and normalized Crop edges. The transform is deterministic and defaults reproduce the previous output exactly.

The authoritative model supports an ordered stack of 0..4 typed processing nodes per supported layer. Selecting a Processing graph node resolves its owning layer and stable node identity into the shared Inspector. The Inspector can add Color Grade or Chroma Key nodes while capacity remains, edit and enable/disable the selected typed node, move it earlier or later, and remove it. Color Grade exposes Brightness in -1..1 and Contrast/Saturation in 0..2; Chroma Key exposes 8-bit key RGB plus normalized Tolerance, Softness and Spill Suppression. Each operation derives a validated next stack from the last confirmed stack and submits that complete stack through the existing Client → ControlHost → RuntimeHost mutation path. Dedicated Program-pixel color picking, arbitrary shader/plugin configuration and free graph rewiring remain unsupported.

Legacy visual/test-layer state remains governed by its existing command and does not acquire unsupported transform or processing semantics.

## Preview

The right-side Preview reuses the same `PreviewViewer` and monitor chrome used elsewhere in the Operator. It consumes the existing monitoring image, source identity, format, timecode, Media Deck transport, cue navigation, fit/maximize and fullscreen paths.

The compositing workspace does not create another monitoring or playback path.

## System & Performance

The System & Performance panel is a view over the existing `OutputRoutingHealthViewModel` and Operator lifecycle evidence.

CPU, GPU and system Memory use the own `RtaimeMetricDial` presentation built on the shared metric-ring renderer. A dial draws a value arc only when a real numeric sample exists. CPU and system Memory now use Runtime-published Windows measurements; GPU uses Runtime-published NVML utilization when a qualified NVIDIA driver exposes it. Missing measurements remain `UNAVAILABLE / UNVERIFIED` rather than being synthesized.

VRAM and Render Time reuse their Runtime-derived values. The small history graph uses only the bounded existing metric history. The Operator panel adds no timer, hardware query, Performance Counter, NVML call or other telemetry probe; hardware sampling is owned and cached by RuntimeHost.

This is **real telemetry only**: missing data remains visibly unavailable rather than being converted into zero, healthy or estimated values.

## Inspector integration

Selecting a Layer, Transform or Processing graph node uses the existing shared Inspector path and resolves back to the same authoritative layer identity. Runtime layer projections expose stable identity, confirmed order, visibility, opacity, transform and the complete ordered typed processing stack.

The Inspector keeps editable processing values only as interaction state. Ordinary status refreshes preserve an in-progress draft for the same stable processing-node identity; a newer confirmed node state or resynchronization rebases the editor from authoritative state while retaining a bounded recovery copy of dirty transform/processing intent. RESTORE DRAFT explicitly restores compatible local values into the current context with local undo; DISCARD COPY removes the recovery copy. Neither action submits a mutation. Up to eight recovery contexts and eight removed-layer draft summaries are retained. Missing layers remain diagnostic evidence and are never recreated automatically. APPLY and stack mutations travel through Operator → Client → ControlHost → RuntimeHost, show pending state while confirmation is outstanding, and only the returned confirmed snapshot updates production presentation. Mutations are unavailable while the Operator is stale, busy, disconnected or Runtime is not ready. The local layer panel complements the existing bounded toolbar commands; processing reorder remains scoped to the selected layer stack.

## Scene and local interaction evidence

The workspace header separates the selected Scene declaration from Runtime-observed layer evidence. RECALL SCENE uses the existing scene command, and a confirmed direct layer or processing mutation invalidates ActiveScene through the existing authority path. Search, selection, collapse, local drafts, pan, zoom, Fit and local undo do not alter Scene or production state. A changed confirmed value is shown alongside a conflicting local layer draft; order submission requires the current confirmed layer identity set to match the staged order.

F3 focuses layer search, Home fits the graph and +/- zooms when a text editor does not own the key. The toolbar remains horizontally accessible at compact widths, the layer panel adapts to the available width, and shared High Contrast/Reduced Motion preferences apply without introducing animation or a second monitoring session.

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

## Processing durability and qualification

The graph continues to project only confirmed ordered processing state. Qualification now treats the ordered processing chain as one end-to-end semantic unit: mixed Color Grade/Chroma Key order has byte-exact golden coverage, disabled nodes retain no-processing parity, and persistence/recovery fixtures preserve stable node identity and order.

Recording and CPU-fallback monitoring are verified against the same final post-processing Program payload rather than separately reconstructed pixels. Operator visual/DPI qualification continues through Required Gates after the processing workflow integration; no second Preview, local renderer, or monitoring poller is introduced.

The hosted processing matrix covers both V1 frame rates and the representative none, Color Grade, Chroma Key, Chroma Key → Color Grade, and Color Grade → Chroma Key cases. The renderer's existing 0 / 1 / 2 / 4 / 8 layer matrix remains a separate GPU-compositor qualification dimension.

## Workspace qualification

`InspectorPrecisionTests` covers stable node/geometry identity across repeated snapshots, retained processing drafts, maximum admitted layers, viewport culling, local state/order drafts, rejected submission, common/mixed review and explicit reconnect recovery. `CompositingWorkspaceQualification` runs inside the existing real MainWindow STA/BAML fixture and exercises the actual graph and shared Inspector controls, numeric validity, local precision undo, compact width, search, collapse and shared presentation preferences. Its controlled transport is presentation evidence, not Runtime output evidence.

`GraphicsOverlayIntegrationTests.Compositing_workspace_commands_cross_real_pipes_preserve_typed_order_and_recall_scene_pixels` uses the real Client, named-pipe ControlHost and RuntimeHost servers, confirmed transforms/typed processing/order, scene recall and final Program pixels. It also checks monitoring parity, ActiveScene invalidation and rejection of recall after bitmap resources are removed. Required Gates executes this focused integration case before the broad suites. CI results must be assessed at the current PR head; running, skipped or cancelled gates are not passing evidence.
