<!--
Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>
All rights reserved.
-->

# Broadcast Test Pattern

## Purpose

rtaime includes an internally generated broadcast reference signal for validating the production video path without relying on a file decoder, filesystem access, capture hardware, or an external media asset.

The reference signal is generated as deterministic RGBA8 content and is assigned to an existing production source slot. Frame timing, source identity, routing, GPU upload, compositing, monitoring, recording and output continue through the normal production pipeline.

## Reference layout

The generated frame contains:

- a legally independent rtaime color-bar arrangement,
- black, near-black, gray and white reference patches,
- sixteen grayscale steps,
- a full-range luma ramp,
- independent red, green and blue ramps,
- center and geometry references,
- action-safe and title-safe guides,
- the lowercase rtaime product mark,
- the visible label `INTERNAL TEST SIGNAL`,
- active resolution, frame rate, scan mode and pixel format,
- the effective channel bit depth for the supported RGBA8 format,
- color-space evidence.

The current video contract does not carry an authoritative color-space descriptor. The pattern therefore displays `UNVERIFIED` instead of inventing a color-space claim.

## Pipeline integration

The generator renders its static pixel payload once. RuntimeHost keeps one runtime-owned reference buffer and reuses it on every production boundary while the test signal is enabled.

The existing `VirtualSyntheticVideoSource` remains responsible for normal source identity, frame descriptors and deterministic timing. Enabling the test signal changes only the resolved pixel content for the selected source slot. It does not create a parallel renderer or bypass the media pipeline.

The resulting path is:

```text
Virtual source timing and frame descriptor
  -> selected source content resolution
  -> internal broadcast reference buffer
  -> normal timed media pipeline
  -> GPU upload
  -> transition and compositing
  -> Program readback / monitoring / recording / output
```

## Operator workflow

The Media Library **Add** action exposes a **GENERATOR** section with **Test Signal**. Activating it applies the static broadcast reference to the current Source selection. If no Source or Clip is selected, the existing Operator selection is used, followed by Preview and then the first available Source as deterministic fallbacks.

Once active, the Video Inspector exposes a dedicated **TEST SIGNAL** section with four explicit presets:

- **STATIC** uses the retained broadcast reference image and disables generated audio on the same Source.
- **MOTION** adds the frame counter, media-time timecode and motion diagnostics described in [MotionTimingTestSignal.md](MotionTimingTestSignal.md), while generated audio remains disabled.
- **A/V SYNC** combines the motion/timing video signal with the existing generated `Pulse` audio signal on the same Source. The combined diagnostics are described in [AvSyncDiagnostics.md](AvSyncDiagnostics.md). An associated audio input is required; the request is rejected before video activation when none exists.
- **OFF** disables both the internal video signal and generated audio for that Source.

Resolution and frame rate are not independent generator overrides. They follow the active Source format and are shown in the Inspector as Runtime-confirmed state. Timecode and frame counter are shown as active only for motion/timing and A/V-sync operation.

The existing Source Bin **CYCLE TEST SIGNAL** action remains available as the fast `OFF -> STATIC -> MOTION -> OFF` workflow.

While active, the source is projected as `TEST`, `STATIC` or `MOTION`, and `VALID`. The underlying physical or media-source signal state can continue to change internally without causing the active internal reference signal to flap to an external-input failure state.

When disabled, the source immediately returns to its underlying input state and content. This makes repeated activation and mode switching deterministic.

## Supported formats

The current generator follows the formats exposed by the V1 virtual media provider:

- 1920x1080 progressive 50 fps RGBA8,
- 1920x1080 progressive 60000/1001 fps RGBA8.

The generator accepts RGBA8 video formats and derives geometry and timing labels from the active `VideoFormat`. Unsupported pixel formats fail closed.

## Runtime and allocation behavior

The complete reference frame is rendered during generator construction. Production-boundary processing reuses the same pixel payload and runtime buffer; it does not rebuild the pattern or allocate a new full-frame image per frame.

Logging is limited to test-signal state transitions. Normal frame processing does not emit test-pattern-specific per-frame log entries.

## Boundaries

`BroadcastTestPatternGenerator` remains a video-only generator. The Operator A/V Sync preset composes it with the existing generated-audio subsystem rather than adding audio generation to the video generator itself.

The feature does not export a test file, introduce HDR-specific reference fields, provide arbitrary per-generator resolution or frame-rate overrides, or claim a color space that the active media contract cannot prove.
