<!--
Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>
All rights reserved.
-->

# Motion and Timing Test Signal

## Purpose

rtaime extends the internal broadcast reference source with a deterministic motion/timing mode for visually diagnosing frame drops, stuttering, repeated frames, timing discontinuities and frozen output without depending on external media.

The mode remains a normal generated source. Source identity, frame descriptors, media scheduling, GPU upload, compositing, monitoring, recording and output continue through the existing production pipeline.

## Authoritative timing

All dynamic content is derived from the `FrameTiming` produced by the existing virtual timing provider.

The generator does not use a WPF timer, wall clock or an independent scheduling loop.

- the frame counter equals `FrameTiming.SequenceNumber`;
- motion phase is calculated from `PresentationTimestamp` and `Timebase`;
- the displayed media-time timecode is calculated from the active rational `FrameRate`;
- switching test modes does not create or reset an independent clock.

## Media-time timecode

The visible timecode uses `HH:MM:SS:FF`.

`HH:MM:SS` represents elapsed media time derived from the exact rational frame rate. `FF` is the zero-based frame ordinal inside that actual media-time second.

This is an internal diagnostic timecode, not an interchange claim for SMPTE drop-frame metadata. The representation intentionally follows rational media time so 60000/1001 operation does not accumulate the drift that a simple nominal 60 fps counter would introduce.

Examples:

- frame 25 at 25 fps -> `00:00:01:00`;
- frame 50 at 50 fps -> `00:00:01:00`;
- frame 60 at 60000/1001 fps -> `00:00:01:00`;
- frame 3597 at 60000/1001 fps -> `00:01:00:00`.

## Motion references

The dynamic region contains:

- the running frame counter;
- the media-time timecode;
- a continuous horizontal marker whose phase repeats every exact media second;
- ten fixed timing ticks across the motion track;
- a sixteen-cell discrete frame indicator whose active cell advances every frame;
- alternating inactive-cell state so repeated or frozen frames remain easy to spot.

The one-second marker phase comes from the media timebase rather than the UI refresh rate.

## Runtime integration

RuntimeHost owns two retained full-frame buffers:

1. the immutable static broadcast reference frame;
2. a motion/timing frame initialized from the same static reference.

`MotionTimingTestSignalGenerator` owns one retained dynamic-region buffer. On each motion boundary only that bounded region is redrawn and copied into the retained motion frame. The complete 1080p reference image is not rebuilt or copied on each frame.

The selected test source is then uploaded and processed through the same GPU and output path as any other RuntimeHost source.

## Operator workflow

The Source Bin action is **CYCLE TEST SIGNAL**.

For the selected source the sequence is:

```text
OFF -> STATIC -> MOTION -> OFF
```

ControlHost remains the authority boundary for the operator command. RuntimeHost owns execution and the generated frame content.

The Source Bin projects active generated sources as:

- `TEST / STATIC` for the static broadcast reference;
- `TEST / MOTION` for the motion/timing diagnostic mode.

## Lifecycle semantics

Generated test signals do not own an independent media transport.

The frame counter and motion state follow RuntimeHost's authoritative production sequence. Therefore:

- changing STATIC to MOTION does not reset media time;
- disabling and re-enabling MOTION does not rewind the RuntimeHost sequence;
- a RuntimeHost lifecycle restart naturally starts with the new runtime sequence;
- underlying live/media input health continues to be retained while the internal test source is active.

This avoids hidden clock discontinuities when diagnosing timing behavior.

## Frame-rate behavior

The timing generator uses rational `FrameRate` and `Timebase` values.

Generator regression coverage includes:

- 25 fps;
- 50 fps;
- 60000/1001 fps (59.94).

The current V1 virtual runtime source formats remain 1080p50 and 1080p59.94. The generator itself is not tied to nominal-integer frame rates and can be used with additional RGBA8 formats when those formats become supported by the normal source pipeline.

## Allocation and latency behavior

Construction performs the required retained allocations.

During frame rendering:

- no full-frame image is allocated;
- the dynamic region storage is reused;
- numeric rendering uses stack-backed formatting;
- only the bounded motion region is copied into the retained motion frame;
- no per-frame test-signal logging is emitted.

Performance remains observable through the existing RuntimeHost performance and dropped-frame telemetry rather than a second diagnostics clock.

## Boundaries

This mode does not:

- generate audio;
- measure A/V synchronization;
- replace the central media clock;
- export a test file;
- introduce a separate renderer or scheduler;
- use wall-clock time as the frame source of truth.

Audio reference generation and explicit A/V synchronization diagnostics remain separate features.
