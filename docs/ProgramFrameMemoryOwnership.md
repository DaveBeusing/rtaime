<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Program Frame Memory Ownership

## Replay writer ownership observations

The Runtime Program owner already transfers retained frame-payload leases to
a bounded Replay capture queue. A separate `ReplayCaptureWriterLifetimeStatistics`
record reports counters at **writer lifecycle transitions only**:
`OpenAttempts`, `Opened`, `FinalizeAttempts`, `Finalized`,
`AbortAttempts`, `Aborted` and `Active`. The host exposes these values
only as read-only local software diagnostics; they are not a new IPC contract,
Program authority, frame lease or media-payload store. A writer is counted
as active after a successful open and ceases to be active only after a
successful completion or abort; failed cleanup remains visible rather than
being optimistically counted as released.

The opt-in SSD memory sidecar reads these counters and the independent bounded
queue depth at four sparse playback checkpoints and once after host drain.
It records process private bytes, managed heap/GC evidence, native MP4
sink-owner/startup/shutdown and scratch-buffer transition counts, and replay
file retention without retaining any Program pixels. The native counters show
whether code released COM owners and pooled buffers; they do not prove that
all Media Foundation internal allocations have been returned to the OS. The original 600-second
memory ceiling (512 MiB process-private growth) and Replay continuity
requirements are unchanged. High-watermark and post-drain memory evidence are
different observations; neither alone establishes native allocation roots.

## Scope

Program Frame Memory Ownership defines both the internal host-memory lifetime used after GPU composition and the bounded device-surface lifetime retained by shared monitoring observations.

The ownership model remains explicit at provider/runtime boundaries. Bulk RGBA memory is not added to stable Media, Runtime, Provider or management IPC contracts; the monitoring contract carries resource identity and lifetime metadata only.

## Ownership chain

The V1 Program path is:

~~~
GPU composite surface
	├─ GpuSharedMonitoringResourceLease — optional sampled read-only surface retention
	│    └─ replacement / disconnect / shutdown → Dispose → provider surface release
	↓
GpuProcessingProvider.RentReadback
	↓
GpuReadbackLease — primary RuntimeHost boundary owner
	├─ physical Program output: synchronous borrow only
	├─ monitoring: Retain → async downscale → Dispose
	├─ recording: Retain → recording worker → Dispose
	└─ boundary caller: Dispose
	↓
last reference returns host buffer to bounded readback pool
~~~

A buffer is eligible for reuse only after the last GpuReadbackLease reference is released. A later frame therefore cannot mutate storage that monitoring, recording or another retained boundary still reads.

ProgramBoundaryResult owns the primary Program readback lease and is disposable. Production callers must dispose it deterministically. RuntimeHostProcess scopes every processed boundary with using.

## Frame lookup and audio-copy ownership

`RuntimeProgramExecutionService` retains two finite managed lookup maps,
`_boundaryFrameScratch` and `_boundaryGpuScratch`, allocated once per
service. They are exclusively mutated under `_boundaryExecutionGate`,
bounded by the admitted input-source registry, and emptied at every completed
or rejected boundary. This changes the allocation of the *lookup containers*,
not the identity, lifetime or mutability of their per-boundary descriptors.
All uploaded source `GpuFrame` instances remain boundary-owned and are
released after composite/output submission or during fault cleanup. Native
backend failures remain diagnosable, and a cleanup exception cannot grant
an output or resource-health PASS.

Audio mixing still uses per-service writable float work buffers. Program
audio returned through `ProgramBoundaryResult.ProgramAudioPayload` remains
an independent `byte[]` owned by its returned result; its contents must
remain valid even if later boundaries overwrite the mix buffers. Recorder
and replay PCM snapshots are copied only when a configured replay consumer
or an active recording payload writer actually requires them. Aux-network
PCM snapshots are copied only when an Aux network source is admitted at that
boundary. The ordinary Aux video provider does not consume this PCM copy.
No per-frame audio scratch is passed to asynchronous recording, replay,
monitoring or network workers; existing read-only payload and lease contracts
remain unchanged.

The scope is limited to avoiding unnecessary managed objects/copies. It
does not imply a zero-allocation Program pipeline: frame descriptor identities,
returned Program audio and asynchronous monitoring/recording/network
subscriptions still incur bounded work. The software baseline and exact-head
measurement limits are recorded in
[Program Cadence Allocation Qualification](ProgramCadenceAllocationQualification.md).

## Bounded readback pool

GpuProcessingProvider owns a fixed-capacity GpuReadbackBufferPool.

The pool:

- allocates exact-size managed byte arrays lazily;
- reuses returned arrays for later readbacks of the same size;
- tracks allocated, available and active buffers plus total and exhausted rents;
- never grows beyond its configured capacity;
- fails closed if every reusable buffer is still leased.

The generic provider default is four readback buffers. RuntimeProgramExecutionService configures the Program pool to ProgramRecorder.DefaultQueueCapacity + 3. The current recording queue capacity is 64, so the V1 bound is 67 buffers. The extra headroom covers one sample actively owned by the recording writer, the current Program boundary, and one independent monitoring sample while the recording queue may retain its already bounded backlog.

The capacity is an upper bound, not an eager reservation. Normal steady-state execution typically allocates one or a small number of full-frame buffers and then reuses them.

## CUDA readback

The production CUDA path uses IGpuProcessingBackend.ReadbackInto and a caller-supplied destination span.

CudaGpuProcessingBackend validates the exact byte length and copies device memory directly into the rented host buffer with cuMemcpyDtoH_v2. The managed by-reference passed to P/Invoke is valid only for the native call lifetime; arbitrary long-lived pinning is not introduced.

The legacy allocating Readback method remains for compatibility and focused callers, but the RuntimeHost Program path does not use it.

CUDA device-surface pooling remains independent from host readback pooling. Surface release failures keep their existing explicit observation path.

CUDA allocations that were rented for an upload or composite but encounter a native-operation failure are quarantined instead of immediately returned to the size-class free pool. A failed CUDA operation does not establish safe GPU completion or safe reuse of its destination pointer. Stop releases tracked quarantined allocations before releasing pooled allocations and destroying the owning CUDA context. Cleanup failures are propagated and retain their tracked allocation for a subsequent Stop/recovery attempt. A successful upload or composite still uses the existing synchronous completion and normal pool ownership semantics. This is a source-level fail-closed safety measure, not evidence that driver reset or CUDA device-loss cleanup succeeds on physical hardware.

Quarantined allocations can temporarily increase retained device memory until controlled shutdown; the GPU provider already treats CUDA-classified upload and composite failures as failed state requiring recovery. Physical device-loss and driver behavior remain UNVERIFIED.

`CudaGpuProcessingBackend.ResourceStatistics` exposes lock-consistent active surface, pooled allocation and quarantined allocation counts plus a saturating cumulative CUDA context/event synchronization failure count. These counts are diagnostic snapshots, not per-frame event logs. They do not imply that the device allocation pool has a global byte budget; physical peak device memory remains UNVERIFIED.



## Managed reference backend

The managed reference backend also implements ReadbackInto.

Its temporary full-frame surface arrays are reused through a globally bounded free list of at most eight arrays across all observed frame sizes. This makes software-only allocation regression meaningful without presenting managed-reference results as hardware evidence.

The managed backend remains reference behavior and is not CUDA qualification.

## Consumer rules

### Physical Program output

Physical Media I/O is synchronous at the current boundary.

MediaIoVerticalSlice.TrySubmitProgram accepts array-backed ReadOnlyMemory<byte>, pins the existing Program buffer only for the provider call, and releases the pin before returning. It does not retain the buffer and does not make a second full-frame copy.

### Monitoring

Monitoring is asynchronous.

RuntimeMonitoringTap retains the Program lease before placing a sampled boundary into its single pending slot. Replacing a pending sample releases the replaced lease. The worker releases its retained lease after Program downscale or when the sample is no longer needed.

The current CPU/WPF fallback publishes a separate bounded 320×180 payload and has no dependency on the original full-resolution Program lease after downscale.

When the GPU provider can export a shared monitoring resource, the sampled Program boundary may additionally transfer a `GpuSharedMonitoringResourceLease` to `RuntimeMonitoringTap`. That lease retains the original provider surface independently from `GpuReadbackLease`. Replacing a pending or published sample, disconnecting the last subscriber, tap shutdown or provider stop releases it deterministically. The shared-resource path never calls a second Program readback.

### Recording

Recording is asynchronous.

The subsystem-local IProgramRecordingPayloadLease expresses ownership without changing stable recording contracts. RuntimeHost transfers a retained Program lease to a payload-capable recording writer only when recording is active.

The writer owns that lease until one of these boundaries:

- successful or failed sample write;
- explicit discard after recorder backpressure/rejection;
- recording abort;
- session reset cleanup.

Reference and Media Foundation writers release staged leases on every one of those paths.

### Boundary callers

A ProgramBoundaryResult must not be used after disposal. Accessing Program pixels after disposal fails rather than exposing memory that may already have been reused.

## Shutdown and failure behavior

Runtime shutdown drains/disposes monitoring and recording before GPU shutdown. RuntimeProgramExecutionService then verifies that both the Program readback pool has zero active buffers and the shared monitoring resource set has zero active leases before disposing GPU resources.

GPU readback exceptions return the rented host buffer before the exception escapes. Recording staging/enqueue failure paths discard transferred payload ownership. Monitoring replacement and cancellation paths release retained leases.

The pool never relies on finalization for normal correctness.

## Verification

Software validation covers:

- repeated Program boundaries reuse a bounded set of host buffers;
- retained Program memory is not mutated by later frames;
- asynchronous recording observes the correct earlier frame;
- asynchronous monitoring remains correct after the primary caller releases its lease;
- readback failure returns the buffer to the pool;
- shutdown reaches zero active Program readback leases;
- resource-only Program monitoring can omit the CPU payload for a subscriber that explicitly does not require it;
- shared resources remain bounded, retain the backing surface while active and return to zero on replacement/disconnect/shutdown;
- sustained Preview/Program shared-resource replacement keeps the published monitoring set bounded to two resources after each settled update, permits only one bounded old-plus-new replacement pair (four provider leases transiently), and returns to zero after subscriber disconnect;
- stale/foreign shared resource identities are rejected;
- shared-resource export does not add another Program readback;
- a warmed 1080p readback allocation regression remains below 1 MiB across 64 readbacks, which is far below one 8,294,400-byte RGBA frame per iteration.

These tests validate managed ownership and allocation behavior. They do not establish physical CUDA timing, DMA behavior, driver qualification or sustained hardware production performance.

## Hardware evidence boundary

CUDA reference hardware remains UNVERIFIED unless the dedicated self-hosted CUDA qualification workflow runs on the approved Windows x64 NVIDIA reference machine and produces accepted evidence.

A green Required Gates run proves that the reusable readback path compiles, software ownership semantics hold and the CUDA path is structurally exercised. It does not prove physical CUDA execution.
