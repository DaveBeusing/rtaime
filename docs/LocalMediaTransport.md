<!--
Copyright (c) 2026 Dave Beusing
David Beusing <david.beusing@gmail.com>
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Frame-Accurate Local Media Transport

## Scope

Local Media Transport adds deterministic transport semantics to the Local Media File Source local-file source. The implementation remains below the Operator UI and does not add timeline, cue, IN/OUT, loop, playlist or autoplay behavior.

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

A probed local file enters `Ready` only after worker preroll qualifies complete video/audio windows. Open/probe runs outside Program admission. The Runtime deck emits `Loading` while preparing or rebuffering; a loaded asset keeps its transport intent and marker identity during that state. Transport commands acknowledge intent/position, while the deck snapshot reports actual readiness.

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

The Operator UI is not a source of truth. Media Timeline Seeker must render its playhead from `MediaTransportSnapshot` and submit commands back through the control/runtime path.

## Evidence

Coverage includes:

- Play -> Pause -> Resume -> Stop;
- frame-based seek with range clamping;
- exact +1/-1 frame stepping in the transport model;
- controlled illegal transitions;
- decoder seek failure -> Error;
- real Media Foundation seek against a deterministic multi-frame 1080p50 H.264/AAC fixture;
- A/V timestamp/timebase consistency after seek;
- Local Media File Source local-file planning/commit/decode path remains covered.

The deterministic multi-frame fixture is stored directly under `tests/TestAssets/media/reference-1080p50-h264-aac-80ms.mp4`. It contains four synthetic 1080p50 video frames plus embedded 48 kHz stereo AAC audio and no third-party footage.

## Handoff to Media Timeline Seeker

Media Timeline Seeker may rely on:

- stable zero-based frame positions;
- exact frame-rate metadata as the timecode basis;
- current position, duration and remaining time in `MediaTransportSnapshot`;
- frame-based Seek commands;
- confirmed RuntimeHost transport state rather than UI-owned playback state.

## Preroll and transport acknowledgement

Runtime now decodes on one bounded worker per session. Open reports Ready only after
complete video/audio preroll and prepared execution admission. Seek/Stop/step/Loop
invalidate the previous generation immediately and acknowledge a requested position;
the existing Loading deck state lasts until new-generation preroll is qualified.
Transport playback intent and marker identity remain intact during Loading. Pause
retains bounded preread. An underrun freezes media time, exposes Loading and uses the
existing Lost-input fallback with unavailable audio while Program cadence continues.
An early queued PTS holds video without replaying audio. See
[ownership and admission policy](LocalMediaFileSource.md#asynchronous-decode-ownership-and-preroll).

Control/Client/Operator continue to send intent through the existing contracts.
There is no second playback timer, production state, bulk-media IPC or format expansion.

## Failed decoder cleanup

Read/seek progress failures carry the stable local-media failure code into Error;
terminal source errors require reopening. A failed worker join/disposal is retained
by the Runtime deck as runtime.media_deck.cleanup_failed. It cannot become an
Unloaded success or admit a replacement reader. Use the supported host lifecycle
restart after reviewing diagnostics. Close, open and probe are serialized on the
lifecycle path; Program admission retains its independent bounded lock and never
waits for their native operations.

## Container-aware metadata

Transport consumes the existing normalized probe after independent MP4 or
QuickTime admission. Native sample times and single qualified timeline edits
are interpreted by the Windows demux; the Runtime still uses decoded PTS and its
existing rational Program cadence. Unsupported edit lists fail before decoder
creation. Seek/preroll and generation fencing are shared across admitted formats;
no alternate timeline or production clock is introduced.

## Source-scoped deck control

Two independent A/B sessions share the existing Program boundary. Transport and
marker commands can carry `SourceId` alongside `AssetId`; an asset may be loaded
on both sources. Source identity selects the session and asset identity fences
replacement. A legacy asset-only command is rejected when ambiguous.

Client snapshot and close overloads accept a source identity. Matched local pipe
peers use distinct `media_deck.scoped.*` operations, so older peers reject an
unknown operation rather than silently ignoring its selector. External control
adds protobuf transport field 9, marker field 7 and source-target operations
65/66; existing field numbers are preserved. Media contract 1.0 and stored
production/checkpoint/marker document layouts remain unchanged. Deploy matched
ControlHost, RuntimeHost, Operator and Client builds for multi-deck operation.

Play, pause, stop, seek, IN/OUT policy, loop and readiness are session-local.
Markers remain asset metadata in the existing persistence lane. A seek/stop or
replacement invalidates the current payload; Program does not republish pixels
from the previous clip generation. Empty/repeated boundaries do not replay audio.
Logical Program tally follows the committed source; selecting a deck for editing
does not perform a TAKE.
