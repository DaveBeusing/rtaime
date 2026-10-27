<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Context Inspector and precision editing

The Inspector follows the existing media, timeline, layer and processing-node selection. Confirmed metadata, mixed-selection summaries, pinnable properties, playback policies, audio gain, graphics placement and typed compositing Apply commands already existed. These remain the source of selection and production evidence.

## Current review and multiple-object workflow

[Inspector draft review and sequential edits](InspectorDraftTransactions.md) defines the current dirty-selection, explicit rebase and bounded per-object submission workflow. The read-only multi-selection baseline below describes the common-property summary and the absence of an atomic multi-object command. Explicit sequential review does not claim that missing atomicity.

The Inspector now shows the original stable target beside confirmed and local values. Apply, Discard and Keep are reachable after selection changes. Reconnect, revision conflict and missing targets retain intent for review instead of silently applying or substituting another source.

## Pinned selection, breadcrumbs and live authority

The same shared Inspector now exposes **PIN / UNPIN** in each selected-context
header. This pins the existing `EditingSelectionIdentity` only: the active
production identity plus the source, timeline/cue or graph-node identifier.
It does not copy live Runtime state, create another property model, persist
a pin in the Show, or pin a Preview frame. The compact breadcrumb identifies
the current media, timeline, graph or no-selection context and latest observed
Control revision. It carries the full selection key as an accessible tooltip.

While pinned, requests to replace the Inspector context with another media
item, timeline clip/cue or compositing node are ignored; the originating
workspace continues to own its ordinary selection and actions. A bounded
counter explicitly reports ignored Inspector selection requests. A repeated
observation for the **same** key still updates its live status, so pinning
does not freeze confirmed evidence. UNPIN restores normal follow-selection
behavior. Multi-object selection is intentionally not pinnable and retains
the existing read-only common/MIXED comparison and explicit sequential
per-target review. Pins never imply a cross-object transaction.

Changing the production identity invalidates the pinned Inspector context:
the pin visibly becomes **STALE**, and typed Apply is gated until UNPIN.
Likewise, a mixer-selected audio input that no longer matches the Inspector's
original source **and stream** disables direct gain Apply. This guard also
applies without an active pin, preventing a previously selected audio draft
from being written to another mixer's current target. Dirty typed edits
remain in the existing bounded Inspector review/history and can only be
restored or applied using the established stable-target checks. The
PIN control never sends a production command or silently replays intent.

## Shared numeric fields

The 23 numeric Inspector fields share immutable property definitions for identity, label, range, unit, default, step and integral precision. Definitions describe an editor; existing view models retain draft values, and Control remains authoritative.

- Type a number using the current UI culture, including exponent notation. Group separators, empty text, NaN, infinity, overflow, fractions in whole-number fields and out-of-range values are rejected.
- Up/Down adjusts one step. Shift uses one tenth of a step; Ctrl uses ten steps. Integral fields always use whole-number steps.
- Hold Alt and drag horizontally to scrub in device-independent pixels, with the same fine/coarse modifiers.
- Ctrl+Home resets that field to its declared default. Escape restores its current valid draft; during scrubbing it restores the value from the start of the drag.
- Enter stays within the numeric editor and does not submit a production edit.
- Units remain visible beside the number. Accessible names, help text, keyboard focus and invalid-input borders use the custom control theme.

Invalid text remains visible for correction and does not replace the last valid draft. Visible invalid fields disable Inspector Apply actions. Hidden contexts do not block the active editor. Selecting a different context restores the field from that context's draft. Controls use the existing compact metrics and per-monitor layout; physical mixed-DPI interaction remains UNVERIFIED until reference-platform testing.

## Draft and confirmation boundaries

Typed, stepped, scrubbed, reset and pasted values are local drafts. Apply uses the existing validated Client mutation and confirmation path. Pending, stale, disconnected and rejected state remains visible; unavailable mutation commands stay disabled. Confirmed metadata and Program/Preview source identities are not replaced by local edits.

Transform drafts survive periodic status refreshes while the confirmed transform is unchanged. A changed confirmation or revision rebases the editor while retaining typed intent in the local review card. In the COMPOSITING live layer stack, staged visibility and opacity additionally retain exact confirmed production revision and layer content identity. A newer revision requires an explicit REVIEW before Apply, whereas content replacement makes the old draft non-reviewable: DISCARD and edit the new asset. Layer reorder drafts similarly retain the original confirmed order and content identities; stale order cannot be submitted. Returning to an unchanged cached context retains its draft. Processing drafts retain their signature-based confirmation behavior and the same explicit review boundary.

## Compatible attributes

COPY ATTRIBUTES stores one bounded in-memory set for the selected transform, Color Grade or Chroma Key. PASTE DRAFT accepts only the same attribute kind on a single editable authoritative layer or processing node. The complete set and combined crop bounds are validated before any draft setter runs. Layer/node identities, source assignments and routing are never copied.

Pasting does not apply or submit a transaction. Apply remains explicit and uses the existing atomic layer-transform or processing-stack mutation. The local attribute copy lasts only for the Inspector's lifetime and does not use the system clipboard.

Multi-selection remains a read-only mixed summary. Batch property editing is deferred because the current contracts do not provide an atomic multi-selection property transaction. Media technical metadata, graph topology and output evidence remain read-only unless an existing dedicated governed action exposes editing. Audio uses the existing mixer/source draft and explicit gain Apply. The Mixer exchanges compatible gain-only values through the same typed Inspector clipboard and revision token; source/bus destination Apply remains explicit. EQ, routing and other audio attribute transfer remain excluded.

## Qualification

InspectorPrecisionTests cover invalid/edge/culture input, fine/coarse/integer steps, compatible and incompatible paste, unchanged-refresh draft retention, pending and rejected edits, stale/invalid command gating and confirmed-source preservation. The real WPF startup qualification exercises the themed numeric template, invalid-input Apply gate and reset while the existing application is rendering. Existing docking, startup, failure/recovery, visual and source-bound qualification gates remain required.

Physical monitor changes and hardware visual results are not inferred from managed tests.
