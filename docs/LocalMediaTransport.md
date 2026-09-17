<!--
Copyright (c) 2026 Dave Beusing
David Beusing <david.beusing@gmail.com>
-->

# Frame-Accurate Local Media Transport

## Scope

AP-42 adds deterministic transport semantics to the AP-41 local-file source. The implementation remains below the Operator UI and does not add timeline, cue, IN/OUT, loop, playlist or autoplay behavior.

Change classification: `REALTIME_CRITICAL` because transport commands reposition the decoder used by the RuntimeHost media path. Command evaluation itself is non-real-time.

## Contract model

`rtaime.Media.Contracts` exposes:

- `MediaTransportState`: `Unloaded`, `Loading`, `Ready`, `Playing`, `Paused`, `Ended`, `Error`;
- `MediaTransportCommandKind`: Play, Pause, Stop, Seek, JumpToStart, StepBackward and StepForward;
- `MediaTransportCommand` with frame-based seek targets;
- `MediaTransportPosition` with current frame, total frames, current position, duration, remaining time and exact frame rate;
- `MediaTransportSnapshot` as the observable authoritative transport state;
- `MediaTransportCommandResult` for controlled acceptance/rejection.

The additive contract remains compatible with Media contract version `1.0` because no existing wire semantics are changed.

## State semantics

A local file that has already passed AP-41 probing enters `Ready`; AP-41 performs synchronous open/probe, so `Unloaded` and `Loading` exist in the contract for observation/composition but are not emitted by the current loaded RuntimeHost session.

Allowed V1 transitions are intentionally narrow:

- `Ready` -> `Playing` by Play;
- `Playing` -> `Paused` by Pause;
- `Paused` -> `Playing` by Play;
- Stop from Ready/Playing/Paused/Ended repositions to frame 0 and returns `Ready`;
- Seek is accepted from Ready/Playing/Paused/Ended and preserves active Play/Pause state; seeking from Ended returns `Paused`;
- JumpToStart uses the same seek path and preserves active Play/Pause state; from Ended it returns `Paused`;
- StepBackward/StepForward are accepted only from Ready/Paused/Ended and return `Paused`;
- decoder/runtime failures move the source to `Error`;
- physical end-of-media moves the source to `Ended`.

Illegal transitions fail closed with `media.transport.transition_invalid` and do not mutate the state.

## Frame and seek semantics

Transport positions are zero-based frame numbers. Seek requests outside the clip range are clamped to `[0, TotalFrames - 1]` before they reach the decoder.

The Windows Media Foundation decoder converts a target frame to the exact clip frame-rate time and uses `IMFSourceReader.SetCurrentPosition`. Because compressed-media seeking may land on an earlier key frame, the decoder discards decoded video/audio samples whose presentation timestamp is earlier than the requested target. The first returned video sample therefore represents the requested frame within the decoder timestamp tolerance.

Audio buffer sample position is derived from its post-seek presentation timestamp instead of a decoder-private running counter. This keeps the observable audio position consistent after forward and backward seeks.

## Runtime authority

`LocalMediaRuntimeSession` owns the transport controller for the committed local source. Commands are rejected until a `PreparedExecutionContract` has been committed. `ProcessNextBoundary` emits media only while the transport is `Playing`; while paused/ready/ended it returns `NotPlaying` or `Ended` without inventing UI state.

The Operator UI is not a source of truth. AP-43 must render its playhead from `MediaTransportSnapshot` and submit commands back through the control/runtime path.

## Evidence

Coverage includes:

- Play -> Pause -> Resume -> Stop;
- frame-based seek with range clamping;
- exact +1/-1 frame stepping in the transport model;
- controlled illegal transitions;
- decoder seek failure -> Error;
- real Media Foundation seek against a deterministic multi-frame 1080p50 H.264/AAC fixture;
- A/V timestamp/timebase consistency after seek;
- AP-41 local-file planning/commit/decode path remains covered.

The deterministic multi-frame fixture is stored directly under `tests/TestAssets/media/reference-1080p50-h264-aac-80ms.mp4`. It contains four synthetic 1080p50 video frames plus embedded 48 kHz stereo AAC audio and no third-party footage.

## Handoff to AP-43

AP-43 may rely on:

- stable zero-based frame positions;
- exact frame-rate metadata as the timecode basis;
- current position, duration and remaining time in `MediaTransportSnapshot`;
- frame-based Seek commands;
- confirmed RuntimeHost transport state rather than UI-owned playback state.
