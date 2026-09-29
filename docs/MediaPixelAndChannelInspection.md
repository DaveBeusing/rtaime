# Media Pixel and Channel Inspection

The Operator monitor supports non-destructive per-pixel inspection on the existing monitoring presentation. Inspection state is viewer-local and does not mutate Runtime, Program output, recording, source media or authoritative GPU state.

## Coordinate semantics

Pointer positions are converted from WPF device-independent coordinates to physical pixels using the active DPI scale, then mapped through the same `MediaPresentationRect` used for Fit, Fill, pixel-perfect, free zoom and pan. The mapped coordinate is floored to the containing source pixel. Letterbox/pillarbox areas and coordinates outside cropped presentation bounds report `OUTSIDE IMAGE`.

## Sample semantics

The current WPF monitoring fallback surface is BGRA32 after the monitoring display transform. Pixel inspection therefore reports **DISPLAY CODE** values, not fabricated source-code values. The compact readout exposes source X/Y plus R, G, B, A and Rec.709/sRGB luma coefficients (Y' = 0.2126 R + 0.7152 G + 0.0722 B).

Sampling reads only a 1x1 rectangle from the already-present monitoring bitmap. The four-byte sampling buffer is thread-local and reused. Pointer sampling is bounded to approximately 30 Hz. Unsupported bitmap formats fail explicitly instead of triggering a full-frame conversion.

## Alpha and grid

The media presentation has a checkerboard beneath the image so meaningful transparency remains visible. The source-aligned pixel grid is rendered only when the effective presentation scale reaches 8 physical display pixels per source pixel. Grid drawing is clipped to the visible source extent and does not alter the media bitmap.

## Channel modes

The viewer-local model defines RGB, Red, Green, Blue, Alpha and Luma inspection modes. The current implementation exposes exact component values in the inspector. Full-frame channel-isolation rendering is intentionally not performed through a CPU bitmap conversion; it requires the GPU presentation/sampling path before it can be enabled without violating the low-copy monitoring requirement.

## Shared GPU monitoring boundary

The monitoring contract can now carry a provider-neutral read-only shared GPU resource descriptor alongside, or for an explicitly eligible subscriber instead of, the bounded CPU Program payload. This foundation preserves source format, color semantics, frame timing, generation and resource identity without exposing CUDA pointers or vendor-specific handle types.

Pixel inspection, channel-isolation rendering and technical scopes do not consume that resource yet. Until the Operator provider-backed/Direct3D presentation adapter is implemented and qualified, those tools continue to operate on the existing CPU/WPF fallback payload. The presence of a shared-resource descriptor must therefore not be presented as GPU pixel-sampling or GPU scope qualification.

A later GPU inspection path must reuse the shared monitoring resource model rather than introduce another decoder, Program renderer or full-resolution Program readback.
