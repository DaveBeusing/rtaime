<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Production Rundown and Playlist

## Purpose

The production rundown is a bounded playout orchestration layer for ordered media, Scene, graphics, audio-routing and hold items. It is not a nonlinear editor and does not introduce a second Program authority or scheduler.

The rundown is owned by ControlHost. Operator selection and authoring state remain presentation state until an explicit save or execution command crosses `rtaime.Client` and is confirmed by ControlHost.

## Authority model

The execution path is:

`Operator -> rtaime.Client -> ControlHost RundownCoordinator -> existing Show Control / Media Deck / Scene / graphics / audio-routing commands -> existing Runtime execution`

The RundownCoordinator never calls Runtime or a provider directly. It adapts one prepared rundown item into the existing bounded Show Control action model and therefore reuses existing production authority, validation, journaling and recovery behavior.

## Domain and bounds

A rundown has a stable `RundownId`, a version, a name and between 1 and 256 stable `RundownItemId` entries.

Supported item types are:

- persistent Media Library clips with a target production source;
- Scene activation;
- bounded bitmap/Production-CG layer visibility;
- FOLLOW_VIDEO or single-source BREAKAWAY audio routing;
- bounded frame-domain hold/wait.

Media items use existing CUT or DISSOLVE semantics. DISSOLVE duration is bounded by the existing Show Control transition limit. Media items may use deterministic `AutoOnMediaEnd`; other item kinds do not synthesize media-completion semantics.

Repeat behavior is explicit and bounded. Unbounded looping and arbitrary conditions are not supported.

## Persistence and editing

Rundown JSON and its storage version are stored inside the existing durable show-project document. Stable item identities survive rename, reorder, save and restart.

Authoring supports bounded insert, delete and reorder. Optimistic storage-version checks reject stale saves rather than silently overwriting newer authored state.

Persistent media entries reference Media Library asset identity rather than raw local paths. Scene, graphics and audio references are validated before execution.

## Prepare and execution

`PREPARE` selects one item, translates it into the existing Show Control action model, selects that generated cue list and arms it. Preparation does not locally claim Program state.

`GO` executes only a prepared item. `NEXT` and `PREVIOUS` navigate deterministically and prepare the target item. `HOLD` stops automatic progression without inventing a local Program state.

A failed governed action stops rundown execution and exposes a failure state. Production errors are never silently skipped.

## Deterministic media auto-advance

For `AutoOnMediaEnd`, ControlHost observes authoritative Media Deck state. Completion is accepted only after the expected asset has been observed playing and then reaches confirmed `Ended`.

The coordinator records the execution revision and current item identity when arming the observer. A later completion signal can advance only if those values still match. Manual navigation or a newer execution invalidates the old observer, preventing a stale completion from double-firing a Program mutation.

End-of-list and repeat behavior use the bounded rundown policy. No UI timer advances production.

## Recovery

Rundown recovery is derived from durable authored state and confirmed Show Control evidence. Local elapsed time is never treated as proof of completion.

If ControlHost cannot prove whether an action completed before interruption, the rundown enters `RecoveryRequired`. The Operator must explicitly resume or cancel. Recovery does not automatically replay an action that may already have committed.

## Operator workflow

The persistent lower workspace keeps the existing multi-track timeline and adds a compact Rundown surface beside it.

The surface distinguishes local selection from confirmed execution state and presents `PREPARED`, `CURRENT`, `NEXT`, `FAILED` and recovery evidence. Commands cross the Client SDK; the Operator does not mutate Runtime or provider state.

Media authoring uses the currently selected persistent Media Library clip plus the selected production source. Scene, graphics and audio items reuse the corresponding existing Operator selections and governed production models.

## Timeline relationship

The multi-track timeline remains the frame-oriented presentation and Media Deck interaction surface. The rundown provides ordered show/planning semantics. It does not convert reserved V2/A2/A3 rows into editable tracks unless a real backend execution model exists.

Existing V1/A1 Media Deck semantics remain authoritative. Additional graphics/audio semantics are represented by rundown items only where the established graphics and audio-routing contracts can execute them. Unsupported overlaps or resource semantics must be rejected rather than painted as committed UI-only clips.

## Non-goals

The rundown does not provide blade editing, arbitrary clip movement, destructive source editing, nested timelines, an advanced waveform editor, generic scripting, unbounded loops, collaborative editing or a comprehensive undo/redo system.
