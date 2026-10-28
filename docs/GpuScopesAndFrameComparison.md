# GPU Scopes and Frame Comparison

## Purpose

Operator technical scopes and A/B comparison are derived monitoring features. They consume the existing read-only shared monitoring resources and never become Runtime, Program, routing, recording or timing authority.

The GPU path is preferred when the current monitoring observation exposes a compatible Windows graphics shared resource. The existing 320×180 CPU monitoring payload remains the deterministic fallback.

## Scope analysis boundary

GpuScopeAnalysisSurface opens the existing Program shared resource read-only on the Operator D3D11 device and executes one bounded compute pass. It does not create another decoder, compositor, Program renderer or Runtime readback.

The public Operator result remains MediaScopeSnapshot. The fixed result layout is:

- luma histogram: 256 bins;
- red, green and blue histograms: 3 × 256 bins;
- luma waveform: 64 × 64 bins;
- RGB parade: 3 × 64 × 64 bins;
- vectorscope: 64 × 64 bins.

The original four display-code scopes still occupy **21,504 unsigned 32-bit values (86,016 bytes)** at their unchanged offsets. A further **15 raw-code uint counters (60 bytes)** are appended for EBU R 103: R′, G′, B′, derived Y′ and unique-pixel union, each tested against nominal/preferred/total bands. The combined staging transfer remains fixed at **21,519 uint values (86,076 bytes)**. Existing legacy 21,504-entry result decoding is supported with R 103 explicitly UNAVAILABLE. No source pixels, ROI pixels or full frame are copied.

## Sampling and scheduling

Scope analysis is opt-in and runs only while scopes are enabled and their Operator surface is active.

The default analysis interval is 200 ms, so scope analysis is capped at 5 Hz independently from monitoring cadence. The minimum source sampling stride is 2. Full-resolution inputs increase the stride deterministically as required to keep the analysis at or below 262,144 samples.

There is no queued per-frame worker. A newer ProgramGpuFrame replaces older pending evidence. If a 200 ms window is still active, one timer invalidation later analyzes the latest frame; superseded frames are not queued for later processing.

The snapshot records processing path (GpuSharedResource or CpuFallback), source dimensions, sample stride and sample count, measured analysis/result-transfer duration, result-transfer bytes, managed allocation bytes for result-model materialization, vectorscope availability and processing detail.

The measured GPU duration is Operator-side wall-clock evidence around dispatch, fixed-result copy and map. It is diagnostic evidence, not a hardware-performance guarantee.

## Color semantics

Histogram, waveform, RGB parade and vectorscope retain MonitoringDisplayTransform display-code semantics. The additional EBU R 103 counters explicitly **do not**: they analyze raw RGBA8 source codes before range expansion/transfer conversion, with authoritative Rec.709 limited-range color and native 8-bit precision required. A declared 10-bit source through RGBA8, unknown/inferred/full-range metadata or unsupported HDR transfer is UNVERIFIED/UNAVAILABLE. Native Cb/Cr samples and the EBU-recommended filtered full-active-image measurement are not claimed.

Histogram, waveform and RGB parade can preserve passthrough values when color metadata is incomplete, matching monitor presentation behavior. Vectorscope requires complete color semantics. If color metadata is incomplete, vectorscope bins remain empty and the snapshot explicitly reports vectorscope unavailable rather than guessing chroma semantics.

The GPU shader uses the same existing integer monitoring formulas after display-code conversion:

- luma: (77R + 150G + 29B + 128) >> 8;
- Cb-like axis: (-43R - 85G + 128B + 128) >> 8;
- Cr-like axis: (128R - 107G - 21B + 128) >> 8.

## A/B comparison

A is the actual Program monitoring resource. B is the source resource matching confirmed Preview routing.

GPU comparison requires a Program shared resource and confirmed Preview source shared resource, read-only access, presentable interop, matching full-resolution dimensions, matching pixel formats, and complete and matching color semantics. Unknown color metadata is not treated as compatible.

GpuMediaCompareSurface implements Split Vertical, Split Horizontal, Wipe Vertical, Wipe Horizontal and Difference as presentation shader operations over the two shared textures. Split and wipe do not generate derived frame storage. Difference converts both samples to the same qualified display-code space and displays abs(A - B) directly in the shader.

No GPU Difference bitmap is copied to the CPU.

## CPU fallback

The existing MediaScopeSnapshot.Analyze path remains available against the bounded CPU monitoring payload. It is used only when no shared Program resource is available or the GPU scope path explicitly reports unavailable.

The existing RtaimeMediaCompareView remains below the GPU compare surface. It becomes visible when GPU comparison is unavailable. CPU Difference uses the same compatibility checks and display-code conversion before creating the bounded fallback bitmap.

GPU and CPU analysis are not intentionally run in parallel for the same healthy path.

## Recovery

Frame replacement closes and reopens shared resources by resource identity. Device/adapter mismatch, unsupported compute capability, shared-handle failure or compute failure marks GPU scope processing unavailable and makes CPU fallback eligible.

GPU compare open/draw failure exposes the CPU comparison surface again. A later resource replacement resets the degraded state so the GPU path can retry.

Monitoring failure never blocks Program continuity.

## Clean Program

Clean Program reuses the existing Program monitoring presentation only. It does not contain scope analysis, A/B comparison, Difference, scope result buffers or comparison overlays.

## Qualification

Automated qualification covers fixed GPU result shape and transfer size, exact sample bounds including non-standard resolutions, bounded managed-result allocation evidence, CPU/GPU result-shape parity for deterministic reference bins, display-code color-transform parity, vectorscope unavailable behavior for incomplete color metadata, Difference compatibility and unknown-color rejection, GPU split/wipe/Difference shader structure, absence of full-frame CPU materialization in GPU scopes and comparison, superseded-frame/no-worker-queue behavior, deterministic resource cleanup, explicit CPU fallback scheduling, and Clean Program exclusion.

Physical GPU duration, driver-specific execution timing and final visual equivalence remain reference-hardware qualification evidence.

## Color Review source selection

The COMPOSITING workspace offers a focused Color Review submode without adding a new workspace ID or an additional monitoring transport. Its scope selector admits only the current **Program A** or **confirmed Preview B** monitoring source. The shared `OperatorMonitoringViewModel` owns active source selection and separate bounded derived snapshots; switching sources clears previous scope data before any new sample can appear. The GPU surface routes a result only when source role, current frame sequence, dimensions and color description still match. Unmatched observations are discarded rather than repainted as current.

The selected GPU path reuses the original 200 ms / 5 Hz latest-only compute budget and fixed **86,076-byte combined derived-result transfer**. The existing sampled CPU fallback uses that same cadence only for the selected currently confirmed stream when the GPU resource is absent or explicitly unavailable. No new decoder, per-frame analysis queue, full-frame Program readback or source/Program authority is introduced. The Color Review panel labels the **sampled frame sequence**, not the latest Program execution frame. A stale/missing source or incomplete color description never grants a vectorscope or HDR accuracy claim. Closing Color Review restores Program scope selection and the prior scope-enable state; compare modes remain display-only.

The implementation is subject to exact-PR-head CI/Quality and real Windows UI validation. GPU adapter/DPI/display-specific visual equivalence and physical color calibration remain **UNVERIFIED** until qualified on reference hardware.
