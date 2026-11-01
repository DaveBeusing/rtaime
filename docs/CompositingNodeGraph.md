<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Compositing Node Graph

## Purpose

The COMPOSITING workspace presents the observed, Runtime-confirmed **live layer stack first**. The existing, read-only-first node graph is available on demand through **ADVANCED GRAPH** for detailed Transform, Color Grade, Chroma Key and source/sink inspection. Both presentations use the same Operator node identities, shared Inspector and production observation; this is not a second execution or routing authority.

## Reference composition

The global production shell is preserved. At the 1920×1080 reference viewport, Media Library uses the 400 px reference left region, Inspector uses the 340 px reference right region and Timeline uses the 320 px reference lower region. Their rendered allocations continue to follow the shared responsive shell rather than forcing those reference pixels.

Inside the 1070×700 reference center surface, COMPOSITING uses the reference **64 / 6 / 36** horizontal split. The graph/preview split remains proportional while the Preview reference height is rendered from the current viewport:

- approximately 64% for the node graph;
- 6 px gap;
- approximately 36% for the Preview and System & Performance stack.

The right stack reuses the established Preview monitor at 390 px high, followed by a 6 px gap and the compact System & Performance panel. No second Preview monitor transport, decoder or timeline is created.

## Primary live layer and effects workflow

By default the confirmed layer list receives the available COMPOSITING viewport.
The advanced graph column is collapsed and its offscreen node/connection
presentations are not realized. **ADVANCED GRAPH** opens the original bounded
graph with the existing proportional layer-list column. Closing the advanced
graph restores the spacious live layer overview without changing selection,
node IDs, Program/Preview routing, playback or any persisted layout. The
shared Preview monitor and runtime status remain in their existing owners.

The live overview displays only observed layer order, visible/contributing
state, content identity and the ordered **confirmed** FX chain. A closed
processing node is identified as **GRADE** or **CHROMA KEY**, including
explicit **OFF** state. Source and layer selection are read-only operations;
they do not automatically render, seek, activate, recall a Scene or modify Program.

The **MORE** menu exposes selected-layer order, visibility and opacity staging.
These actions change local intent only. The persistent draft footer exposes
**APPLY LAYER**, **REVIEW**, **DISCARD**, **APPLY ORDER** and **DISCARD ORDER**
when the corresponding draft is unresolved. Apply submits through the existing
Operator → Client → ControlHost → RuntimeHost mutation path. `APPLYING` is
not a confirmation; only matching Runtime-observed layer state or layer
order can be labeled **CONFIRMED**. Rejected or ambiguous results retain
the local draft rather than projecting it into Program.

Layer state drafts retain the production revision and content identity first
observed when editing began. Concurrent Runtime mutation invalidates implicit
Apply until an explicit **REVIEW**, and a replaced content identity cannot be
review-rebased at all; discard and edit the replacement instead. Order drafts
also retain the ordered layer IDs, kind, content identity and production
revision. Any mismatch forbids APPLY ORDER until DISCARD and restaging.
Unsupported kinds, invalid numeric input, stale/disconnected/busy Runtime
and unavailable mutation capability remain fail-closed.

## Compact chrome, targeted actions and draft recovery

The graph chrome prioritizes the canvas rather than a long horizontal
mutation toolbar. It exposes **LAYERS**, **ADVANCED GRAPH**, **SELECT**,
**PAN**, **FIT** (when the advanced graph is open), **MORE** and observed
zoom percentage. SELECT/PAN are visibly distinguished by active mode.
The viewport allocation continues to use existing responsive widths and the
actual measured graph region; no new graph layout, node renderer or
pan/zoom coordinate owner is introduced.

**MORE** opens the existing styled `RtaimeContextMenu` with
`RtaimeMenuItem` actions. Its hierarchy is:

1. **Presentation:** Reset zoom to 100%; Auto layout.
2. **Selected object:** Move layer up/down (local order draft); Toggle
   layer visibility (local draft); Decrease/increase opacity (local draft).
3. **FX order:** Stage selected FX earlier/later; Apply FX order draft;
   Discard FX order draft.
4. **Draft decisions:** Apply, Review or Discard selected layer state;
   Apply or Discard layer order.

The same menu is available from **MORE**, the graph background, a graph
node or a layer-list item. Opening it on a node/layer selects that existing
object through `SelectNodeCommand` and the shared Inspector; it never
creates a second selection or mutation model. Selection and menu dismissal
do not execute a production change. Command `CanExecute` gates reflect
selected object kind, single vs. multiple selection, supported processing
kind, Runtime synchronization, revision and in-flight state. Disabled
operations retain their state/evidence tooltips. For a blank/empty graph,
presentation commands and the menu remain available while editing
commands are ineligible.

A **draft footer** is placed above the graph/list content and remains
reachable independent of the LAYERS panel and ADVANCED GRAPH visibility.
It only collapses when no unresolved layer state, layer order or FX order
draft exists. It shows each dirty layer's identity and local conflict
evidence (including drafts on layers that are no longer selected), with
an action to reselect the owner. It provides the currently selected layer's
**APPLY LAYER / REVIEW / DISCARD**, plus independent
**APPLY ORDER / DISCARD ORDER** and **APPLY FX / DISCARD FX** decisions.
Rejected, applying, unresolved and stale/revision-conflict evidence stays
visible as text rather than relying on color. Where the selected layer is
not the owner of an unresolved draft, its Apply/Review/Discard are disabled
until the operator selects the affected row. The per-layer local controls
and shared Inspector remain available for precise draft editing.

Closing the context menu never submits or discards a draft. **Apply**
remains a deliberate guarded action using the pre-existing
`CompositingGraphViewModel` commands; stale FX/order drafts fail closed
until explicit Discard and restaging, while compatible same-content layer
state drafts may use the existing Review/Apply flow. A changed layer
content identity cannot be re-reviewed into the replacement. No additional
ControlHost, RuntimeHost or Program mutation path is introduced.

**Keyboard and pointer:** Shift+F10 or the Menu key opens the focused
object's menu, with **MORE** as a fallback. Context-menu invocation on a
node/layer follows the existing selection path. F3 still focuses layer
search; Home fits; +/- zoom; the mouse wheel zooms around the pointer;
middle mouse or PAN drags the viewport; Alt+drag moves a graph node
locally; Ctrl+Arrow nudges the focused graph selection. Keyboard handling
does not intercept text editing. The menu does not modify pointer capture
or synthesize another graph drag, and the footer occupies measured layout
space rather than introducing a second scrollable toolbar.

**Qualification limits:** Structural menu/binding tests and the
existing CompositingGraphAuthoring/CompositingWorkspaceQualification
regressions cover the command routes, draft state, viewport and selection
contract. Windows WPF rendering, small viewport and DPI behavior,
high-contrast focus accessibility and physical GPU/display acceptance
require their actual Windows/physical qualifications; a static UI test
alone cannot establish those results.

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

SELECT projects the chosen node into the existing Inspector. **LAYERS**, **ADVANCED GRAPH**,
**SELECT**, **PAN** and **FIT** remain the compact, non-scrolling graph chrome.
Mouse-wheel zoom and middle-mouse panning remain available. The **MORE**
menu holds **Reset zoom to 100%** and **Auto layout**, both presentation-only
commands that leave Runtime routing and Program unchanged.

Arbitrary source/routing topology rewiring is not exposed by the current contracts
and the graph still provides no decorative REWIRE action. The selected-object
section of the menu stages, but does not apply:

- moving the selected active layer earlier/later in order;
- toggling visibility for bitmap graphics or Production CG;
- decreasing/increasing the local 8-bit opacity for bitmap graphics or Production CG.

The shared command owners retain their existing capability and revision gates;
disabled actions cannot be used to bypass authority.

The LAYERS panel shares the same node identities and selection as the graph. It displays confirmed order, visibility, opacity, content and health. Search and per-layer collapse are local presentation state. Ctrl/Shift selection provides a read-only common/mixed review in the shared Inspector; single-layer editing resumes after selecting one layer. Dragging a layer stages a local insert-before order; APPLY ORDER submits it and UNDO ORDER discards it. Visibility and integral 0..255 opacity fields also remain local until APPLY LAYER; invalid numeric input disables submission. Rejection preserves the draft and its confirmed comparison. REVIEW LAYER selects its existing Inspector entry.

Layer selection and selected-object staging commands remain presentation-only. Only an explicit APPLY, APPLY ORDER or governed Inspector mutation sends the existing client command through ControlHost to RuntimeHost, displays the in-flight state and refreshes the graph from confirmed authoritative state. Bitmap graphics and Production CG layers expose the bounded transform model: normalized Position X/Y, Scale, Rotation in degrees, normalized Anchor/Pivot and normalized Crop edges. The transform is deterministic and defaults reproduce the previous output exactly.

The authoritative model supports an ordered stack of 0..4 typed processing nodes per supported layer. Selecting a Processing graph node resolves its owning layer and stable node identity into the shared Inspector. The Inspector can add Color Grade or Chroma Key nodes while capacity remains, edit and enable/disable the selected typed node, move it earlier or later, and remove it. Color Grade exposes Brightness in -1..1 and Contrast/Saturation in 0..2; Chroma Key exposes 8-bit key RGB plus normalized Tolerance, Softness and Spill Suppression. Each operation derives a validated next stack from the last confirmed stack and submits that complete stack through the existing Client → ControlHost → RuntimeHost mutation path. Dedicated Program-pixel color picking, arbitrary shader/plugin configuration and free graph rewiring remain unsupported.

Legacy visual/test-layer state remains governed by its existing command and does not acquire unsupported transform or processing semantics.

## Graph authoring, link inspection and capability constraints

The advanced graph exposes local, keyboard-addressable connection inspection. A
selected edge reports its exact `FromNodeId/FromPortId → ToNodeId/ToPortId`
and observed ACTIVE/INACTIVE state. It uses the existing node selection to
open the shared Inspector for the downstream node. No edge click produces a
Control request or changes Program. The graph validates projected identities,
port direction, existing endpoints and acyclicity before drawing any live
connection. An invalid projection is shown as INVALID with edges withheld
rather than silently converting a broken route into apparent execution.
**Free connection editing remains UNAVAILABLE** until an accepted versioned
Control/Runtime contract exists.

SELECT supports explicit **Alt+drag** to move an existing node in the local
graph arrangement; **Ctrl+Arrow** nudges the selected node while graph focus
is active. The move honors the real viewport zoom and clamps positions to
the finite current canvas. Middle mouse/PAN, FIT and AUTO LAYOUT preserve their
existing behavior. Positions are not authoritative transforms, are not
persisted to production, and are never sent to ControlHost. A drag only
recalculates affected visible connection geometry. Closing ADVANCED GRAPH
releases pointer capture and removes offscreen node/edge realizations;
reopening preserves Inspector selection but not any uncommitted connection
highlight.

The selected node's capability label distinguishes a supported **STATIC
TRANSFORM / STATIC FX** (editable through the existing Inspector's validated
local parameter draft and explicit APPLY) from **OBSERVE ONLY** source/route/
sink nodes. No compositor keyframe execution contract exists here:
nonexecuting keyframes are not authored or advertised as live.

The closed production processing stack already supports at most four
Color Grade/Chroma Key nodes per authoritative layer. The advanced graph can
locally stage a selected processing node one position earlier/later through
**Stage selected FX earlier/later** in the **MORE** menu. The draft retains production revision, owning layer content
identity, layer kind and the complete confirmed typed baseline. **APPLY FX
ORDER** checks all those identities again, validates the same bounded node
set, then submits the reordered stack through the **existing** Operator →
Client → ControlHost → RuntimeHost processing-stack command. Only an identical
Runtime-observed final order can become CONFIRMED; rejection or ambiguous
outcome retains the local draft. A concurrent revision/content/stack change
marks it STALE and disables APPLY until explicit **DISCARD FX ORDER**.
Node selection, drag, connection inspection, FX draft and discard leave
Program and active Scene unchanged. Other processing operations (add,
enable/disable, remove, static parameter edits) remain owned by the shared
Inspector; this graph does not create a competing edit context.

The read-only overview exposes the observed production revision, topology
verification and FX status separately from saved Scene intent and Program
pixels. While an FX order draft exists, the draft footer keeps **APPLY FX**,
**DISCARD FX** and the revision/conflict evidence visible even when the
advanced graph is collapsed. Neither a valid projection nor a confirmed
Control revision proves physical monitor/output presentation; existing
dedicated qualification is still required.

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
