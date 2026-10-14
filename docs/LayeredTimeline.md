<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Layered Timeline

## Purpose

The Operator timeline is a first-class production workspace for frame-accurate media navigation, cue operation and bounded editing. It is an editor/client surface only. Runtime remains the execution authority, and confirmed Media Deck and marker snapshots remain the source of truth for transport, IN/OUT and cue state.

## Time model

Timeline state is represented in integral frame numbers. Timecode labels are derived from the confirmed media frame rate. Nominal frame-field formatting remains compatible with the admitted local-media envelope through 240 fps. Zoom and scrolling calculate a visible frame range; pointer positions are converted back to frames only at the interaction boundary.

The viewport supports:

- a visible time ruler;
- distinct pending, confirmed media and optional draft edit positions;
- horizontal scrolling;
- 1x through 32x horizontal zoom;
- Fit;
- optional prioritized snapping to cues, clip boundaries and the confirmed playhead;
- current, duration and remaining timecode.

Playback observation does not recreate timeline clips or cue objects on every refresh. Stable projections are rebuilt only when the loaded source, marker range or cue metadata changes.

Rendering is additionally viewport-bounded. Tracks retain their stable backing projection, while `VisibleItems` and `VisibleCues` expose only objects intersecting the current visible frame range. Zoom and horizontal scroll refresh that bounded presentation without duplicating production state.

## Reference layout

At the 1920×1080 reference surface, the lower workspace occupies x=98–1573 and y=760–1079, for an exact 1476×320 timeline region. The timeline itself owns that full lower span; the legacy separate bottom transport bar is collapsed and its active Play/Pause, Stop, current-time and fullscreen affordances are integrated into the 44-pixel timeline toolbar.

The vertical contract is:

- 44 px timeline toolbar;
- 30 px frame ruler and cue-marker lane;
- 42 px V3 Graphics;
- 42 px V2 Video;
- 42 px V1 Video;
- 40 px A1 Music;
- 40 px A2 SFX;
- 40 px A3 VO.

The track-header column is 238 pixels wide at the reference viewport and is rendered from the shared viewport scale with a bounded 170-pixel minimum. The remaining width is the frame canvas. The ruler uses the existing frame-to-pixel converter, the Program playhead uses a 2-pixel cyan line with a cyan head, and visible named cues are projected above the tracks rather than consuming a dedicated cue row. Compact viewports collapse optional sequence/link context badges and the reserved empty V2 Video, A2 SFX and A3 VO reference lanes while retaining the governed V3 Graphics, V1 Video and A1 Music roles plus transport, timecode, cue and production-relevant controls.

## Semantic tracks

The six visible reference lanes are presentation roles over the existing timeline categories:

- **V3 Graphics** — existing Graphics resource projection;
- **V2 Video** — reserved video presentation lane; no second governed video-track edit authority exists in V1;
- **V1 Video** — the authoritative loaded Media Deck clip and effective IN/OUT range;
- **A1 Music** — existing Audio resource projection;
- **A2 SFX** — reserved audio presentation lane; no separate SFX routing/edit contract exists in V1;
- **A3 VO** — reserved audio presentation lane; no separate VO routing/edit contract exists in V1.

The underlying TimelineTrackCategory values remain intact so existing selection, Inspector and compatibility code does not acquire a second domain model. Only Graphics accepts Graphics-resource drops, Audio accepts Audio-resource drops, and V1 Video accepts the loaded Clip context. Reserved V2/A2/A3 lanes remain empty until governed product semantics exist.

Named Media cues remain first-class timeline objects, but they are rendered in the ruler marker lane rather than as a seventh visible track. Cue identity, selection, navigation and Inspector integration remain unchanged.

No timed Graphics, Audio, AI or Control mutation is invented by the timeline.

## Toolbar capability boundary

The toolbar exposes the current timeline context, SELECT mode, snapping, observed frame rate, Preview Play/Pause and Stop, previous/next cue, zoom, Fit, Operator fullscreen, current timecode and named cue creation through existing commands.

The product currently exposes no governed sequence switching, clip-link command, Blade/Cut edit command or alternate timeline frame-rate mutation. Those unavailable capabilities are identified as context/status only and are not wired to decorative commands.

## Cues

Media cues are explicit timeline objects with:

- stable cue identity;
- name;
- frame;
- derived timecode;
- cue type;
- optional source reference.

The current implementation exposes the real existing Media cue semantics. The timeline includes explicit named cue creation at the confirmed playhead frame. Previous Cue, Next Cue and Jump To Cue seek through the existing media timeline controller. Page Up and Page Down provide keyboard navigation; double-clicking a cue jumps to it. Selecting a cue exposes Jump, Rename and Delete in the shared Inspector through the existing Media Deck marker commands.

Other cue categories are not presented as active commands until matching backend semantics exist.

## IN / OUT

Confirmed Media Deck IN and OUT markers are rendered across the timeline. The Video item represents the active effective playback region.

IN and OUT markers are trim handles. Dragging a handle first updates a local visual preview using the same frame-to-pixel and snapping rules as the final operation. No marker mutation occurs during that preview. Mouse release resolves the final integral frame and sends the existing marker command path; Escape or lost mouse capture cancels the preview. The Operator does not commit a local range independently. Invalid ranges are rejected by the authoritative marker semantics and are never forced into production state.

## Media Pool drag and drop

Drag/drop is semantic and bounded:

| Media Pool resource | Valid track | Result |
| --- | --- | --- |
| Loaded Clip | V1 Video | Reuses the existing loaded Media Deck context and selects its authoritative timeline item. |
| Audio | A1 Music | Adds a `PROJECTED` Operator metadata item only. |
| Graphics | V3 Graphics | Adds a `PROJECTED` Operator metadata item only. |
| Source / Composition / mismatched resource | None | Drop is rejected. |

Projected items are intentionally not shown as `COMMITTED`. They provide timeline context for existing product state while preserving the backend boundary.

## Inspector integration

Selecting a Video item, projected resource or cue populates the existing right-side Inspector. No track-specific property dialog exists.

Timeline items support additive Shift selection and Ctrl toggle selection. One item remains the primary Inspector context, while a multi-selection is projected into the same Inspector using common values and explicit `MIXED` values. The selected item, its active track and a focused cue use distinct presentation states, and stable selection identities are restored after marker/source projection rebuilds when the same objects remain available. This selection state is Operator presentation state only and does not create an edit authority.

Committed media values are identified as `COMMITTED`. UI-only resource projections remain `METADATA`. Existing desired configuration controls continue to require their existing explicit apply/command paths.

## Interaction reference

- Left / Right: one frame backward / forward.
- Home / End: media start / end.
- Page Up / Page Down: previous / next cue.
- Ctrl++ / Ctrl+-: zoom in / out.
- Ctrl+Shift+F: fit timeline.
- Ctrl+mouse wheel: zoom.
- Shift+mouse wheel: horizontal timeline scroll.
- Empty track drag: bounded seek.
- Timeline item click: replace the current item selection.
- Shift+item click: add an item to the current timeline selection.
- Ctrl+item click: toggle an item in the current timeline selection.
- IN / OUT handle drag: preview locally, then commit through the explicit trim command.
- Escape during trim drag: cancel the local trim preview.
- Cue name + + CUE: add a named Media cue at the confirmed playhead frame.
- Cue double-click: jump to cue.
- Selected cue Inspector: jump, rename or delete through existing marker commands.

## Performance boundary

The timeline does not perform disk or network I/O from rendering code. Media observation remains on the existing bounded management cadence. Track and cue projection objects are not allocated per playback frame. Only viewport-intersecting track items and cues are exposed to the WPF item presenters. Pointer seeking remains coalesced through the existing timeline controller.

## Explicit non-goals

The V1 timeline does not claim:

- ripple, roll or slip editing;
- nested timelines;
- advanced keyframe curves;
- multi-camera editing;
- distributed show control;
- timed automation without an authoritative backend contract.

The current governed contracts also expose no clip-move edit operation, timeline undo/redo history, section/show-marker domain, transition-domain projection or reusable audio-waveform projection. The Operator therefore does not synthesize those capabilities. The mockup palette reserves quieter waveform and transition treatments for a future governed projection, but no fake waveform or transition graphic is rendered today.

Unavailable edit capabilities are presented only as explicit N/A/context state where the mockup requires their location; no disabled button is wired to a synthetic production command.
## Relationship to Show Control

Media timeline cues remain media-domain markers and retain their existing seek/jump semantics. Show Control may reference a named media cue through the existing Media Deck path, but it does not turn the timeline into a second automation authority or NLE scheduler.

Show Control sequencing, GO progression, frame-domain waits and recovery are owned by ControlHost and documented in `docs/ShowControlCueSequencing.md`. Timeline row selection and Show Control cue selection remain presentation-only until their explicit execution command is invoked.

## Production rundown integration

The persistent lower workspace now hosts the governed Production Rundown beside the existing layered timeline. The rundown owns ordered show/playlist preparation and playout semantics while the timeline remains the frame-oriented Media Deck and cue presentation surface.

Rundown Media items reference persistent Media Library asset identity and a governed production source. Scene, graphics and audio-routing entries reuse their established production models. Reserved V2/A2/A3 lanes remain non-authoritative unless a real backing execution model exists; the Operator does not turn those rows into synthetic NLE tracks.

Rundown selection is local presentation state. PREPARE, GO, NEXT, PREVIOUS and HOLD cross `rtaime.Client` into ControlHost and reuse Show Control execution primitives. Deterministic media auto-advance is based on confirmed Media Deck completion, never on a UI timer. See `docs/ProductionRundown.md`.

## Draft cursor, snapping and reversible editing

Existing frame stepping, IN/OUT commands, named cues, trim preview, stable selection, zoom, bounded seek coalescing and resource projections remain in place. The new controls extend these paths.

### Magnetic snapping

Frame quantization is always integral and uses the existing nearest-frame rounding. SNAP toggles optional magnetic targets. Within an eight-DIP pointer radius, capped at ten frames, the nearest candidate wins. Equal distances prefer cue, clip boundary, then confirmed media playhead; equal candidates of one kind prefer the lower frame. The visible feedback names the target kind and exact timecode. Candidate inspection is bounded to 4,096 entries.

Alt-drag uses one tenth of the pointer displacement for precise seeking or trim preview. Snap can be turned off for single-frame precision. Coordinates remain device-independent; synthetic 100/125/150/200% mapping is tested. Physical mixed-DPI interaction remains UNVERIFIED until reference-platform execution.

### Edit and confirmed positions

EDIT toggles a local draft cursor. In this mode pointer movement and Left/Right change only the edit frame. Shift+Left/Right moves ten frames; Home/End uses the asset bounds. Escape or capture loss cancels the local drag. Playback can continue without adopting the edit cursor as a confirmed position.

The yellow edit line is separate from the confirmed transport line and pending seek display. PREVIEW and PROGRAM labels require current routing evidence and a matching loaded source. Program position is shown only when the confirmed media transport also reports Program participation. An independent Program position that the current contract does not expose is explicitly unavailable.

The diamond action places the edit cursor on a selected cue or clip start without seeking. The adjacent seek action explicitly seeks Preview to the draft cursor through the existing controller; it is disabled for stale/unavailable routing, another Preview source or Program participation. Marker and seek commands remain confirmed operations, with no implicit undo.

J requests one Preview frame backwards, K retains the existing Pause command, and L requests normal-rate Preview playback. J/L are guarded to the confirmed Preview source and excluded while that source participates in Program. Reverse/shuttle speed is not exposed by the current contract. Text entry retains its typing behavior and TAKE shortcuts retain their existing guards.

### Selection and authoring history

Ctrl-click toggles item selection. Shift-click selects the contiguous range from the primary item in track/item order. Ctrl+A selects current timeline items; the clear action clears selection. These are presentation operations. Cue double-click and Page Up/Down retain the existing explicit transport paths.

The ruler provides custom Undo/Redo, cursor-to-selection, explicit seek and clear buttons without changing the 44-pixel toolbar or 30-pixel ruler contract. Ctrl+Z/Ctrl+Y apply only when focus is inside the timeline and outside text entry.

Undo/Redo retains at most 32 local cursor/resource-projection snapshots. A drag is one history entry. Local resource projections are capped at 256. Confirmed seek/trim/marker changes are never replayed or rolled back. Asset/source or marker/cue changes invalidate history and active gestures; a completed explicit seek clears history. Pointer capture loss cancels queued unsent seek intent; an in-flight or confirmed transport command is not rolled back.

Rundown has its own authoring history described in [Production Rundown](ProductionRundown.md). The two scopes never consume each other's shortcuts. Existing cue creation/rename/deletion and transport execution are not authoring-history entries.
