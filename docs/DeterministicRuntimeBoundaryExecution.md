<!-- Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>. -->

# Deterministic Runtime Boundary Execution

## Runtime production authority

RuntimeHost has one production cadence authority for V1 Program execution: the epoch-based `RationalFrameSchedule` owned by `RuntimeHostProcess.RunMediaLoopAsync`.

One due rational deadline defines one opportunity to execute a production boundary. The ordered process path is:

```text
Program cadence deadline
	→ pump physical Media I/O inputs
	→ admit one local-media deck boundary, when available
	→ execute one committed Runtime Program boundary
	→ submit Program to physical output
	→ observe governed AI
	→ publish bounded timing/performance evidence
```

There is no independent media-deck production timer. Local media may prepare/decode through its subsystem implementation, but video/audio admission into RuntimeHost happens exactly once from the Program cadence before the corresponding Program boundary.

## Coherent local-media admission

`LocalMediaDeckRuntimeService.ProcessBoundary()` returns one `LocalMediaRuntimeBoundaryResult`. RuntimeHost consumes video and audio from that same result during `AdmitMediaDeckBoundary`.

This keeps deck video and audio attached to one logical media boundary and prevents scheduler ordering between two periodic loops from introducing a one-frame pairing race. Pause, seek, loop and Program-edge autoplay remain owned by the existing media-deck transport state machine.

The local-media provider retains its reusable decode/output storage. Boundary synchronization does not introduce a second unbounded frame queue.

## Runtime state capture and publication

`V1RuntimeHostService` uses two distinct synchronization responsibilities:

- `_boundaryExecutionGate` provides single-writer boundary execution and serializes committed execution replacement between Program boundaries.
- `_boundaryCaptureGate` serializes frame-affecting control/configuration mutations against the finite boundary-capture phase without blocking them for the later composite/readback/output phase.
- `_gate` protects short metadata/state reads and atomic boundary-result publication.

At boundary start, RuntimeHost captures the committed execution and Program binding under the short metadata lock. While holding only the dedicated capture gate it then materializes the exact input frames, Aux submission, GPU input/layer surfaces, monitoring source snapshot and audio payload inputs for that boundary. Frame-affecting mutations arriving after this capture point are accepted for the next boundary and cannot partially alter the already captured frame.

Heavy data-plane work executes without holding the broad Runtime state lock:

- GPU input/layer materialization;
- Aux provider write;
- audio payload generation and metering;
- GPU composite;
- reusable Program readback;
- Program provider write;
- monitoring enqueue;
- recording payload staging/enqueue.

AFV state advances only after GPU composition, readback and Program provider submission have succeeded. The final short `_gate` section publishes the completed audio/composition/timing state, Aux failure evidence, transition completion and next Program sequence.

An execution apply that arrives while a boundary is running waits at `_boundaryExecutionGate`. It therefore cannot partially change the currently executing frame and becomes eligible only after the preceding boundary has completed.

Ordinary frame-affecting control mutations synchronize only with `_boundaryCaptureGate`: a mutation that wins the capture point is part of the current boundary; one that arrives after capture becomes visible to the next boundary. Read-only snapshots continue to use `_gate` and do not wait for GPU uploads, composite, readback, Program output or recording work.

## Output evidence publication

Virtual Program/Aux outputs can receive their provider frame before the RuntimeHost boundary state is finally published. Output-role snapshot construction therefore ignores output evidence whose frame sequence is not older than `NextSequenceNumber`.

This prevents a concurrent snapshot from presenting a provider frame as committed Runtime evidence before the boundary publication point. After publication advances `NextSequenceNumber`, the matching sink/source evidence becomes eligible for normal health evaluation.

## Monitoring ownership

Program monitoring keeps the existing retained `GpuReadbackLease`.

Source A/B storage can be mutable. Runtime monitoring therefore downsizes and snapshots sampled source pixels synchronously at boundary capture before handing the sample to the asynchronous monitoring worker. The worker never reads a later mutation while claiming the earlier boundary sequence.

Monitoring remains sampled, bounded and non-authoritative.

## GPU resource health

`GpuProcessingProvider.ActiveSurfaceCount` is published through atomic diagnostic counters rather than taking the provider execution lock. Runtime snapshots can therefore read resource evidence while a backend operation is in progress.

If a backend surface release throws, the surface identity remains in explicit unreleased-resource accounting. Resource health cannot report zero merely because the managed active-frame entry was removed first. Provider stop retries known failed releases before backend shutdown; successful backend shutdown clears the retained release-failure set.

## Cadence overrun and backlog policy

RuntimeHost never starts a second concurrent Program boundary. A slow boundary therefore causes lateness rather than an unbounded boundary queue.

`RuntimeTimingQualificationProbe` records scheduler interval jitter and complete boundary processing duration. Processing beyond one frame period is a violation and maps through the established timing-health state model. `RuntimeFrameDropCounter` and physical Media I/O backpressure/rejection observations remain the bounded dropped/output evidence surfaces.

The scheduler coalesces late wakeups into the latest due slot and counts skipped opportunities once. It computes absolute deadlines from one monotonic epoch rather than accumulating rounded frame intervals. The first committed observation does not count startup gaps. Media sequence and audio advancement remain tied to completed boundaries; skipped opportunities are observational loss, not independent command/audio progression.

Schedule start lateness, internal completion lateness and output backpressure are separate evidence. Completion timing now includes input pumping and media-deck admission. See [RenderSchedulingSynchronization.md](RenderSchedulingSynchronization.md) for arithmetic, thresholds, reset semantics and measurement limits.

The architecture deliberately does not compensate for overrun by launching overlapping frame work.

## Software qualification

Generic CI can prove deterministic ordering, bounded ownership and recovery properties without claiming physical timing performance. Relevant evidence includes:

- deterministic boundary concurrency regressions;
- media-deck playback/loop regressions;
- monitoring temporal-aliasing regression;
- Program readback allocation/lease regressions;
- GPU release-failure accounting;
- host IPC shutdown/recovery regressions;
- the full Required Gates and CI-safe Reference Platform Qualification software profile.

Software tests must keep retained diagnostic counts, readback leases and tracked host resources bounded. They supplement rather than replace the existing component-specific allocation/performance tests.

## Physical qualification boundary

Software qualification does not prove CUDA production timing, AJA Media I/O, external-reference behavior, physical A/V synchronization, physical end-to-end latency or long-duration reference-platform soak.

Those claims remain owned by the dedicated exact-source physical qualification workflows and evidence bindings. Without accepted evidence for the exact candidate source, the corresponding reference-platform dimensions remain `UNVERIFIED`.
