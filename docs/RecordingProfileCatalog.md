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
- video codec, codec profile and optional level;
- audio codec;
- supported Program input video formats;
- required Program audio format;
- nominal video and audio bitrates;
- acceleration classification;
- provider identity;
- current availability and an explicit unavailable reason;
- evidence/qualification state.

The profile catalog rejects duplicate profile identities. The writer-provider registry additionally rejects duplicate provider identities, profile/provider mismatches and duplicate profiles across providers.

## Current production profile

The current default profile remains `mp4-h264-aac`.

| Field | Confirmed value |
| --- | --- |
| Provider | `windows-media-foundation` |
| Container | ISO Base Media File Format (MP4) |
| Extension | `.mp4` |
| Video | H.264/AVC Main |
| Audio | AAC-LC |
| Program video input | 1920×1080 progressive RGBA8 at 50 fps or 60000/1001 fps |
| Program audio input | Stereo 48 kHz Float32 |
| Video bitrate | 20 Mbit/s |
| Audio bitrate | 192 kbit/s |
| Acceleration class | Software |
| Evidence | Qualified software-interoperability path with independent reopen/decode regression |

Availability is platform-confirmed. The Windows Media Foundation profile is available only when the required Windows platform capability exists. The codec name does not imply hardware acceleration.

The existing MP4 writer behavior, target reservation, partial-file finalization, A/V timestamp mapping, codec settings and reopen/decode qualification remain unchanged.

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

The selected writer normalizes the target. For `mp4-h264-aac`, no extension becomes `.mp4`; an explicit incompatible extension is rejected. This preserves the current MP4 target-safety behavior while allowing future providers to own their own extension rules.

## Confirmed state and Operator behavior

RuntimeHost publishes:

- the profile catalog;
- default profile identity;
- active profile identity;
- active provider identity;
- availability, acceleration and evidence for every advertised profile.

ControlHost and Client carry only provider-neutral data. They do not expose Windows Media Foundation writer types.

The Operator builds its recording profile selector only from this confirmed catalog. START REC submits the selected stable profile identity. The active profile/provider shown during or after a session comes from Runtime-confirmed state.

## Test/evidence writer boundary

`ReferenceRecordingPayloadWriter` remains a deterministic test/evidence injection. It is not registered as a professional delivery profile and is not advertised to the Operator.

This preserves existing automated Recording tests without falsely presenting the `.rtaime-recording` artifact as a delivery format.

## Future formats and providers

MOV, MXF, ProRes, DNxHR, AVC-Intra and hardware encoding are **not available** in this package. They require separate concrete writer/provider implementations and evidence before they may appear as available catalog profiles.

Hardware acceleration remains unverified and unsupported by the current production catalog. The current H.264/AAC profile remains explicitly classified as software.

## Capability evidence

The repository projection at `docs/qualification/RecordingCapabilityCatalog.json` provides a machine-readable statement of the current catalog and qualification boundary. Runtime availability remains authoritative for the running host because platform capabilities can differ from the repository reference declaration.
