<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Program Frame Memory Ownership

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

V1ProgramBoundaryResult owns the primary Program readback lease and is disposable. Production callers must dispose it deterministically. RuntimeHostProcess scopes every processed boundary with using.

## Bounded readback pool

GpuProcessingProvider owns a fixed-capacity GpuReadbackBufferPool.

The pool:

- allocates exact-size managed byte arrays lazily;
- reuses returned arrays for later readbacks of the same size;
- tracks allocated, available and active buffers plus total and exhausted rents;
- never grows beyond its configured capacity;
- fails closed if every reusable buffer is still leased.

The generic provider default is four readback buffers. V1RuntimeHostService configures the Program pool to ProgramRecorder.DefaultQueueCapacity + 3. The current recording queue capacity is 64, so the V1 bound is 67 buffers. The extra headroom covers one sample actively owned by the recording writer, the current Program boundary, and one independent monitoring sample while the recording queue may retain its already bounded backlog.

The capacity is an upper bound, not an eager reservation. Normal steady-state execution typically allocates one or a small number of full-frame buffers and then reuses them.

## CUDA readback

The production CUDA path uses IGpuProcessingBackend.ReadbackInto and a caller-supplied destination span.

CudaGpuProcessingBackend validates the exact byte length and copies device memory directly into the rented host buffer with cuMemcpyDtoH_v2. The managed by-reference passed to P/Invoke is valid only for the native call lifetime; arbitrary long-lived pinning is not introduced.

The legacy allocating Readback method remains for compatibility and focused callers, but the RuntimeHost Program path does not use it.

CUDA device-surface pooling remains independent from host readback pooling. Surface release failures keep their existing explicit observation path.

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

A V1ProgramBoundaryResult must not be used after disposal. Accessing Program pixels after disposal fails rather than exposing memory that may already have been reused.

## Shutdown and failure behavior

Runtime shutdown drains/disposes monitoring and recording before GPU shutdown. V1RuntimeHostService then verifies that both the Program readback pool has zero active buffers and the shared monitoring resource set has zero active leases before disposing GPU resources.

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
- sustained Preview/Program shared-resource replacement keeps the published monitoring set bounded to two resources and returns to zero after subscriber disconnect;
- stale/foreign shared resource identities are rejected;
- shared-resource export does not add another Program readback;
- a warmed 1080p readback allocation regression remains below 1 MiB across 64 readbacks, which is far below one 8,294,400-byte RGBA frame per iteration.

These tests validate managed ownership and allocation behavior. They do not establish physical CUDA timing, DMA behavior, driver qualification or sustained hardware production performance.

## Hardware evidence boundary

CUDA reference hardware remains UNVERIFIED unless the dedicated self-hosted CUDA qualification workflow runs on the approved Windows x64 NVIDIA reference machine and produces accepted evidence.

A green Required Gates run proves that the reusable readback path compiles, software ownership semantics hold and the CUDA path is structurally exercised. It does not prove physical CUDA execution.
