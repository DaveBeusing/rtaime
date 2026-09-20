<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# GPU Processing Foundation

## Scope

This document records GPU Processing Foundation, `GPU Processing Foundation`.

Change classification: `REALTIME_CRITICAL`.

The package implements the first GPU-processing provider boundary while preserving the existing architecture rule that Control and Planning remain capability-based and vendor-neutral.

No serialized Control, Runtime, Media, AI, or Provider contract is changed by this package.

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

One RGBA layer is supported.

The layer has:

- source surface,
- visibility,
- opacity `0..255`,
- source alpha.

Effective alpha is source alpha multiplied by layer opacity, followed by deterministic straight-alpha composition over the transitioned background.

GPU Processing Foundation does not introduce a graphics authoring system, browser graphics, multi-layer scene graph, or UI-timer animation.

## GPU observations

`GpuProcessingProvider` records ordered observations for significant processing events, including:

- provider start,
- provider stop,
- surface allocation,
- surface release,
- CUT,
- DISSOLVE,
- rejected composite input,
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

DISSOLVE and the RGBA layer are proven as GPU-provider processing primitives in GPU Processing Foundation. Their future authoritative production-control representation must be introduced only through an explicit later contract/product integration step; GPU Processing Foundation does not silently extend the Control contract.

## Failure and recovery

The provider fails closed for:

- unavailable backend,
- disposed/foreign surfaces,
- format mismatch,
- timing mismatch,
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
- one RGBA layer,
- output allocation/release,
- provider lifecycle.

The threshold is intentionally broad and is a managed semantic/performance regression guard only.

It is **not**:

- CUDA performance evidence,
- GPU latency qualification,
- DMA evidence,
- zero-copy device interoperability evidence,
- genlock evidence,
- hard-real-time certification.

Hardware performance remains `UNVERIFIED` until measured on the qualified reference GPU/driver configuration.

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
- multiple key layers,
- native C++ adapter code,
- professional media capture/output hardware,
- DMA/import of external vendor surfaces,
- production graphics authoring,
- Audio Follow Video,
- recording,
- AI inference.
