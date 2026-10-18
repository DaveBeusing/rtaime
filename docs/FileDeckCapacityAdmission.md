<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# File-deck capacity admission

Status: implemented Runtime ticket admission; default A/B and opt-in four-owner
development profile. Expansion is authorized by [Operator experience scope](OperatorExperienceScope.md).
Software capacity is separate from physical SSD/GPU/MFT qualification. No new
codec, hardware, latency or sustained-throughput claim follows from this policy.

## One Runtime ledger

RuntimeHost composes one LocalMediaResourceLedger inside its existing
LocalMediaDeckRuntimeService. Open, replacement, Probe, resident Preload and asset
Preflight reserve there before file/native open or fixed-pool allocation.
Source identity is independent of the number of simultaneously reserved owners.
A failed ordinary open conservatively keeps its reservation until Close.

Resident tickets include Runtime epoch, source, prepared execution ID, authority
state/revision and plan generation. The Runtime pipe rejects older/different
authority after a production execution has committed. The registry validates the
prepared source/provider route before replacing an existing owner. Repeated
identical successful requests return the existing deck. Control remains the
source authorizer and supplies prepared bindings from its configured sources.

Per-session TransactionalRuntime still commits the execution contract; it does
not grant global media capacity. There is no second admission ledger in Control,
the client, optional features or the Program renderer.

## Ownership and release

Each ticket charges one owner and the full known pool/scratch payload. The worker
charges each leased slot once, irrespective of the number of retained consumers.
Close stops/cancels the worker and joins it outside the Program gate. Native
cleanup and reusable event disposal must succeed before owner completion.
Only owner completion plus the final slot consumer releases the ticket.

Old frames remain readable after Close. An empty registry slot cannot admit a
new decoder while an old pool still consumes the capacity limit. Seeking and
looping use generations inside the existing owner; they do not allocate an
uncharged replacement pool. The current ownership gate is closed at shutdown,
while final consumers may still release old leases.

Timeout, failed cancellation or failed native/file cleanup quarantines the
ticket. The ledger retains the uncertain owner and charges; later completion
cannot silently turn the failure into available capacity. Shutdown attempts
cleanup of other owners even when an optional owner fails. Use the supported
RuntimeHost restart lifecycle; cancellation is never proof of native release.

## Finite profiles and source bindings

| Profile | Decoder/pool owner capacity | Source policy |
| --- | --- | --- |
| conservative (default) | 1 or 2; default 2 | Existing distinct A/B identities and legacy known-source fence |
| development-four (explicit opt-in) | 1 through 4 | Prepared authoritative source bindings; additional sources registered through the existing Runtime registry |

Select the development profile with
`--local-media-profile=development-four --local-media-decoders=4`, or
`RTAIME_LOCAL_MEDIA_PROFILE=development-four` and
`RTAIME_LOCAL_MEDIA_DECODERS=4`. Four without the explicit profile is rejected.
The existing source registry remains bounded to eight sources. The local provider
exposes four stable route/decode descriptors; Control selects them from the
authoritative configured source order, including third/fourth slots. Existing
descriptor IDs, source IDs and route-only prepared compatibility remain valid.
Descriptor enumeration alone never grants a decoder.

The media loop publishes all admitted sources on its existing cadence. Operator
snapshots carry up to eight configured source deck observations; unloaded sources
do not imply active reservations. These are additive bounds, with unchanged wire,
persistence and contract versions.

## Known managed payload and explicit uncertainty

Each 1920x1080 owner reserves ten 8,294,400-byte video slots, ten 32,768-byte audio
slots and one known decoder RGBA scratch frame:

| Charged owners, including retained old pools | Video/audio pool bytes | Including decoder scratch |
| --- | ---: | ---: |
| 1 | 83,271,680 | 91,566,080 |
| 2 | 166,543,360 | 183,132,160 |
| 4 | 333,086,720 | 366,264,320 |

This is conservative payload accounting, not measured working set. Probe and
Preflight also reserve a full owner allowance before their smaller allocations.
Array/object headers, GC fragmentation, output/audio windows, Runtime external
source buffers, Replay, monitoring and process baseline are outside these exact
payload values. They retain their existing independently finite limits.
Native MFT/driver memory, GPU bytes and source-bound storage pressure remain
UNKNOWN, never zero. No measured native/GPU allowance is invented.

Runtime support diagnostics expose `mediaResources.capacity`,
`reservedOwners`, `workers`, `knownManagedBytes`, `retainedPoolOwners`,
`quarantinedOwners`, `unknownNativeOwners`, `nativeBytes`, `gpuBytes`,
`storagePressure` and `lastRejection`. Reserved owners include failed opens
awaiting Close, optional work and retained pools; this is not a measured MFT count.

## Program priority and optional work

Committed Runtime state supplies Program identity. Playing/Program decks cannot
be evicted or replaced. Eligible inactive eviction remains deterministic and must
finish cleanup before a successor can reserve. A retained old pool can therefore
deny replacement after the inactive entry closes.

No storage pressure measurement is currently available. Probe and background
Preflight are denied alongside resident file owners, never evict them, and run
only in the conservative idle lane. Live Open cancels active Preflight and
returns an explicit retry action without waiting for its native cleanup.
Resident Preload remains the existing bounded explicit playback preparation:
32 MiB identity verification, read lock, finite deadline/rate and no eviction.
It reserves its future resident ticket before hashing and transfers that same
ticket to Open; its identity handle stays charged through preparation release.
It is not a speculative background analysis lane.

Program consumes prepared queues and bounded publication only. It performs no
file hashing, native open, resource allocation or worker join. This does not
promise OS/MFT priority or zero lock contention.

Reasons distinguish capacity, identity/epoch/authority, intent conflict,
pressure_unknown, optional_releasing, quarantine and stopped admission, alongside
existing deck reasons. Messages direct deliberate synchronize/prepare, cancel,
close/release/retry or supported restart actions. No automatic retry storm,
forced native cleanup or timing/handle threshold increase is introduced.

## Retained evidence baseline

The last inspected reference run at source
`5c19a9aac09aec0bd96aa99a1c95d3ec5b3bf3fb`
([run 39066412509](https://github.com/DaveBeusing/rtaime/actions/runs/39066412509))
is a prior-source hosted observation, not current-source or physical acceptance:

| Workload | Actual observation | Interpretation |
| --- | --- | --- |
| Single file, ten-minute test | 30,000 decoded frames; 28,800,000 audio samples; zero read failures/retries/audio timestamp discontinuities | Collection/audio PASS; not multi-deck or physical throughput proof |
| Two decks, 1080p50 | 600.8984182 s; handle growth 80 > 64; 1,277 missed slots; cleanup PASS | Collection FAIL; subsequent timing acceptance not reached |
| Two decks, 1080p59.94 | 601.347891 s; handle growth 66 > 64; 2,477 missed slots; cleanup PASS | Collection FAIL; subsequent timing acceptance not reached |
| Four decks | Software development profile available; no physical workload measured | PHYSICAL UNVERIFIED |
| Eight decks | No approved workload or measurements | NOT_SUPPORTED |

Aggregate process handles/private bytes and sampled queue occupancy do not identify
per-deck RAM/native handles or source-only SSD bandwidth. Current reports explicitly
leave VRAM, GPU utilization, selected MFT/acceleration, physical output/A-V and
file-only read latency UNVERIFIED. Do not divide aggregate growth by deck count
and call the result a per-deck measurement or infer a permanent leak from one delta.
Current-head tests and outcomes belong in the PR evidence, not under the prior SHA.


## Software verification and physical qualification

Focused regressions extend the existing MultiDeckLocalMediaIntegrationTests,
AsynchronousLocalMediaDecodeTests, MediaAssetPreflightIntegrationTests,
ProductionIpcIntegrationTests and ExecutableHostLifecycleTests. They cover
1/2/4 actual native owner profiles, 10,000 concurrent ledger intents, retained old
pools, quarantine, identity conflicts, shutdown, explicit configuration and the
existing named-pipe Runtime API. Legacy Client/Control/Runtime A/B playback and
prepared-next tests remain required.

Test code is not a PASS result. Record exact-head executed outcomes in the PR;
queued, skipped, unavailable or failed gates do not qualify a release. Required
CI, Quality, Security, Provider Smoke and Packaged E2E remain unchanged.

Physical four-deck qualification is UNVERIFIED. Retain the prior handle/timing
failures above and complete [reference SSD hardware acceptance](ReferenceSsdHardwareAcceptance.md)
with correlated Windows/driver/MFT, media/binary/source hashes, source-to-device
mapping, storage pressure, RAM/handles, VRAM and physical A/V observations.
Existing A/B physical workload commands remain:

```powershell
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 60m -Format 1080p50 -MediaA D:\Reference\a.mp4 -MediaB E:\Reference\b.mov -RequirePhysicalPass
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 8h -Format 1080p59.94 -MediaA D:\Reference\a.mp4 -MediaB E:\Reference\b.mov -RequirePhysicalPass
```

They are A/B evidence collection, not four-deck hardware qualification. Professional
codec scope follows the separate [decoder decision](ProfessionalDecoderDecision.md).
