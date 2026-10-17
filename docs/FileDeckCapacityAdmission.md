<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# File-deck capacity admission

Status: PROPOSED capacity design; four-deck scope NOT APPROVED.  
Source audit: `3b9594e469d16922b9d04aec3960b2575bd16688`, 2026-10-17.  
Implemented scale remains one or two A/B file decks. No four/eight-deck or physical
SSD/GPU/MFT qualification is granted by this document.

## Existing ownership and binding audit

| Area | Current behavior | Scaling consequence |
| --- | --- | --- |
| Runtime registry | LocalMediaDeckRuntimeService owns one Entry array, maximumDecoders 1..2; optional allowedSources must contain two distinct identities | Reuse this registry; do not multiply service classes |
| Source identity | Production A/B allowlist plus first-two known-source fence; Close does not forget those source identities | A free decoder slot does not authorize a third production source |
| Management serialization | Open, Close and Probe serialize on _management; allocation/native open/preroll/join run outside _gate | Management may wait for cleanup; Program must not perform that work |
| Program boundary | Existing source/cadence drives queue admission and bounded publication under _gate | No separate clock/renderer; lock contention is not a proven zero-latency guarantee |
| Control ownership | MediaDeckControlService authorizes source, chooses route/decode resources by source index, creates PreparedExecution from authoritative revision | UI selection cannot authorize another source or choose Program priority |
| Provider descriptor | LocalMediaFileProvider advertises two route resources and four decode resources | Descriptor enumeration is not permission to open four file sessions |
| Session reservation | Each LocalMediaRuntimeSession constructs its own TransactionalRuntime/InMemoryRuntimeResourceReservationManager; ApplyExecution requires a matching source/provider route binding | Not a host-wide byte/worker ledger; Runtime accepts route-only prepared compatibility, while Control emits route plus decode bindings |
| Worker | One background reader owner/session, one wake event; eight queued frames plus reading/admitted slots; three-frame preroll | Worker/queue bounds are per session, not a measured MFT concurrency budget |
| Native reader | MF reader/callback owns native video/audio conversion, two reusable read/flush events and decoder scratch | Actual MFT/native allocation/handle/driver concurrency needs tracing |
| Replacement | Active Program or Playing source cannot be replaced; capacity eviction chooses oldest eligible Ready/Paused/Ended off-Program entry | Deterministic inactive eviction exists; it is not byte/SSD-pressure admission |
| Failure ownership | Retiring entry remains reserved until Close succeeds; failed native cleanup retains quarantine; failed shutdown preserves owner | A timeout cannot free decoder capacity or authorize replacement |
| Optional work | Probe shares _probeReservations. Asset preflight requires zero entries and no other probe, never evicts live decks. Live Open cancels preflight then returns explicit retry requirement | No background metadata/decoder work may take Program's reservation |
| Observations | Resources counts entries plus probe reservations; counters report rejected/evicted. DecodeSnapshot reports queue/generation/starvation | An Error entry may hold no native reader; Reserved is not actual MFT count or retained bytes |
| Retained leases | Old admitted payload stays valid through Close/replacement until consumer release | Entry removal alone does not establish a global cap on retained old pool bytes |

Sources: [registry](../src/Hosts/rtaime.RuntimeHost/LocalMediaDeckRuntimeService.cs),
[slot](../src/Hosts/rtaime.RuntimeHost/LocalMediaDeckSlot.cs),
[worker/leases](../src/Hosts/rtaime.RuntimeHost/LocalMediaDecodeWorker.cs),
[session](../src/Hosts/rtaime.RuntimeHost/LocalMediaRuntimeSession.cs),
[preflight](../src/Hosts/rtaime.RuntimeHost/LocalMediaAssetPreflight.cs),
[Control composition](../src/Hosts/rtaime.ControlHost/MediaDeckControlService.cs),
[provider](../src/Media/rtaime.Media/LocalMediaFileSource.cs).
Public snapshots/commands remain the existing MediaDeck and transport contracts.

## Known managed allocation floor

Each 1920x1080 RGBA8 session preallocates ten 8,294,400-byte video slots and ten
32,768-byte audio slots. The Windows decoder additionally retains one RGBA scratch
frame. These are exact array payload calculations, not measured working set.

| Simultaneously retained session pools | Video/audio pool bytes | Plus one RGBA decoder scratch/session |
| --- | ---: | ---: |
| 1 | 83,271,680 | 91,566,080 |
| 2 | 166,543,360 | 183,132,160 |
| 4 (planning only) | 333,086,720 | 366,264,320 |
| 8 (arithmetic only) | 666,173,440 | 732,528,640 |

Exclude array/object headers, GC fragmentation, audio packets/output windows,
pipeline/external source buffers, native MFT allocations, GPU surfaces, Replay,
monitors and process baseline. Do not label these totals RAM or VRAM acceptance.
Held old-generation/session leases can outlive registry replacement. A future
ledger must charge their actual retained pools until final release; entry count
alone cannot prove the byte ceiling.

At most eight queued frames is about 160 ms at 50 Hz or 133.47 ms at 59.94 Hz.
This is queue depth, not end-to-end latency or an optimal low-latency setting.
No existing queue depth, deadline, handle ceiling or playback budget is increased.

## Single Runtime-owned policy proposal

Introduce no public API or production policy from this proposal until scope and
physical baseline are approved. The future implementation belongs in the existing
Runtime registry, composed once per host and used by deck Open/replacement, Probe,
asset preparation and later seamless preload. Do not create per-feature ledgers.

The admission request must bind Runtime epoch, source identity, prepared execution
ID, authority revision/generation, provider/resource identities, input/profile and
target format. It carries purpose (active Program, Preview/prepared or optional)
derived from committed Runtime state; client-provided priority is not authoritative.

A reservation has a unique ticket and a resource vector:

- active/prepared deck count, worker count and native-reader count;
- fixed pool/scratch bytes and retained lease/pool bytes;
- measured native memory/handle allowance and GPU bytes where applicable;
- selected-decoder concurrency group and tested concurrency quota;
- storage-device identity, measured pressure weight and observation expiry.

Approved limits must be explicit and finite. Current operational deck ceiling stays
two; requested four is rejected until an accepted profile changes the ceiling.
Do not substitute the provider's four decode descriptors for this policy. Unknown
native/GPU/storage budgets cannot be assigned zero to make a new scale fit.

Proposed management-only operations, not new wire contracts:

1. Evaluate(request, resourceSnapshot) returns an immutable decision with the
   snapshot epoch, requested vector, availability, reason and recovery.
2. TryReserve(request, decisionEpoch) atomically revalidates identities/quotas and
   inserts the ticket before any file read, allocation or decoder open. Evaluation
   alone never reserves. Stale decisions reject explicitly.
3. MarkPrepared/Commit bind the existing PreparedExecution and observed generation;
   failed prepare rolls back only that ticket after confirmed cleanup.
4. Retire retains charges while reader/worker/leases remain. Release is idempotent
   and requires owner termination plus last charged lease release.
5. Quarantine retains all uncertain charges and gives a restart-required reason.
   Cancellation is a request, not proof that native release occurred.

State sequence: Reserved -> Opening -> Prerolling -> Prepared -> Active -> Retiring
-> Released. Failure may enter Retiring or Quarantined from any owned state.
Seeking/looping fences payload generations inside the same ticket; it does not
temporarily create an uncharged second decoder. Replacement must reserve any
overlap explicitly or close the eligible inactive owner before opening its successor.
No negative/overflow vector, duplicate release or stale epoch may increase capacity.

The Program boundary reads only already prepared execution/queue state; it never
evaluates disk pressure, hashes files, reserves native resources, allocates pools,
waits for an optional job or joins a worker.

## Priority, pressure and deterministic rejection

Protect committed Program reservations first. Keep Preview/prepared work bounded,
then optional preflight/preload. New optional work is denied under missing/stale
pressure evidence, native uncertainty or a full vector. Program keeps its committed
source; optional degradation cannot reroute it. Do not terminate a healthy Program
decoder or force native release to reclaim capacity.

A future pressure weight must be calibrated per actual device and codec/profile
from source-only read latency/bandwidth/queue depth plus observed decode demand.
A drive letter, average compressed bitrate, aggregate process I/O or file hash does
not establish this weight. There is no measured weight or admission threshold in
current source. Until it exists, preserve conservative exclusion of optional work
from resident file decks. This proposal does not promise OS/MFT scheduling priority.

If inactive eviction is permitted by the approved policy, select only eligible
off-Program/non-Playing tickets, lowest purpose priority then oldest admission order;
use source identity as the final stable tie-break. Never evict failed/uncertain owners.
No partial multi-resource grant survives a rejected admission. Report which limit
or identity failed and the exact requested/reserved/available values.

Existing wire reasons remain capacity, source_invalid, source_active,
preflight_cancelling and cleanup_failed. Additional proposed budget/stale-evidence
reasons must be defined and version-reviewed with the approved API; they are not
advertised as implemented errors here. Surface deliberate close/retry/restart actions
without an automatic retry storm.

## Evidence baseline and missing measurements

The last inspected reference run at source
`5c19a9aac09aec0bd96aa99a1c95d3ec5b3bf3fb`
([run 39066412509](https://github.com/DaveBeusing/rtaime/actions/runs/39066412509))
is a prior-source hosted observation, not current-source or physical acceptance:

| Workload | Actual observation | Interpretation |
| --- | --- | --- |
| Single file, ten-minute test | 30,000 decoded frames; 28,800,000 audio samples; zero read failures/retries/audio timestamp discontinuities | Collection/audio PASS; not multi-deck or physical throughput proof |
| Two decks, 1080p50 | 600.8984182 s; handle growth 80 > 64; 1,277 missed slots; cleanup PASS | Collection FAIL; subsequent timing acceptance not reached |
| Two decks, 1080p59.94 | 601.347891 s; handle growth 66 > 64; 2,477 missed slots; cleanup PASS | Collection FAIL; subsequent timing acceptance not reached |
| Four decks | No approved workload or runner | BLOCKED_SCOPE / UNVERIFIED |
| Eight decks | No approved workload or measurements | NOT_SUPPORTED |

Aggregate process handles/private bytes and sampled queue occupancy do not identify
per-deck RAM/native handles or source-only SSD bandwidth. Current reports explicitly
leave VRAM, GPU utilization, selected MFT/acceleration, physical output/A-V and
file-only read latency UNVERIFIED. Do not divide aggregate growth by deck count
and call the result a per-deck measurement or infer a permanent leak from one delta.
Current-head tests and outcomes belong in the PR evidence, not under the prior SHA.

## Qualification and handoff

First resolve existing resource/Program-timing blockers and complete
[reference SSD hardware acceptance](ReferenceSsdHardwareAcceptance.md). Obtain
Product Owner approval for exact four-deck sources/profiles, platform and resource
ceilings, plus architecture/contract approval for shared source/resource admission.
The accepted two-deck approval is not a four-deck approval. Professional codec
scope follows the separate [decoder decision](ProfessionalDecoderDecision.md).

Record a 1/2/4 matrix for each approved 1080p50/59.94 workload with exact source,
binary/media hashes, Windows/driver/MFT identity, source-to-device mapping, cache
procedure and pressure trace. For each count record per-deck queue/preroll/worker/
decoder/lease data and correlated whole-process RAM/handles/CPU plus GPU VRAM,
source-only SSD I/O and physical output/A-V. Use identical assets/settings and
durations, retain failures and compare baseline deltas without relaxing ceilings.
Four is a future approval-dependent run; current tools only implement A/B. Eight
is exploratory only after actual four-deck evidence and separate scope approval;
no eight/sixteen-deck support claim follows from arithmetic.

Existing two-deck reference execution on an isolated Windows x64 reference host:

```powershell
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 60m -Format 1080p50 -MediaA D:\Reference\a.mp4 -MediaB E:\Reference\b.mov -RequirePhysicalPass
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 8h -Format 1080p59.94 -MediaA D:\Reference\a.mp4 -MediaB E:\Reference\b.mov -RequirePhysicalPass
```

Repeat both formats/durations and required SATA/NVMe same/separate-device cases.
These commands collect bounded evidence and deliberately fail physical acceptance
while required measurements are absent; they are not four-deck commands.

Software regression prerequisites include source identity/full-capacity denial with
Program retained; pause/seek/loop of the other deck; same-asset isolation; concurrent
Close/Open/seek; retained leases; quarantine; optional preflight cancellation and
atomic cleanup/admission. Existing tests cover these in MultiDeckLocalMediaIntegrationTests,
AsynchronousLocalMediaDecodeTests and MediaAssetPreflightIntegrationTests.
After approved policy implementation also test atomic vector reservation, stale
snapshots, overflow, partial rollback, exact-once ticket release, priority ordering
and retained-pool accounting before extending any hardware workload.

Use SDK 10.0.401 and the canonical Release build, targeted capacity/ownership tests,
full solution tests and exact-head CI/Quality/Security/Provider Smoke/Packaged E2E.
A failed or skipped packaged lifecycle, authoritative-state preservation failure,
resource excess or observed Program loss remains a release blocker. No unchanged
failure retry or larger timeout converts it into acceptance. This proposal adds
no project, state store, persisted schema, independent clock or Program renderer;
version target remains 0.5.39.
