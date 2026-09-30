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

The viewer-local model defines Combined, Red, Green, Blue, Alpha and Luma inspection modes. The existing RGB command remains the Combined-view command so the UI does not introduce a second inspection-state model.

When a presentable shared GPU resource is available, full-frame channel isolation is performed in the Operator D3D11 presentation shader. Red, Green and Blue isolate the selected display channel, Alpha presents premultiplied white through the existing checkerboard so transparency remains directly readable, and Luma uses the same Rec.709 display-code weighting as the 1×1 inspector (0.2126 / 0.7152 / 0.0722). Combined retains the normal alpha-checkerboard presentation semantics.

The channel view is derived presentation state only. It does not modify the shared monitoring texture, Runtime composition, Program output, recording or routing. If the qualified GPU path is unavailable, non-Combined channel modes report an explicit unavailable state; the implementation does not silently synthesize a full-frame CPU channel image.

## Region-of-Interest analysis

ROI selection is stored in source-pixel coordinates and is clamped to the current source dimensions. The overlay projects that source-space rectangle through the same presentation geometry used by Fit, Fill, Pixel Perfect, free zoom, pan and DPI changes, so resizing the Operator view never rewrites the authored inspection rectangle.

ROI statistics execute against the read-only shared GPU resource. A compute shader produces sample count, mean R/G/B/A/luma, and per-channel/luma minimum and maximum. Analysis is independently rate-limited to 200 ms and bounded to at most 262,144 samples; large ROIs use a deterministic sampling stride rather than increasing analysis cost without limit.

Only a 16-element uint result buffer is copied to CPU-visible staging memory. No ROI pixel payload and no full-resolution frame is transferred to the Operator model. Statistics recompute only when newer frame evidence, ROI state or the inspection mode changes. GPU/resource loss reports an explicit unavailable reason and may recover on a later valid resource.

ROI interaction is presentation-only. Operators can enable ROI selection, drag a rectangle, move an existing ROI, resize it from the four visible corner handles, and clear it; the overlay and handles never appear in Clean Program.

## Shared GPU presentation boundary

Monitoring contract version 1.3 can carry a read-only shared GPU resource with Windows graphics presentation metadata alongside the bounded CPU fallback. `GpuMonitorPresentationSurface` consumes that resource for Preview and Program presentation when adapter/resource validation succeeds.

The presentation surface does not expose CUDA pointers, mutate the shared resource, create a second decoder or become Program authority. The existing CPU payload remains the source for the legacy 1×1 pixel inspector, CPU scopes and Difference analysis; it is not used to build the visible full-resolution GPU channel image or ROI statistics.

Clean Program reuses the same Program GPU resource but intentionally excludes pixel inspection and diagnostic overlays.

## Qualification boundary

Automated tests cover physical coordinate mapping, Fit/Fill/Pixel Perfect geometry, DPI conversion, full-resolution-to-bounded-sample mapping, source-space ROI clamp/mapping, channel shader semantics, bounded ROI analysis policy and the absence of CPU bitmap materialization in the GPU presentation control.

The existing 1×1 inspector remains the bounded CPU monitoring-sample path and therefore retains its documented resolution limitation. ROI evidence is GPU-derived and returns only reduced numeric results. Physical NVIDIA/CUDA/D3D11 validation remains reference-hardware evidence rather than a claim inferred from software-only tests.
