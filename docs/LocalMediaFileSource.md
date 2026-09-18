<!--
Copyright (c) 2026 Dave Beusing
David Beusing <david.beusing@gmail.com>
-->

# Local Media File Source

## Scope

Local Media File Source introduces local SSD/HDD media as a real rtaime Media/Runtime source. It does not introduce transport controls, seeking, timeline UI, cue points, IN/OUT, playlists, streaming URLs or network media.

Change classification: `REALTIME_CRITICAL` for decoded-frame admission into the existing media pipeline; the probing/open path is non-real-time.

## V1 import contract

The reference local-file provider accepts MP4 files with H.264/AVC video and embedded AAC audio. Native file resolution, frame rate and audio sample rate no longer need to match the active production format exactly.

On Windows, the Media Foundation Source Reader uses advanced video processing to normalize decoded video to the RuntimeHost production format:

- 1920x1080 progressive at 50/1; or
- 1920x1080 progressive at 60000/1001.

Decoded video is converted to the existing RGBA8 Runtime representation. Embedded AAC audio is requested as 48 kHz stereo PCM and then converted to the existing interleaved Float32 representation.

This normalization keeps the existing Runtime/GPU production format invariant intact while allowing ordinary H.264/AAC MP4 files with different native dimensions or frame rates to be imported. If Windows Media Foundation cannot perform the requested conversion, or if the container/codecs are unsupported, the file fails closed with an explicit `Failure` value.

## Contract boundary

`rtaime.Media.Contracts` adds only stable media identity and metadata:

- `MediaAssetId`;
- `MediaContainerFormat`;
- `MediaVideoCodec`;
- `MediaAudioCodec`;
- `LocalMediaProbe`.

Bulk decoded video/audio payloads remain implementation-local and are not added to cross-process contracts. `MediaContractVersion.Current` remains `1.0`; Local Media File Source is an additive contract extension and does not change existing wire semantics.

## Provider and decoder boundary

`LocalMediaFileProvider` exposes the existing `media.route` capability plus the explicit `media.file.decode` capability. The Windows reference implementation uses Media Foundation through direct COM/P/Invoke interop from C#/.NET. Advanced Source Reader video processing performs resize and frame-rate normalization before frames enter rtaime's Runtime pipeline. No WPF `MediaPlayer`, FFmpeg runtime dependency or new production NuGet package is introduced.

The provider is unavailable on non-Windows hosts. That is deliberate for the V1 Windows reference-platform family and is surfaced as `media.file.platform_unsupported`.

## Runtime path

A file is opened with the production `MediaSourceId` that already corresponds to `ProductionSourceId`. Planning can therefore use the normal source-agnostic production graph and `media.route` admission. The prepared execution retains that `MediaSourceId` and the selected local-media provider resource.

`LocalMediaRuntimeSession` accepts only a matching `PreparedExecutionContract`, commits it through `TransactionalRuntime`, decodes the next file frame, creates the normal `FrameDescriptor`, and submits/consumes it through `MediaFramePipeline`. The UI does not own or synthesize playback state and there is no side-channel WPF playback path.

Video and decoded audio use the Media Foundation 100-nanosecond timebase (`1/10000000`) so A/V timestamps remain directly comparable. Frame sequencing is the existing media-pipeline sequence, not decoder-private state.

## Reference evidence

`tests/TestAssets/media/reference-1080p50-h264-aac.mp4` is a short deterministic reference asset generated from synthetic color/audio sources. It is intentionally small and contains no third-party footage.

Coverage includes:

- contract metadata validation;
- MP4/H.264/AAC probing;
- V1 video/audio metadata;
- missing-file rejection;
- unsupported-container rejection;
- corrupt-MP4 rejection;
- production planning with the local provider;
- PreparedExecution commit through `TransactionalRuntime`;
- decoded frame admission through `MediaFramePipeline`;
- embedded audio availability and shared A/V timebase.

## Known limitations / handoff to Local Media Transport

Local Media File Source is sequential-read only. End-of-media is reported explicitly. There is intentionally no Play/Pause/Stop/Seek/Frame-Step state machine yet; those semantics belong to Local Media Transport. Decoder seeking, resume behavior and frame-accurate positioning must build on the existing `MediaAssetId`, `LocalMediaProbe`, `MediaSourceId` and Runtime session rather than creating a second playback authority.
