<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Sequence authoring and persistence

## Scope and authority

Sequence capability 1.0 provides durable multitrack authoring through
`SequenceAuthoringClient -> existing named-pipe/gRPC transport -> ControlHost -> ShowProjectPersistenceStore`.
A sequence is an authored composition, not a Media Deck projection or a Rundown item.
The Operator timeline still projects existing media/marker state; this backend does not make
its reserved lanes editable. See [Layered Timeline](LayeredTimeline.md) and
[Production Rundown](ProductionRundown.md).

Control owns validation, revision assignment and durable writes. Runtime remains the only
execution and clock authority. List/Get return `UNAVAILABLE` for playback and Get returns a
null active execution revision. There is no play, arm, take, seek or decoder operation in this
API. A future execution package must explicitly validate and admit an authored revision.

## Canonical model

Sequence, track, clip and transition identities are nonempty GUIDs, unique across the show
library and stable across reads, edits and restarts. Link group IDs identify two or more
clips with equal timeline start/duration; they are distinct from entity IDs.
Track kind is Video=1, Audio=2 or Graphics=3; layer order, enable and mute are authored data.
Audio tracks name a current canonical audio production bus (legacy default: `program`).
Clip enable/mute remain independent from their track; neither changes Program.

Clips refer to real catalogue asset ID, media kind and SHA-256 fingerprint. No path is accepted
as a media reference. Video/audio references resolve against the retained media catalogue,
including online availability, exact content fingerprint and source duration. Graphics
references resolve against the canonical retained bitmap identity and checksum. Production-CG
text has no independent sequence asset identity and is not fabricated into an asset.
Removal or content-changing relink retains the saved reference and causes Get validation and
subsequent writes to reject it; repair requires an explicit authored reference update.
An unchanged-content relink retains sequence validity. Catalogue revision leases prevent a
catalogue mutation during validation/save. Static graphics use an explicit source range
bounded to the authoring horizon; they have no decoded frame duration.

## Time and boundaries

The sole authoring unit is an integer tick at exactly **240000 ticks/second**.
This is a serialization/edit unit, not a running clock. All source and timeline ranges are
half-open: `[SourceIn, SourceOut)` and `[TimelineStart, TimelineStart + Duration)`.
Unit-speed clips require `Duration == SourceOut - SourceIn > 0`; negative, overflowing or
beyond-24-hour endpoints are rejected. Source and timeline positions may be subframe.

A rational frame rate is a display/snap contract. 25/1 uses 9600 ticks/frame,
50/1 uses 4800, and 60000/1001 uses exactly 4004. 48 kHz audio samples use 5 ticks.
Conversion uses checked integer/BigInteger arithmetic, never floating-point accumulation.
`SequenceTime` floors conversions by default; its explicit ceiling option supports exclusive
end boundaries. A future consumer must floor native source start and ceil source end at the
conversion boundary and clamp to the admitted source, without changing saved authoring ticks.
TimeSpan catalogue duration is conservatively floored into authoring ticks.

Same-track overlaps require an explicit transition matching exactly the intersection.
Video/graphics dissolve and audio crossfade are distinct typed values. Cut has zero duration
at adjacent endpoints. Unknown kinds, crossing/ambiguous transition intervals and orphan
clip/track/transition references are rejected. Cross-track overlaps represent composition.

## Transactions and history

Save replaces/adds one definition. Edit applies a nonempty batch atomically, then validates
the complete resulting library. Every request carries version, transaction ID and expected
library revision. A failed batch changes neither authored state nor history.

The typed edit contract supports InsertClip, MoveClip, DeleteClip, SplitClip, TrimClip,
RippleDelete, CloseGap, Link, Unlink, AddTrack, UpdateTrack, DeleteTrack, AddTransition and
DeleteTransition. Inapplicable/missing fields are rejected.

- Move/trim/delete act on the full linked group. A move's optional destination track applies
  to the selected member, with final kind/reference validation.
- Trim changes source IN/OUT and timeline boundary by the same left/right tick deltas.
- Split preserves left IDs and requires explicit right IDs ordered by linked member GUID
  text (ordinal). Linked splits additionally require a distinct right group ID.
- Ripple delete removes the selected group, then closes its interval across all tracks.
  CloseGap also shifts all suffix clips/transitions across all tracks. Neither operation
  implicitly trims clips or transitions crossing the interval.
- Track deletion requires an empty track. Edits must explicitly remove/repair affected
  transitions in the same batch. Linking existing linked clips requires explicit unlinking.

Undo/Redo target the library (`SequenceId = Guid.Empty`, no definition/edits). They restore
authored content and entity identities while issuing a **new monotonic revision**. They never
invoke production commands. A new edit after Undo clears Redo.

The last 64 successful transaction IDs and canonical operation/request hashes are persisted.
An identical retry returns `Saved=true, Replayed=true` and the original committed revision,
with no sequence body; use Get to read the current state. Reusing an ID with different
content/operation is a transaction conflict. Receipt lookup precedes expected-revision checks,
so retries survive restart and later writes. Receipts older than the bounded window are no
longer replayable; the stale expected revision still rejects the original request.

## Persistence and migration

Sequences live only inside the canonical SQLite-backed `show.project` document as
`sequenceJson`, containing `rtaime.sequence-authoring.v1`, the saved library, up to eight
Undo and eight Redo library states and receipts. There is no second sequence database.
Outer compare-and-swap and the existing SQLite transaction commit all changes together;
cancellation, validation errors and competing writers cannot publish partial history.

Show format migration is explicit:

| Show format | Required authored extension |
| --- | --- |
| `rtaime.show-project.v1` | no canvas or sequence |
| `rtaime.show-project.v2` | canvas, no sequence |
| `rtaime.show-project.v3` | sequence, optional canvas |

Reading v1/v2 creates an empty revision-zero sequence view without a write. The first sequence
mutation upgrades to v3, preserving scene IDs/routing, graphics, audio, Show Control, Rundown,
macros, adopted sources and canvas semantics. Future/inconsistent versions fail closed.
Older executables reject v3 rather than silently dropping authored sequences. Rollback to an
older executable therefore requires an explicitly compatible pre-upgrade backup.
Existing Media Deck/marker data is never guessed into clips, since it cannot establish
independent track/sequence identity. Legacy UI behavior remains available.

## API and bounds

Named-pipe messages: `control.sequence.list/get/save/edit/undo/redo`.
External gRPC uses the existing Execute envelope and six additive operation tags 114–119
inside the allocated 114–145 sequence range. Capability discovery lists these operations;
Observer may List/Get, Operator/Administrator may mutate. Existing authenticated session,
TLS, request limits, cancellation/deadline, audit and rate limits are reused. The local pipe
retains the existing local operator trust model.

List takes an empty object; Get takes `{ "sequenceId": "GUID" }`.
Mutations use `SequenceMutationRequest`; responses are `SequenceMutationResult`.
Structured issues include revision/transaction conflicts, missing assets, invalid buses,
ranges, overlaps and orphan references. Invalid envelopes return `sequence.rejected` or the existing transport-level invalid-argument error.

Limits per show library: 16 sequences, 64 tracks, 2048 clips, 1024 transitions, 512 KiB
serialized authored library. Each request is at most 512 KiB with 128 edits, depth 32,
strict unknown-member and duplicate-property rejection. Retained history has a 10 MiB
subdocument cap; each retained library is validated against the same limits.
The [definition schema](../schemas/sequence/v1/Sequence.schema.json) documents serialized
shape; Control validation additionally enforces arithmetic and reference semantics.

## Qualification

`SequenceAuthoringTests` covers rational boundaries, two video/two audio track composition,
linked edits, deterministic split identities, gap/ripple behavior, overlap validation and
malformed/future input. Integration tests use the real retained MP4 test asset and real
ControlHost/RuntimeHost with both existing transports, 25/50/60000:1001 rates, restart,
idempotency, stale writers, Undo/Redo, missing assets, RBAC and cancellation.
The same host test first persists a v2 canvas, then verifies its contents after sequence writes
and recovery. Legacy wire peers without sequence handlers must surface explicit pipe/gRPC
errors; clients do not fabricate a successful empty sequence response. Existing transport
session fencing requires a full snapshot after host replacement before further operations.
Store tests compare every unrelated legacy show field before/after upgrade, reject malformed
history and verify concurrent compare-and-swap behavior.

The focused backend qualification runs before the full build. Required CI, Quality, Security,
Provider Smoke and Packaged E2E remain authoritative and unchanged in their acceptance rules.
A focused pass does not imply full release or sequence playback qualification.
