<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Reference SSD hardware acceptance

The existing [SSD playback runner](../build/qualification/Invoke-SsdPlaybackQualification.ps1)
is the only endurance execution path. Hardware acceptance remains **UNVERIFIED / NO-GO**.
No SATA/NVMe class, controller, decoder or GPU tuple is declared supported by this document.
There is no new decoder, Program clock, persistence format or release binding type.

## Evidence boundaries

| Observation | Automatic collection | Acceptance still needed |
| --- | --- | --- |
| Source/build/media | Exact commit, compiled metadata, two file hashes/sizes, actual input probes, elapsed duration, TRX | Preserve original artifact bytes and run/attempt |
| Storage topology | Direct local partition to disk, hashed UniqueId, disk number, partition offset, NTFS/filesystem and free space before/after | Isolated reference-machine provenance; storage association is observational |
| SSD medium/bus | SSD only when a unique physical-disk serial association reports SSD; SATA/NVMe bus recorded separately | Do not infer SSD from bus, model name or drive letter |
| Controller/firmware | Disk firmware, associated SCSI controller PnP and signed-driver version/date/INF/provider when exposed | Unexposed RAID/Storage Spaces/USB/NVMe association remains unknown |
| Decoder | Native read/decode distributions and actual decoded progress | Selected transform CLSID, binary hash/version, acceleration mode and driver |
| Source disk I/O | None; native-read duration includes decode and scheduling | File-attributed I/O trace, cache hits, storage service latency and queue depth |
| GPU/output | GPU inventory and driver only | Utilization, VRAM, physical output drops, reference lock and measured A/V |
| Timing/resources | Program missed slots, sampled observer distributions, bounded queues, handles/RAM, long-profile hourly windows | Physical output continuity and uninterrupted hardware plateau |
| Recovery | Controlled worker faults and native missing/truncated-copy Stop/close/reopen at both V1 formats | Actual driver/device disappearance and sustained late reads under load |

The hardware tuple in `support-profile.json` binds the exact source, media/probes,
Program format, environment and outcome hashes, storage identities and GPU inventory.
Its acceptance rows retain observed, failed and missing dimensions individually.
Generated inputs and unavailable mappings retain reasons and topology UNVERIFIED.
Device keys are hashes for correlation, not attestations; fabricated JSON cannot
establish real equipment. Serial numbers and source paths are not redistributed.

The environment sidecar schema is 1.1; playback schema remains 1.0. The independent
environment verifier checks two decks, before/after topology, file size/modification,
firmware/controller identity, timestamps bracketing playback and the exact outcome
hash. Reparse paths, mounted directories and UNC paths are not assigned the disk
behind their drive letter. Unknown identity is preserved rather than guessed.
A mapping change fails correlation; it does not become a supported new device.

## Reference-machine execution

Use the dedicated Windows x64 `rtaime-ssd` machine with rights-cleared, expendable
copies and the pinned SDK. Record its operator-approved test plan, available
instrumentation and acceptance limits before collecting evidence. Keep installed
production data, production volumes and credentials outside the fixture.

1. Check out the exact candidate commit with a clean tree. Retain source SHA,
   product/build identity and the approved plan alongside all raw artifacts.
2. Record Windows build, physical SSD model/firmware, controller topology and
   driver, NTFS volume, free capacity, GPU driver, selected decoder and output device.
   Check the automatic mapping against physical inventory. For RAID/Storage Spaces,
   junctions or missing associations, retain an independent mapping; automatic
   topology stays UNVERIFIED.
3. Select two qualified files with documented bitrate/GOP/audio/CFR/VFR profiles.
   Use identical bytes for comparisons. A and B may share a disk even when their
   drive letters differ. Separate-device tests require distinct physical identities.
4. Execute both existing Program formats and each required real elapsed duration.
   Start with 10m, then 60m and 8h on each approved SATA/NVMe tuple. Retain all failures;
   do not select only a later green run. 24h/72h are separate explicit runs.
5. Repeat with both files on the same SSD, then separate SSDs. Keep source, file
   hashes, decoder, output and other load constant; record each change as a new tuple.
6. Retain the complete run directory, trace hashes, UTC time alignment and any
   external output/reference measurements. The workflow uploads JSON/TRX only;
   trace retention must use the approved restricted evidence store.

From the repository root, after the canonical build:

~~~powershell
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 60m -Format 1080p50 -MediaA D:\Reference\a.mp4 -MediaB D:\Reference\b.mov -NoBuild
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 8h -Format 1080p59.94 -MediaA D:\Reference\a.mp4 -MediaB E:\Reference\b.mov -NoBuild
~~~

These paths illustrate placement only. Verify the reported topology; D:/E: do not
prove distinct devices. Input frame rate/probe and Program normalization format are
separate observations. The existing runner uses virtual Media I/O, continuous Replay
and repeated Preview/TAKE. It does not exercise physical scan-out or simultaneous
recording. A realistic recording/output workload needs the existing approved
production/reference qualification path and independently correlated evidence;
do not describe this runner as having exercised it.

The opt-in SSD workflow now selects the Program format. Hosted reference CI runs
ten minutes at both cadences; hosted storage is not a reference SSD. No 60m/8h
hardware run has been executed merely by adding workflow inputs.

## Cache, decoder and source-only I/O procedures

**Warm:** retain the runner's pre-playback media hashing time, a repeat read/warm-up
procedure and the subsequent trace. Hashing reads both complete files. A warm
condition must be observed, not inferred from a label.

**Cold:** the existing runner hashes immediately before opening media and therefore
cannot establish cold first-touch playback by default. Do not call a reboot followed
by this hashing path COLD. An approved isolated procedure must prove source cache
misses during the measured playback interval without a global cache purge, and
retain its order relative to hash validation. Until such instrumentation/procedure
is approved and executed, record COLD as UNVERIFIED. No automatic cache eviction
or device-disable command is provided.

**Source-only latency:** use the reference machine's approved Windows ETW/WPR trace
profile, with kernel file/disk I/O and process/thread correlation. Retain tool/profile
version/hash, clock basis, lost-event count and measurement interval. Attribute reads
to the actual two source files and testhost/decoder ownership; exclude probes, SHA
hashing, journals, checkpoints, recording writes and unrelated processes. Distinguish
cached file-read completion from physical disk service. Report per-file and per-device
counts, bytes, P50/P95/P99/max, queue depth, cache attribution and trace completeness.
Any missing attribution/lost required events leave source-only latency UNVERIFIED.
Never rename Decode or NativeVideoRead duration to SSD latency.

**Selected decoder:** use the approved Media Foundation trace tooling on that machine
to retain the selected source-reader transform CLSID, actual loaded binary path
(redacted for distribution), binary SHA/version, input/output media types and
hardware/software acceleration evidence. An installed codec enumeration or DLL
loaded into the process is not proof that it decoded either deck. Retain Windows,
GPU/decoder driver and transform changes as new tuple identities. The production
source-reader is not reconfigured for qualification.

**Pressure/late reads:** use an approved, bounded external read workload on disposable
reference files only, with declared duration and free-space limits. Retain actual
source-read distributions and workload timing. No automated disk-filling writer,
unbounded stressor or production-volume pressure is permitted by this runbook.
A controlled blocked decoder demonstrates software isolation, not slow SSD behavior.

## Failure and recovery record

For every scenario retain fault onset/time, both source/deck identities, native
error code, decoder queue/generation, Program progress/misses, output/A/V measurement,
accepted/persisted authority revisions, cleanup result and recovery attempt/result.
Do not restart into a fresh empty production to hide a recovery failure.

| Scenario | Safe procedure | Required recovery / current limit |
| --- | --- | --- |
| Missing file before open | Use a nonexistent fixture path, then the original verified file | Native failure, release reservation, explicit reopen/Play; other deck remains admitted |
| Truncated file before open | Truncate a private closed copy only | Failure classified, owner released, original hash preserved, clean Stop/close/reopen |
| Sharing denial | Hold an exclusive handle to a disposable copy | Explicit open failure and release; retry only after releasing external lock |
| Truncated tail during decode | Approved isolated copy and fault time | Measure actual reader error/EOF, fencing and cleanup; currently UNVERIFIED |
| Storage missing/disconnected | Dedicated removable reference storage with no production data; operator-approved lab procedure only | Observe actual error, no stale-generation admission, healthy other deck/Program, graceful cleanup and supported restart/reopen; currently UNVERIFIED |
| Device disappearance under open handle | Separate approved lab intervention; never automated device disable/removal | Retain driver/native trace, worker join/quarantine, reference/output continuity and authority durability; currently UNVERIFIED |
| Sustained late reads | Bounded approved pressure procedure above | Distinguish source stall, decoder starvation, Program missed slot and physical output loss; currently UNVERIFIED |
| Repeated recovery/long plateau | Same tuple, repeated Stop/close/reopen, real 8h window | All reservations/leases released, existing handle/RAM limits and post-warmup hourly plateau; no forced GC or enlarged budgets |

The native regression uses private missing/truncated copies at 50 and 60000/1001,
four recovery cycles, another playing deck, final owner count zero and unchanged
original bytes. It is not hot-unplug, real-time Program or driver-disappearance proof.
A bounded cleanup failure, unpersisted accepted state, unexplained Program/output
loss or missing long plateau blocks acceptance regardless of a later smoke PASS.

## Source-bound readiness handoff

~~~powershell
./build/qualification/Test-SsdPlaybackEvidence.ps1 -EvidencePath <run>/ssd-playback.outcome.json -ExpectedSourceCommit <sha> -ExpectedProfile 60m -ExpectedFormat 1080p50
./build/qualification/Test-SsdPlaybackEnvironment.ps1 -EnvironmentPath <run>/environment.json -ExpectedSourceCommit <sha> -OutcomePath <run>/ssd-playback.outcome.json
./build/release/New-ReleaseReadinessIndex.ps1 -ExpectedSourceCommit <sha> -SsdPlaybackEvidencePaths @("<run>/ssd-playback.outcome.json") -SsdPlaybackEnvironmentPaths @("<run>/environment.json") -OutputPath <new-index-path>
~~~

Use a separate retained index per hardware/format tuple; the duration-index input
does not merge multiple runs of the same duration into a codec/device support claim.
The inventory contributes FAIL for inconsistent evidence or observed Program misses,
otherwise UNVERIFIED. It cannot contribute physical PASS.

The existing qualification-evidence policy is authoritative:
CUDA_REFERENCE, MEDIA_IO_REFERENCE and TIMING_REFERENCE_SOAK retain their established
payload validators, exact-source hashes, run/attempt, product/stage and twelve release
requirements. Bind actual passed physical payloads through those paths. An SSD
support projection or edited environment cannot substitute for them. Do not change
DEV/Stable, support policy or release trust as part of collection.

No new physical tuple has been executed or accepted here. Source-only disk latency,
selected MFT, cold/warm/pressure proof, simultaneous physical output/recording,
device disappearance, 60m/8h reference execution and associated recovery/plateau
remain UNVERIFIED. Previously observed checkpoint drain and Program timing failures
require attribution and repair before production acceptance; later software passes
do not erase earlier evidence. Next dependency: safe reference-machine execution
and fault attribution, followed by exact-source existing physical release binding.
