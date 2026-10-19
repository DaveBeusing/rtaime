<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Sequence playback and transition execution

## Integration status

Sequence authoring is durable and can represent simultaneous Video, Audio and
Graphics tracks, but **the current product has no live multitrack sequence
playback authority**. This change begins the execution integration with a
source-bound, finite Control-side plan compiler and rational selection tests.
It does **not** create, advertise or implicitly enable Preview playback,
Program Take, Runtime media decoding, audio mix or pixel compositing. The
existing sequence List/Get playback state remains `UNAVAILABLE` until the
real cross-host path is implemented and qualified.

See [Sequence Authoring](SequenceAuthoring.md), [Local Media File Source](LocalMediaFileSource.md), [Production Rundown](ProductionRundown.md),
[Operator Experience Scope](OperatorExperienceScope.md) and
[Runtime Media Resource Admission](FileDeckCapacityAdmission.md).

## Compiled authoring-time execution plan

`SequenceExecutionCompiler.Compile` takes an existing saved
`SequenceDefinition`, its authoritative authoring revision, online
catalogue-backed `SequenceAsset` references, available audio buses and
explicit Runtime decoder-owner capacity. The existing
`SequenceModel.Validate` rejects malformed model, orphan transitions,
missing or changed-fingerprint media, source-range errors and invalid bus
references before a plan is produced.

All timestamps remain integer units of exactly 240,000 ticks per second.
The compiler emits ordered half-open windows split at every clip and transition
edge, including black/silent gaps. Within each window the previously validated
clips, exact source offsets, track order/bus, dissolve or crossfade direction,
and interval occupancy are fixed. Draft edits do not change a compiled snapshot.

Selection at a zero-based Runtime Program frame boundary uses the existing
rational 25/1, 50/1 or 60000/1001 frame rate. It computes
`floor(frame * 240000 * denominator / numerator)`; no rounded frame period
is accumulated. The 48-kHz PCM sample window uses integer 5-tick sample units.
Dissolve and audio crossfade shares are fixed-point 0–255 contributions
computed from the same transition interval; incoming=0 and outgoing=255
at the first overlap instant and shares sum to 255 at every sample time.
At the exclusive end the outgoing clip disappears. The allocation-free
`TrySelectAtTick` hot selection API fills a caller-owned preallocated
buffer; it never opens, decodes, seeks, allocates per frame, blocks a worker,
or takes a production lock.

Overlapping decoder owners are estimated conservatively by unique asset
identity plus source/timeline time offset, allowing a synchronized
video/audio pair with the same media file and offset to share one owner,
but counting distinct offsets separately. The actual Runtime ledger must
reserve and verify **real native owners and retained pools** before any
Prepare can succeed; the compiler cannot reserve hardware, promise a frame,
or replace the Runtime's one global ledger. A sequence exceeding the
explicit one-to-four-owner software profile is rejected without Program
side effects. Four-owner planning is a software test profile, not physical
SSD/GPU/decoder-throughput qualification.

## Runtime-boundary position mapping

The non-rendering `SequenceRuntimePositionMap` in the existing
`rtaime.Runtime` assembly binds an immutable execution position to the
**already owned Program boundary slot**, with the configured exact
`FrameRate`, admitted duration and saved sequence revision. It starts
Stopped and changes only through explicit Play, Pause, Seek, Stop and
SetLoop requests. None of these operations starts a timer, decoder,
worker or production mutation.

A Seek/Pause/Resume/Stop reanchor always changes the mapping generation.
Loop passes have unique monotonically increasing pass identifiers, so
prepared samples must match both generation and pass after wraparound.
Position mapping rejects a reversed boundary slot, invalid seek,
unsupported rate, exhausted time or invalid identity. Paused maps
retain their source tick; Stopped and finished non-loop playback have
no active position. Control's compiled windows and the Runtime position
mapper are cross-compared at 50/1 and 60000/1001 in Unit tests.

**This is a deterministic slot/transport mapping helper, not a
connected playback session.** The remaining Runtime integration must
bind actual admitted native media owner tickets, prepared bounded
queues and Program output to this generation/pass; a mapped tick by
itself never proves usable decoded frame/audio data.

## Shared Runtime media admission and native preroll

The existing `LocalMediaDeckRuntimeService` now owns an internal sequence
preparation seam. It is deliberately **not a new process, decoder registry
or ControlHost operation**. The seam is available to the future authorized
sequence execution coordinator; ordinary A/B deck behavior remains unchanged.

- `LocalMediaResourceLedger.TryReserveGroup` charges every distinct
  SourceId/native pool owner **atomically** against the same Runtime capacity,
  existing resident reservations, retained consumer leases and quarantined
  owners. A group that cannot fit is rejected without partially reserving
  or evicting Program.
- `PrepareSequenceMedia` requires the genuine PreparedExecution resource
  bindings, matching Runtime format, authority revision and execution-plan
  generation. It verifies each actual local file fingerprint against the
  Control-supplied catalogue identity under a read lock. The current bounded
  resident preparation lane is limited to **32 MiB per file**, five seconds
  for fingerprint verification and ten seconds for native preroll.
  These are existing reference-preload constraints, not a general decoder
  throughput/capacity claim. Preparation may be cancelled and cannot start
  from the Program boundary.
- Native owners are created with the **existing**
  `LocalMediaFileProvider` and `LocalMediaDecodeWorker`, including the
  eight-frame ready queue, retained frame leases, joined cleanup and
  uncertain-native-owner quarantine. Source offset conversion and
  repreroll occur off the Program cadence.
- `TryTakeSequenceMedia` accepts only an expected sequence identity,
  authoring revision, generation/loop pass, native decode generation,
  exact source/timeline offset and corresponding SourceId. Missing,
  ended, too-old or stale decoded frames are rejected rather than
  fabricated. Native frame/audio buffer memory is leased; it is not
  copied into a second media cache.
- `TryTakeSequenceFrameSet` gathers one to four distinct native owners at
  a single sequence tick under the existing Runtime generation fence.
  It validates all source/timeline offsets before consuming any queued
  frames, rejects duplicate owners, and releases every already-acquired
  lease if another owner cannot supply a due frame. Callers never receive
  an incomplete frame set; no additional decoded-frame cache or Program
  mutation is introduced. The subsequent output sink must still recheck
  every lease immediately before use. Multi-source consumption remains
  bounded by the configured Runtime resource profile.
- `SequenceMediaFrameLease` retains the source/epoch/generation identity.
  `IsCurrentSequenceFrame` is the final sink fence: a previously acquired
  video/audio payload remains memory-safe while retained, but is **not
  eligible for presentation** after an authoritative seek, loop replace
  or shutdown. Native reservations remain charged until every consumer
  releases its leases.
- Registry disposal closes sequence workers as part of the existing
  managed shutdown, but does not release uncertain owners or broaden
  existing hardware/IO qualification. Interrupted native cleanup must
  quarantine the ledger owner.

The attached Windows integration fixtures use the real native MP4/AAC
decoder twice, with separate SourceIds, frame/audio payloads and native
reader owners. These checks establish independently buffered source
selection, source fencing, atomic reservation/rollback, fingerprint failure,
cancellation, seek, and retained-pool cleanup. They do **not** establish
two different pictures in one **combined Program frame**, correctly mixed
audio samples at the committed sink, or a live sequence Take.

## Required productive execution integration

These are **open acceptance obligations**, not simulated features:

1. Control must issue a versioned source/revision/plan-generation bound
   Prepare through the existing Client, ControlHost and RuntimeHost path,
   retain current catalogue leases and validate Runtime-admitted decoder
   tickets for every simultaneous source and transition overlap.
2. Connect the now-implemented internal native preparation/queue seam to
   a Control-authorized prepared plan, including explicit begin/stop/seek/loop,
   source-to-clip ownership, real runtime output admission and bounded
   recovery. Work is prepared **off** Program cadence; no second clock.
3. Runtime's existing GPU compositor and audio production mixer must
   combine at least two actual independent video frames and two actual
   audio tracks with the same rational boundary. Graphics layers,
   alpha/track order, cut/dissolve, sample-level crossfade, gaps/silence
   and last-frame policy require concrete connected sink evidence.
4. An explicit Control-authorized Preview sequence route and Take must
   retain the established production prepare/commit semantics, durable
   source identity, old-peer capability rejection and ambiguous-Take
   recovery. Saved show format v1/v2/v3 and current source identities
   must remain readable; a draft edit must not silently replace the
   executing revision.
5. Real-decoder MP4 and PCM fixture tests must correlate committed
   Program source/revision, two distinct video pixels in one frame,
   two correct independent audio windows, source PTS, resource ledger,
   seek/loop/host loss and actual output. Only then may List/Get
   advertise playback as available.

Until that end-to-end qualification exists, neither the renderer,
ControlHost API, Runtime IPC nor external operation registry is
modified to claim playback support.

## Reference safety and recovery

A missing/relinked media asset invalidates a new plan; it does not modify
running Program. Over-budget slices are rejected with exact interval
and owner-count evidence before resource reservation. Runtime admission
failures, quarantined native handles, worker cancellation, late samples,
stale sequence/production epoch and transport faults must all fail closed
in the future host integration rather than retake or replay committed
Program. Source and sample generation fences prevent using old queued
media after seek, replace, loop or Runtime restart. A lost peer must
first reestablish fresh authoritative state; automatic Take replay is
prohibited.

The existing A/B media decks, Rundown AutoGo, Program recorder, monitoring
plane, output mapping, asset catalogue, durable show migration and fixed
Runtime Program cadence remain unchanged by execution-planning work.

## Validation boundary

`SequenceExecutionPlanningTests`, `SequenceRuntimePositionMapTests`, `LocalMediaResourceLedgerTests` and `SequenceMediaPreparationIntegrationTests` exercise rational 50/60000:1001 frame
and 48-kHz sample mapping, simultaneous videos/audio, precise transition
edges, source offsets, overbudget/missing asset rejection, black gaps,
immutability and deterministic fixed-point fade envelopes.
Compiler tests are pure scheduling tests; the native preparation integration
uses real decoded RGBA/PCM frame leases but **does not composite or mix them
into committed Program pixels or samples**. The production Runtime and physical hardware require
their own integrated evidence. Run focused sequence Unit tests, then the
canonical Release developer build and complete `rtaime.slnx` tests.

Merge remains gated on exact-head `CI`, `Quality`, `Security`,
`Provider Smoke` and `Packaged E2E`. The independent hardware
qualification and known Program timing/handle findings remain separate
UNVERIFIED / NO-GO evidence until measured.
