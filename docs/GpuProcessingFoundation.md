<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# GPU Processing Foundation

## Scope

This document records GPU Processing Foundation, `GPU Processing Foundation`.

Change classification: `REALTIME_CRITICAL`.

The package implements the first GPU-processing provider boundary while preserving the existing architecture rule that Control and Planning remain capability-based and vendor-neutral.

The later authoritative compositor-control extension adds additive Control and Runtime compositing fields for Rotation, Anchor/Pivot, Crop and a bounded ordered typed processing stack. Those fields remain provider-neutral and do not expose CUDA or backend identities.

## Boundary

The implementation follows the accepted direction:

```text
Managed Runtime / Host composition
        ↓
Provider Contract
        ↓
rtaime.Provider.Gpu managed provider
        ↓
managed GPU backend abstraction
        ↓
CUDA Driver API when hardware is available
```

No native C++ adapter is introduced because GPU Processing Foundation does not currently have evidence that a separate native layer is required for SDK, memory, interop, or measured real-time reasons.

The NVIDIA CUDA backend uses direct managed P/Invoke to the CUDA Driver API. It is entirely behind `rtaime.Provider.Gpu` and does not leak CUDA identities, handles, or types into Control, Planning, Runtime contracts, Media contracts, or Provider contracts.

## Capability model

`GpuProcessingProvider` advertises stable capability kinds through the existing generic Provider contract:

- `gpu.rgba.static`
- `gpu.rgba.dynamic`
- `gpu.rgba.composite`
- `gpu.transition.cut`
- `gpu.transition.dissolve`
- `gpu.monitoring.shared-resource` on eligible hardware-accelerated device-resident backends

A single processing resource is advertised as:

- kind: `gpu.processing`
- capacity units: `1`
- reservable: `true`

The capability descriptors currently advertise the two V1 development formats:

- 1080p50 RGBA8
- 1080p59.94 RGBA8

The generic `CapabilityRequirementMatcher` can match these capabilities without any GPU-specific Control or Planning implementation. The managed-reference backend deliberately does not advertise `gpu.monitoring.shared-resource`; it remains host-backed reference behavior and must not impersonate hardware sharing capability.

## Backend evidence model

Two backend implementations exist.

### Managed reference backend

`ManagedReferenceGpuBackend` provides deterministic functional behaviour for CI, tests, and semantic comparison.

It is deliberately reported through the Provider descriptor as:

```text
ProviderAvailabilityState.Degraded
failure = gpu.backend.reference_only
```

It must never be interpreted as hardware qualification evidence.

The reference backend performs RGBA processing in managed memory and is used to verify:

- source semantics,
- transition semantics,
- compositing semantics,
- lifetime handling,
- failure containment,
- provider lifecycle,
- deterministic expected pixel results.

### NVIDIA CUDA backend

`CudaGpuProcessingBackend` is the hardware-accelerated implementation candidate for the Windows V1 reference platform.

The executable RuntimeHost now performs fail-closed CUDA capability detection during composition. When CUDA is available and reports hardware acceleration, the production RuntimeHost selects `CudaGpuProcessingBackend`. Environments without CUDA continue with `ManagedReferenceGpuBackend`, whose provider availability remains explicitly `Degraded`; that fallback is functional evidence only and must never be presented as production GPU qualification. Direct `V1RuntimeHostService` construction keeps the managed reference default so deterministic tests remain hardware-independent.

Capability detection is fail-closed:

- missing `nvcuda.dll` → unavailable,
- missing required Driver API entry point → unavailable,
- incompatible process architecture → unavailable,
- missing requested CUDA device → unavailable,
- CUDA initialization/device detection failure → unavailable.

Only successful CUDA device detection produces:

```text
hardwareAccelerated = true
available = true
ProviderAvailabilityState.Available
```

The backend owns:

- CUDA context creation/destruction,
- PTX module loading,
- bounded device-memory allocation reuse for frame surfaces,
- direct host-buffer-to-device upload without an intermediate full-frame managed copy,
- device-to-host Program readback,
- deterministic RGBA composite kernel launch,
- synchronization at the GPU Processing Foundation processing boundary.

Released frame surfaces are returned to a bounded per-size device-memory pool and reused by subsequent frame boundaries. This removes repeated `cuMemAlloc/cuMemFree` operations from the steady-state frame path while preserving provider-visible surface ownership and release semantics.

The current hosted Windows CI environment does not provide qualified NVIDIA GPU evidence. Therefore CUDA execution remains `UNVERIFIED` unless a run on an approved GPU environment is explicitly captured.

## Surface model and memory lifetime

Bulk pixel data is not added to normal contracts.

Implementation-side host RGBA content is represented by `RgbaFrameBuffer` only inside `rtaime.Provider.Gpu` APIs.

A materialized GPU frame exposes the existing contract model:

```text
FrameDescriptor
  → SurfaceDescriptor
      → SurfaceId
      → VideoFormat
      → StorageDomain
      → Ownership
      → SurfaceLifetimeDescriptor
      → OpaqueSurfaceHandle
```

The opaque handle contains an rtaime surface identity, not a raw CUDA device pointer.

`GpuFrame` explicitly owns a materialized provider surface. Disposal releases the backend allocation. Provider stop releases all remaining active surfaces and marks their frame wrappers released.

This keeps raw GPU allocations inside the backend implementation and prevents vendor pointers from becoming transport contracts.

Program host readback now has a separate explicit lifetime. `GpuProcessingProvider.RentReadback` fills a fixed-capacity reusable host buffer and returns a reference-counted `GpuReadbackLease`. CUDA implements the path through `ReadbackInto`, copying device memory directly into the caller-supplied reusable buffer. RuntimeHost, monitoring and recording release retained references deterministically; the buffer returns to the readback pool only after the last consumer releases it.

The legacy allocating `Readback` API remains for compatibility and focused semantic tests. It is not used by the RuntimeHost Program hot path. Detailed ownership rules are recorded in [ProgramFrameMemoryOwnership.md](ProgramFrameMemoryOwnership.md).

## Shared monitoring resource leases

`GpuProcessingProvider.TryExportMonitoringResource` establishes a bounded read-only monitoring lease over an already materialized GPU frame. The export path reuses the existing `SurfaceId`, `VideoFormat` and generation semantics while publishing a dedicated rtaime-owned `MonitoringResourceId`. Stable monitoring contracts do not expose `OpaqueSurfaceHandle`, CUDA pointers or CUDA driver identities.

The provider retains the backing source surface while an exported monitoring lease is active. The published monitoring set remains one Preview plus one Program resource. Provider capacity is four leases so one complete replacement pair can be exported before the monitoring tap atomically retires the previously published pair. Capacity exhaustion remains a normal unavailable-for-this-frame result rather than blocking or failing Program execution.

On the qualified Windows CUDA backend, `CudaD3D11MonitoringInterop` creates a shareable D3D11 RGBA8 texture on the CUDA device's adapter and performs the sampled transfer entirely on the GPU. The resulting Windows graphics shared handle and adapter LUID are narrow presentation metadata; the CUDA device pointer remains private to the backend. The resource is opened read-only by the Operator's `GpuMonitorPresentationSurface`.

This path is GPU-resident but is not zero-copy: the sampled monitoring boundary performs one device-to-device copy from the provider surface into the shareable presentation texture. It performs no additional GPU-to-CPU Program readback.

Replacing a pending/published observation, subscriber disconnect, Runtime shutdown and provider stop all release retained presentation resources deterministically. The Operator also releases its opened D3D resource on frame replacement or graphics-surface recreation.

Hosted CI qualifies contract, lifetime, fallback and software presentation policy. Physical CUDA/D3D11 interoperability remains `UNVERIFIED` until captured on an approved NVIDIA reference system.


## Static RGBA source

`StaticRgbaSource` owns RGBA source content with generation `0`.

Normal `Materialize` retains the original one-upload-per-materialization behavior for short-lived/ad-hoc callers. `MaterializeReusable` is an explicit opt-in for long-lived sources: the provider retains one bounded backend surface while source identity, format, generation and tracked RGBA content version remain unchanged, while each returned frame descriptor still carries the current production timing.

Direct buffer mutation advances the RGBA content version and therefore invalidates the reusable entry before the next materialization.

RuntimeHost uses the reusable path for its long-lived compositing layers only. See [GpuMemoryTransferOptimization.md](GpuMemoryTransferOptimization.md) for transfer accounting and cache bounds.

## Dynamic RGBA source

`DynamicRgbaSource` owns replaceable RGBA content with a monotonic `Generation`.

Each successful update:

- keeps the video format stable,
- advances generation exactly once,
- changes only future materializations.

Normal `Materialize` remains non-retaining. `MaterializeReusable` reuses the device surface only while both generation and RGBA content version remain unchanged. A generation/content change retires cache retention without freeing a surface that still has an outstanding frame or monitoring owner.

Dynamic source generation remains separate from frame timing and surface identity.

## Minimal compositor

GPU Processing Foundation implements a deterministic RGBA8 compositor with:

```text
Background A
      +
Background B / transition
      +
optional RGBA key/compositing layer
      ↓
Output RGBA surface
```

All composite inputs must:

- belong to the same `GpuProcessingProvider`,
- still own live surfaces,
- use the same `VideoFormat`,
- use the same `FrameTiming`.

Invalid input is rejected before backend processing.

### CUT

CUT uses an exact blend weight:

```text
0   → Background A
255 → Background B
```

Intermediate CUT weights are invalid.

### DISSOLVE

DISSOLVE uses a deterministic integer blend weight from `0..255`.

Channel blend is evaluated as:

```text
(A * (255 - weight) + B * weight + 127) / 255
```

This avoids wall-clock dependence and keeps reference and hardware backend semantics aligned.

### Key/compositing layer

The provider accepts a bounded ordered list of RGBA layers. The current production bound is `GpuCompositeLimits.MaxActiveLayers = 8`. The legacy one-layer request constructor remains supported for compatibility.

Each layer has:

- source surface,
- visibility,
- opacity `0..255`,
- source alpha.

The request order is authoritative for provider execution. CUT/DISSOLVE resolves the background first; visible layers are then composed in declared order, so later layers are visually above earlier layers. Effective alpha is source alpha multiplied by layer opacity, followed by deterministic straight-alpha composition.

The provider reuses the established backend primitive for every contributing layer. Managed-reference and CUDA therefore share the same ordered execution model without introducing a second compositor or exposing vendor types outside the provider boundary. Intermediate surfaces are bounded to the active request and released after each pass or on failure.

A layer that is explicitly hidden or has opacity `0` is an exact semantic no-op and is not submitted as a backend pass. All requested layers are still validated before this reduction, request ordering remains authoritative, and at least one transition pass is retained when no layer contributes. See [CompositorPipelineOptimization.md](CompositorPipelineOptimization.md) for golden parity, pass-count profiling and the evidence boundary that currently blocks a fused multi-layer CUDA kernel.

GPU Processing Foundation does not introduce a graphics authoring system, arbitrary scene graph, browser graphics, or UI-timer animation.

### Authoritative transform and processing preparation

RuntimeHost now materializes supported bitmap and Production CG layers into the existing preallocated full-frame dynamic layer surfaces before the established GPU composite call. The deterministic operation order is:

```text
source crop
→ scale
→ rotation around explicit anchor/pivot
→ translation
→ bounded ordered typed processing stack (Color Grade and Chroma Key) when configured
→ existing GPU ordered RGBA composite
```

Crop, transform, Color Grade and Chroma Key do not create a second Program renderer. They prepare the same Runtime-owned dynamic layer surface that the existing `GpuProcessingProvider` consumes. Program, monitoring and recording therefore continue to observe the same post-composite Program pixels.

The processing model is deliberately bounded to an ordered stack of 0..4 typed nodes per supported layer. The closed node-kind union defines Color Grade (Brightness, Contrast and Saturation) and a deterministic Chroma Key baseline (8-bit RGB key color plus normalized Tolerance, Softness and Spill Suppression). Stable node identities and explicit list order cross authoritative Control state, prepared execution, IPC, Runtime confirmation, Scene state and recovery. Disabled nodes remain configured but are skipped during execution. Chroma Key uses BT.709-derived normalized chroma distance, multiplies its matte with source alpha, and suppresses spill only in RGB. Duplicate identities, oversized stacks, unknown kinds and invalid kind-specific settings fail closed. Arbitrary shader blobs, plugin-defined nodes and unbounded stacks remain unsupported. The Chroma Key capability is a bounded typed production baseline, not a certified full broadcast keyer; HDR/wide-gamut keying and external key/fill qualification remain out of scope.

A confirmed direct graphics or compositing mutation is not acknowledged as synchronized while Runtime still carries an older Control `AuthoritySnapshot`. ControlHost reapplies the current prepared execution within the same serialized mutation boundary when the authoritative Production Revision advances, then verifies that Runtime reports the same Production identity and revision. This prevents routine Inspector/graphics edits from being mistaken for recovery drift by the Runtime binding loop.

Layer materialization reuses the existing Runtime scratch arrays and dynamic frame buffers. Reconfiguration performs bounded work when an authoritative transform or processing mutation is applied; it does not introduce a new per-frame allocation queue or a high-rate logging path.

## GPU observations

`GpuProcessingProvider` records ordered observations for significant processing events, including:

- provider start,
- provider stop,
- surface allocation,
- surface release,
- CUT,
- DISSOLVE,
- rejected composite input,
- active composite layer count,
- measured composite duration,
- backend processing failure,
- backend release failure.

Unexpected backend exceptions are converted into stable processing failure results at the provider boundary. A failed composite does not automatically destroy previously valid input surfaces and a subsequent operation may recover if the backend remains usable.

## VirtualMedia vertical reuse

The GPU Processing Foundation integration proof keeps the existing authority path unchanged:

```text
Production Specification
→ Authoritative State
→ Capability Planning
→ Prepared Execution
→ Runtime Commit
→ committed Program binding
→ Virtual timing/source identity
→ GPU Provider
→ CUT/composite output
```

Control and Planning still resolve `media.route` through the VirtualMedia provider. They do not contain GPU-, CUDA-, or NVIDIA-specific branches.

The committed Runtime Program binding selects which background is used for GPU CUT processing.

DISSOLVE and ordered RGBA layers remain GPU-provider processing primitives. Governed Scene activation now has an explicit later integration above this provider boundary: Control may carry a bounded, versioned Scene compositing snapshot through the existing Prepared Execution transaction, while RuntimeHost maps that snapshot onto the already established stable layer identities. The GPU provider itself remains unaware of Scene identity and gains no Control authority.

## Failure and recovery

GPU provider lifecycle and recovery are defined in `docs/GpuLifecycleRecovery.md`.

The provider now distinguishes production-critical CUDA failures from isolated degraded conditions. CUDA upload, composite and Program readback failures fail closed until controlled recovery. Monitoring export/release failures and bounded lease pressure remain explicitly degraded when Program correctness is not affected.

Failed surface/allocation releases remain tracked until cleanup succeeds; a failed cleanup cannot be reported as a clean Stop. Successful Start or Recover rotates lifecycle generation and monitoring instance identity so stale resources cannot be reused.

Managed-reference execution remains software evidence only and is never a silent live-production fallback for a failed CUDA path.

## Repeated start/stop and recovery

The provider supports deterministic repeated Start/Stop cycles plus explicit Recover from Degraded/Failed state. Stop and Recover drain tracked surfaces and monitoring resources before backend restart. If cleanup cannot complete, the provider remains Failed with reason evidence instead of claiming Ready or Stopped.

## Performance evidence

The Performance test project includes 1080p50 and 1080p59.94 compositor regression measurements using the managed reference backend.

These measurements exercise:

- full 1920×1080 RGBA buffers,
- background blending,
- ordered RGBA layer composition,
- explicit 0 / 1 / 2 / 4 / 8 layer-count cases,
- provider-reported composition duration and wall-clock comparison,
- layer-count-dependent output/intermediate allocation and release,
- active-surface return to the persistent input baseline after each case,
- provider lifecycle.

The threshold is intentionally broad and is a managed semantic/performance regression guard only. The layer-scaling cases record measurements without introducing a new physical hardware PASS threshold.

It is **not**:

- CUDA performance evidence,
- GPU latency qualification,
- DMA evidence,
- zero-copy device interoperability evidence,
- genlock evidence,
- hard-real-time certification.

Hardware performance remains `UNVERIFIED` until measured on the qualified reference GPU/driver configuration.

The Operator monitoring stack has a separate consolidated software qualification matrix in `docs/MonitoringVisualPerformanceQualification.md`. That matrix covers shared-resource lifetime, GPU/WPF geometry parity, bounded scope/ROI analysis, reconnect and sustained resource replacement, but it does not promote hosted-CI measurements into CUDA, driver, display or scan-out qualification.

The Performance test project also measures authoritative transform plus typed processing materialization at both supported 1080p50 and 1080p59.94 development formats. The regression guard uses a full-frame RGBA bitmap, retains the maximum four-node mixed Color Grade/Chroma Key stack, alternates bounded rotation updates, records total/per-mutation wall-clock cost and verifies that reconfiguration leaves the GPU active-surface baseline unchanged. The threshold is deliberately broad and protects against runaway managed materialization cost rather than asserting a real-time hardware budget. Reference-platform execution is still required before any hardware latency claim is made; no new hardware PASS claim is inferred from hosted CI.

## Explicit evidence boundary

A green hosted CI run may prove:

- managed build correctness,
- contract/architecture dependency correctness,
- deterministic reference compositor semantics,
- source/lifetime semantics,
- CUDA capability detection fail-closed behaviour,
- integration with the existing committed VirtualMedia path,
- managed performance-regression bounds.

A green hosted CI run does **not** prove:

- an NVIDIA GPU was present,
- CUDA kernel execution succeeded on production hardware,
- professional GPU support,
- validated/certified GPU status,
- driver qualification,
- sustained real-time 1080p production latency.

Per project governance, `UNVERIFIED` is never treated as `PASS`.

## Out of scope

GPU Processing Foundation does not implement:

- GPU-specific Control state,
- GPU-specific Planning branches,
- multi-GPU scheduling,
- arbitrary graphics scene graphs,
- unbounded layer counts,
- native C++ adapter code,
- professional media capture/output hardware,
- DMA/import of external vendor surfaces,
- production graphics authoring,
- Audio Follow Video,
- recording,
- AI inference.
