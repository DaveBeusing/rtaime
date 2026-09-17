<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Media Timeline & Seeker

## Scope

AP-43 adds a professional, frame-oriented media timeline to the WPF Operator without moving playback authority into the UI. The timeline consumes the AP-42 media transport semantics and translates pointer or keyboard intent into seek commands. Cue points, IN/OUT ranges, waveform/thumbnails, timeline zoom and playlists remain outside this package.

## Authority and reconciliation

`MediaTimelineController` lives in `rtaime.Client` and is the presentation/client seam between a confirmed `MediaTransportSnapshot` and a `MediaTransportCommand` sender. Its confirmed position is always derived from the latest transport snapshot. Pointer drag may expose a temporary preview frame for immediate visual feedback, but the preview never replaces the confirmed transport state.

Every completed seek is reconciled with the snapshot returned by the command sender. External transport updates may replace the confirmed snapshot at any time. While a pointer drag is active, an external update changes the confirmed position without stealing the visible drag position; completing the drag sends the requested frame and then displays the returned confirmed snapshot.

The WPF `MediaTimelineControl` contains input mapping and presentation only. It does not decode media, own playback state, or connect directly to RuntimeHost. The established Operator → ControlHost → RuntimeHost authority direction remains unchanged. AP-45 can bind the controller to the live media-deck transport seam when the deck composition is completed.

## Interaction model

The timeline supports click/drag seeking, one-frame backward/forward keyboard movement, and Home/End clip-boundary navigation. Drag updates are coalesced on a 40 ms default cadence so high-rate mouse movement does not create a command flood. Pointer release always sends the exact final target frame.

The WPF layout uses device-independent units. Pointer-to-frame mapping is defined in `MediaTimelineGeometry`; explicit tests cover 100%, 125% and 150% DPI equivalence. The seeker exposes an automation name and help text, while all transport buttons remain keyboard-focusable.

## Timecode semantics

Timeline labels use `HH:MM:SS:FF` non-drop frame-count timecode. The frame field uses the nominal integer rate derived from the exact `FrameRate`: 25 for 25 fps, 50 for 50 fps and 60 for 60000/1001 (59.94) fps. Duration and remaining labels first convert the exact timespan to the nearest frame using the exact rational frame rate, then format that frame count using the nominal frame field.

Drop-frame 59.94 timecode is not introduced by AP-43. If broadcast drop-frame labeling becomes a product requirement, it must be added as an explicit contract/presentation policy rather than inferred by the UI.

## AP-42 qualification precondition

The branch also replaces the previous 80 ms AP-42 Windows decoder qualification fixture with a deterministic one-second / 50-frame 1080p50 H.264 Baseline + 48 kHz stereo AAC MP4. The previous four-frame clip exercised an unrealistic decoder-buffering edge case on Windows Server Media Foundation and caused the required integration evidence to return end-of-stream before decoded output.

The one-second fixture remains synthetic and deterministic, but provides enough GOP/sample depth for production-path decode and frame-accurate seek qualification.

## Acceptance evidence

AP-43 unit evidence covers:

- 25, 50 and 59.94 nominal `HH:MM:SS:FF` semantics;
- clip start and end progress;
- 100%, 125% and 150% DPI pointer equivalence;
- optimistic drag position versus externally confirmed state;
- drag coalescing and exact final seek;
- keyboard-relative seek and clip-boundary clamping.

Repository acceptance still requires the normal Release solution build, architecture tests, contract tests, unit tests and the Windows integration suite to pass without new compiler warnings.

## Handover to AP-44

AP-44 may build persisted IN/OUT and cue-point markers on the stable frame-based timeline coordinate system. It must not change the authority rule: marker presentation may originate in Operator, but persisted media metadata and effective runtime ranges remain model/control concerns.
