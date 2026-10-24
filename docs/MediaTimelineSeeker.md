<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Media Timeline & Seeker

## Scope

Media Timeline Seeker adds a professional, frame-oriented media timeline to the WPF Operator without moving playback authority into the UI. The timeline consumes the Local Media Transport media transport semantics and translates pointer or keyboard intent into seek commands. Cue points, IN/OUT ranges, waveform/thumbnails, timeline zoom and playlists remain outside this package.

## Authority and reconciliation

`MediaTimelineController` lives in `rtaime.Client` and is the presentation/client seam between a confirmed `MediaTransportSnapshot` and a `MediaTransportCommand` sender. Its confirmed position is always derived from the latest transport snapshot. Pointer drag may expose a temporary preview frame for immediate visual feedback, but the preview never replaces the confirmed transport state.

Every completed seek is reconciled with the snapshot returned by the command sender. External transport updates may replace the confirmed snapshot while command authority is current. While a pointer drag is active, an external update changes the confirmed position without stealing the visible drag position; completing the drag sends the requested frame and then displays the returned confirmed snapshot.

The WPF `MediaTimelineControl` contains input mapping and presentation only. It does not decode media, own playback state, or connect directly to RuntimeHost. The established Operator → ControlHost → RuntimeHost authority direction remains unchanged. Media Deck Operator can bind the controller to the live media-deck transport seam when the deck composition is completed.

## Recoverable command failures

I/O loss, request deadlines and ControlHost session/full-snapshot faults leave the last confirmed
position intact. Operator presents `MEDIA ACTION UNCONFIRMED`, discards the temporary drag
position and all unsent seek intent, and disables media mutations until an authoritative snapshot
is restored by an explicit full synchronization and deck refresh begun after that failure.
Passive snapshots and late responses cannot clear a newer uncertainty generation.
An already dispatched operation may have executed; it is never automatically replayed.
A snapshot can reveal the requested position or a different position without claiming the lost
acknowledgement was successful.

The timeline controller records the uncertainty before propagating a direct request failure to its
presentation boundary. Keyboard, cue, IN/OUT, explicit edit-cursor, pointer-release and trim paths
contain the expected failure. Coalesced drag and marker failures are recorded before releasing their send gates and stop queued work.
Cancellation after dispatch is also an unknown outcome; cancellation before dispatch sends no mutation.
Programmer/invariant faults retain the normal fatal-error behavior. Teardown cancels and drains
in-flight direct and coalesced seeks before releasing synchronization resources. A successful reply
racing cancellation remains cancelled, and late replies cannot publish into disposed controllers.

## Interaction model

The timeline supports click/drag seeking, one-frame backward/forward keyboard movement, and Home/End clip-boundary navigation. Drag updates are coalesced on a 40 ms default cadence so high-rate mouse movement does not create a command flood. Pointer release sends the exact final target frame while transport authority remains current; uncertainty cancels unsent intent.

The WPF layout uses device-independent units. Pointer-to-frame mapping is defined in `MediaTimelineGeometry`; explicit tests cover 100%, 125% and 150% DPI equivalence. The seeker exposes an automation name and help text, while all transport buttons remain keyboard-focusable.

## Timecode semantics

Timeline labels use `HH:MM:SS:FF` non-drop frame-count timecode. The frame field uses the nominal integer rate derived from the exact `FrameRate`: 25 for 25 fps, 50 for 50 fps and 60 for 60000/1001 (59.94) fps. Duration and remaining labels first convert the exact timespan to the nearest frame using the exact rational frame rate, then format that frame count using the nominal frame field.

Drop-frame 59.94 timecode is not introduced by Media Timeline Seeker. If broadcast drop-frame labeling becomes a product requirement, it must be added as an explicit contract/presentation policy rather than inferred by the UI.

## Local Media Transport qualification precondition

The branch also replaces the previous 80 ms Local Media Transport Windows decoder qualification fixture with a deterministic one-second / 50-frame 1080p50 H.264 Baseline + 48 kHz stereo AAC MP4. The previous four-frame clip exercised an unrealistic decoder-buffering edge case on Windows Server Media Foundation and caused the required integration evidence to return end-of-stream before decoded output.

The one-second fixture remains synthetic and deterministic, but provides enough GOP/sample depth for production-path decode and frame-accurate seek qualification.

The repository stores this qualification fixture as `reference-1080p50-h264-aac-1s.mp4.gz`. Integration tests expand it to a unique temporary `.mp4` before invoking the production `LocalMediaFileProvider`, then remove the temporary file on disposal. Compression is repository packaging only and does not alter the media path under test.

## Acceptance evidence

Media Timeline Seeker unit evidence covers:

- 25, 50 and 59.94 nominal `HH:MM:SS:FF` semantics;
- clip start and end progress;
- 100%, 125% and 150% DPI pointer equivalence;
- optimistic drag position versus externally confirmed state;
- drag coalescing and exact final seek;
- keyboard-relative seek and clip-boundary clamping.

Repository acceptance still requires the normal Release solution build, architecture tests, contract tests, unit tests and the Windows integration suite to pass without new compiler warnings.

## Handover to Media Markers

Media Markers may build persisted IN/OUT and cue-point markers on the stable frame-based timeline coordinate system. It must not change the authority rule: marker presentation may originate in Operator, but persisted media metadata and effective runtime ranges remain model/control concerns.

