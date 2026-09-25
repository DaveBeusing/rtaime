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

The later authoritative compositor-control extension adds additive Control and Runtime compositing fields for Rotation, Anchor/Pivot, Crop and one bounded typed processing node. Those fields remain provider-neutral and do not expose CUDA or backend identities.

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

A single processing resource is advertised as:

- kind: `gpu.processing`
- capacity units: `1`
- reservable: `true`

The capability descriptors currently advertise the two V1 development formats:

- 1080p50 RGBA8
- 1080p59.94 RGBA8

The generic `CapabilityRequirementMatcher` can match these capabilities without any GPU-specific Control or Planning implementation.

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

## Static RGBA source

`StaticRgbaSource` owns immutable RGBA source content.

Materialization uploads the same content with production frame timing and generation `0`.

The source is independent from the compositor and may later be backed by a more efficient persistent upload/cache without changing Control or Provider contracts.

## Dynamic RGBA source

`DynamicRgbaSource` owns replaceable RGBA content with a monotonic `Generation`.

Each successful update:

- keeps the video format stable,
- advances generation exactly once,
- changes only future materializations.

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

The provider reuses the established backend primitive for every layer. Managed-reference and CUDA therefore share the same ordered execution model without introducing a second compositor or exposing vendor types outside the provider boundary. Intermediate surfaces are bounded to the active request and released after each pass or on failure.

GPU Processing Foundation does not introduce a graphics authoring system, arbitrary scene graph, browser graphics, or UI-timer animation.

### Authoritative transform and processing preparation

RuntimeHost now materializes supported bitmap and Production CG layers into the existing preallocated full-frame dynamic layer surfaces before the established GPU composite call. The deterministic operation order is:

```text
source crop
→ scale
→ rotation around explicit anchor/pivot
→ translation
→ bounded Color Grade when configured
→ existing GPU ordered RGBA composite
```

Crop, transform and Color Grade do not create a second Program renderer. They prepare the same Runtime-owned dynamic layer surface that the existing `GpuProcessingProvider` consumes. Program, monitoring and recording therefore continue to observe the same post-composite Program pixels.

The initial processing-node implementation is deliberately bounded to one typed Color Grade node per supported layer. It exposes Brightness, Contrast and Saturation only; arbitrary shader blobs, arbitrary stacks and Keying are not implemented. Processing-node configuration is validated before execution and is carried through the normal prepared/transactional Control path.

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

The provider fails closed for:

- unavailable backend,
- disposed/foreign surfaces,
- format mismatch,
- timing mismatch,
- more than eight active layers,
- duplicate layer surfaces in one request,
- backend exceptions.

A backend exception is observed as:

```text
gpu.composite.backend_failure
```

The failed output allocation is released when possible. Existing input surfaces remain owned by the caller.

## Repeated start/stop

The provider supports repeated:

```text
Start
→ process
→ Stop
→ Start
→ process
```

Stop releases active provider surfaces before the backend is stopped.

This is required groundwork for host restart, recovery, and controlled shutdown behaviour.

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

The Performance test project also measures authoritative transform plus Color Grade materialization at both supported 1080p50 and 1080p59.94 development formats. The regression guard uses a full-frame RGBA bitmap, retains one typed Color Grade node, alternates bounded rotation updates, records total/per-mutation wall-clock cost and verifies that reconfiguration leaves the GPU active-surface baseline unchanged. The threshold is deliberately broad and protects against runaway managed materialization cost rather than asserting a real-time hardware budget. Reference-platform execution is still required before any hardware latency claim is made; no new hardware PASS claim is inferred from hosted CI.

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
