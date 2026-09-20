<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Media IN/OUT & Cue Points

## Scope

Media Markers adds persistent, frame-based media markers on top of the Local Media Transport transport and Media Timeline Seeker timeline coordinate system.

Supported V1 marker operations:

- set and clear IN;
- set and clear OUT;
- jump to IN or OUT;
- add a named cue point at the confirmed transport frame;
- rename a cue point;
- delete a cue point;
- jump to a cue point;
- persist and restore all marker metadata by `MediaAssetId`.

Waveforms, thumbnails, timeline zoom, playlists, macros, cue colors and external trigger protocols remain outside Media Markers.

## Authority

Marker state is not owned by WPF controls.

`MediaMarkerController` in `rtaime.Media` applies versioned `MediaMarkerCommand` values and returns a confirmed `MediaMarkerSnapshot`. The client-side `MediaTimelineMarkerController` only translates operator intent into marker commands and reconciles the returned snapshot.

Jump operations use the Media Timeline Seeker absolute timeline seek seam, which in turn submits the existing Local Media Transport transport seek command. The Operator therefore does not acquire a second playback or marker authority.

## Frame semantics

All IN, OUT and cue positions are stored as zero-based frame numbers.

A marker snapshot also carries the asset's total frame count. Every marker must be inside `0..TotalFrames-1`. If both IN and OUT are present, IN must be less than or equal to OUT.

Cue-point identities are stable `MediaCuePointId` values. Cue presentation order is deterministic:

1. frame position;
2. ordinal cue name;
3. cue-point identity.

Names are trimmed, must not be empty and are limited to 64 characters.

## Persistence

Markers are management metadata. The original MP4 file is never modified.

`MediaMarkerPersistenceStore` stores one checksummed JSON management document per `MediaAssetId` under the existing `management_documents` table with area `media.asset.markers`. This intentionally reuses the established transactional SQLite management lane and does not introduce a schema migration.

Persistence uses optimistic document versions. A stale expected storage version fails with the existing `persistence.version_conflict` failure. Restore also verifies that the persisted total frame count still matches the current media asset; a mismatch fails closed rather than applying markers to a changed clip.

## Media Foundation qualification stabilization

The Media Markers branch also simplifies the inherited local-media decoder boundary used by Local Media Transport/Media Timeline Seeker qualification. Windows Media Foundation is asked for decoder-native NV12 video and PCM audio using only major type and subtype. rtaime converts NV12 to RGBA and PCM16 to Float32 in managed code.

This removes the optional Source Reader RGB32 video-processing stage from the qualification path while preserving the existing external RGBA/Float32 media contracts.

## Acceptance evidence

Media Markers evidence covers:

- contract validation for frame ranges, cue identities and command-specific fields;
- deterministic cue ordering;
- IN/OUT ordering and controlled rejections;
- add/rename/delete cue behavior;
- client commands sourced from the confirmed transport frame;
- jump-to-IN and jump-to-cue through the Media Timeline Seeker seek seam;
- SQLite save/load roundtrip;
- optimistic persistence version conflict;
- changed-media frame-count rejection;
- normal Release build, architecture, contract, unit, integration and provider smoke gates.

## Handover to Media Deck Operator

Media Deck Operator may compose these marker capabilities into the Media Deck Operator surface: visible IN/OUT indicators, cue list, buttons, keyboard bindings, tally/loading/error presentation and deck lifecycle.

Media Deck Operator must not move marker truth into the UI. It must render confirmed `MediaMarkerSnapshot` state and submit commands through the existing client/control seams.
