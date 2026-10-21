<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Managed Allocation, GC and Program Latency Qualification

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
the complete baseline, its source/build SHA and every retained raw
artifact hash, then emits per-scenario P99, producer-thread
bytes/frame and approximate process-byte deltas to `comparison.json`.
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
Future official CI scheduling and broader native/replay/network/Operator
cross-products require explicit ownership and supported producer evidence;
until then those remain **UNVERIFIED**.
