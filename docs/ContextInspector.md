<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Context Inspector and precision editing

The Inspector follows the existing media, timeline, layer and processing-node selection. Confirmed metadata, mixed-selection summaries, pinnable properties, playback policies, audio gain, graphics placement and typed compositing Apply commands already existed. These remain the source of selection and production evidence.

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

Transform drafts survive periodic status refreshes while the confirmed transform is unchanged. A new confirmed transform or explicit selection resets the editor to the authoritative values. Processing drafts retain their existing signature-based confirmation behavior.

## Compatible attributes

COPY ATTRIBUTES stores one bounded in-memory set for the selected transform, Color Grade or Chroma Key. PASTE DRAFT accepts only the same attribute kind on a single editable authoritative layer or processing node. The complete set and combined crop bounds are validated before any draft setter runs. Layer/node identities, source assignments and routing are never copied.

Pasting does not apply or submit a transaction. Apply remains explicit and uses the existing atomic layer-transform or processing-stack mutation. The local attribute copy lasts only for the Inspector's lifetime and does not use the system clipboard.

Multi-selection remains a read-only mixed summary. Batch property editing is deferred because the current contracts do not provide an atomic multi-selection property transaction. Media technical metadata, graph topology and output evidence remain read-only unless an existing dedicated governed action exposes editing. Audio uses the existing mixer/source draft and explicit gain Apply. The Mixer exchanges compatible gain-only values through the same typed Inspector clipboard and revision token; source/bus destination Apply remains explicit. EQ, routing and other audio attribute transfer remain excluded.

## Qualification

InspectorPrecisionTests cover invalid/edge/culture input, fine/coarse/integer steps, compatible and incompatible paste, unchanged-refresh draft retention, pending and rejected edits, stale/invalid command gating and confirmed-source preservation. The real WPF startup qualification exercises the themed numeric template, invalid-input Apply gate and reset while the existing application is rendering. Existing docking, startup, failure/recovery, visual and source-bound qualification gates remain required.

Physical monitor changes and hardware visual results are not inferred from managed tests.
