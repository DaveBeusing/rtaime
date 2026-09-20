<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Media Deck Operator Control

## Scope

Media Deck Operator composes the local-media capabilities delivered by Local Media File Source through Media Markers into one showcase-ready Operator module.

The deck exposes:

- Windows file selection for a local MP4;
- production source-slot selection through the existing Operator source bank;
- confirmed clip name and source identity;
- resolution, frame rate, video codec, audio codec/channels/sample rate and duration;
- Play/Pause and Stop;
- frame-accurate timeline/seeker with current, duration and remaining timecode;
- IN/OUT set, clear and jump;
- cue add, rename, delete and jump;
- explicit unloaded, ready, playing, paused, ended and error presentation;
- keyboard access without stealing the existing production CUT shortcut.

Playlist, clip-bank, multi-deck, waveform, thumbnails and final showcase styling remain outside Media Deck Operator. Program-triggered playback and deterministic end behavior are added by Media Autoplay & End Behavior below.

## Authority and process boundaries

The Operator remains non-authoritative.

```text
MediaDeckControl / MediaDeckViewModel
  -> MediaDeckController in rtaime.Client
  -> NamedPipeOperatorControlTransport
  -> ControlHost MediaDeckControlService
  -> NamedPipeRuntimeHostTransport
  -> RuntimeHost LocalMediaDeckRuntimeService
  -> LocalMediaRuntimeSession
  -> LocalMediaFileProvider / Windows Media Foundation
```

Transport and marker commands are never applied directly by WPF. The UI renders only confirmed `MediaDeckSnapshot`, `MediaTransportSnapshot` and `MediaMarkerSnapshot` state returned through the Client SDK.

ControlHost validates that the requested local-media source is one of the authoritative production source slots. It owns persisted marker metadata. RuntimeHost owns file decode, transport state and frame advancement.

## Runtime isolation

The local media deck has a dedicated periodic RuntimeHost worker. It does not decode on the normal Program media-loop hot path.

The deck worker advances only while the confirmed transport state is `Playing`. Paused, ready and ended decks therefore do not consume decode work.

No raw RGBA video or audio payload is transferred over management Named Pipes. The IPC surface carries commands, metadata and confirmed state only.

## Local media decode qualification

The Windows Media Foundation COM declarations flatten inherited COM VTables explicitly for `IMFMediaType` and `IMFSample`. Advanced video processing normalizes video directly to RGB32 at the active Runtime format. The decoder copies rows using the Media Foundation 2D-buffer pitch into one reusable RGBA scratch buffer and normalizes channel order in place. RuntimeHost then updates its already allocated source framebuffer instead of constructing another full-frame object per boundary.

Audio decode requests PCM16 and converts it to the existing stereo Float32 contract.

## Marker persistence

IN/OUT and cue metadata remain in the existing SQLite management-document lane through `MediaMarkerPersistenceStore`. The original MP4 is never modified.

Marker writes use optimistic storage versions and remain outside the Runtime media hot path.

## Operator lifecycle

`MediaDeckViewModel` does not run an independent state poll. The existing Operator synchronization snapshot already asks ControlHost for the confirmed media-deck state; that same snapshot now carries the complete `MediaDeckSnapshot` to the Client and drives the deck/timeline UI. This keeps Program-triggered autoplay visible without adding a second management polling loop or competing Runtime request stream.

The WPF file dialog selects an MP4 path only. Opening the selected path still crosses the full Client -> ControlHost -> RuntimeHost path.

## Keyboard controls

The existing production keys remain unchanged:

- Space: CUT;
- Ctrl+Space: DISSOLVE/AUTO;
- Ctrl+P: set Preview.

Media-deck keys are intentionally separate:

- P: Play/Pause;
- S: Stop;
- I: set IN;
- O: set OUT;
- M: add cue;
- Delete: delete selected cue.

Timeline focus retains Left/Right frame stepping and Home/End clip-boundary seeking.

## Acceptance evidence

Media Deck Operator requires:

- Release solution build;
- architecture tests;
- contract tests for media-deck snapshot invariants;
- client/unit tests proving confirmed-state composition and command routing;
- Operator policy checks proving the deck stays behind `rtaime.Client`;
- ControlHost/RuntimeHost integration evidence for open, transport and marker state;
- Windows local-media decode integration;
- provider smoke.

The work package is complete only when those gates are green.


## Media Autoplay & End Behavior

Media Autoplay & End Behavior makes the single local-media deck behave like a bounded production clip player when its assigned source becomes Program.

Playback policy is configured through the existing command path:

```text
Operator
  -> rtaime.Client MediaDeckController
  -> ControlHost MediaDeckControlService
  -> RuntimeHost LocalMediaDeckRuntimeService
```

No WPF timer or local UI event starts production playback. RuntimeHost observes the source bound to the committed Program sink. A rising edge from off-Program to on-Program starts the deck when **Auto Play on Program** is enabled. This applies equally to CUT and DISSOLVE because both converge on the same committed Runtime execution state.

### Effective playback range

Persisted ControlHost marker state remains authoritative for IN and OUT. When playback policy or markers change, ControlHost sends RuntimeHost the confirmed effective range.

The range is:

- start = IN when set, otherwise clip frame 0;
- end = OUT when set, otherwise the final clip frame.

A valid cue position already inside this range is preserved when the source is taken to Program. If the transport is ended or outside the effective range, Program autoplay seeks to effective IN before playing.

Remaining time and the visible countdown are calculated against the effective OUT rather than the physical file duration. Source-tile remaining time uses the same effective value.

### End behavior

The V1 deck exposes four explicit end modes:

- **Hold Last Frame**: transport enters ENDED while holding the effective OUT frame;
- **Stop**: transport returns to READY at clip frame 0;
- **Loop**: transport seeks to effective IN and continues PLAYING;
- **Return to IN**: transport becomes PAUSED at effective IN.

Loop therefore respects IN/OUT rather than looping the complete source file.

Removing the media source from Program does **not** automatically pause it in Media Autoplay & End Behavior. Auto-pause-on-remove was optional in the work-package definition and is intentionally not claimed without a stronger product rule.

### Runtime video and audio feed

The local-media Runtime worker stages successfully decoded RGBA video into the existing V1 external-input surface for the assigned source, alongside the Float32 audio feed. Windows Media Foundation performs the admitted decode, resize and frame-rate normalization to the active V1 Program format before the frame reaches this worker.

Raw media remains inside RuntimeHost. Management IPC continues to carry only commands, metadata and confirmed state.

### Evidence

Media Autoplay & End Behavior qualification covers:

- cued media starting when its source becomes Program;
- CUT and DISSOLVE through real ControlHost/RuntimeHost IPC;
- Hold Last Frame, Stop, Loop and Return to IN;
- IN/OUT effective-range enforcement;
- loop restart at effective IN;
- effective remaining/countdown metadata;
- retake after ENDED;
- disabled Auto Play preserving PAUSED state;
- client round-trip of confirmed playback policy.

Playlist auto-advance, rundown automation and macros remain out of scope.
