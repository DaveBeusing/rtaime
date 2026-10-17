<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Resident next-clip preparation and governed playout

## Supported workflow

Use the existing Rundown media item and PREPARE / GO / HOLD controls. Select the
next catalogue asset and explicitly target the other A/B source. PREPARE now opens
and prerolls that resident decoder before GO. NEXT/PREVIOUS and approved follow
actions reuse this same preparation path; they do not create a playlist database
or another scheduler. Scene/graphics/audio/hold items retain their existing paths.

The initial resident workflow is deliberately bounded:

- existing one/two-decoder capacity and existing 1080p50 or 1080p59.94 RGBA8 output;
- direct local Windows files, without UNC, reparse/mounted paths;
- files up to 32 MiB for the optional SHA-256 verification lane;
- approximately 8 MiB/s requested hash reads, with a five-second managed identity
  budget; native open and existing preroll/cleanup limits are unchanged;
- one next candidate per Runtime registry, with at least three video/audio frames
  of qualified preroll and actual decoded audio;
- a 30-second candidate deadline, enforced by a monotonic Runtime clock as well
  as the published UTC expiry.

Larger or video-only media remain available through ordinary explicit Media Deck
playback. Their rejection by resident preparation is not a global input-format
rejection. No additional codec, four-deck scale or physical SSD capability is added.

An available target is a free slot or the explicitly selected stopped off-Program
A/B slot. Replacing that inactive slot is deliberate PREPARE intent. Preload never
evicts another source, a Playing owner, Program or uncertain cleanup. Stop/close
an occupied target explicitly when it is not eligible. The original file is never
modified. If verification/open fails after deliberately replacing an inactive clip,
that clip is not silently restored or taken to Program.

## Ownership and handshake

ControlHost resolves stable catalogue identity, online location and fingerprint.
Runtime admits the work through the existing management serialization and shared
probe/deck reservations before reading the file. It recomputes SHA-256 while holding
a FileShare.Read identity lock, opens the normal provider and retains that lock
through preparation/handoff until explicit replacement, cancellation or close.
There is no independent prepared-media cache or new decoder backend.

Persisted IN/OUT markers are synchronized through the existing MediaDeck control
service. Runtime seeks to effective IN, qualifies preroll and fixes the prepared
clip's end behavior to Hold Last Frame. Preparation does not select Preview or
route Program. A resident candidate binds:

- Runtime epoch, source and asset identity;
- local PreparedExecution identity and authority revision;
- decoder generation, effective IN/current position and OUT;
- verified content fingerprint, opaque single-use ticket and deadline.

Control additionally binds catalogue revision, source location and connected Runtime
identity in ephemeral Control-lifetime evidence. A relink/remove/catalogue change,
unexpected production revision or Runtime identity change rejects GO before the
ordinary cue actions can take the candidate.

GO retains the existing generated Show Control sequence: MediaOpen, SetPreview,
CUT or DISSOLVE, MediaPlay. Within this prepared execution, MediaOpen validates
the candidate and does **not** reopen. SetPreview and TAKE use a distinct guarded
Runtime apply operation. The Runtime checks the exact ticket/epoch/asset/generation/
position/preroll/deadline under the same registry gate as deck mutations and executes
only the normal authorized Runtime apply. TAKE's Program target must match the
prepared source; successful TAKE consumes the ticket exactly once.

Control retains the expected authority revision across the existing serialized
mutations. An interleaved manual production command cannot be silently adopted
into that prepared action sequence. Show Control acquires its execution gate before
a revision lease on the existing catalogue. That lease spans the prepared cue actions,
so a remove/relink cannot race the final TAKE; cancellation/failure releases it exactly
once. This is management serialization, not Program work. The final MediaPlay command is source-scoped,
including when A and B share an asset. No raw video/audio crosses management IPC.

The existing Program cadence observes the committed source and admits its already
queued frames. It performs no file hashing, native open/read/seek, preparation wait
or worker join. Existing generation and lease fencing remain responsible for payload
lifetime. The same prepared decoder generation and IN position survive GO; there is
no extra decoder or seek at handoff. DISSOLVE retains its existing anchored transition.

## Failure and end behavior

| Condition | Deterministic behavior |
| --- | --- |
| Missing/offline/corrupt/changed file | PREPARE fails visibly; no Preview or Program mutation |
| Target on Program, Playing/full or uncertain owner | Admission rejects; no Program eviction or automatic retry |
| Large/slow identity verification | Resident lane rejects its finite budget; ordinary explicit deck workflow remains |
| Missing decoder, incomplete/starved preroll or no decoded audio | No NEXT READY candidate and no prepared TAKE |
| Seek/stop/configure/close/replacement after preparation | Ticket invalidates; GO fails closed rather than reopening or taking stale media |
| Catalogue relink/removal or changed production revision | Prepared transaction fails; prepare deliberately again |
| Expired deadline or Runtime restart | Old evidence cannot commit; an old UTC clock value cannot extend Runtime lifetime |
| Early EOF before confirmed OUT in prepared playback | Explicit runtime.media_preload.early_eof, lost-signal/failure path; no fabricated successful full clip completion |
| Normal OUT/EOF | Hold Last Frame / Ended; no implicit TAKE unless an authored existing follow rule authorizes progression |
| Operator HOLD | Cancel pending follow and local next evidence; no route/rewind. An in-flight serialized commit is not undone |
| End of rundown | Existing bounded Completed/repeat policy; no hidden loop or synthetic next source |
| Interrupted transition/ambiguous commit | Existing Show Control/Control recovery rules apply; no automatic replay of uncertain TAKE |
| Operator disconnect | Control owns candidate; resync observes its current state and expiry, without initiating action |
| ControlHost restart | Candidate is not persisted/reused. Restored media Arming is held; explicit PREPARE is required |
| Native cleanup failure | Existing owner quarantine and restart-required error; never free capacity from a timeout alone |

The existing media-end follow observer is now explicitly source-scoped. It checks
the expected source/asset and reports failure or replacement rather than watching
the most recently opened other deck. It still observes confirmed completion at its
existing cadence and enters the existing governed prepare/GO path. It is not a
sample-accurate crossfade scheduler.

Manual preparation while an automatic follow is running uses the existing manual
override/HOLD semantics: old automation is invalidated first. This slice does not
silently add automatic lookahead while another authored rule is running. Configure
any desired automatic follow explicitly in the existing rundown.

## Operator and compatibility

Prepared media rows display NEXT READY and the confirmed deadline in the existing
Rundown detail area. Failure reasons remain ordinary confirmed rundown failures.
GO is authoritative; the UI never directly routes the prepared source. Preparation
observations can become stale after another operator changes a deck, so the Runtime
fence is always checked again at commit.

Matched upgraded Control/Runtime hosts are required for resident preparation.
New preload and guarded-apply operation names fail closed on older Runtime peers;
unknown optional fields cannot turn a guarded request into an ordinary TAKE.
The existing pipe response and gRPC JSON projection carry additive readiness fields.
Older clients may ignore that presentation data while execution stays Host-owned.

Authored rundown documents remain version 1.1, with existing 1.0 compatibility.
No stored production/checkpoint/catalogue/marker schema is migrated or duplicated;
ephemeral preparation evidence is not saved. Existing media items targeting the
currently active Program source must choose the other A/B source for this resident
workflow. Ordinary explicit Media Deck and authored non-resident Show Control cues
keep their established behavior. Product release target remains 0.5.39.

## Qualification and limits

PreparedMediaHandoffIntegrationTests covers real Windows preroll/file identity,
IN/OUT, single-use tickets, stale seek/close/cancel/epoch, monotonic expiry, A playing
while B stages, existing CUT/DISSOLVE, catalogue/authority invalidation and HOLD/resync.
Existing async decode, quarantine, resource, Show Control, Rundown, protocol and
Operator suites continue to run. The dedicated reference workflow runs these tests
before its generated format/long-playback observations.

A moving Program source and passing transaction test do not prove gapless output.
No-gap/physical A-V acceptance is **UNVERIFIED**. Existing hosted A/B measurements
already show Program timing failures; this feature does not waive them or increase
a timeout/resource ceiling. Qualify each exact source/format/codec/SSD/GPU/driver
tuple using [reference SSD acceptance](ReferenceSsdHardwareAcceptance.md), retaining
output frame identity, actual transition timing, audio sample continuity and physical
A-V traces through IN/OUT and failure/recovery. Do not generalize virtual-output
software success to physical devices or untested profiles.

The [capacity proposal](FileDeckCapacityAdmission.md) remains proposed: no calibrated
per-device pressure weighting, global retained-byte ledger or four-deck admission is
claimed here. This resident slice uses the existing conservative A/B owner limits,
excludes concurrent optional probe/preflight and bounds its identity reads. A measured
larger scale or preload budget requires its separate approval and evidence.
