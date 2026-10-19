<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Sequence playback and transition execution

## Integration status

Sequence authoring is durable and supports simultaneous Video, Audio and
Graphics tracks. Source/revision-bound planning, native Runtime admission,
bounded decoder preroll and a versioned Client -> ControlHost -> RuntimeHost
**preparation-only** lifecycle are implemented. The product still has **no
live multitrack sequence playback authority**. Native PREPARED means neither
Preview playback nor Program pixels/audio, Take or committed execution.
Sequence List/Get deliberately retain playback state `UNAVAILABLE` until
the real output path and recovery are integrated and qualified.

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
preparation seam. It is deliberately **not a new process or decoder
registry**. The new ControlHost preparation operations call this existing
Runtime ownership path without creating a production mutation. Ordinary
A/B deck behavior remains unchanged.

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
- `TryTakeSequenceClipFrameSet` resolves up to 32 selected Video/Audio
  clips at one exact sequence tick against the already prepared catalogue
  identity, fingerprint and source/timeline offset. Multiple clips representing
  matching video and audio from the same native asset/offset reuse a single
  frame lease, while distinct source offsets require distinct admitted owners.
  Caller-owned bounded spans retain the clip-to-owner index mapping. Duplicate
  clip identities, forged fingerprints, mismatched offsets and stale sequence
  generations are rejected before native read. If an already validated native
  frame-set read encounters buffering or failure, previously acquired leases
  are disposed and no partial result is published; dequeued native frames
  cannot be put back into the worker queue. Every selected video clip also
  requires actual RGBA payload and every selected audio clip actual decoded
  PCM; missing media invalidates and releases the complete group. No Program
  routing, video
  composite, audio mix or playback capability is activated by this seam.
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

## Versioned client-to-Runtime native preparation

`rtaime.sequence-preparation.v1` is a separate, bounded observation and
resource-staging capability. `SequencePreparationClient` uses the existing
Named Pipe or authenticated external gRPC path. Additive Control operation
tags 120-123 are Prepare, preparation Get, preparation Cancel and read-only
context discovery; prior
operation tags 114-119 and saved show format v3 are unchanged. Observer
role may read current preparation observations; only Operator/Administrator
may prepare/cancel. Old peers reject the unknown capability explicitly.

Clients first read `SequencePreparationContext` to obtain the current
saved sequence revision and RuntimeHost epoch. This is not a decoder readiness
guarantee. A request contains only a saved sequence ID, exact authoring revision and
previously observed RuntimeHost identity. It cannot supply file paths,
source IDs, GPU resources or a production command. Control loads the
canonical show and catalogue, validates all referenced assets, audio buses
and fixed-point execution windows, chooses genuine available media.route
resources and binds a fresh prepared-execution identity to the current
committed production authority. One staged sequence is allowed per Control
lifetime. Every distinct asset/source-timeline offset requires a retained
native owner; staging conservatively rejects a whole sequence needing
more than four total owners, even if fewer overlap in any one interval.

The staging operation has its own serial Control gate and a separate
bounded Runtime-IPC request lane: neither the global Control mutation gate
nor the ordinary Program/control IPC request gate is held during native
fingerprint verification and preroll. Control rechecks saved and production revisions
after Runtime confirms preparation and cancels stale work. A subsequent
Get also compares the original catalogue revision: removing or relinking
an asset makes the prepared native decoder group STALE and requires
confirmed cancellation, even when authoring and production are unchanged.
A committed production can legitimately have initial revision zero;
this state is accepted only when both hosts agree on its exact authority
identity and revision, with a separate positive preparation generation.
RuntimeHost
validates its epoch and the currently committed production authority,
reserves via the existing global ledger, opens/prerolls native owners,
and returns only identity/generation/resource count and typed status.
Get/Cancel are fenced by that exact identity and epoch. No original
media bytes or paths are returned to the Client.

States `PREPARED`, `REJECTED`, `STALE`, and `CANCELED` describe
the **native preparation lifecycle only**. In particular, they never
promote `SequenceList.PlaybackState` or `SequenceSnapshot.PlaybackState`
from `UNAVAILABLE`. The separately versioned shuttle staging below
adds cue and native transport intent, but **not** output or Take.

## Authority-fenced native shuttle staging

The optional `rtaime.sequence-shuttle.v1` capability extends the exact
prepared sequence from `rtaime.sequence-preparation.v1`. It does **not**
replace authoring or declare an output renderer. The Client uses the existing
Named Pipe or authenticated external-control gRPC path with additive tags
124 (observation) and 125 (action). Operator or Administrator may Cue, Play,
Pause, Stop, Seek, step one rational frame backward/forward and change loop
intent; Observer may inspect read-only state. All mutating commands include
the last confirmed shuttle state revision and complete original preparation
reference. An ambiguous result is observed again, never blindly retried.

ControlHost revalidates current committed production, saved sequence revision,
catalogue revision and exact RuntimeHost lifetime before forwarding each
request. Any changed authority invalidates the prepared owner and requires
explicit new preparation; it cannot adopt a newer editor draft. RuntimeHost
also checks the current committed production authority. Old Runtime peers
reject the additive operation rather than treating an unknown command as a
successful media change.

The Runtime-owned `SequenceRuntimePositionMap` uses the **existing**
Program frame sequence as its only time base. Cue/Seek and frame-step use
integer 240000-tick rational time; Pause/Stop/Loop generation changes
fence previous native queues. Native seek and preroll use the existing
bounded `LocalMediaDecodeWorker` owners and the shared resource ledger
on the management path, never a second timer or synchronous native read
on Program cadence. A confirmed preroll reanchors the logical sequence
position to the actual current Runtime Program slot **without changing**
its native generation. Stopping retains native staging but invalidates
the previous playback generation until an explicit new preroll. The
original preparation reference and its generation remain stable until
Cancel even when individual decoder generations change.

`SequenceShuttleObservation` states `STOPPED`, `PAUSED`,
`PLAYING`, and `ENDED` are **native position/queue management states**,
not visible video or actual produced audio. `NativePrepared=true` means
all prepared native queues have evidence for the currently expected
decoder generation and loop pass. At a loop wrap with no confirmed new
preroll, `NativePrepared=false` rather than a repeated old frame.
`OutputState` is always **`UNAVAILABLE`**. There is no committed
sequence Preview picture, output bus, Program Take, or UI playback
capability in this stage, and `SequenceList/Get.PlaybackState` remains
`UNAVAILABLE`. A real compositor/mixer sink must still validate every
lease after queued work and before presenting any pixel/sample.

Windows integration tests exercise native queue ownership through
Cue, frame-step, Play and Stop, plus authenticated Client/Control/Runtime
roundtrips, rejection of older optimistic revisions, changed
catalogue/authoring authority and unchanged Program state.
These tests establish authoritative transport *staging*, not a combined
two-video/two-audio broadcast output.

## Required productive execution integration

These are **open acceptance obligations**, not simulated features:

1. Connect the admitted source/revision plan to real Runtime source-to-clip
   ownership, native Play/Pause/Seek/Loop on the one existing clock, and
   the committed compositor/audio sink with explicit recovery. Preparation
   and its bounded native queues exist, but no playout is activated.
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

Until that end-to-end qualification exists, the new ControlHost,
Runtime IPC and external operations are **preparation-only**. The renderer,
program routing and advertised sequence playback are unchanged.

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
Compiler tests are pure scheduling tests; the native preparation and
Client/Control/Runtime integration exercise real catalogue-authorized
MP4 native admission and resource cleanup but **do not composite or mix
decoded RGBA/PCM into committed Program pixels or samples**. The production Runtime and physical hardware require
their own integrated evidence. Run focused sequence Unit tests, then the
canonical Release developer build and complete `rtaime.slnx` tests.

Merge remains gated on exact-head `CI`, `Quality`, `Security`,
`Provider Smoke` and `Packaged E2E`. The independent hardware
qualification and known Program timing/handle findings remain separate
UNVERIFIED / NO-GO evidence until measured.
