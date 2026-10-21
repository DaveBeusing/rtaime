<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Program Cadence Managed Allocation Qualification

## Purpose and measurement boundary

Quantify avoidable managed allocations on the existing
`RuntimeProgramExecutionService.ProcessNextBoundary()` path without introducing
another media clock, disabling GC, altering rational frame deadlines, or
weakening lease and output-authority checks.

`ProgramPipelineMeasurementBaselineTests.Program_service_load_matrix_reports_consumers_separately_from_physical_output`
runs on the Windows Release runner with pinned .NET SDK 10.0.401 and the
`ManagedReferenceGpuBackend`. Each video rate (1080p50 and 1080p59.94)
covers eight combinations of monitoring, recording and optional synthetic
output-copy load, with three warm-up and twelve measured Program boundaries
per combination. Existing Program/Aux routes, GPU composite and synchronous
readback are exercised; the test does **not** use a real physical output or GPU
driver. Advanced transition/layer, replay, network and recorder pressure have
separate integration/consumer tests and are not represented as part of this
small software comparison matrix.

Measurements explicitly distinguish:

- `GC.GetAllocatedBytesForCurrentThread()`: caller-thread bytes, including
  synchronous Program work and the measuring harness; valid only because the
  measured loop stays on one managed thread;
- `GC.GetTotalAllocatedBytes(false)`: approximate whole-process bytes,
  including unrelated and asynchronous consumer worker allocations. These
  values must not be misrepresented as direct Program allocations;
- Gen0/Gen1/Gen2 collection-count deltas and
  `GC.GetTotalPauseDuration()` during the measured window; last recorded GC
  heap size, fragmentation, LOH and POH are diagnostics, not direct per-frame
  allocation rates;
- CPU wall-time P50/P95/P99 and the existing stage/timing probe, bounded
  monitoring/recording queues, and GPU readback lease statistics.

No `GC.Collect`, `NoGCRegion`, global cache or artificial retry changes the
measured production-equivalent workload. Twelve samples are too few to
establish hard deterministic P99 or long-run Gen2/LOH/POH confidence.
Repeat on the same qualified runner and with longer soak workloads before
making pause or latency improvement claims.

## Pre-optimization source-bound Windows measurement

- Source and exact build: `408658f769e580aace73f56cfc0c0fa3c861ae92`
- Integration base: `03dbc4378da78ac6c665ab002f94ad0ab615cec3`
- Windows Required Gates: [run 39512654205, attempt 1](https://github.com/DaveBeusing/rtaime/actions/runs/39512654205)
- Successful CI step: **Renderer comparable low latency software profile**
- Source-matched evidence artifact:
  `rtaime-program-pipeline-39512654205-1` (artifact ID `12306855786`)
- Runtime: .NET 10, SDK 10.0.401, Windows x64, managed software GPU
- Values below are measured **caller-thread B/boundary**, not process-wide
  allocations or native hardware throughput.

| Software consumer profile | 1080p50 | 1080p59.94 |
| --- | ---: | ---: |
| Monitoring off, recording off, synthetic output copy off | 31,807 B | 29,657 B |
| Monitoring on, recording off, synthetic output copy off | 147,451 B | 145,520 B |
| Monitoring off, recording on, synthetic output copy off | 32,295 B | 30,481 B |
| Monitoring on, recording on, synthetic output copy off | 147,939 B | 145,712 B |
| Monitoring off, recording off, synthetic output copy on | 31,807 B | 29,453 B |
| Monitoring on, recording off, synthetic output copy on | 147,466 B | 145,030 B |
| Monitoring off, recording on, synthetic output copy on | 32,295 B | 29,821 B |
| Monitoring on, recording on, synthetic output copy on | 147,954 B | 145,481 B |

Representative no-consumer CPU-wall P95/P99: 1080p50 61.0423/61.0423 ms;
1080p59.94 71.9456/71.9456 ms. They exceed nominal media frame
budgets on the managed reference backend and are **not** 50/59.94-fps
real-time acceptance. In the unmonitored 1080p59.94 first measurement
the approximate process counter also includes an unusually large
15,270,363 B/boundary; it is not a stable direct-frame allocation
figure and must not drive the hotpath change.

## Implemented optimization and invariants

The production owner is `src/Hosts/rtaime.RuntimeHost/RuntimeProgramExecutionService.cs`.
The formerly named `V1RuntimeHostService.cs` is not present on this base.

- Source and GPU frame dictionaries are retained per Runtime service, initially
  sized for the current finite source registry, cleared on success/failure and
  only read while holding the existing exclusive Program boundary gate.
- Committed Program and Preview binding cardinalities are still validated
  against the active prepared execution on every boundary without creating
  a Preview route array. External input GPU lookup uses direct dictionary
  enumeration instead of allocating a LINQ iterator per frame.
- Async consumer-owned full-frame readback leases and audio snapshot arrays
  remain separate. Only unused recording/replay PCM snapshots and unused
  Aux-network PCM snapshots are omitted. Returned Program audio retains an
  independent per-boundary byte array.
- No execution revision, output mapping, sequence number, rational scheduler,
  frame descriptor, output authority, recorded/monitored pixels or audio
  contract is changed.

## After-optimization qualification and acceptance

A final-head Windows comparison is required with **the same** software
profile, format, eight load combinations, warm-up and sampling counts.
Report caller-thread B/boundary and approximate process B/boundary separately,
alongside GC collections/pauses, CPU wall P95/P99, verified program/aux
payloads and retained resource counts. Do not declare the implementation
faster, pause-free or zero-allocation without those exact-head values.

Required regression coverage includes
`RuntimeBoundaryScratchOwnershipTests`,
`ProgramFrameMemoryOwnershipTests`,
`DeterministicRuntimeBoundaryTests`,
`RuntimeConsumerIsolationTests`,
`RuntimeMemoryBoundednessTests`,
`ProgramPipelineMeasurementBaselineTests` and full solution gates.

Exact-head CI, Quality, Security, Provider Smoke and Packaged E2E must
all PASS before integration. Physical NVIDIA/CUDA, native decoder and
professional Media I/O, SSD stress, HDR/genlock and sustained real
end-to-end latency remain **UNVERIFIED** without hardware-backed evidence.
