<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Managed Allocation, GC and Program Latency Qualification

## Sustained dual-deck Replay process-memory observation

The SSD qualification path is separate from the Program-only managed-allocation
matrix below. An opt-in `RTAIME_SSD_MEMORY_DIAGNOSTIC=1` sidecar at the existing
dual-deck test checkpoints captures no more than five structured process/GC/Replay
observations (64 KiB total) during the **unchanged** 600-second 1080p50 or
1080p59.94 workload. It measures process private/working-set bytes, handles,
thread counts, live managed-byte estimate, cumulative allocations, GC pause and
collection counts, last-completed-GC heap committed/fragmented/LOH/POH evidence,
Replay queue depth/high-water/capacity, encoded segment retention, and actual
writer lifecycle opens/finalize/abort and active owners. The Media Foundation
writer also reports successful native sink acquisitions/releases, MF startup/
shutdown calls and active NV12 scratch buffer rentals at sparse checkpoints.
These are software resource-owner balances, not ETW/native allocation stacks. A fifth observation
is made after Runtime/Control shutdown. No per-frame statistics collection,
forced GC, producer allocation or changed acceptance bound is introduced.

The native/managed attribution is **comparative**, not a naive subtraction:
process private bytes include committed managed heap, native allocations and
other overhead; last-GC committed values need not coincide exactly with process
samples. The original 59.94 failure on `a1c7fa4a` was 966.57 MiB versus the
unchanged 512 MiB limit, while the isolated observation-source `1678848b`
successfully completed 600 seconds with **521,105,408 bytes (~497 MiB)** of
assertion-baseline growth against the original 512 MiB ceiling. The sparse
sidecar's first-to-last playback snapshots grew ~290 MiB over a shorter
measurement interval, not the test baseline. The unchanged repeat on the
**same** `a1c7fa4a` source also passed with 304,893,952 bytes (~291 MiB),
showing source-matched variability. Significant private bytes were reclaimed
after shutdown. The differing source/run
means a current-master non-leak conclusion is **UNVERIFIED**. The newer NV12
scratch-pool change must be qualified on its own exact commit; production Replay
backpressure and continuity evidence must not be conflated with deck decode
failure (see [Recording Foundation](RecordingFoundation.md)).

The existing Windows [Reference Media Regression](../.github/workflows/reference-media-regression.yml) SSD job
runs both formats using the canonical `Invoke-SsdPlaybackQualification.ps1`
runner, emits the sidecar alongside the original hashed media and TRX reports
and fails on the **original** duration/memory/handle/continuity assertions.
An unrelated passing Required Gates run is not replay soak PASS.
The physical SSD, native decoder, GPU and storage pressure tuples remain
**UNVERIFIED** until separately measured.

## Scope and existing producer ownership

This is an optional, isolated Windows x64 **software** measurement contract.
The canonical Program producer is
[`ProgramPipelineMeasurementBaselineTests`](../tests/rtaime.Tests.Performance/ProgramPipelineMeasurementBaselineTests.cs).
The qualification fact calls its existing
`Program_service_load_matrix_reports_consumers_separately_from_physical_output`
method twice, for 1080p50 and 1080p59.94. It does **not** duplicate the
Runtime Program scheduler, compositor, readback or its already qualified
`ProcessNextBoundary` measurement. There are no new production hooks,
on-frame logging, file writes, global polling loops, managed payload
allocations or `GC.Collect` calls in the producer cadence.

The versioned source profile is
[`qualification/performance/managed-allocation-gc.json`](../qualification/performance/managed-allocation-gc.json).
The `smoke` profile runs one isolated producer testhost; `repeat` runs
three separately started producer testhosts and also invokes the existing
isolated native decoder/worker diagnostics once. Both profiles bind the
same exact 40-character source/build SHA, profile SHA-256, SDK 10.0.401,
OS/process architecture, machine/CPU/processor count, runner identity,
testhost PID, measured producer-thread ID, TRX result and actual source
artifact hashes.

**Only the existing measured matrix is claimed:**

| Dimension | Measured or validated behavior | Boundary |
| --- | --- | --- |
| Program format | 1920×1080 RGBA8 at 50/1 and 60000/1001 | Real managed-reference Runtime Program service |
| Consumer masks | Eight combinations of CPU monitoring, recording and an optional output-buffer copy | Monitoring is a bounded CPU fallback, the recording writer is a test double and output copy is explicitly synthetic |
| Timing | Three warm-up Program boundaries, 12 measured boundaries per format/mask, CPU-wall P50/P95/P99/max | Whole measured 12-boundary window; not an end-to-end GPU/SDI/network deadline |
| Allocations | `GC.GetAllocatedBytesForCurrentThread` around the actual invoking Program service boundary; separate approximate `GC.GetTotalAllocatedBytes(false)` | Same-thread bytes are not process-wide; process counter includes unrelated or asynchronous allocations |
| GC | Gen0/1/2 count deltas, total GC pause delta, last-GC heap, fragmentation and LOH/POH indicators | Last-GC heaps are snapshots, not a per-boundary allocation inventory; no forced collection |
| Program GPU | Provider logical upload/readback bytes, current readback pool, live surfaces, bounded recording/monitoring queues | ManagedReference host backend; device-domain HtoD/DtoH and physical PCIe **not observed** |
| Recording/monitoring | Existing queue capacity and fully released mapped/Program frame/readback ownership assertions | No retained output consumer lease or extra reader allowed after the measured session |
| Program scalar / Operator metrics | Existing `RuntimePerformanceStatusBarPerformanceTests` zero-allocation invariant, checked separately | Instrumentation contract only; WPF UI rendering/Dispatcher/visual latency **UNVERIFIED** |
| GPU compositing / transitions | Existing `GpuCompositorPerformanceProfileTests`, `OutputMappingSoftwarePerformanceTests` and the factored V1 combined reference test | Independent correctness/performance guards, **not** a full 0/2/8-layer × CUT/DISSOLVE × eight-consumer Program cross product |
| Native media | Optional existing `ReferenceMediaPlaybackAllocationDiagnosticsTests` native H.264/AAC reader and worker slot observations | Isolated reader and worker; not simultaneous real A/B Program playback, not a long SSD session |
| Replay/network | No full numerical Program cross-product or native transport trace from this harness | **UNVERIFIED** |
| GPU hardware, drivers, VRAM, CUDA events, external output, AV sync | No reference-hardware execution from this qualification | **UNVERIFIED** |

The optional native test's sampled EventListener LOH sizes are **samples**,
not a count of every allocation or a complete ETW trace. Even if native
assertions pass, absence of correlated machine-readable ETW events remains
`UNVERIFIED`. The native evidence is explicitly
`PASSED_ASSERTION_ONLY` rather than reclassified as continuous playback.

## Run on an identified Windows x64 test runner

Install the pinned SDK, ensure the current repository has no tracked
source changes and execute the dedicated script:

```powershell
./build/qualification/Invoke-ManagedAllocationGcQualification.ps1 -Profile smoke
```

For three independent software producer processes and one isolated native
decoder/worker diagnostic:

```powershell
./build/qualification/Invoke-ManagedAllocationGcQualification.ps1 -Profile repeat
```

The invocation **always performs the canonical Release build** via
`Invoke-DeveloperBuild.ps1`. There is no uncontrolled `-NoBuild`
shortcut that might attribute an old binary to the new commit. Artifacts
are written under a unique, never-overwritten directory within
`artifacts/qualification/managed-allocation-gc/`.
Each iteration retains the existing `program-load-False.json` and
`program-load-True.json`, a producer-process identity/GC snapshot and
its TRX result. Separate TRXs record the scalar and compositor factor
guards. The longer profile additionally retains a native TRX and bounded
test console log. Only after every subprocess and result has succeeded
does the runner write a private `manifest.pending.json` and verify
all its hashes, source identities, rates, consumer masks, quantiles,
allocated bytes per boundary and ownership/queue release evidence.
For the smoke profile, the negative verifier regression suite also
completes before the staged manifest is renamed to the final
`manifest.json`. A failed or interrupted run therefore does not publish
a final manifest with a misleading successful status. It then writes
`subsystem-summary.json`, a **derived presentation-only** per-case table of
Program CPU-wall P50/P95/P99, thread/process allocation bytes per frame,
GC counts/pauses/last-GC LOH/POH snapshots, logical readback bytes,
comparison deltas and specific next-action recommendations for Program,
GC, Decoder, GPU readback, Operator UI and Replay/Network. Its numeric
authority remains the unchanged manifest-hashed source producer files;
the summary itself is not a second measurement source.

The independent verifier can be rerun without rebuilding or executing
production code:

```powershell
./build/qualification/Test-ManagedAllocationGcEvidence.ps1 -EvidencePath "<evidence-root>/manifest.json" -ExpectedSourceCommit "<exact-40-character-source-SHA>"
```

On a completed `smoke` artifact, use
`Test-ManagedAllocationGcVerifierRegressions.ps1` with the same
`-EvidencePath` and `-ExpectedSourceCommit` to exercise negative cases.
The negative suite uses temporary, explicitly discarded synthetic
mutations of actual reports; it must reject wrong source/build, runner,
missing artifacts, omitted repeats, invented GC-event/hardware status,
failed scalar guards, leaked readback leases, invalid P99, and a measured
regression against a *test-only* zero-growth fixture. The fixture is not
a committed or accepted performance budget.

## Comparability and automatic regression policy

A previous **verified**, same-format, same-profile, same-runner artifact
may be provided with `-BaselineEvidencePath`. The verifier checks
the complete baseline, its source/build SHA, the exact producer/guard
test identities within each passing TRX and every retained raw artifact
hash, then emits per-scenario P99, producer-thread bytes/frame,
approximate process bytes/frame and GC-pause deltas to `comparison.json`.
The per-frame allocation results are not interchangeable with a process
allocation inventory. Absolute thread/process byte deltas are emitted
even for a zero-allocation baseline, where relative percentages are null.
A missing baseline is `UNVERIFIED/NO_APPROVED_BASELINE`. Two otherwise
comparable software profiles without an approved numerical threshold
remain `UNVERIFIED/NO_APPROVED_MANAGED_REGRESSION_THRESHOLD`;
measurement deltas do **not** automatically constitute a validated
performance improvement or regression.

The existing renderer `maximumP99RegressionPercent = 20` policy is
**renderer-reference-profile-specific** and is **not** silently applied
to managed Program/GC measurements. If operations have independently
approved managed-software P99 and thread-allocation regression limits,
pass a separate `-ApprovedPolicyPath`, bound to the exact descriptor
SHA-256 and runner ID. Its contract is
`rtaime-managed-allocation-gc-regression-policy/1.0`, with
`status=APPROVED`, `maximumP99IncreasePercent` and
`maximumThreadAllocationIncreasePercent`.
Without that explicit authorization this tool has no invented numerical
threshold. When present, it fails any matching scenario above the
approved limit, including a zero-baseline allocation case with a
nonzero new allocation. This optional policy is *not* production
signing trust, proof of approval or a universal platform SLA.

Integrity and behavior regressions always fail independently of
numerical thresholds: source or runner mismatch, overwritten/missing
files, partial TRX, duplicate/omitted rate/consumer case, nonfinite
or nonmonotonic latency, inconsistent bytes/frame denominators,
live readback/monitoring leases, queue overcapacity, and false claims
of complete GC events or hardware qualification.

## Source-bound same-runner reference comparison

The dedicated path-scoped Windows workflow preserves its original,
unchanged `Windows software producer smoke` job. On qualifying pull
requests, a **separate** `Windows same-runner reference and repeat` job
checks out the exact event `pull_request.base.sha` and
`pull_request.head.sha` into isolated directories on **one** Windows
x64 `windows-2025` runner. It first confirms both full checkout SHAs
and the profile descriptor SHA-256. Only when descriptors are
byte-identical does it:

1. Execute the existing `smoke` producer from the PR-base commit,
   capturing its own verified manifest, raw JSON, testhost TRX and
   hashes in a separate non-overwritten baseline directory.
2. Execute the same `smoke` profile from the PR-head commit on the
   **same runner**, passing the verified baseline manifest to
   `-BaselineEvidencePath`.
3. Execute `repeat` from the PR head: three independent producer
   testhosts plus the already-owned isolated native reader/worker
   assertions, with their own verified manifest and raw/TRX artifacts.
4. Validate both candidate manifests, the 16 rate/mask comparison
   entries, source identities, separate processes and `repeat` native
   assertion status. Archive baseline and candidate data even if
   qualification fails, but only name a manifest successful when the
   producer and verifier actually completed.

This is an **exact-PR-base vs exact-PR-head** software comparison, not
an assumption that a previously retained artifact from a different
GitHub-hosted VM is physically comparable. Running on one job VM
avoids confusing two identically named `windows-2025` runner *images*
with the same actual machine. It does **not** assert that two
different GitHub Actions jobs or separate hosted workflow runs use
the same CPU/VM. Baseline and candidate are compared by source SHA,
profile hash, runner ID, machine name, Windows version, processor ID,
architecture, processor count, formats, and consumer masks; raw
artifacts and TRX are verified independently on both sides. If the
descriptor changed, the baseline comparison is **UNVERIFIED** rather
than silently comparing different workloads; candidate `smoke` and
`repeat` still run as independent qualifications.

The comparison JSON records baseline/head SHAs, the runner/profile
identity, per-scenario P99 and allocation bytes per boundary, absolute
and percentage deltas (where the denominator is nonzero), and observed
GC-pause deltas. Its result remains
`UNVERIFIED/NO_APPROVED_MANAGED_REGRESSION_THRESHOLD` without a
separately authorized, descriptor-/runner-bound policy. A successful
job therefore proves comparable **measurement integrity**, not that
the P99 or byte growth meets an unapproved target. Each run retains
its own actual event timestamp, testhost PID and source identity;
repeatability is limited to the tested run and runner.

The existing verifier's negative fixtures now also reject a
passing TRX substituted from an unrelated test, a missing producer
or scalar-guard test identity, a missing TRX, an invalid testhost PID,
a nonexistent baseline, an approved-policy claim without a baseline,
and an allocation increase that exceeds the **fixture-only**
zero-growth policy. These negative fixtures are discarded immediately
and are **not** actual measurements or accepted performance budgets.

For a qualified *persistent* Windows reference machine, perform
`smoke` and `repeat` against each exact source checkout in succession
on the same identified host. Carry forward the first `manifest.json`
to the second source using `-BaselineEvidencePath`; retain both source
trees, manifest/JSON/TRX hashes and runner inventory. GitHub-hosted
reference comparisons are not a substitute for a permanent hardware
performance baseline.

## Production subsystem decision and physical dependency matrix

| Subsystem | Accepted current software scope | Missing evidence and responsible qualification |
| --- | --- | --- |
| Runtime Program / GC | 50 and 59.94 fps × 8 consumer masks, 12 measured boundaries per case, strict manifest/TRX and P99/bytes/GC counters | Same-runner reference/head comparison reports deltas only; a managed regression budget needs explicit policy approval, and longer sustained producer load needs independent profiling |
| Native decoder / SSD | `repeat` invokes the preexisting isolated H.264/AAC decoder and worker assertions | Actual simultaneous two-deck SSD throughput, A/B switching and sustained read latency require [SSD playback qualification](../.github/workflows/ssd-playback-qualification.yml) on self-hosted `rtaime-ssd` |
| GPU transfer / VRAM | Managed-reference logical readback and zero retained lease assertions | CUDA device, driver, real HtoD/DtoH, fences and reference GPU performance require [CUDA reference qualification](../.github/workflows/cuda-reference-qualification.yml) on self-hosted `rtaime-cuda-reference` |
| Professional output / AV sync | Software output assertions and independent renderer/recording factor tests, not physical sinks | AJA/SDI and independent output require [Media I/O qualification](../.github/workflows/media-io-reference-qualification.yml); clock/latency/soak require [Timing reference qualification](../.github/workflows/timing-reference-qualification.yml), both on self-hosted `rtaime-media-io-reference` |
| Operator WPF / viewer | Separate Windows real-WPF visual/Dispatcher qualification and scalar HUD allocation guard | P99 for a simultaneous Program viewer + runtime/monitoring workload and real monitor hotplug are not captured by this producer; separate [GPU viewer qualification](OperatorGpuViewerShutdownQualification.md) does not promote them to this measurement scope |
| Replay / network / multitrack transitions | Independent existing feature and correctness guards only | No combined 0/2/8-layer × CUT/DISSOLVE × consumer-mask and actual replay/network sink producer exists in this profile; retain `UNVERIFIED` rather than filling cases with synthetic timing |
| Complete GC allocation attribution | Gen0/1/2 counters, pause deltas and last-GC heap/LOH/POH snapshots | Full source-correlated EventPipe/ETW object allocation stacks, long-running collection coverage and attribution remain `UNVERIFIED`; isolated EventListener samples cannot stand in for complete events |

The self-hosted workflows above are **existing opt-in hardware routes**, not
automatically executed by a hosted PR job. The dedicated runner label
alone does not prove the physical device is currently available.
Only completed evidence with device/runner identities, exact candidate
source SHA, independent sink/clock metrics and verified raw artifacts can
change an individual hardware domain from `UNVERIFIED`. An inaccessible
runner, missing signal or unqualified physical component remains
`BLOCKED/UNVERIFIED`, never a software-simulated PASS.

## Evidence limits and release acceptance

A green hosted test only verifies its own named software case and
source commit. It does not qualify a reference NVIDIA GPU, an actual
AJA/SDI sink, native two-deck SSD output, GPU/DRAM/PCIe traffic,
WPF presentation P99, GC allocation type exhaustiveness or a sustained
hours-long session. Complete comparable **before/after** numerical
results require execution of the same source-controlled profile on
the same identified machine and approval of any new regression limits.
Do not compare a managed-reference sample to a CUDA/driver revision
or treat a missing hardware run as zero latency.

The existing Required Gates must still succeed on the exact candidate
commit: CI, Quality, Security, Provider Smoke and Packaged E2E.
This package does not modify the central workflow or create a second
performance monitor. The new opt-in Windows scripts can run independently
and the normal Performance test project compiles their producer harness.
A path-scoped [managed allocation and GC software qualification workflow](../.github/workflows/managed-allocation-gc-qualification.yml)
runs the actual `smoke` profile on a hosted Windows x64 runner when this
qualification's producer, verifier or profile changes, and retains the
source-bound raw reports, TRXs, comparison and manifest as Actions artifacts.
It checks out the exact PR head and does not change the five Required Gates.
An absent, skipped, queued or failed hosted run is not qualification PASS;
read the exact commit, run, job and immutable evidence before accepting it.
The companion PR job executes the opt-in `repeat` profile and same-job
before/after software comparison without changing the original `smoke`
gate. Their outcome must be taken from that **exact final-head** run,
not presumed from workflow configuration. An independent repeat
on a persistent reference workstation, full ETW coverage, hardware
latency and future mandatory gate integration remain separately
**UNVERIFIED** without the requisite source-bound equipment evidence.
Broader native/replay/network/Operator cross-products still require their
own verified producers and clear ownership.

## Cross-subsystem real-time playout software matrix

The opt-in [real-time playout evidence workflow](../.github/workflows/realtime-playout-evidence.yml)
runs the existing managed Program GC producer, native decoder/worker-copy
diagnostics, real distinct H.264/AAC sequence Preview/Program integration,
bounded runtime and monitoring guards, two-deck SSD playback at 1080p50
and 1080p59.94, and staged recording/recovery mixed load. Its
[source-controlled profile](../qualification/performance/realtime-playout-matrix.json)
offers finite `smoke` and `reference` durations only; neither is an
uninterrupted physical long soak.

Each run records the exact full source SHA, SDK 10.0.401, machine/OS,
report hashes and producer-specific measurements. The aggregate verifier
reopens the hashed raw producers and rejects missing, skipped, changed or
unattributed reports. P99 is an observed 12-boundary managed-reference
CPU-wall statistic; the nominal frame-period comparison is descriptive,
not an approved real-time or physical latency pass. Measured native
copy costs are isolated worker diagnostics, not proven PCIe transfer
or native-Program end-to-end headroom.

The 25 fps native path, CUDA driver/VRAM, physical I/O/cache-pressure
latency, SDI/NDI/genlock, physical A/V synchronization, real device-loss
recovery and continuous multitrack long soak remain **UNVERIFIED**.
Software-matrix collection cannot satisfy physical release requirements.
