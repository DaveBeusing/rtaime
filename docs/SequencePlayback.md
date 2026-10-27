<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Sequence playback and transition execution

## Integration status

Sequence authoring is durable and supports simultaneous Video, Audio and
Graphics tracks. Source/revision-bound planning, native Runtime admission,
bounded decoder preroll, authority-fenced shuttle and the separately versioned
Client -> ControlHost -> RuntimeHost **sequence output** integration are
implemented. The current admitted software path composes native Preview
pixels and 48-kHz PCM on the existing Runtime clock, and permits an explicit,
ControlHost-authorized Preview-to-Program Take. Native `PREPARED` or shuttle
`PLAYING` alone still proves no presented output. Real source/readback,
owner and recovery tests are required to qualify the integrated path.

The legacy Sequence List/Get playback fields deliberately remain
`UNAVAILABLE` until their public status contract and compatibility migration
are accepted. This compatibility value is **not** a claim that the opt-in
`rtaime.sequence-output.v1` Preview/Take route is absent, nor a claim that
physical devices or additional formats are qualified.

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

Native staging currently has no frame-rate conversion stage. Preparation
rejects any saved sequence whose rational authored cadence differs from the
committed Runtime Program cadence, rather than incorrectly interpreting
50-fps and 60000/1001-fps frames as interchangeable. Both native and
Control-side mappings continue to use the same one Program clock.

After native preroll, Control rechecks the saved authoring revision,
catalogue revision, committed Control/Runtime authority and exact RuntimeHost
epoch before acknowledging PREPARED. A changed identity triggers bounded,
explicit native cancellation. Failed or ambiguous cancellation retains the
Control owner fence and cannot be reported as cleaned; an interrupted
response also requires confirmation before releasing the prepared owner.

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

Control now transmits an additive, bounded
`rtaime.sequence-execution-snapshot.v1` alongside native preparation:
exact source IDs, saved authoring revision, rational cadence, contiguous
compiled slices, clip/source offsets, fingerprints, layer order and
transition/bus metadata. The Runtime validates that every active decoded
clip matches the admitted native source, fingerprint and time offset
*before* opening decoders. The finite transport budget is 256 KiB,
256 time slices, 32 clips per slice and 2,048 total slice-clip references;
a larger show is rejected without a native reservation instead of
silently dropping clips or exceeding management IPC capacity.

The retained snapshot can now select genuine leased Video/Audio clip
sets for an exact existing Runtime Program slot and current shuttle
generation. Synchronized audio/video clips sharing one native asset and
time offset share a decoder lease. Black/silent gaps select zero native
owners. Stopped, stale-generation or invalid native frames cannot
escape the selection seam; acquired leases are explicitly owned by
the caller. **This is an internal decoder-to-compositor input boundary,
not committed Preview or Program output.** Graphics still require their
authoritative output/compositor connection, and the current isolated
reference GPU/PCM composer has not been made a Program sink.
Authored media gaps with zero selected native owners are now composed as
black GPU frames and exact 48-kHz silent sample windows through the existing
AudioProductionEngine; this remains non-authoritative without the output hook.

External-control native preparation uses a dedicated, bounded single-attempt
gRPC request deadline (at least 45 seconds, or a longer configured limit)
on both client and server instead of the ordinary five-second interaction
deadline. Lost preparation responses are **not automatically retried**
because native resource staging is not safely replayable. The native
operation remains serialized and all other control RPC deadlines are unchanged.

States `PREPARED`, `REJECTED`, `STALE`, and `CANCELED` describe
the **native preparation lifecycle only**. In particular, they never
promote `SequenceList.PlaybackState` or `SequenceSnapshot.PlaybackState`
from `UNAVAILABLE`. The separately versioned shuttle staging below
adds cue and native transport intent; only the distinct, explicit sequence
output contract described below can authorize Preview and Take.

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
successful media change. Control rechecks the same authority set **after**
potentially delayed native preroll too; a newer saved/committed revision
cannot be acknowledged by an older shuttle command.

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
Cancel even when individual decoder generations change. If native repreroll
fails but the former worker pool is **confirmed cleaned**, the Runtime
retains only a small original-identity cleanup receipt with `CANCELED`
state. Control can observe or explicitly cancel the receipt and release
its previous preparation owner before a new Prepare. Failed native
cleanup remains quarantined and requires supported RuntimeHost recovery.
Ambiguous commands are never automatically replayed.

`SequenceShuttleObservation` states `STOPPED`, `PAUSED`,
`PLAYING`, and `ENDED` are **native position/queue management states**,
not visible video or actual produced audio. `NativePrepared=true` means
all prepared native queues have evidence for the currently expected
decoder generation and loop pass. At a loop wrap with no confirmed new
preroll, `NativePrepared=false` rather than a repeated old frame.
The shuttle-only observation's `OutputState` remains **`UNAVAILABLE`**;
it is not an output qualification signal. The current dedicated
`SequenceOutputObservation`, reached only through explicit Preview
activation and authorized Take, reports its own source-bound states.
`SequenceList/Get.PlaybackState` remains `UNAVAILABLE` for public
compatibility. The output compositor/mixer validates native leases
before publishing pixels or audio.

Windows integration tests exercise native queue ownership through
Cue, frame-step, Play and Stop, plus authenticated Client/Control/Runtime
roundtrips, rejection of older optimistic revisions, changed
catalogue/authoring authority and unchanged Program state.
These shuttle tests independently establish authoritative transport staging;
the distinct native output, Program sink and mixed-source integration tests
below provide the additional real pixel/PCM evidence.

## Isolated two-source image and audio composition

`SequenceFrameComposition` is an internal, non-authoritative Runtime
materialization seam. It takes previously acquired, generation-fenced native
frame leases together with the **existing Runtime slot's** FrameTiming and
48-kHz sample window. It neither opens a decoder nor advances its own clock.

- Actual native RGBA pixels are copied into finitely reserved reusable CPU
  scratch buffers, uploaded to the existing `GpuProcessingProvider`, and
  combined as ordered source-over layers. The managed reference and CUDA
  implementations retain the same `GpuCompositeRequest` semantics.
- Actual native stereo Float32 samples are preweighted using exact
  240,000-tick/second sequence transition boundaries and 5-tick PCM
  sample positions, then combined and clipped through the existing
  `AudioProductionEngine.ProcessBus`, not a second mixing engine.
- Current sequence identity, authoring revision, native decoder generation
  and loop pass are checked before rendering and again after image/audio
  processing. Invalid input, unavailable PCM/RGBA, incorrect source format,
  unconfigured audio identity and lost native ownership fail closed.
  Scratch PCM is not published until both media paths succeed.
- The reference implementation preallocates no more than eight reusable
  video staging buffers and eight audio tracks of 2,048 stereo samples.
  GPU uploads/composites and readback remain real provider operations.
  The isolated composer alone does not establish Program delivery; the
  integrated Runtime output path below now uses this seam on the existing
  Program clock. Its returned GPU frame remains disposable caller-owned evidence.

`SequenceFrameCompositionIntegrationTests` exercises two genuine native
MP4/H.264/AAC decoder owners, their actual combined RGBA readback through
the reference GPU backend and actual mixed PCM through the established
audio-production engine. It also tests complementary sample weights and
denial of output after native seek/generation replacement. This **isolated**
source-to-image/audio proof must be distinguished from the additional
real Preview/Take and two-distinct-source Program readback/PCM tests below.

## Preview/Program native source integration (qualification pending)

A separately negotiated `rtaime.sequence-output.v1` capability now authorizes a
**previously prepared and revision-bound native sequence** for the current
Preview route through ControlHost. Native `PREPARED` and shuttle `PLAYING`
never implicitly activate a source. The Operator must first select an
existing otherwise-unused Preview source through the established ControlHost
source-selection transaction; native output activation is rejected when
that source already has a loaded media deck or is active on Program.

The bounded Runtime source adapter selects the exact compiled clips at
the existing Runtime Program frame slot, composes admitted genuine native
RGBA via the retained GPU provider and mixes decoded stereo 48-kHz
Float32 through the existing AudioProductionEngine. It feeds the resulting
pixels and PCM into the established Runtime source buffers. The existing
Program compositor, route, output-mapping, recorder, network, replay and
monitoring consumers remain the only downstream production path.
Before the first completed Preview boundary, native source ingress
remains `UNAVAILABLE` and cannot by itself publish `PREVIEW_READY`.
Subsequent readiness is retained only within the existing one-frame
confirmed-Preview recency window. A genuine `BUFFERING` failure is not
converted into readiness by an older delayed boundary; source/revision
guards remain mandatory before arming Take.
A supported silent video/audio gap results in a real black frame and
sample-exact silence; native source failures fence stale generations and
mark the source unavailable rather than manufacturing frames.

An explicit Preview-to-Program CUT uses the same Control
`Stage -> Runtime Prepare/Commit -> Control Confirm` mutation as normal
production sources. Before invoking that mutation Control requires
confirmed Preview output and an exact source/revision/generation arm in the
Runtime; an ordinary CUT to a bound sequence without the arm is rejected.
The native promotion and the normal Runtime commit execute within one
existing Program-boundary lock, so a frame cannot interleave between those
two operations. ControlHost also serializes authored sequence edits and
ordinary production mutations with its existing mutation gate, then holds
the exact catalogue revision lease until the Take outcome is known.
The native management/output locks span both the Runtime commit and native
owner promotion, preventing a concurrent Seek/Cancel between validation
and Program activation. Any accepted shuttle transport mutation now revokes
previously confirmed Preview samples and a pending native Take **before**
decoder repreroll; Seek, Stop and explicit owner cancellation cannot carry
an older `TAKE_PENDING` ticket into a later Program commit. Already
authorized Program routing is not silently retaken by the shuttle. The catalogue lease is not re-entered: rereading
a catalogue snapshot while it is held would deadlock its non-reentrant
semaphore. Control's follow-up Runtime confirmation is idempotent.
A lost response must be resolved using the read-only output observation
and explicit `RecoverTake` for the already committed successor; Take
itself is never replayed or retried with a new command identity.
Only actual post-Take ProgramBoundaryResult readback pixels plus a produced PCM
payload can report `PROGRAM_CONFIRMED`. A delayed pre-Take Program boundary
cannot confirm or falsely fail a newer Take even if its callback arrives
after the commit. Once a qualifying post-Take boundary reports an
unconfirmed Program sink, the native sequence source is withdrawn and the
failure stays terminal for that prepared session: later successful source
ingress alone cannot restore `PROGRAM_CONFIRMED`. Explicit cancellation
and fresh authorized preparation are required.

The additive external Control operations reserve tags 126–129 for
Preview activation, Take, output observation and explicit recovery.
Named Pipe and authenticated gRPC clients use the same ControlHost
authority; Observer is read-only, Operator/Administrator may mutate.
Older peers reject unknown capabilities, with no changes to saved Show
or authoring formats. `SequenceList/Get.PlaybackState` remains
`UNAVAILABLE` pending complete acceptance and migration of that
public status contract.

**Current admitted limits:** software reference format RGBA 1080p50,
at most four native owner requests per frame, eight video layers and
eight 48-kHz audio tracks, program-bus audio only. The playout hook
currently rejects Graphics clips and unsupported bus mappings instead
of publishing an incomplete show. Reverse/multirate shuttle, long-running real-time cadence headroom,
physical SDI/NDI/GPU hardware activation and source-format expansion
remain separate qualification obligations. Bounded automatic native loop
repreroll is implemented below, but exact-source loop/Program qualification
remains PENDING, not a general compatibility claim. The existing Preview source
is temporarily reused as the bound sequence identity; a dedicated
catalogued production sequence source is not yet a proven durable
migration. This stage must remain **Draft / NO-GO** until exact-head
Windows, actual two-distinct-source RGB/PCM and failure/recovery E2E
results pass and all five Required Gates are green.

## Required productive execution qualification

The source-bound implementation is integrated into `master`.
Qualification and capability admission still require exact-source Windows
evidence and separate physical proof for any advertised hardware mode:

1. The existing Program clock and native ledger select real decoded clip
   groups and feed GPU composition and Float32 PCM into the committed Runtime
   source path. Verify on the exact source that the ordinary Preview
   monitoring/render path and Program output actually show the expected
   two distinct sources and correct audio, including nonlinear frame timing.
2. Prove the one normal Control Stage / Runtime Prepare-Commit / Control
   Confirm Take, authorization denial, optimistic revision identity, saved
   edit and catalogue relink exclusion, and non-replayed uncertain-result
   recovery through actual Named Pipe and authenticated gRPC hosts.
3. Qualify both pre-commit rollback and post-commit fail-closed recovery
   under concurrent native Cancel, Seek, host replacement and lost replies.
   Only the same exact already committed successor can be recovered; the
   software must never start a second Program transaction to guess.
4. Verify actual transition layer order, dissolve, sample-level crossfade,
   black/silent gaps, last-frame handling, loop repreroll, audio bus mapping,
   source PTS and independent native owner release. Graphics source
   integration, wider codecs/resolutions, reverse shuttle and professional
   hardware I/O require additional explicit admission, not invented output.
5. Confirm the complete Windows Release solution build, focused native
   two-source H.264/AAC tests, all five exact-head Required Gates and
   packaged lifecycle. Physical SDI/NDI/GPU/driver, multi-output latency
   and long-running headroom remain UNVERIFIED without measured devices.

The feature is not an accepted public sequence playback capability yet:
`SequenceList/Get.PlaybackState` and the historical preparation/shuttle
observations deliberately keep their compatibility value `UNAVAILABLE`.
The separately versioned sequence output observation is the only opt-in
source-bound Preview/Take evidence while acceptance is incomplete.

## Reference safety and recovery

A missing/relinked media asset invalidates a new plan; it does not modify
running Program. Over-budget slices are rejected with exact interval
and owner-count evidence before resource reservation. Runtime admission
failures, quarantined native handles, worker cancellation, late samples,
stale sequence/production epoch and transport faults must fail closed
in the integrated host path rather than retake or replay committed
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
Compiler tests are pure scheduling tests. The integrated repository also
contains distinct actual native H.264/AAC source composition, Preview
monitoring, Program readback/PCM and Control/Runtime Take recovery
qualifications, plus source/lease and authority-fence regressions. Their
presence in source is not a PASS: each requires completed exact-head
Windows execution and coherent evidence. Neither software-only sink
proof nor a CI green check is a physical hardware qualification.
Run the focused sequence tests, canonical Release developer build,
complete `rtaime.slnx` tests and packaged Windows gates.

Any further code integration or release acceptance remains gated on
exact-head `CI`, `Quality`, `Security`, `Provider Smoke` and
`Packaged E2E`. The independent physical hardware qualification and
long-running Program timing/handle headroom remain separate
UNVERIFIED / NO-GO evidence until measured.
