<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Media Deck Operator Control

## Scope

AP-45 composes the local-media capabilities delivered by AP-41 through AP-44 into one showcase-ready Operator module.

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

Playlist, clip-bank, multi-deck, autoplay-on-program, waveform, thumbnails and final showcase styling remain outside this work package.

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

The Windows Media Foundation COM declarations flatten inherited COM VTables explicitly for `IMFMediaType` and `IMFSample`. Video decode requests NV12 and converts it to the existing RGBA8 contract in managed code. NV12 storage padding is handled independently from visible 1920×1080 dimensions, including the common 1088-row decoder allocation.

Audio decode requests PCM16 and converts it to the existing stereo Float32 contract.

## Marker persistence

IN/OUT and cue metadata remain in the existing SQLite management-document lane through `MediaMarkerPersistenceStore`. The original MP4 is never modified.

Marker writes use optimistic storage versions and remain outside the Runtime media hot path.

## Operator lifecycle

`MediaDeckViewModel` starts a lightweight 100 ms state poll only while the deck is playing. The polling loop is cancellation-aware and is disposed when the Operator window closes.

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

AP-45 requires:

- Release solution build;
- architecture tests;
- contract tests for media-deck snapshot invariants;
- client/unit tests proving confirmed-state composition and command routing;
- Operator policy checks proving the deck stays behind `rtaime.Client`;
- ControlHost/RuntimeHost integration evidence for open, transport and marker state;
- Windows local-media decode integration;
- provider smoke.

The work package is complete only when those gates are green.
