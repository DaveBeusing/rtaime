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

Select a source in the Source Bin and use **CYCLE TEST SIGNAL**.

The action cycles the selected source through `OFF -> STATIC -> MOTION -> OFF`. Static mode uses the retained broadcast reference image. Motion mode extends the same reference source with frame/time diagnostics described in [MotionTimingTestSignal.md](MotionTimingTestSignal.md).

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

This feature does not generate audio, export a test file, introduce HDR-specific reference fields, or claim a color space that the active media contract cannot prove.

Audio reference generation and explicit audio/video synchronization signals are separate concerns and are not part of this implementation.
