<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Inspector draft review and sequential edits

The productive Inspector follows the existing shared selection. Library, timeline and graph do not acquire another selection owner. Object/type, stable identity, confirmed values, local values and pending/rejected evidence remain distinct. Existing property pins retain their IDs and technical metadata remains read-only.

## Precision and local transactions

The shared numeric fields retain exact typed values, current-culture parsing, declared units/ranges/defaults, fine/coarse stepping, Alt-drag and Ctrl+Home reset. Enter never submits production. Unit labels are displayed separately; unit-suffixed text, group separators, NaN, infinity, overflow and out-of-range values are invalid. de-DE uses a decimal comma while UI labels remain English. Invalid text keeps the last valid value and blocks its numeric Apply context; navigation and independent reviewed targets remain available.

A continuous focus edit or pointer gesture is one local undo transaction. Escape cancels that interaction; it does not reverse confirmed output. Ctrl+Z/Ctrl+Y in text editors retain native text undo, Inspector focus outside text uses its own draft history, and timeline/rundown histories stay separate. Typed paste and Reset each form one local transaction. History is bounded to 32 snapshots per context and eight Inspector contexts.

## Selection pin and review separation

PIN in the shared Inspector header locks only the presentation's current
production/source/graph/cue selection identity. Selection events from other
workspaces do not replace that Inspector context while pinned; repeated
observations for the same identity continue to update confirmed values.
PIN and UNPIN never call Client mutation methods or change active Program.

The retained draft review cards remain a separate, already-existing
eight-context in-memory recovery mechanism. A pin is **not** a draft copy,
an automatic rebase, a substitute for target revision validation, or a
way to bypass supported command permissions. A new production identity
makes the pin stale and blocks typed editing until explicit UNPIN.
The legacy direct-edit panels also become disabled for stale pins, for
mismatched loaded clips, replaced graphics and another mixer-selected
audio stream; the PIN/UNPIN header itself remains interactive. Direct
gain Apply additionally verifies the mixer's current source and stream
match the Inspector target even when PIN is off. Multi-selection
cannot be pinned; its explicit sequential reviewed operations continue
to report per-object confirmation, rejection and stop without atomic claims.

The shared Inspector header presents context breadcrumb, selection pin
button and explicit pin status in the review surface. The existing 23
numeric controls still show units, use localized finite-range parsing
and keep all pointer/keyboard adjustments local until explicit Apply.
PIN has no persistent schema or protocol effect.

## Dirty selection and recovery

Selection can move immediately while the previous typed intent stays visible in LOCAL DRAFTS. The card identifies the original target and compares CONFIRMED with LOCAL; it never labels the draft as a Runtime value.

- **KEEP DRAFT** retains intent in memory without changing selection or production.
- **DISCARD** removes the selected local intent and its local history; it cannot reverse a confirmed operation.
- **APPLY DRAFT** addresses the reviewed original stable target through the existing Operator lifecycle and Client command. It does not temporarily select another Library object.
- **REBASE / REVIEW** replaces only the comparison baseline and revision with current evidence. It never applies values.
- **RESTORE LOCAL** restores a compatible reviewed copy into the same selected context as one local undo transaction. Apply remains separate.

The review holds up to eight typed copies. Capacity displacement explicitly identifies the displaced values in the notice. It is session memory, not a new persistence format. A revision/reconnect rebase clears the corresponding local history but retains typed intent for explicit review. Missing targets remain named and unavailable; they are never replaced by the first available source or recreated. Copies from another production cannot be reassigned. Successful confirmation removes the matching outstanding intent; rejection or unknown confirmation retains it.

Confirmed-state and revision checks run again through the existing synchronization owner immediately before a reviewed request. Controls show conflicts instead of silently rebasing and applying. Existing single-object processing/transform, cue Rename and audio Apply controls remain available.

## Compatible multiple objects

The multi-selection summary is read-only. It displays common or MIXED values and stable identities; unsupported contexts give a reason. Copy a typed set from an editable single object, select targets, then use **REVIEW BATCH**. The review shows every target's confirmed and proposed values before **APPLY REVIEWED** becomes available.

A review contains at most 32 targets, with no silent truncation. Supported existing command contributions are:

| Typed set | Compatible targets |
| --- | --- |
| Transform | Confirmed editable bitmap/Production CG layers |
| Color Grade / Chroma Key | Selected layers with exactly one existing node of the copied kind; ambiguous node identity is excluded |
| Audio gain | Existing selected audio source/stream identities; mute, EQ, routing and buses are excluded |
| Cue name | Single original asset/marker identity through its existing Rename command |
| Library technical metadata / timeline topology | Read-only; no generic property endpoint is inferred |

Batch execution is **sequential, not atomic**. Each request uses an existing dedicated command and is followed by matching confirmation. Fresh source/target/revision/readiness checks precede each request. Existing backend contracts retain their own revision/generation behavior; these UI checks do not add cross-object compare-and-swap or a lock against other operators.

The first rejection, missing target, stale evidence or revision conflict stops later submissions. Results remain visible per object: CONFIRMED, REJECTED/UNKNOWN, EXCLUDED or NOT ATTEMPTED. **STOP** waits for the current request and prevents subsequent mutations. It cannot cancel or reverse an already accepted request. **REVIEW REMAINING** refreshes unresolved baselines without sending anything; another explicit Apply is required. Confirmed rows are skipped, including on retry. Local undo never rolls back the confirmed part of a batch.

## Productive integration and qualification

The extracted Inspector view owns the review presentation; Inspector draft/history and typed adapters own its behavior. The borrowed MediaSelectionContext and Library browsing projection are unchanged. Stable-target audio/marker adapters reuse Operator's busy/error/lifecycle handling and existing Client transport; no additional connection, poller, renderer, global command registry or persisted schema is introduced.

InspectorDraftTransactionTests extends the existing fixtures for dirty selection, original-target audio/marker Apply, explicit revision review, incompatible/empty/missing targets, partial failure, stop/retry and 100 bounded local history cycles. Its real MainWindow/STA phase uses the productive Inspector controls, draft card, common-property review and retained subscription ownership. InspectorPrecisionTests retains locale, NaN/overflow/range and typed clipboard validation.

GraphicsOverlayIntegrationTests adds a real named-pipe Client/ControlHost/RuntimeHost target journey: a confirmed audio edit survives a later rejected target, remaining individual editing still works and Program identity stays unchanged. The existing compositing pixel/scene-recall case qualifies transform and ordered processing through the same real hosts. These are distinct from the controlled-transport UI interaction tests; they do not claim one connected physical UI/hardware certification.

All five Required Gates must be read at the complete final source SHA. Managed tests do not qualify physical keyboard layouts/IME, screen readers, mixed-monitor movement, hardware latency or long runs; those retain their existing evidence requirements.
