<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# GPU-resident local media: transfer audit and adoption decision

Date: 2026-10-18  
Inspected master: `8fbfd3623c65e3c29c72d4d991133e8c073da987`  
Decision: **NO-GO for a production GPU decode path; retain existing CPU/RGBA admission**  
Classification: source inventory and proposed architecture; no hardware or scope approval

## Decision and evidence boundary

A GPU-surface decoder is not implemented or advertised by this evaluation. Existing
Media Foundation decoding, A/B ownership, resident next preparation, one Program
renderer and host readback remain in place. No production dependency, public
contract, codec/output admission, saved-state schema or product target (0.5.39)
changes.

The repository has approved restricted MOV/H.264 AAC/PCM and two A/B file decks.
Those approvals do not approve GPU decode, a new provider, P010/HDR or four decks.
The [professional decoder ADR](ProfessionalDecoderDecision.md) is explicitly
PROPOSED. No accepted GPU-media adoption/performance approval was found in the
inspected source/docs and recent PR inventory. The general development architecture
documents refer to an external V1 Product Definition; a standalone definition or
additional accepted GPU-decode ADR is not present in this repository tree. This
evaluation does not invent approval on their behalf.

A measured reference-machine win is also absent. Existing qualification documents
retain physical CUDA/SSD/A-V as UNVERIFIED. The latest 100 Actions runs inspected
during this audit contained no CUDA or physical timing-reference run. That is a
bounded inventory observation, not proof that no equipment or older evidence exists.
No identified reference GPU/driver/MFT/SSD tuple or runner-local trace was supplied
for this evaluation. Hosted software results cannot substitute for it.

The safe completion is this source-derived audit, a proposed surface/fence decision,
a reproducible measurement protocol and early execution of existing compatibility,
ownership and fallback regressions. Implementation requires the adoption record and
missing measurements described below.

## Current source path and ownership

| Boundary | Verified current behavior | Measurement / limitation |
| --- | --- | --- |
| Media Foundation output | WindowsMediaFoundationLocalMediaDecoder configures RGB32 at the normalized output size/rate; native read is outside Program | Native ReadSample CPU-wall histograms; selected MFT binary and acceleration are not identified |
| RGB32 to RGBA | ConvertToContiguousBuffer; IMF2DBuffer.Lock2D or contiguous lock; visible rows copied into reusable RGBA scratch; R/B swizzle and opaque alpha | Copy/conversion CPU time is not separated from aggregate decode; contiguous conversion may itself copy |
| Worker ownership | LocalMediaDecodeWorker copies scratch video/audio into LocalMediaFrameSlot; queue capacity 8, pool 10; leases retain slots and fence source generation | Queue/peak/discard/starvation/state and read/decode histograms exist; these are host-memory frames |
| Runtime admission | RuntimeHostProcess admits a decoded lease through V1RuntimeHostService.SetExternalInputContent; RgbaFrameBuffer.CopyPixelsFrom copies into the existing source buffer | InputAdmission timing includes admission work, not independent memory-bandwidth attribution |
| GPU materialization | GpuProcessingProvider materializes required RGBA inputs; CUDA Allocate uses cuMemcpyHtoD_v2 | Provider counters distinguish logical copies from device-domain payload bytes; not a PCIe bus trace |
| Program composite | Existing CUDA RGBA kernel; synchronous cuCtxSynchronize under the backend gate; CUT can alias an input | Optional CUDA-event kernel duration plus CPU launch/sync and lock timing; events are not enabled in normal Runtime construction |
| Monitoring export | CudaD3D11MonitoringInterop copies CUDA memory to a shareable D3D11 texture, unmaps, synchronizes, unregisters and flushes before publication | Device copy and interop are not a GPU media-input path or zero-copy proof |
| Program output | One bounded Program readback; host leases shared with output/recording/replay/CPU monitoring as applicable | cuMemcpyDtoH_v2 remains required by current consumers; GPU input would not eliminate it |

Source anchors:
[decoder](../src/Media/rtaime.Media/WindowsMediaFoundationLocalMediaDecoder.cs),
[decode worker](../src/Hosts/rtaime.RuntimeHost/LocalMediaDecodeWorker.cs),
[Runtime](../src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs),
[CUDA backend](../src/Providers/rtaime.Provider.Gpu/CudaGpuProcessingBackend.cs),
[transfer counters](../src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs).

There are at least three explicit full-frame host copy boundaries for an admitted
decoded frame: native buffer to decoder scratch, scratch to worker slot, and slot
to Runtime source buffer. They are not three fresh allocations per frame. Swizzling,
native normalization, padding, audio, cache traffic and retained output consumers
add costs that this count does not measure. A decoded frame may be dropped, repeated
or resampled before GPU materialization, so decoded-frame count is not upload count.

No IMF DXGI device manager / DXGI buffer admission exists in this decoder. The
current opaque handle is a descriptor, not a safe imported-resource implementation.

## Payload arithmetic, not measured bandwidth

For 1920 x 1080 visible pixels with no pitch/alignment overhead:

| Layout, evaluation only except existing RGBA8 | Bytes / frame | MB/s at 50/1 | MB/s at 60000/1001 |
| --- | ---: | ---: | ---: |
| RGBA8 | 8,294,400 | 414.720 | 497.167 |
| NV12 8-bit 4:2:0 | 3,110,400 | 155.520 | 186.438 |
| P010 10-bit stored in 16-bit planes | 6,220,800 | 311.040 | 372.875 |

MB is decimal. These are one-transfer payload rates; a host memcpy also reads and
writes memory. Two GPU-materialized RGBA inputs per Program slot would request
829.440 MB/s HtoD at 50 fps, or 994.334 MB/s at 60000/1001. Whether both inputs are
actually materialized must be established by counter deltas and committed routing.
One Program DtoH adds the single-RGBA row above independently of decoder choice.
Do not call these totals actual PCIe, DRAM bandwidth, utilization or measured savings.

The existing worker alone reserves ten RGBA slots (82,944,000 video bytes) plus one
decoder scratch frame (8,294,400 bytes) per opened deck: 91,238,400 video bytes,
182,476,800 for two decks. This excludes audio, pending packets, Runtime source
buffers, GPU allocations, MFT internals, readback, replay and recorder retention.
A future surface pool must budget all of those retained domains; replacing these
host bytes with texture payload arithmetic does not establish peak VRAM.

## Existing evidence and missing attribution

| Required observation | Existing implementation | What still prevents a GPU-media comparison |
| --- | --- | --- |
| Native read / decode / seek CPU wall | LocalMediaPlaybackMeasurements whole-run fixed histograms | ReadSample includes codec/driver/blocking; not file-only SSD latency; RGB copy/swizzle and audio conversion lack separate spans |
| Host copies / bandwidth | Source copy map and process allocation/resource observations | No per-copy CPU-wall/byte series or physical DRAM bandwidth measurement |
| HtoD / DtoH | GpuMemoryTransferStatistics device-domain counters; managed backend reports zero device bytes | Successful API payload counts are not physical bus traffic; existing file qualification outcomes do not bind per-source transfer deltas |
| Kernel / synchronization | CudaGpuTimingCollector CPU wall, GPU events, lock wait/hold | Optional collector not injected by RuntimeHostProcess; compositor microbench is not simultaneous A/B file playback |
| Full Program P95/P99 | RuntimeTimingQualificationProbe and renderer qualification | Retained rings overwrite; specify the actual measurement window and loss. Existing file timing summaries are not whole-run distributions |
| Queues / leases / memory | Deck state/generation/queue, shared/readback statistics, process memory and handles | Missing correlated MFT-surface/internal allocation counts, source/backend epoch and sustained VRAM attribution |
| Hardware identity | SSD inventory and renderer manifests provide partial host/device metadata | Selected decoder binary/version/hash, actual hardware acceleration, same-adapter interop and exact physical output traces remain unqualified |

CPU synchronization wait and GPU-event duration overlap and must not be summed.
Bounded retained timing percentiles must not be relabeled whole-run percentiles.
Polling at 250 ms does not measure every Program boundary. Unknown telemetry stays
UNVERIFIED, never zero.

Historical software context, **not evidence for the inspected master or a GPU path**:
[reference run 39074614181](https://github.com/DaveBeusing/rtaime/actions/runs/39074614181)
at `43236aee75164020257d34b6c2027be93af2ac0a` collected one ten-minute clip
with 30,000 frames, 28,800,000 audio samples, no missing audio frames, timestamp
discontinuities or read failures. Its two ten-minute A/B collections and cleanup
passed, but Program timing failed: 715 missed slots at 50 fps and 2,476 at 59.94.
These results neither locate the bottleneck in transfer/decode nor demonstrate that
GPU decoding improves it. The prior head's five green gates do not qualify a later
merge SHA. Current PR-head checks are reported separately in the PR evidence.

## Adoption gate and next dependency

Before implementing a GPU path, retain:

1. Product Owner approval of one exact input/profile/color/audio/platform slice,
   existing A/B scale, fallback policy and quantitative performance acceptance.
2. Architecture acceptance of private surface ownership, completion, retained-byte
   budgets, source/backend epochs, native dependency decisions and recovery.
3. Exact-hardware baseline showing that copies/transfer materially contribute to
   full Program P95/P99, with codec/MFT/driver identity and trustworthy telemetry.
4. A paired experiment on that same tuple demonstrating a full-boundary win,
   correct visuals/audio, bounded resource plateau and no continuity/recovery
   regression. No unqualified path is enabled by a successful capability probe.

No implementation dependency is selected by this audit. NVIDIA video SDK/NVDEC,
new FFmpeg builds, additional outputs, P010/HDR and runtime shader compilation
remain outside approved scope. Existing CPU fallback and saved production remain
the supported behavior until those gates close.

Implementation alternatives, typed surface/fence semantics, fallback and exact-host
benchmark procedure are specified as a proposal in
[GPU media surface design](GpuMediaSurfaceDesign.md).
