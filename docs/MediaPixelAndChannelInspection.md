# Media Pixel and Channel Inspection

The Operator monitor supports non-destructive per-pixel inspection on the existing monitoring presentation. Inspection state is viewer-local and does not mutate Runtime, Program output, recording, source media or authoritative GPU state.

## Coordinate semantics

Pointer positions are converted from WPF device-independent coordinates to physical pixels using the active DPI scale, then mapped through the same `MediaPresentationRect` used for Fit, Fill, Pixel Perfect, free zoom and pan. Letterbox/pillarbox areas and coordinates outside cropped presentation bounds report `OUTSIDE IMAGE`.

When a presentable shared GPU resource exists, the presentation geometry uses that resource's full video dimensions. The inspector therefore keeps the full-resolution GPU source coordinate even though its current value sampler remains bounded to the CPU monitoring payload.

## Sample semantics

The existing CPU fallback surface is BGRA32 after `MonitoringDisplayTransform`. Pixel inspection reports **DISPLAY CODE** evidence, not fabricated production source-code values.

Sampling still reads only a 1×1 rectangle from the already-present CPU monitoring bitmap. No full-resolution GPU readback, staging texture or ROI transfer is introduced. When the visible GPU resource is higher resolution than the 320×180 fallback, the full-resolution source coordinate is deterministically mapped to the corresponding bounded monitoring sample. The readout keeps the full-resolution X/Y coordinate while the value detail identifies the bounded monitoring sample.

The four-byte sampling buffer is thread-local and reused. Pointer sampling is bounded to approximately 30 Hz. Unsupported bitmap formats fail explicitly instead of triggering a full-frame conversion.

## Alpha and grid

The shared monitor keeps its checkerboard beneath the media presentation so meaningful transparency remains visible. The source-aligned pixel grid is rendered only when the effective presentation scale reaches 8 physical display pixels per full-resolution source pixel. Grid drawing is clipped to the visible source extent and does not alter either the GPU resource or CPU bitmap.

## Channel modes

The viewer-local model defines RGB, Red, Green, Blue, Alpha and Luma inspection modes. The compact inspector exposes component values from the bounded display-code sample.

Full-frame channel-isolation rendering, GPU scopes and full-resolution ROI sampling are intentionally not implemented by the GPU-resident presentation path. They require separate qualified GPU processing capabilities and must not be approximated through a new CPU full-frame conversion.

## Shared GPU presentation boundary

Monitoring contract version 1.3 can carry a read-only shared GPU resource with Windows graphics presentation metadata alongside the bounded CPU fallback. `GpuMonitorPresentationSurface` consumes that resource for Preview and Program presentation when adapter/resource validation succeeds.

The presentation surface does not expose CUDA pointers, mutate the shared resource, create a second decoder or become Program authority. The existing CPU payload remains the source for pixel values, scopes and Difference analysis; it is not used to build the visible full-resolution GPU image.

Clean Program reuses the same Program GPU resource but intentionally excludes pixel inspection and diagnostic overlays.

## Qualification boundary

Automated tests cover physical coordinate mapping, Fit/Fill/Pixel Perfect geometry, DPI conversion, full-resolution-to-bounded-sample mapping and the absence of CPU bitmap materialization in the GPU presentation control.

Exact full-resolution pixel-value inspection remains unqualified because the current package intentionally does not add GPU ROI readback. Physical NVIDIA/CUDA/D3D11 validation remains reference-hardware evidence rather than a claim inferred from software-only tests.
