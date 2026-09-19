<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Layered Timeline

## Purpose

The Operator timeline is a first-class production workspace for frame-accurate media navigation, cue operation and bounded editing. It is an editor/client surface only. Runtime remains the execution authority, and confirmed Media Deck and marker snapshots remain the source of truth for transport, IN/OUT and cue state.

## Time model

Timeline state is represented in integral frame numbers. Timecode labels are derived from the confirmed media frame rate. Nominal frame-field formatting remains compatible with the admitted local-media envelope through 240 fps. Zoom and scrolling calculate a visible frame range; pointer positions are converted back to frames only at the interaction boundary.

The viewport supports:

- a visible time ruler;
- an explicit Program playhead;
- horizontal scrolling;
- 1x through 32x horizontal zoom;
- Fit;
- bounded snapping to IN, OUT and cue frames;
- current, duration and remaining timecode.

Playback observation does not recreate timeline clips or cue objects on every refresh. Stable projections are rebuilt only when the loaded source, marker range or cue metadata changes.

Rendering is additionally viewport-bounded. Tracks retain their stable backing projection, while `VisibleItems` and `VisibleCues` expose only objects intersecting the current visible frame range. Zoom and horizontal scroll refresh that bounded presentation without duplicating production state.

## Semantic tracks

The workspace exposes the following semantic tracks:

- Video;
- Graphics;
- Overlay;
- Audio;
- AI;
- Control;
- Cue.

A track name is a UI semantic category, not a promise that timed backend automation exists for that category.

The loaded Media Deck clip is projected onto the Video track using its confirmed source identity and effective IN/OUT range. Existing Audio and Graphics resources may be dropped on their matching semantic tracks as clearly marked `PROJECTED` metadata spanning the current media duration. These timeline-only projections are cleared whenever the loaded media asset or source context changes. This changes only the Operator projection and does not create a production mutation.

No timed Graphics, Audio, AI or Control mutation is invented by the timeline. AI and Control tracks therefore remain empty until authoritative product semantics exist.

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
| Loaded Clip | Video | Reuses the existing loaded Media Deck context and selects its authoritative timeline item. |
| Audio | Audio | Adds a `PROJECTED` Operator metadata item only. |
| Graphics | Graphics or Overlay | Adds a `PROJECTED` Operator metadata item only. |
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

The current governed contracts also expose no clip-move edit operation, timeline undo/redo history, section/show-marker domain, or reusable audio-waveform projection. The Operator therefore does not synthesize those capabilities. They can be connected later when an existing authoritative command/history/data source is available.

These capabilities must not be implied by disabled or decorative controls.
