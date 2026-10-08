# GPU Memory and Transfer Path Optimization

## Status

- implementation baseline: master at `88b971df65f6c7329241876a9c570072f6f94ea9`
- .NET SDK: 10.0.401
- stable contract version change: none
- Program readback removal: none
- backend-native Program output capability: not introduced
- reusable Runtime layer uploads: implemented as explicit provider-internal opt-in
- physical PCIe / CUDA performance comparison for this source: UNVERIFIED

This document records the V1 transfer map, the bounded reuse optimization that can be proven from source semantics, and the evidence boundary for future low-copy output work.

## Full-frame transfer inventory

| Path | Direction | Current behavior | Consumer / ownership | Optimization state |
| --- | --- | --- | --- | --- |
| Runtime input materialization | Host -> GPU | one upload for each GPU-materialized input boundary | compositor backgrounds / Preview or Aux when required | retained; input pixels may change each boundary |
| Static legacy visual layer | Host -> GPU | first generation upload, then reusable device surface | compositor layer | repeated unchanged uploads removed |
| Dynamic legacy visual layer | Host -> GPU | first upload for a generation, then reusable until generation/content mutation | compositor layer | repeated unchanged uploads removed |
| Bitmap graphics layer | Host -> GPU | transformed CPU buffer uploads once per generated content version | compositor layer | repeated unchanged uploads removed |
| Production CG layer | Host -> GPU | generated CPU buffer uploads once per generation/content version | compositor layer | repeated unchanged uploads removed |
| Composite output | GPU write | kernel writes target surface; multi-layer passes may use bounded intermediate surfaces | Program result | no host transfer |
| Program readback | GPU -> Host | one synchronous readback into reusable `GpuReadbackLease` | Program host consumers | retained; required by current consumers |
| Physical Program output | Host lease -> native provider | existing Program readback is pinned/borrowed for synchronous submit | Media I/O output | no second full-frame managed copy |
| Recording | Host lease | retains the existing Program readback | bounded recorder queue | no second GPU readback |
| Replay | Host lease | retains the existing Program readback | bounded replay queue | no second GPU readback |
| Program network output | Host lease | retains the existing Program readback | bounded network-output queue | no second GPU readback |
| Aux network output | GPU -> Host | separate readback when Aux network output consumes a different source surface | bounded Aux output path | required by current host-based encoder contract |
| Program monitoring CPU fallback | Host lease | retains the existing Program readback | monitoring tap | no second GPU readback |
| GPU monitoring presentation | GPU -> GPU | one CUDA/D3D11 device copy into the shareable presentation resource | Operator monitoring | not zero-copy; physical cost UNVERIFIED |
| Physical Media I/O input | native host lease -> Runtime host buffer -> GPU when required | adapter lease is copied into bounded Runtime working memory before normal GPU admission | Runtime current input frame | retained by current host-memory Media I/O contract |

RGBA8 1920x1080 is 8,294,400 bytes per full-frame transfer. One avoided unchanged layer upload therefore removes that amount of Host-to-Device payload from the logical transfer count. Physical PCIe traffic and driver behavior remain reference-hardware evidence.

## Explicit reusable upload model

Reuse is opt-in through `StaticRgbaSource.MaterializeReusable` and `DynamicRgbaSource.MaterializeReusable`.

Normal `Materialize` remains unchanged for short-lived/ad-hoc sources. This avoids silently retaining arbitrary source surfaces.

RuntimeHost opts in only for its long-lived compositing resources:

- legacy static layer;
- legacy dynamic layer;
- bitmap graphics layer;
- Production CG layer.

The provider maintains at most `GpuProcessingProvider.ReusableUploadSurfaceCapacity` retained reusable surfaces. The V1 capacity is 16. Capacity pressure evicts the least-recently-used cache retention; a still-live frame or monitoring lease keeps the underlying surface valid until its final owner releases it.

Each reused materialization still receives a new `FrameDescriptor` with current boundary timing. The underlying `SurfaceDescriptor`, `SurfaceId`, generation and backend allocation are reused only while the source generation, format and tracked RGBA content version remain identical.

This preserves the existing compositor requirement that all frame descriptors in one request carry current matching timing without re-uploading identical pixels.

## Mutation and generation safety

`RgbaFrameBuffer` tracks a monotonic in-process content version. Both full-buffer and region mutations advance it.

Reusable lookup requires all of the following to match:

- logical source identity;
- reusable source kind;
- the same RGBA buffer instance;
- video format;
- source generation;
- RGBA content version;
- live provider-owned surface.

Buffer identity prevents a newly constructed static source with the same logical identity and initial generation/version from reusing stale pixels from an older source instance.

A dynamic source update advances its existing `Generation`. Direct mutation of a reusable RGBA buffer advances the content version. Either condition invalidates the retained cache entry and causes one new upload.

The old surface is not freed early. Cache retention is removed first; backend release occurs only after outstanding frame references and monitoring leases are gone.

## Transfer and pool pressure evidence

`GpuProcessingProvider.MemoryTransferStatistics` exposes bounded cumulative counters for:

- logical provider upload operations and bytes;
- physical-domain Host-to-Device operations and bytes only when the backend surface domain is `Device`;
- logical provider readback operations and bytes;
- physical-domain Device-to-Host operations and bytes only when the backend surface domain is `Device`;
- successful monitoring device-copy operations and bytes;
- reusable-upload cache capacity and retained surface count;
- reusable-upload hits, misses, content/source invalidations and capacity evictions;
- logical upload bytes avoided through reusable materialization plus the HtoD subset when the backend is device-resident;
- readback pool capacity / allocated / available / active / exhausted state;
- shared monitoring resource capacity / active / total / rejected state.

This distinction is deliberate: managed-reference execution can prove that a logical full-frame provider upload/readback was eliminated, but it must report zero HtoD/DtoH traffic because it is host-resident software. CUDA/device-resident qualification can populate the physical-domain counters, but actual PCIe bus behavior still requires hardware evidence.

RuntimeHost exposes the provider snapshot through `GpuMemoryTransfers`, and `RuntimeHostDiagnostics` publishes the transfer/reuse/readback/monitoring pressure counters into the existing bounded support-health snapshot. No per-frame history, file I/O, network I/O or new hot-path allocation is introduced by these counters.

Counters are saturating. They are diagnostic evidence, not production authority.

## Pool policy

### Reusable device surfaces

The retained reusable upload cache is capped at 16 surfaces. RuntimeHost currently requires far fewer persistent compositing resources, leaving bounded replacement headroom without creating an unbounded texture cache.

### Program readback buffers

The existing RuntimeHost capacity remains derived from bounded downstream ownership:

`ProgramRecorder.DefaultQueueCapacity + 3`, plus separately bounded network/replay requirements configured by RuntimeHost.

Buffers are allocated on demand, not eagerly reserved. Pool exhaustion remains explicit degraded health and does not allocate around the bound.

### CUDA allocation reuse

The CUDA backend retains its existing bounded per-size device allocation pool. This change reduces how often unchanged long-lived layers enter the upload/allocation path; it does not increase the backend pool limit.

## Output low-copy capability decision

No backend-native Program-output path is introduced in this change.

The current Media I/O output contract and native ABI consume the existing pinned host Program readback. Network output and recording likewise consume host payload leases. There is no current provider-neutral capability that proves a target can safely consume a GPU-native surface with explicit compatible lifetime and synchronization.

Adding CUDA pointers, D3D object pointers or device identities to stable contracts would violate the backend-neutral resource boundary.

A future low-copy output implementation therefore requires all of the following before integration:

1. an explicit provider capability advertising backend-native surface consumption;
2. a provider-neutral resource/lifetime contract or narrow adapter-local seam;
3. exact completion semantics compatible with `ProducerCompletedBeforePublication`;
4. deterministic fallback to the existing host-readback path;
5. byte-exact output parity;
6. bounded in-flight ownership and shutdown drain;
7. reference-hardware evidence showing fewer transfers or lower P95/P99 complete-boundary time.

Until those conditions exist, the host-readback path remains authoritative.

## Software evidence

Software regression coverage proves:

- repeated explicit reusable materialization performs one upload plus cache hits;
- frame timing advances while the backend surface remains the same;
- content mutation/generation change replaces the cache entry and preserves exact pixels;
- an old surface remains valid until its final frame reference is released;
- cache retention is bounded and eviction is deterministic;
- provider stop drains reusable surfaces to zero;
- transfer counters record readback and avoided-upload byte counts;
- the default non-reusable source path retains its previous ownership behavior.

The managed-reference 1080p performance regression counts logical full-frame transfers. It is not physical PCIe performance evidence.

## Physical evidence boundary

The following remain **UNVERIFIED** for this source until an exact-SHA reference-hardware run is captured:

- actual PCIe Host-to-Device and Device-to-Host traffic;
- CUDA allocation peak behavior under the complete production workload;
- physical monitoring D2D cost;
- P50/P95/P99 frame-time improvement from reusable layer uploads;
- driver-specific pinned-memory behavior;
- benefit of any future backend-native output capability.

Do not infer physical performance improvement from software transfer counters alone.
