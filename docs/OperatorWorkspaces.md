<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Operator Workspaces, Multiview and Keyboard UX

## Purpose

The Operator provides registered task-oriented workspaces over one shared production state. A workspace changes presentation only: panel visibility, panel dimensions, viewer emphasis, timeline height and the current UI context. It does not own routing, media transport, recording, graphics, AI, health or output authority.

The canonical workspaces are LIVE, SCENES, MULTIVIEW, MEDIA, EDIT, COMPOSITING, OUTPUTS, HEALTH, AI and SETTINGS. The original eight IDs remain valid. Switching workspaces never creates a second production snapshot or forks product state.

## Accepted experience direction

The [Operator Experience Design Contract](OperatorExperienceDesign.md), accepted on 2026-10-17, defines the cross-workspace target: a professional media-first Live Operator, dark graphite surfaces, restrained cyan interaction accents, contextual editing and compact operational evidence. A generic infrastructure dashboard is not the primary production surface. This document describes the existing workspace model; the [experience roadmap](OperatorExperienceRoadmap.md) separates present capabilities from proposed refinements and qualification work.

The [scope decision effective 2026-10-17](OperatorExperienceScope.md) supersedes
former V1 functional deferrals. Independent analysis/scrubbing, multitrack
sequences/editing, Preview shuttle, 2D canvas/mapping, Inspector extensions and an
independent AI workspace were approved for implementation. The dedicated AI
workspace now reuses governed provider evidence and Program monitoring; the other
workstreams retain their separate delivery status. The original eight workspace
identities remain compatible with additive schema-7 destination registration.
An additive destination or changed persisted geometry needs migration/tests.
Current scope approval grants no hardware/format/security/release certification.

The four existing experience groups map onto current identifiers without replacing or migrating them:

| Experience group | Existing destinations |
| --- | --- |
| Live Operator | LIVE, SCENES and MULTIVIEW |
| Media & Timeline | MEDIA, EDIT, COMPOSITING |
| Outputs | OUTPUTS |
| System & Health | HEALTH, AI, SETTINGS |

The existing governed AI controls are integrated in AI. A 2D canvas/mapping editor remains a separately approved implementation target with real contracts/Runtime sinks; no inactive editor tab is registered.
Existing output routing and AI controls remain available. A 3D stage, new driver,
commercial codec or additional physical-support claim is not implicitly delivered. Preview remains green and Program red, with explicit role labels; cyan is for selection/navigation/focus. The normal dual-view order remains Preview left and Program right. Mockup values, extra outputs and codec/HDR badges are not capability evidence.

## Shared visual contract

All workspaces consume the shared Operator design system for typography, media/thumbnail canvases, diagnostic surfaces, monitor overlays, scrims, guides, badges and repeated panel chrome. Shared values belong in `Themes/OperatorTokens.xaml` or common theme resources; feature XAML must not create a competing palette. One-off functional geometry may remain local. Preview and Program share presentation mechanics, never command or state authority.

The 1920×1080 composition is qualification/reference geometry, not a fixed render size. The reference top bar is 60 DIP, navigation 92 DIP, panel defaults 400 / 340 / 320 and region gap 6 DIP. Existing 30/26-DIP standard/compact controls and 48-DIP media-list rows retain usable interaction geometry rather than scaling all text and targets down with the image.

### Responsive render geometry

Persisted workspace geometry remains in reference coordinates. Rendering derives from the live view container; temporary scaled values are not written to the layout store. Schema 7 retains reference geometry and adds bounded destination registration plus lossless supported migration.

| Logical viewport | Presentation scale | EDIT render height | Mode |
| --- | ---: | ---: | --- |
| 1920×1080 | 1.00 | bounded by the measured center viewport | Reference cap |
| 1600×900 | 0.83 | viewport-bounded | Standard window |
| 1536×864 | 0.80 | viewport-bounded | Scaled |
| 1280×720 | 0.67 | viewport-bounded | Scaled |
| 960×540 | 0.60 floor | one bus, viewport-bounded | Compact |

The shell derives Media Deck preview height, LIVE lower output height, Compositing preview, Health sidebar/cards, Source Bin thumbnail width, Quick Control cards and Timeline track-header width from the current container or bounded compact minimum. EDIT retains its reference proportions. Repeated output rows, monitor chrome, metric tracks and bounded aspect-ratio thumbnails may retain interaction/content dimensions.

The former Media Deck 430-px root minimum, 610-px center-column minimum, Inspector/LIVE/Output feature-root minimum widths and Multiview minimum height must not override their host allocation. Scrolling belongs to actual overflow such as Inspector properties, LIVE controls, output lists and stacked preparation content, not to hiding oversized viewer roots.

Compact mode temporarily removes side/auxiliary regions, reduces lower-region allocation and suppresses optional top-bar/timeline telemetry and empty V2/A2/A3 reference lanes before core production identity or commands. Governed V3 Graphics, V1 Video and A1 Music roles remain visible. Restoring a larger viewport restores the saved reference presentation, not a rewritten compact layout.

PerMonitorV2 awareness, layout rounding, pixel snapping, compact allocation and local vertical scrolling support the documented logical-size matrix. The normal reference window minimum is 960×500 DIP; real small work areas can require lower window minima to keep chrome reachable. Physical mixed-monitor behavior remains a separate qualification obligation. See [Operator Visual Qualification](OperatorVisualQualification.md).

## LIVE

LIVE is the media-first production console. The existing Library is left, the shared
Preview/Program pair is central, the contextual Inspector is right and the existing
Rundown/Timeline is below. Preview remains left/green and Program right/red with
equal allocation in dual mode. Detailed EDIT diagnostic cards are hidden in LIVE.
The action strip provides one explicit Set Preview, CUT and AUTO context; playback
remains in the existing Preview viewer. Scene/cue ARM/GO semantics remain separate.

Show/project identity, synchronization, readiness, Program identity and incident access
remain in the shell. Library/Inspector toggles reopen the existing panes. At compact
sizes one bus is visible, with both bus identities and immediate PVW/PGM switching;
the stored dual-view preference is retained. Controls and text are not scaled down.

## MULTIVIEW

MULTIVIEW retains the existing source bank, scene/cue preparation, compact output
evidence and Live Controls. It uses the same production commands and monitoring owner.

## AI

AI owns an independent full-center processing workspace with provider/model evidence,
descriptor-only TEST, revision- and Program-source-bound approval, explicit ON/OFF,
a shared Program monitor and separate accepted/executed/fallback state. It reuses
the existing AIEffectBrowserViewModel, OperatorViewModel and Control/Runtime authority;
there is no second inference session, clock, viewer renderer or approval path.

The viewer is an observation of **current Program**, not an A/B Original-vs-Result
test image. The reference TEST returns metadata only. Resource requirements are
advertised descriptors, not measured CPU/GPU/VRAM or costs. Unsupported packages
are shown as unavailable and are never installed automatically. See
[AI Processing Workspace](AIProcessingWorkspace.md) for the workflow, failure recovery
and qualification boundaries.

The single OperatorWorkspaceRegistry contributes stable IDs, labels, groups and
canonical viewer defaults to navigation, the command palette and persistence.
Groups are Live, Media & Timeline, Outputs & Mapping and System & Health. Only
productive destinations are registered; there is no placeholder Mapping tab.
Unknown IDs select LIVE and retain their bounded stored layout for later versions.

## EDIT

EDIT is the reference Preview/Program production workspace. At 1920×1080 its center resolves to the 1070×700 reference composition; smaller viewports derive the rendered size from the shared scale:

- The complete region is capped by the actual center scroll viewport. The upper row retains the 390 reference weight, equal Preview/Program viewers and a 6-px separator; the reference viewer split is 532 / 6 / 532.
- Shared monitor chrome retains a 40-px header, aspect-safe image and 42-px custom transport region.
- The lower row retains the 320:462:276 Scene Stack / Output Routing / System Status proportions and 6-px gaps, within the 390:304 upper/lower reference allocation.
- The compact Scene Stack reuses source projection and explicit Set Preview/CUT/AUTO; governed Scene activation remains a separate action.
- Output Routing projects confirmed Program/Preview, governed Aux and existing Clean Feed monitoring. Missing backend evidence remains UNVERIFIED.
- System Status uses Engine/Control/Runtime/Media/GPU evidence. Disk/Network/Temperature remain unavailable where their owning contract publishes no measurement; the Operator does not probe locally.
- The timeline keeps its 320-px reference height and container-derived presentation outside compact mode.

Seeking, IN/OUT, cue operations and take commands retain the existing Media Deck, Timeline and authoritative Control paths. Viewer maximize/fullscreen changes no production state.

## MEDIA

MEDIA emphasizes preparation: expanded Media Pool, governed NDI discovery/adoption, Preview-focused viewing, Media Deck, metadata/Context Inspector, import/open and IN/OUT/playback-policy preparation. It introduces no second ingest or media-management subsystem.

The Media Library already provides virtualized grid/list browsing, typed search, named Smart Bins, selection-aware context menus, bounded batches and pressure-aware thumbnail reuse. Independent position scrubbing is currently unavailable and approved TO IMPLEMENT
through bounded Runtime-owned analysis contracts; hover must not open or seek a
production deck. This is a missing implementation, not a renewed V1 scope gate. See [Media Library](MediaLibraryAssetBrowser.md).

Explicit asset preparation uses the integrated bounded non-Program verification path. File availability, a current representative conditional observation and actual resident deck readiness remain distinct. Prepare/Retry/Cancel reflect real capability and failure state; an expired observation or catalogue entry must not be presented as resident READY. Preparation does not route Program or change saved production.

NDI SOURCES projects bounded Runtime/provider discovery through Client and ControlHost. REFRESH is observational. ADOPT explicitly adds stable source identity to the durable production source catalog without changing Preview/Program. Selection never routes or takes. The Operator hosts no NDI finder, receiver, decoder or audio path; input lifecycle is confirmed Runtime/provider evidence.

## SCENES

SCENES is the preparation and scene/layer destination; LIVE also exposes the governed Scene catalog and explicit TAKE SCENE. Selection, declared routing and optional Scene compositing-state inspection are local presentation of desired content, not Runtime confirmation. COMPOSITING separately projects Runtime-confirmed layers.

TAKE SCENE submits through the existing Client/ControlHost/Runtime transaction. A declared layer stack recalls that stack with Preview/Program routing; a legacy Scene without one remains routing-only. Confirmed direct layer changes can invalidate `ActiveSceneId` when production diverges from the active Scene. Selection itself never activates or rewrites a Scene.

## COMPOSITING

COMPOSITING uses the existing Runtime-owned graphics path: Preview/Program, Media Pool graphics, confirmed ordered layers, stable bitmap/Production CG/visual-layer identities, bounded reorder/visibility/opacity, selected-node Inspector and independently supported AI composition controls.

Each supported bitmap graphics or Production CG layer has one authoritative Transform node with Position, Scale, Rotation, Anchor/Pivot and Crop, followed by zero to four ordered typed Color Grade or Chroma Key nodes. The graph is Layer → Transform → processing stack → Composite. Stable node identity preserves selection across ordinary refreshes.

The Inspector exposes Color Grade Brightness/Contrast/Saturation and Chroma Key RGB/Tolerance/Softness/Spill Suppression with bounded add/move/remove operations. Mutations submit a complete validated next stack through Client → ControlHost → RuntimeHost and remain pending until confirmed. Drafts, local selection, pan and zoom never become production truth. Commands remain unavailable when stale, disconnected, busy or Runtime is not ready.

Source/routing topology is read-only. Program-pixel color picking, arbitrary shaders, plugin-defined/unbounded graphs and free rewiring are unsupported. No second compositor, renderer or production store is introduced. See [Compositing Node Graph](CompositingNodeGraph.md).

## OUTPUTS

OUTPUTS presents governed Program/Aux roles, authoritative Preview routing, Clean Program monitoring, recording and output-health evidence. CUT/DISSOLVE uses the existing Preview path and becomes SAFE READ-ONLY when the shared mutation gate is unavailable. Aux changes use the governed output-role command, not a UI-local route.

Display only confirmed format, target and provider evidence. Runtime CPU/memory, available NVIDIA GPU/VRAM and measured Program FPS use the shared performance projection. Missing telemetry remains UNAVAILABLE. Bounded frame-time/cadence/drop/hardware mini histories and the permanent shell status bar reuse the same observations without another poller or new thresholds.

The existing Audio Mixer supports up to eight confirmed source strips, four bus strips, a compact source×bus matrix, typed three-band source EQ and bus compressor/sample-peak limiter drafts, L/R peak/reduction/clipping evidence, and Program/Aux audio-bus assignments. Compact/Detailed session presets, named fine-step draft faders and strip-local Apply/Discard preserve the distinction between drafts and confirmed state. Pending presentation coalesces without resetting selection; stale meters stay explicit. The existing 200-ms management path remains; no local WPF audio analysis, generic DSP graph, LUFS or true-peak claim is added.

See [Output Routing, Health and Performance](OutputRoutingHealth.md) and [Runtime Performance Status Bar](RuntimePerformanceStatusBar.md). Local Program, Clean Program monitoring and external-send evidence must remain distinct in every compact layout.

## HEALTH

HEALTH consumes the central event-driven snapshot provider for CPU, Memory, GPU/VRAM, Media, Decoder, Compositing, Output, Frame Timing, Control, Runtime, providers and monitoring. Subsystem health is not global Runtime Readiness. Show duration, last successful check, measurements, recovery and optional detail without creating another authority. Offer recovery only through an existing safe command. See [Health Center](HealthCenter.md).

## SETTINGS

SETTINGS retains shell configuration and compact lifecycle/system surfaces. It owns neither Runtime configuration nor a parallel diagnostics state. PASS/FAIL/UNVERIFIED semantics remain unchanged. Layout, template and shortcut preferences are presentation data, not production settings.

## Quick Controls

Quick Controls pin stable Inspector property identifiers, never authoritative values. The bounded initial set covers transition duration, clip playback policy, selected audio gain/mute, graphics visibility/position/scale and AI enable state, with at most eight pins. Every mutation reuses the existing Apply/Toggle command and remains desired until normal authoritative confirmation.

Pins are stored in `rtaime/operator-quick-controls.json` below the user's local application data. Unknown/incompatible data is ignored safely.

## Keyboard-first operation

`OperatorKeyboardCommandRegistry` centrally resolves window-level shortcuts, checks collisions and supplies discoverable help. The following table describes the base production/view bindings, not every supported shortcut; active validated profiles and the central registry determine the effective gesture.

| Shortcut | Base operation |
| --- | --- |
| Space | Preview media Play/Pause when the loaded source is confirmed Preview |
| K / S | Preview media Pause / Stop |
| I / O | Set Preview media IN / OUT |
| M | Add named Preview media cue |
| Delete | Delete selected Preview cue when available |
| Up / Down | Previous / next Preview cue |
| Enter | AUTO confirmed Preview to Program |
| Ctrl+Enter | CUT confirmed Preview to Program |
| R | Start/stop Program recording when available |
| Ctrl+F | Focus Media Library search |
| Ctrl+P | Set selected source to Preview |
| F5 | Synchronize authoritative state |
| F11 | Operator fullscreen/windowed |
| Esc | Contextual cancel/restore, then exit fullscreen |
| Ctrl+1 / Ctrl+2 / Ctrl+0 | Maximize Preview / Program / restore dual viewers |
| Ctrl+K | Commands palette |
| F9 | Explicit Show Control GO when armed and eligible |

Timeline-local Page Up/Page Down remains in the timeline. J currently steps one Preview frame backwards and L plays at normal rate.
Both commands require the confirmed Preview source and exclude an on-Program
source. Reverse/multirate Preview shuttle is approved TO IMPLEMENT; extend real
rate/direction/cancel semantics through the existing shared registry. Text-entry controls suppress production shortcuts; non-destructive synchronization/view commands retain their allowed scope. Repeat key-down events must not repeat production actions. Buttons and keyboard paths share command availability and authoritative validation.

## Shortcut discoverability

Button labels/tooltips and Help use the active registry/profile. The existing profile editor supports validated customization, import/export, collision handling and reset; the earlier no-keyboard-editor limitation is superseded by [Commands and Shortcut Profiles](OperatorCommandsAndShortcuts.md). Do not add a second shortcut editor or hard-coded help registry.

## Clean Program monitoring

Clean Program presents the existing Runtime-derived Program image in a dedicated window/display, using the same `OperatorMonitoringViewModel.ProgramImage` as the in-application monitor. It has no production controls over the image, can start/stop and restore safely, supports established Windows display targeting and windowed/fullscreen monitoring, and does not route or change physical Program output.

It is monitoring presentation, not output authority.

## Per-workspace layout persistence

Schema 7 stores each workspace's left/right widths, lower height, collapsed state, center-maximized state and viewer mode. Selected workspace, fullscreen preference and normalized window placement remain top-level UI preferences. Legacy names migrate to accepted destinations without production state. Supported v2–v6 geometry, collapse/viewer choices, templates and placement migrate without a reset. Missing fields use established defaults and invalid dimensions are clamped. Loading does not write. Explicit user actions save atomically; a newer schema is opened with safe presentation defaults and saving is blocked so its original bytes survive. Up to 32 valid destination layouts are retained; unsupported destination IDs remain inactive. Older executables cannot consume schema 7; export/import rejects unknown future schema versions explicitly.

SAVE LAYOUT persists the active presentation. Switching captures the outgoing workspace and restores the destination's own preferences. LAYOUT RESET affects only the selected workspace. Missing, corrupt, non-finite, out-of-range or incompatible data recovers to safe defaults. The store remains `rtaime/operator-layout.json` below local application data, with serialized temporary-sibling/atomic replacement writes.

| Workspace | Left | Right | Lower | Collapsed by default | Viewer |
| --- | ---: | ---: | ---: | --- | --- |
| LIVE | 400 | 340 | 320 | none | DUAL |
| MULTIVIEW | 400 | 340 | 320 | none | DUAL |
| AI | 400 | 340 | 320 | none | PROGRAM |
| EDIT | 400 | 340 | 320 | none | DUAL |
| MEDIA | 400 | 340 | 320 | none | PREVIEW |
| SCENES | 400 | 340 | 320 | none | DUAL |
| COMPOSITING | 400 | 340 | 320 | none | DUAL |
| OUTPUTS | 400 | 340 | 320 | none | PROGRAM |
| HEALTH | 400 | 340 | 320 | none | PROGRAM |
| SETTINGS | 400 | 340 | 320 | none | PROGRAM |

## Docking, focus and visual polish

The three production-shell splitters share focus-visible styling, keyboard access and arrow-key tooltips. Product controls use the shared custom presentation while retaining underlying WPF focus/input mechanics as appropriate. Loading/error/empty states use shared styles, not local palettes.

Production XAML retains the no-decorative-Storyboard policy: tally, focus and eligibility change immediately. Proposed eased transitions in the experience contract require a separate shared-motion/reduced-motion and policy qualification change; this documentation does not enable them.

## Authority and performance boundaries

Workspace changes, selection, maximize/restore and docking do not mutate production. Multiview/Clean Program reuse monitoring. Quick Controls store identifiers, not truth. Existing command availability gates shortcut execution. Management/monitoring cadences, resource bounds and authoritative snapshots are unchanged. No second transport, renderer, decoder or polling loop is introduced for visual polish.

## Operator workflow

1. Prepare media and IN/OUT in MEDIA; review actual availability/preparation evidence.
2. Refine cues and timing in EDIT.
3. Prepare scenes/layers in SCENES or COMPOSITING.
4. Pin frequently edited properties through the existing Inspector.
5. Use LIVE for confirmed Preview/Program, explicit actions and Quick Controls.
6. Open Clean Program for dedicated monitoring when needed.
7. Use AI for provider/model discovery, descriptor-only testing and explicitly governed Program activation.
8. Use OUTPUTS for provider/recording/audio evidence, HEALTH for diagnosis and SETTINGS for shell preferences.

One underlying production state remains active throughout.

## Production monitor operation

Preview and Program role controls delegate common header/frame/guides/timecode/unavailable/overlay presentation to `RtaimeMonitorPresentation`. Role-specific header/status/footer and command ownership stay separate. MEDIA and standard-viewer workspaces reuse those roles rather than another playback path.

Preview alone exposes Media Deck transport, gated to a loaded source confirmed as Preview. Program has no Preview transport authority. Local Fit/Fill/pixel-inspection/zoom/pan/ROI/channel controls follow [Operator UI V1](OperatorUiV1.md); guide overlays remain presentation-only. FULL captures the previous viewer/center/fullscreen context and restores it on Escape or the established toggle without restarting decoding, monitoring or Runtime.

The experience contract requires honest [image-detail semantics](OperatorExperienceDesign.md#honest-image-detail-contract): source/Program format, delivered monitoring dimensions and physical target are different. Pixel-perfect inspection of a downscaled monitoring image is not original-source detail. Show only supplied metadata and the actual GPU/fallback path; unavailable color space/bit depth remains unavailable.

Program ON AIR describes the local confirmed Program bus/monitor, not external transmission. Clean Program is separate monitoring; external transmission needs its own provider evidence.

## Show Control workflow

SCENES is the primary Show Control authoring/preparation destination; the same SCENES & CUES region supports production execution. Prepare a cue list in Show Control EDIT, save and explicitly select it in ControlHost, ARM it, inspect the displayed cue/action, then execute one cue with GO/F9. Progress requires explicit GO; CANCEL unexpected state or acknowledge backend `RecoveryRequired` through the supported path.

Cue selection never changes Program. The Operator neither times cues nor infers completion. Asset cue markers and Show Control actions remain separate domains. See [Show Control Cue Sequencing](ShowControlCueSequencing.md).

## Optional docking and detached panels

Built-in layouts remain the default. SETTINGS exposes BUILT-IN / DOCKING LAYOUT; Ctrl+Shift+D toggles the same presentation. Existing TOOLS, MONITORS, INSPECTOR and TIMELINE view instances occupy bounded LEFT/CENTER/RIGHT/BOTTOM split groups. Drag tabs or use named group buttons to combine them; splitters support mouse and arrows. No arbitrary plugin panels or extra control/monitoring sessions are created.

FLOAT/Ctrl+Shift+W detaches the focused panel into an owned secondary window. Closing it returns the panel to its remembered group, not engine shutdown. Main and detached windows share models, source identities, command instances, input suppression and mutation guards. Moving/selecting a panel never takes it.

| Shortcut | Docking operation |
| --- | --- |
| Ctrl+Shift+N | Cycle panels |
| Ctrl+Shift+F | Focus/restore selected panel |
| Ctrl+Alt+Left/Up/Right/Down | Move to LEFT/CENTER/RIGHT/BOTTOM |
| Ctrl+Shift+R | Reset selected workspace to built-in layout |

Each workspace keeps an additive version-1 docking component inside schema 6: four known panel identities, at most four groups/windows, bounded split ratios and selected tabs. Missing components retain existing geometry and use built-in mode. Duplicate/unknown panels, future component versions or invalid geometry recover safely. Layout reads are capped at 1 MiB.

Compact mode temporarily presents docked groups as one tab group without overwriting stored splits. Floating placement uses actual monitor/work-area coordinates and DPI. Missing displays fall back to an available primary work area, sizes/offsets are rescaled and the complete window is clamped to a real work area; virtual-desktop gaps are not displays. The primary window uses the same recovery. Small work areas can reduce window minimums so chrome remains accessible.

To recover a missing panel, cycle with Ctrl+Shift+N, dock centrally with Ctrl+Alt+Up, or reset with Ctrl+Shift+R. Primary navigation/status/transport stay outside docking. Operator shutdown closes owned windows and removes display subscriptions before normal monitoring/control teardown.

## Context Inspector editing

[Context Inspector](ContextInspector.md) owns numeric ranges/units, Alt-drag precision, fine/coarse keys, reset, pinned metadata, mixed summaries, compatible attribute drafts and explicit Apply. [Inspector draft transactions](OperatorCommandsAndShortcuts.md#inspector-draft-undo-and-typed-attribute-transfer) add grouped local draft undo and typed transfer. They do not undo confirmed live operations. Compatible multi-object Inspector editing is approved TO IMPLEMENT. Its owning
backend must provide revision-bound transaction/validation semantics before live
Apply; current single-context commands must not masquerade as bulk transactions.

## Commands and shortcut profiles

Ctrl+K or Commands opens the shared palette in main/detached windows. It searches workspace/library/viewer/draft/production actions and explains unavailable results. Production activation requires the explicit Execute button. Profiles, recent references, stable-ID favorites and contextual help reuse [Commands and Shortcut Profiles](OperatorCommandsAndShortcuts.md); reference opening only inspects and does not execute.

## Show checks and activity

Open Show checks from navigation or search Show preflight in Commands. The [shared review view](ShowPreflightAndActivity.md) combines advisory asset/cue/output/audio/AI/durable-state evidence, bounded correlated requests and existing notifications. Program/Aux, recording-target and asset preparation evidence remain separate. Review links lead to existing workspaces. Acknowledgement or a reminder pause does not resolve an incident, change health or authorize launch. Recovery and support export stay in their established services.

## Focus, Cinema and deterministic restore

SETTINGS and Commands expose FOCUS, CINEMA and RESTORE. Ctrl+Shift+F focuses a panel; Ctrl+Shift+C toggles Cinema using existing Program monitoring. Escape restores the prior mode even from a windowed start; numeric-field Escape retains first chance to cancel an edit. F11 exits a transient mode and toggles displayed fullscreen; Ctrl+1/2/0 exits it before applying the requested viewer. Monitor FULL retains its established path.

Entry captures workspace, built-in/docked/tabbed/floating arrangement, viewer, center and fullscreen/monitor-fullscreen context. Focus presents the existing panel centrally. Cinema temporarily uses the existing MEDIA/MONITORS Program presentation, including from specialized workspaces; the original workspace remains the saved/template destination. Restore returns its arrangement and reachable focus. Workspace switch/reset, explicit dock/float and template Apply leave transient mode first. Temporary projections never overwrite ordinary saved layout/fullscreen preferences, including Save and shutdown.

Shared panel chrome shows Program identity, connection, safety and Restore. Cinema cannot TAKE, route, seek or create another renderer.

## Named workspace layout templates

Open LAYOUT TEMPLATES in SETTINGS or the named Commands entry from any workspace/detached panel. One owned preference editor captures the actual current workspace rather than forcing SETTINGS; it suppresses production shortcuts and restores reachable focus on Escape/close.

Save a unique name of up to 64 characters; at most sixteen templates are retained. Selection inspects the destination; Apply explicitly switches and restores only that workspace, leaving the other seven layouts intact. Delete removes only the local definition. Templates remain an additive component in schema-6 `operator-layout.json`, not another database. Missing data means an empty list; invalid/duplicate entries normalize out and failed writes remain visible.

Export uses `rtaime.operator-workspace-template`, document version 1 and source layout-schema version, with name, canonical workspace and normalized presentation/docking/floating preferences only. It contains no production sources, media paths, credentials or command state. Writes use temporary-sibling atomic replacement. Import validates asynchronously within a strict 1-MiB bound, adds a unique name and never applies or overwrites automatically.

Supported v2–v6 migration reuses the established policy: pre-v4 geometry resets, v4–v6 values normalize, legacy names map and future versions reject. Importing a full legacy layout captures only its selected workspace. File open/read/parse/export stays off the UI thread in two bounded slots; shutdown cancels/drains them before final persistence/teardown. Floating windows reuse real-work-area/DPI recovery.

## Detached-view retention and qualification

Unrelated tab/dock changes retain each unchanged floating native window, content, ViewModel and monitoring subtree; they must not unload/reparent it. Closing resolves the current return group. Shutdown closes all owned windows and unregisters display events before monitoring teardown.

Existing `WorkspacePresentationTemplateTests` and real-window qualification cover transient-save exclusion, independent layouts/reset, explicit template Apply, migration/roundtrip, bounds and malformed/future/oversized data, duplicate rejection, cancelled export/write failure, retained window/content identity, zero monitor unloads on unrelated changes, visible Program status, focus restore and unchanged Preview/Program identities. Synthetic 100/125/150/200% DPI/off-screen/topology tests remain required.

Hosted geometry and window tests are not physical mixed-monitor hotplug, screen-reader/IME, touch or GPU scan-out qualification. Retain those as UNVERIFIED until actual reference evidence is recorded. The [experience roadmap](OperatorExperienceRoadmap.md#validation-contract) extends these existing checks with connected visual review, state honesty and combined operator journeys; it introduces no parallel qualification framework.

### Confirmed cue operation in MULTIVIEW


MULTIVIEW shows local selection separately from confirmed NEXT/PVW and LIVE/PGM. CUT/AUTO name the confirmed Preview target, Scene TAKE names the selected Scene, RUNDOWN GO names the prepared/pending rundown target, and SHOW GO names the armed Show Control cue/action. Disabled actions retain a reason and next step. Rundown is available from the Scenes & Cues tabs; order dragging and undo stay local until Save. Catalogue availability is separate from prepared resident readiness. See [Live operation](LiveMultiviewAndSceneControl.md#confirmed-live-console-operation) and [Rundown drafts](ProductionRundown.md#live-operator-console-and-local-order-drafts).
