<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Recording Profile Catalog and Provider Boundary

## Purpose

Program Recording exposes a provider-neutral catalog of concrete recording profiles while RuntimeHost remains the sole owner of recording execution. A profile describes delivery semantics and availability; a writer provider owns the concrete container/codec implementation.

The catalog does not create production authority. Selecting a recording profile is configuration metadata for the next recording session and does not advance Production revision.

## Provider-neutral model

Every production recording profile has a stable `RecordingProfileId` and every writer provider has a stable `RecordingWriterProviderId`.

A `RecordingProfileDescriptor` publishes:

- stable profile identity and display name;
- container and file extension;
- video codec/essence, profile and optional level;
- audio codec/essence;
- supported Program input video formats;
- required Program audio format;
- nominal or maximum encoded/payload rate metadata;
- acceleration classification;
- provider identity;
- current availability and an explicit unavailable reason;
- evidence/qualification state.

The profile catalog rejects duplicate profile identities. The writer-provider registry additionally rejects duplicate provider identities, profile/provider mismatches and duplicate profiles across providers.

## Current production profiles

The backward-compatible default remains `mp4-h264-aac`.

| Profile | Provider | Container | Video | Audio | Acceleration | Availability |
| --- | --- | --- | --- | --- | --- | --- |
| `mp4-h264-aac` | `windows-media-foundation` | MP4 | H.264/AVC Main, 20 Mbit/s | AAC-LC, 192 kbit/s | Software | Windows Media Foundation |
| `mov-2vuy-pcm` | `managed-quicktime` | QuickTime MOV | Uncompressed 8-bit YUV 4:2:2 (`2vuy`) | Stereo 48 kHz signed PCM16 LE (`sowt`) | Software | Managed provider |

Both profiles accept 1920x1080 progressive RGBA8 Program input at 50 fps or 60000/1001 fps and Stereo 48 kHz Float32 Program audio.

The MOV profile converts RGBA8 to packed `2vuy` and Float32 audio to PCM16 on the asynchronous Recording worker. It is intentionally high-bandwidth and must not be interpreted as physical-storage-throughput qualification.

## Provider implementations

### Windows Media Foundation

The existing MP4 writer retains its target reservation, partial-file finalization, A/V timestamp mapping, codec settings and independent reopen/decode qualification. It remains the default profile.

### Managed QuickTime

`ManagedQuickTimeMovRecordingWriter` writes a bounded QuickTime structure directly from managed code:

- `ftyp` with QuickTime brand;
- 64-bit-size `mdat`;
- `moov` with one video and one audio track;
- `2vuy` visual sample description;
- `sowt` audio sample description;
- exact `stts`, `stsc`, `stsz` and `co64` sample tables;
- version-1 movie/media headers for 64-bit durations.

No external encoder executable, codec SDK, native media library or new Recording NuGet package is required.

Finalization is partial-first: `<name>.partial.mov` is closed and independently parsed before it may be atomically promoted to `<name>.mov`.

## Independent MOV probe

`QuickTimeMovProbe` is separate from the writer and reconstructs the finalized container state from bytes on disk. It validates:

- QuickTime brand and atom boundaries;
- exactly one video and one audio track;
- `2vuy` and `sowt` sample entries;
- width/height and 48 kHz stereo PCM16 audio;
- media timescales and durations;
- sample counts and time-to-sample totals;
- sample-to-chunk coverage;
- monotonically increasing chunk offsets;
- every media chunk remaining inside `mdat`;
- A/V duration alignment within one video-frame tolerance.

The probe is qualification evidence for the recording artifact. It does not imply that Media Deck/local playback supports `2vuy` MOV.

## Writer-provider selection

RuntimeHost composes a bounded `RecordingWriterProviderRegistry`. A recording start may supply an optional profile identity.

Selection rules are:

1. an absent profile resolves to the catalog default;
2. an unknown profile fails before `ProgramRecorder.StartAsync`;
3. an unavailable profile fails before `ProgramRecorder.StartAsync`;
4. the selected provider creates one concrete writer for the session;
5. the concrete writer owns target normalization and container/codec behavior;
6. provider/writer failure is reported through the existing failure-isolated Recording lifecycle and cannot mutate Program.

No arbitrary assembly loading, reflection-based plugin discovery, external encoder command lines or process spawning is part of this boundary.

## Target normalization

The Operator supplies a destination and a filename stem or an explicitly compatible filename. It does not infer a container extension.

The selected writer normalizes the target:

- `mp4-h264-aac`: no extension becomes `.mp4`; another explicit extension is rejected.
- `mov-2vuy-pcm`: no extension becomes `.mov`; another explicit extension is rejected.

## Confirmed state and Operator behavior

RuntimeHost publishes:

- the profile catalog;
- default profile identity;
- active profile identity;
- active provider identity;
- availability, acceleration and evidence for every advertised profile.

ControlHost and Client carry only provider-neutral data. They do not expose concrete writer types.

The Operator builds its recording profile selector only from this confirmed catalog. START REC submits the selected stable profile identity. The active profile/provider shown during or after a session comes from Runtime-confirmed state.

## Dependency, packaging and licensing boundary

The MOV provider adds no third-party runtime component. Therefore:

- there is no new bundled native binary;
- there is no new external process to discover or trust;
- there is no new codec redistribution grant to track;
- the existing release package/SBOM pipeline remains authoritative for the shipped managed assembly;
- no new third-party notice is required for the MOV provider itself.

The detailed decision and rejected dependency classes are recorded in [Professional Recording Formats](ProfessionalRecordingFormats.md).

## Test/evidence writer boundary

`ReferenceRecordingPayloadWriter` remains a deterministic test/evidence injection. It is not registered as a professional delivery profile and is not advertised to the Operator.

## MXF and proprietary codec boundary

MXF, ProRes, DNxHR and AVC-Intra remain unavailable. In particular, no vague “broadcast MXF” capability is advertised. MXF requires a concrete operational pattern/essence implementation and independent validation before it can enter the catalog.

Hardware acceleration also remains unverified and unsupported by the production catalog. Both current profiles are explicitly classified as software.

## Capability evidence

The repository projection at `docs/qualification/RecordingCapabilityCatalog.json` provides a machine-readable statement of the current catalog and qualification boundary. Runtime availability remains authoritative for the running host because platform capabilities can differ from the repository reference declaration.
