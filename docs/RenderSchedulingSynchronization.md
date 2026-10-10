# Render Scheduling and GPU Synchronization Evidence

## Status

- implementation baseline: master at `177090e86e3aa364ab4fa7fd2f28e29375d60b08`
- .NET SDK: 10.0.401
- scheduling authority change: none
- CUDA stream policy change: none
- backend lock topology change: none
- production timing instrumentation: disabled by default
- physical reference-GPU comparison evidence for this source: UNVERIFIED

This document records the scheduling/synchronization evidence added before any attempt to make the CUDA backend more asynchronous.

## Preserved authority model

RuntimeHost still owns one committed Program cadence through the epoch-based `RationalFrameSchedule` in `RunMediaLoopAsync`. Program boundaries do not overlap. A slow boundary becomes timing/deadline evidence rather than a second in-flight authoritative boundary or an unbounded queue.

ControlHost remains production authority. RuntimeHost owns committed execution, timing and production surfaces. Operator remains observational.

No second renderer, compositor, scheduler, timebase or state store is introduced.

## Current CUDA completion chain

The production path remains synchronous and ordered:

1. surface materialization performs `cuMemcpyHtoD_v2`;
2. the uploaded surface is published only after that call succeeds;
3. composite launches `composite_rgba` on the default stream;
4. `cuCtxSynchronize` completes before the output surface is inserted into provider-owned surface state;
5. monitoring export may copy the completed surface into a shareable D3D11 resource;
6. Program readback uses synchronous `cuMemcpyDtoH_v2` into a bounded reusable host buffer;
7. surface release remains serialized by the backend gate and provider ownership bookkeeping.

Therefore the earliest current safe publication point for a composite output is after successful context synchronization. Moving publication earlier would require explicit in-flight ownership and completion semantics and is not justified without physical measurements.

## Qualification-only timing instrumentation

`CudaGpuTimingCollector` is optional. RuntimeHost continues to construct `CudaGpuProcessingBackend` without one, so the normal production path does not create CUDA timing events or collect per-operation samples.

The dedicated CUDA qualification path enables the collector and records a bounded ring of:

- `UploadHostToDevice`: CPU wall time for `cuMemcpyHtoD_v2`;
- `KernelLaunch`: CPU wall time for `cuLaunchKernel`;
- `ContextSynchronize`: CPU wall time waiting in `cuCtxSynchronize`;
- `KernelGpuElapsed`: CUDA-event device elapsed time around the composite kernel;
- `ReadbackDeviceToHost`: CPU wall time for `cuMemcpyDtoH_v2`;
- `MonitoringExport`: available for focused diagnostics when the monitored export path is exercised.

CPU synchronization wait and GPU kernel elapsed overlap and must not be summed. They answer different questions.

The collector capacity is fixed and qualification sample count is bounded to 10–10,000. No file or network I/O occurs inside the CUDA frame methods.

## Tail evidence

CUDA qualification evidence schema 1.1 retains:

- P50;
- P95;
- P99;
- maximum synchronous Composite + Readback latency;
- the same percentile set for each collected backend timing operation.

The existing pass/fail limits remain P95 <= 5 ms and maximum <= 10 ms for synchronous Composite + Readback. P99 is evidence-only until enough immutable reference-hardware runs exist to define a defensible independent threshold.

## Deadline and backpressure evidence

No second deadline system is added.

Existing Runtime evidence remains authoritative for complete Program-boundary behavior:

- `RuntimeTimingQualificationProbe` records scheduler jitter, processing duration and processing-budget violations;
- processing beyond the configured frame budget is explicit timing degradation;
- `RationalFrameSchedule` identifies skipped slots once; `RuntimeFrameDropCounter.ObserveScheduled` combines those cumulative slots with disjoint physical-output losses;
- native Program-output backpressure/rejection counters are included in the same bounded drop evidence.

These mechanisms remain observational and do not launch compensating concurrent Program work.

## Concurrency and lifetime regression coverage

Software regressions now explicitly cover:

- Stop requested while composite work is blocked: Stop serializes behind the current provider operation and completes only after the operation leaves the critical section;
- repeated layer reconfiguration: output pixels remain deterministic and output surfaces return to the persistent input-surface baseline on every iteration;
- monitoring-resource expiry: a descriptor from before Stop/Start is rejected after provider-instance/generation rotation.

Existing regression coverage remains authoritative for:

- CUDA-classified upload/composite/readback failure and fail-closed recovery;
- output-role failure and native output backpressure/rejection;
- readback-pool exhaustion;
- monitoring export/release failure;
- deterministic one-boundary Runtime execution;
- bounded Program readback and monitoring ownership.

## Why streams/events are not yet a production optimization

CUDA events are currently used only to measure qualification work. They are not used to defer completion or expose asynchronous surfaces.

No stream split, event-driven publication, finer-grained backend lock or multiple in-flight Program frames is implemented by this change because no immutable physical comparison exists yet showing that:

1. context synchronization is a material contributor to frame-boundary latency;
2. an alternative reduces P95/P99/worst complete-boundary latency;
3. the alternative preserves exact pixels and ordering;
4. Stop/recovery can drain every in-flight resource deterministically;
5. monitoring and readback do not observe incomplete surfaces;
6. memory/resource bounds remain fixed.

Changing those semantics before measurement would violate the evidence-first optimization policy.

## Reference-hardware decision procedure

Run the dedicated CUDA reference workflow against the exact candidate SHA and retain the immutable evidence binding.

Compare at minimum:

- 1080p50 and 1080p59.94;
- CUT and DISSOLVE;
- synchronous Composite + Readback P50/P95/P99/maximum;
- HtoD wall time;
- launch wall time;
- context synchronization CPU wait;
- kernel GPU elapsed time;
- DtoH wall time;
- exact output pixels;
- provider surface lifetime returning to baseline.

If context synchronization dominates CPU wall time while kernel GPU elapsed is materially lower, a bounded stream/event prototype may be justified on a new branch. If transfer time dominates, stream changes alone are not a supported remediation. If lock contention becomes measurable under legitimate concurrent non-authoritative operations, lock refinement can be evaluated independently.

Any optimization candidate must be compared against the synchronous baseline on the same reference device, driver, format, workload and exact source revisions.

## Evidence boundary

Software CI can prove code structure, bounded instrumentation, serialization, ownership and deterministic synchronization regressions. It cannot prove that a stream/event or lock change would improve a physical NVIDIA workload.

Until an exact-SHA reference-hardware run is captured, the performance impact of `cuCtxSynchronize`, HtoD, DtoH and monitoring interop remains **UNVERIFIED**.

## Rational scheduling and deadline accounting

The Program scheduler computes deadline slot k as epoch + ceil(k × denominator × 10,000,000 / numerator) TimeSpan ticks. It uses Int128 arithmetic for both 50/1 and 60000/1001. The process's existing monotonic Stopwatch supplies observations; Task.Delay is only a cancellable wakeup hint and is rechecked before admission. Sub-millisecond remaining waits use a minimum 1 ms delay to avoid busy polling.

A delayed wake executes only the latest due slot. Earlier unexecuted slots are counted once; repeated observations cannot admit a second boundary. The first committed observation establishes a baseline, so pre-commit startup delays do not manufacture Program drops. Slot identities are scheduling opportunities, separate from committed media sequence and audio positions. Missed opportunities do not silently advance transitions, Control commands or audio independently of committed video.

Start lateness beyond the existing 25% jitter budget is a schedule deadline miss. Completion beyond the next rational slot deadline is internal Program presentation lateness. These overlap as timing evidence and are not added again to dropped-frame totals. Presentation lateness measures completion through input/deck admission, render/readback, physical submission and AI observation; it is not physical display presentation latency.

The scheduler has no pending-frame queue and never overlaps authoritative boundaries. At process restart a new epoch and fresh observations are created. RationalFrameSchedule.Reset supports an explicit format/resync epoch in deterministic qualification; live V1 formats remain immutable for a process.

Support snapshots expose missedSchedulerSlots, scheduleDeadlineMisses, presentationLateBoundaries and last lateness values under timing. Before any committed boundary, timing.scheduleEvidence is UNVERIFIED. Thereafter it is MEASURED_SOFTWARE, independently of physical readiness.

RuntimeTimingQualificationProbe.ObserveBoundary updates the existing fixed ring and health without allocating a retained-sample snapshot on each frame. On-demand snapshots still contain the same chronological observations.

The synchronous CUDA completion/readback chain described above remains a current limitation. This scheduling repair does not create asynchronous GPU ownership, a backend-native output path or hardware performance evidence.
