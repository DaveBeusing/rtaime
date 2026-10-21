<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Sequence Operator Playout

## Scope and authority

The Sequence Timeline exposes an explicit native sequence playout workflow over
the existing authoring, preparation, shuttle, output and ControlHost transaction
clients. It introduces **no second Program clock, native decoder, producer,
media store, routing authority or transport protocol**. Client requests use the
connected Operator's existing named-pipe ControlHost transport. ControlHost
owns optimistic production authority; RuntimeHost owns native decoder leases,
actual Preview frames/PCM, Program sink boundaries and output observations.

Existing persisted Sequence/Show revisions, source IDs, `EDIT` workspace ID,
layout schema 7 and the 240,000 authored ticks/second contract are unchanged.
The old sequence List/Get `PlaybackState` intentionally remains
`UNAVAILABLE` and is not the native output status.

## Operator sequence

1. In **Timeline**, select and LOAD a real saved sequence. Authoring
   mutations including clip insert, trim, move and undo/redo are individually
   saved ControlHost operations, not queued Program actions. No local edit,
   selection, waveform request or ruler click issues a runtime seek.
2. Select a current, eligible Preview source using the existing production
   routing controls. Source must differ from Program; the Operator cannot
   silently choose or fabricate one.
3. Toggle the sequence toolbar to **PLAYOUT**. **PREPARE** re-reads the
   authoritative sequence revision and preparation context, then requests
   admission of native owners with matching RuntimeHost epoch. The
   PREPARED state means resident resources only, *never* a picture or Take.
   Failure details report unsupported provider/format, Graphics-track
   mappings, exhausted owner capacity, changed media fingerprints, offline
   catalogue assets, or stale authority as supplied by the actual backend.
4. **PREVIEW** explicitly binds the prepared source to the *already selected*
   and revision-bound Preview route. **CUE** is the only bridge from the
   independent local edit cursor to a native shuttle position. **PLAY**,
   **PAUSE** and **STOP** send source/revision-fenced shuttle commands; native
   shuttle PLAYING has `OutputState=UNAVAILABLE` and does not prove output.
5. Press **OBSERVE** to refresh the actual native output status. Only a matching
   `PREVIEW_READY` observation with nonzero published frames, a real source
   slot, no Program slot and the same production revision can enable ARM.
   Observe is explicit; no UI-local timer, renderer, frame clock or background
   polling session is started.
6. **ARM** reconciles the saved sequence again and captures **one**
   Preview-source/production-revision/preparation/transaction identity.
   **TAKE** is a separate deliberate action. An accepted commit alone is
   not `PROGRAM_CONFIRMED`: the Operator waits for genuine
   `PROGRAM_CONFIRMED` output including a source frame and Program sink slot.
7. If TAKE's reply is lost or ambiguous, **do not press TAKE again**.
   Synchronize the authoritative state, then use **RECOVER TAKE**.
   Recovery is permitted only for the *same saved sequence and original
   transaction*, and only when the confirmed Program source and revision
   equal the exact already committed successor. It confirms ownership; it
   never repeats the production mutation. An unchanged/wrong successor,
   new Runtime epoch, changed saved revision or failed native output remains
   UNCONFIRMED/STALE until explicitly reconciled.
8. **RELEASE** cancels a prepared native owner before Take. An issued,
   ambiguous Take cannot be discarded or silently retried by Release; use
   authoritative recovery or existing production lifecycle controls.

## Status contract

| Presentation | Minimum evidence |
| --- | --- |
| UNAVAILABLE | No connected, qualified native playout client |
| PREPARING / PREPARED · NO OUTPUT | Saved revision and Runtime admission, not Preview frames |
| NATIVE PLAYING · PREVIEW UNCONFIRMED | Shuttle staging changed; output has not been observed |
| PREVIEW READY | Real Preview output with source slot, positive frames, exact authority |
| TAKE ARMED / TAKE PENDING | One original transaction captured / submitted; no Program claim |
| TAKE UNCONFIRMED | Reply/commit uncertain; no automatic replay |
| PROGRAM CONFIRMED | Matching committed successor and observed Program sink slot/frame |
| STALE / ERROR | Changed source/revision/epoch/asset or rejected backend observation |

The footer shows the saved revision, resident owner generation, shuttle
position/decode generation, published source frames and Program slot. This is
observational evidence, not an independent Program transport or synthetic video
preview. Dedicated error details remain available in the state tooltip.

## Safety, focus and recovery

The existing Sequence Editor's bounded row virtualization, independent edit
cursor, zoom/scroll, local focus, draft/undo gesture rules and shortcut router
remain unchanged. The playout toolbar uses custom rtaime buttons. ARM and TAKE
are pinned, separate actions and remain disabled until genuine Preview
observation. The command palette exposes explicit equivalents through Ctrl+K
with confirmation-required semantics for potentially mutating actions.
Disconnected, stale, busy or unsafe production state cannot initiate a
new Take. No automatic reconciliation issues another Take request.

The recorded software qualification concerns existing connected
ControlHost/RuntimeHost native output and WPF Operator presentation on Windows
x64 / pinned .NET SDK. Independent reference-source integration tests and all
five Required Gates must be inspected on the final PR SHA. The presence of an
Operator button or a managed simulation is never evidence of hardware output.
Graphics-sequence output, reverse shuttle, arbitrary bus mappings, physical
SDI/NDI, unqualified GPU capabilities and gapless delivery remain
**UNVERIFIED/UNSUPPORTED** unless separately admitted and evidenced.
