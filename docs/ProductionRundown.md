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

Media items use existing CUT or DISSOLVE semantics. DISSOLVE duration is bounded by the existing Show Control transition limit.

Every item carries one versioned follow action:

- `Manual` — successful completion leaves the rundown held;
- `PrepareNext` — prepares and validates the bounded next item without Program mutation;
- `AutoGoNext` — prepares and executes the bounded next item after confirmed completion;
- `AutoGoNextAfterFrames` — waits a positive bounded number of authoritative Runtime production frames before the same governed prepare/GO path;
- `AutoOnMediaEnd` — media-only completion driven by the existing Media Deck observer;
- `Hold` — explicitly stops automatic progression after successful completion.

The current rundown contract is 1.1. Version 1.0 documents remain readable: an absent follow action maps to `Manual`, while legacy `AutoOnMediaEnd` deterministically migrates to the explicit media-end follow action.

Repeat behavior is explicit and bounded. `RepeatItem` repeats the current item a finite number of times; `RepeatRundown` repeats from the first item only at the bounded end of the rundown. Remaining repeat counts are projected in confirmed execution state. Unbounded looping and arbitrary conditions are not supported.

## Persistence and editing

Rundown JSON and its storage version are stored inside the existing durable show-project document. Stable item identities survive rename, reorder, save and restart.

Authoring supports bounded insert, delete and reorder. Optimistic storage-version checks reject stale saves rather than silently overwriting newer authored state.

Persistent media entries reference Media Library asset identity rather than raw local paths. Scene, graphics and audio references are validated before execution.

## Prepare and execution

`PREPARE` selects one item, translates it into the existing Show Control action model, selects that generated cue list and arms it. Preparation does not locally claim Program state.

`GO` executes only a prepared item, except when the operator explicitly overrides an already armed follow target; that override first invalidates the old automation revision, then prepares and executes the confirmed pending target through the same Show Control path. `NEXT` and `PREVIOUS` navigate deterministically and prepare the target item. `HOLD` atomically cancels pending follow execution and leaves Program state under existing authority.

A failed governed action stops rundown execution and exposes a failure state. Production errors are never silently skipped or automatically bypassed.

## Completion evidence and follow execution

A follow action may fire only from confirmed completion evidence. Valid evidence is:

- Show Control completion of the current bounded action sequence;
- confirmed Media Deck end for `AutoOnMediaEnd`;
- confirmed Show Control production-frame wait completion for delayed follow.

Operator/WPF state is not completion evidence.

The coordinator records the current item identity and execution revision when automation is armed. Before any later follow mutation, both must still match and the expected bounded next item must still be current. Manual navigation, HOLD, manual preparation, manual GO, or newer execution invalidates the old revision so a stale trigger cannot double-fire.

`PrepareNext` performs the same reference validation and Show Control arming as manual PREPARE, but stops before GO. It therefore performs no Program mutation.

`AutoGoNext` and the firing phase of `AutoGoNextAfterFrames` always re-enter the existing Show Control prepare/GO serialization. The RundownCoordinator does not call Runtime or providers directly.

## Production-frame delayed follow

`AutoGoNextAfterFrames` does not introduce a second scheduler. After current-item completion, the coordinator adapts the bounded delay into the existing Show Control `WaitFrames` primitive.

Show Control persists the RuntimeHost instance identity and target frame sequence and observes Runtime frame progress. The rundown snapshot projects the start frame, target frame, confirmed remaining frames and pending next item for Operator presentation.

A RuntimeHost identity change or ambiguous timing state enters recovery. Wall-clock time is only the existing bounded failure timeout; it never authorizes Program progression. No UI timer advances production.

## Deterministic media auto-advance

For `AutoOnMediaEnd`, ControlHost reuses the single existing Media Deck observation path. Completion is accepted only after the expected asset has been observed playing and then reaches confirmed `Ended`.

The same item/revision/expected-target validation used by the other follow actions applies before progression. Media completion therefore remains deterministic and cannot double-fire a later mutation after manual override.

End-of-list and repeat behavior use the bounded rundown policy.

## Recovery

Rundown recovery is derived from durable authored state and confirmed Show Control evidence. Local elapsed time is never treated as proof of completion.

Show Control durably carries the active cue identity, execution identity, production-frame wait target and RuntimeHost identity required to recognize interrupted rundown work. The authored follow action and bounded sequence determine the expected pending item. If restart occurs while an automatic follow outcome is ambiguous, the rundown enters `RecoveryRequired`.

Recovery never automatically replays CUT, DISSOLVE, recording, or another Program mutation whose prior commit cannot be proven. For ambiguous automatic follow, a resume acknowledgement may safely prepare the expected next item, but GO remains an explicit subsequent action. Cancellation leaves the rundown held.

Repeat progress is intentionally not guessed after ambiguous restart. Automatic repeat continuation stops at the recovery boundary rather than risking an extra Program mutation.

## Operator workflow

The persistent lower workspace keeps the existing multi-track timeline and adds a compact Rundown surface beside it.

The surface distinguishes local selection from confirmed execution state and presents `PREPARED`, `CURRENT`, `NEXT`, `FAILED` and recovery evidence. It also presents confirmed AUTO/HOLD state, active follow mode, pending next item, production-frame countdown/target and remaining bounded repeat counts. Commands cross the Client SDK; the Operator does not mutate Runtime or provider state.

Authoring exposes the bounded follow-action union, frame delay, repeat mode/count, CUT/DISSOLVE and hold-frame inputs using the existing custom rtaime controls. `AutoOnMediaEnd` is accepted only for media items. Media authoring uses the currently selected persistent Media Library clip plus the selected production source. Scene, graphics and audio items reuse the corresponding existing Operator selections and governed production models.

## Journal and Client/IPC state

Rundown automation uses the bounded production journal with stable causation identities. Events cover follow armed/cancelled/fired/invalidated, delayed-follow start/completion, repeat-current/repeat-rundown, operator hold and recovery-required transitions.

The existing rundown workspace snapshot is extended rather than adding another transport. Client/IPC state includes the follow kind, pending next identity, delay frames, RuntimeHost identity, start/target frame sequence, confirmed remaining frames and remaining repeat counters. Missing fields from older peers default to manual/no-pending automation semantics.

## Timeline relationship

The multi-track timeline remains the frame-oriented presentation and Media Deck interaction surface. The rundown provides ordered show/planning semantics. It does not convert reserved V2/A2/A3 rows into editable tracks unless a real backend execution model exists.

Existing V1/A1 Media Deck semantics remain authoritative. Additional graphics/audio semantics are represented by rundown items only where the established graphics and audio-routing contracts can execute them. Unsupported overlaps or resource semantics must be rejected rather than painted as committed UI-only clips.

## Non-goals

The rundown does not provide blade editing, arbitrary clip movement, destructive source editing, nested timelines, an advanced waveform editor, generic scripting, unbounded loops, collaborative editing or a comprehensive undo/redo system.
