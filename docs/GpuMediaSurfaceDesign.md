<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Proposed GPU media surface and measurement boundary

Date: 2026-10-18  
Status: **PROPOSED / NOT IMPLEMENTED / PERFORMANCE UNVERIFIED**  
Source baseline and decision: [GPU media evaluation](GpuResidentMediaEvaluation.md)

This proposal defines the review boundary before adding hardware code. It is not
an accepted ADR, public API, supported pixel format or decoder selection.

## Alternatives

| Candidate | Technically required work | Cost / risk / disposition |
| --- | --- | --- |
| Existing CPU RGB32/RGBA | Reuse Media Foundation, existing host slots and normal GPU upload | Supported baseline; retain until a measured replacement wins |
| D3D11-aware MFT -> DXGI NV12 -> D3D11 normalization -> RGBA interop -> current CUDA compositor | Select a compatible device manager/MFT, retain sample plus texture/subresource, negotiate a shareable normalized texture, import/copy into existing CUDA ownership | First candidate to investigate after approval; removes some host traffic only if GPU residency is demonstrated. Conversion, D2D copy and synchronization may erase the benefit |
| Direct NV12/P010 CUDA import and conversion | Prove planar format interop, pitch/subresource interpretation, conversion and compositor input access on the exact driver | Not assumed viable. NVIDIA's documented registration-format list does not list NV12/P010; a successful allocation is insufficient |
| D3D11-only decode/composite/output | New compositor/output implementation and recovery matrix | Rejected here: conflicts with the single existing Program renderer |
| NVDEC or another native decoder library | New dependency/ABI, packaging, servicing and explicit scope decision | Outside approval; no production SDK/library pin is selected |
| P010/high-depth processing | Typed depth/chroma/transfer semantics, precision and independent visual qualification | Evaluation only; not SDR/HDR support or permission to change PixelFormat |

Microsoft documents that a Source Reader device manager can enable DXVA when the
decoder supports it; IMFDXGIBuffer exposes the resource and subresource index.
Neither proves that the current decoder selects a compatible MFT. See
[Source Reader device manager](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-source-reader-d3d-manager)
and [DXGI buffer](https://learn.microsoft.com/en-us/windows/win32/api/mfobjects/nn-mfobjects-imfdxgibuffer).

NVIDIA documents resource/type/format restrictions and potentially high registration
overhead. Texture subresources are CUDA arrays, while the current compositor consumes
linear CUDA allocations. A compatible texture may still require D2D normalization/
copying or a new kernel access path. A native pointer or monitoring handle cannot
bypass those checks. See
[CUDA D3D11 interop](https://docs.nvidia.com/cuda/cuda-driver-api/group__CUDA__D3D11.html).
These are platform constraints; candidate feasibility and benefit remain hypotheses.

The smallest proposed experiment is one already admitted SDR, progressive, 8-bit
4:2:0 H.264 MP4 tuple, normalized to existing 1080p50/59.94 RGBA8 and existing audio.
Exact profile/color/range/driver selection and measurable acceptance must be
approved first. HEVC, a second tuple, is not inferred from H.264 success. Installed
HEVC availability is not a pinned decoder or approval.

## Private typed descriptor proposal

Keep the existing vendor-neutral FrameDescriptor, SurfaceLifetimeDescriptor and
PreparedExecution semantics. PixelFormat currently permits RGBA8 only. Device
storage and an opaque handle do not describe planes, fences or a valid import.

Initially model native decoded samples in the implementation/provider boundary,
without COM types, CUDA pointers or backend names entering Control/Runtime contracts
or management IPC. Proposed immutable fields:

| Group | Required meaning |
| --- | --- |
| Owner | Registry source, asset identity/fingerprint, decode generation, unique lease identity, provider instance and GPU device epoch |
| Timing | Original source PTS/timebase, intended presentation frame and prepared execution/revision association; no new media clock |
| Layout | Native encoding, visible/coded extent, crop, plane count, each plane's offset/pitch/rows/subresource, allocated byte extent and bounds |
| Color | Primaries, transfer, matrix, nominal range, chroma siting, bit depth, alpha rule, metadata authority and the exact normalization policy |
| Orientation | Top-down/bottom-up, visible origin and rotation; unsupported interlace/rotation cannot be silently reinterpreted |
| Residency | Selected adapter identity/LUID at the private boundary, validated consumer affinity, opaque registry resource token |
| Completion | Producer-completed proof tied to device epoch and resource generation; not a timestamp interpreted as a fence |
| Retention | Explicit independent sample/texture/map ownership, bounded reference count and one terminal cleanup/quarantine result |

NV12's subsampled planes and P010's container depth must not be guessed from the
output RGBA dimensions. Validate row extents, odd dimensions/crop, overflow,
alignment and allocated bounds before use. No shader reads from an arbitrary
opaque address. Metadata labeled unknown/inferred does not become authoritative.

Normalization must yield the existing RGBA byte order, top-left origin and opaque
alpha for this initial slice. Agree exact range/matrix/rounding/chroma reconstruction
and transfer behavior before comparison. Preserve the established compositor's
current color math; changing linear-light blending is another decision. Do not
declare Rec.709 merely because width is 1920. Unsupported/ambiguous GPU layouts
choose the current CPU path before publication, with a recorded reason.

Byte-exact CPU parity is the default for cases with identical defined conversions.
Where existing MFT conversion differs, predeclare a per-channel/per-pattern error
tolerance and clipping/range policy before testing; retain full-image hashes/error
maps and an independent reference. Passing a center pixel is insufficient.

## Ownership, completion and recovery state machine

Proposed states:

`Reserved -> Decoding -> Normalizing -> ProducerCompleted -> Ready -> Retired -> Released`

Failure from an uncertain in-flight state becomes `Quarantined`, retaining its
capacity/bytes until actual completion or device/process teardown is proven.

1. Management/worker admission reserves from existing A/B limits before creating
   native resources. Bound queue leases by the existing eight queued / ten slots
   model, but separately budget MFT internal, staging, imported, old-generation
   and quarantined surfaces. Unknown retained bytes cannot be treated as zero.
2. Hold the decoded sample and exact subresource until normalization/import and
   all dependent work finish. A recyclable MFT texture is not owned by retaining
   only its integer index.
3. Complete D3D work on the producer side, use the documented CUDA map/stream
   ordering, perform conversion/copy, unmap and establish final consumer completion
   before publishing Ready. Retain registration only under an audited bounded
   lifetime; registration is not repeated speculatively on every Program tick.
4. CUDA mapping excludes simultaneous graphics access. NVIDIA documents graphics
   work before map as ordered before subsequent work in the given CUDA stream,
   and CUDA work before unmap as ordered before later graphics work. These rules
   do not make an arbitrary cross-device fence or CPU publication safe. See
   [CUDA graphics ordering](https://docs.nvidia.com/cuda/cuda-driver-api/group__CUDA__GRAPHICS.html).
5. No pending-fence state crosses the current
   ProducerCompletedBeforePublication contract. The worker may use a bounded
   completion probe; Program must never wait on native decode, a driver fence,
   SSD read, MFT flush or decoder join. A worker holding the current backend lock
   or default-stream/context barrier can still stall Program; measure and reject
   that regression rather than calling it asynchronous.
6. Before admission recheck source/asset, PreparedExecution/revision, decode
   generation and GPU epoch. Seek/stop/replace invalidates unpublished work and
   NEXT READY evidence. Old-generation leases drain; never recycle a live texture.
7. Consumer release is idempotent. Sample, map, registration and texture cleanup
   follow dependency completion in reverse acquisition order. A timeout does not
   release a reservation or justify reusing a handle.
8. Device loss/reinitialization changes the GPU epoch and invalidates all imported
   evidence. Preserve committed routing and use existing visible failure/recovery.
   A replacement CPU decoder requires a new generation/preroll and renewed
   preparation, not a silent memory-domain swap or automatic replay of GO.
9. Monitoring disconnect/loss cannot own Program or its sole input lease. Host/
   process loss uses existing supervision and teardown. Do not persist native
   handles or readiness proof in saved production.

## Capability and downgrade contract

Probe outside Program, before choosing the decoder execution:

- exact container/codec/profile/depth/chroma/scan/audio admission already supported;
- selected MFT identity/version/hash and actual D3D11-aware decode result;
- expected adapter/device affinity, format conversion support and import result;
- typed geometry/color/plane extents, completed test sample and resource budget;
- known driver/OS tuple and required runtime libraries.

A probe alone is not a performance or physical qualification. Unsupported hardware,
foreign adapter, missing MFT, unsupported layout, import failure, unavailable
completion primitive or exhausted preparation budget selects the existing CPU
provider before a candidate is published. If the ordinary input itself is
unsupported/corrupt, preserve its existing rejection. Record capability absence
separately from media corruption.

After publication, an error invalidates the candidate and follows established
failure/recovery. Never silently consume a stale GPU frame, guess a timestamp,
switch decoder under the same generation or change committed routing.

## Measurement procedure and evidence contract

Use one Windows x64 reference host and immutable assets. Bind every artifact to
full source/build SHAs, SDK 10.0.401, clean tree, UTC interval, asset SHA-256 and
markers, exact rational format, audio/codec profile, MFT DLL identity/hash, Windows
build, GPU/adapter LUID/driver, SSD/device/volume/cache state, output provider/driver,
monitoring/recording/replay configuration and workload seed. Identify instruments,
sampling/window semantics and dropped/overwritten observations.

Existing prerequisite commands, on the reference host:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
./build/qualification/Invoke-CudaReferenceQualification.ps1 -ExpectedDeviceName "<reference GPU>" -DeviceOrdinal 0 -SampleIterations 30
./build/qualification/Invoke-RendererReferenceQualification.ps1 -Profile stress -ExpectedDeviceName "<reference GPU>" -DeviceOrdinal 0 -SourceCommit "<exact source SHA>" -NoBuild
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 10m -Format "1080p50" -MediaA "<fixed file A>" -MediaB "<fixed file B>" -NoBuild
./build/qualification/Invoke-SsdPlaybackQualification.ps1 -Profile 10m -Format "1080p59.94" -MediaA "<fixed file A>" -MediaB "<fixed file B>" -NoBuild
```

These commands exist but do not implement the proposed GPU decoder or complete
the combined experiment. Renderer uses its existing workloads; SSD runner observes
real A/B files. The existing single-file long fixture targets its current reference
profile. A combined one/two-deck, both-format hardware experiment with injected CUDA
timing and per-source copy attribution is still missing. Do not present separate
successful runners as that missing comparison or dispatch an unidentified runner.

Before implementation approval, capture the CPU path on that fixed host with
approved external ETW/GPU/DRAM instruments plus existing counters. Missing per-copy
spans, full-window boundary distributions and selected-MFT identity need bounded,
qualification-only capture support. No profiler, file writing or unbounded samples
may be introduced into Program.

After approval, perform paired baseline/candidate repetitions in alternating order,
fixed warmup, identical asset/cache/workload configuration and predefined confidence/
non-regression criteria. Exercise one and two decks, both rational output rates,
CUT/DISSOLVE, monitoring, recording/replay pressure, IN/OUT, repeated prepare/seek/
stop/replace and device loss. Retain:

- CPU wall per native read, RGB conversion/copy, worker copy, admission, upload,
  composite, synchronization, readback and complete Program boundary;
- successful HtoD/DtoH/D2D payload deltas and independent physical bus/DRAM traffic;
- device event durations separately from CPU waits, queue depth/starvation,
  generation/epoch, live/retired/quarantined leases and completion failures;
- Program and per-source throughput, sequence continuity/missed deadlines, full
  window P50/P95/P99/max with method/count/loss, actual output/A-V traces;
- steady-state and post-fault CPU/VRAM memory plateau, driver/internal surfaces,
  allocator alignment/peak retained bytes and cleanup baseline;
- golden whole-frame/color/chroma/orientation comparison, audio continuity and
  independent known-reference validation.

Existing CUDA P95 <= 5 ms / max <= 10 ms limits remain unchanged; passing that
microbenchmark does not pass complete Program latency. Define the required measurable
win and non-inferiority bounds with the Product Owner before the experiment. Retain
the complexity only with a repeatable full-boundary/CPU-bandwidth benefit and no
visual, audio, timing, ownership or recovery regression. Otherwise remove/disable
the candidate and keep the existing implementation. No before/after numbers exist
for a GPU decode candidate at this decision.

## Regression coverage and remaining tests

Existing software guards remain relevant and run before broad CI suites:
BackendAgnosticResourceContractTests, BackendNeutralResourceArchitectureTests,
GpuMemoryTransferOptimizationTests, GpuReadbackOwnershipConcurrencyTests,
GpuSchedulingSynchronizationTests, GpuLifecycleRecoveryTests and
GpuBackendResourceSemanticsTests. They prove current ownership/completion/format
boundaries, reusable upload invalidation, serialized stop, readback/lease limits and
software failure isolation; managed/structural backends are not a physical fallback
or GPU-decode integration test.

Existing LocalMediaDecoderSafetyTests and AsynchronousLocalMediaDecodeTests cover
CPU row/stride/orientation, seek/generation, cancellation and quarantine. Existing
prepared handoff tests cover no-reopen and readiness fencing. The full solution,
Provider Smoke and Packaged E2E remain mandatory at the exact final PR head.

After a GPU slice is approved, new device-backed tests must cover planar geometry/
color parity and unknown metadata, same/foreign adapter, unavailable MFT/import,
delayed completion, pool pressure, old leases across seek/replacement, loss during
map/copy/publish, failed unmap/release quarantine, GPU reinit epoch mismatch,
monitoring failure, process loss and explicit CPU reprepare. They are requirements,
not tests claimed as implemented by this design.
