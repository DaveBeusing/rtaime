<!--
Copyright (c) 2026 Dave Beusing
David Beusing <david.beusing@gmail.com>
-->

# Local Media File Source

## Scope

Local Media File Source introduces local SSD/HDD media as a real rtaime Media/Runtime source. It does not introduce transport controls, seeking, timeline UI, cue points, IN/OUT, playlists, streaming URLs or network media.

Change classification: `REALTIME_CRITICAL` for decoded-frame admission into the existing media pipeline; the probing/open path is non-real-time.

## V1 import contract

The reference local-file provider accepts MP4 video through the Windows Media Foundation source/decoder path. The admitted codec set is H.264/AVC, HEVC/H.265, AV1, VP9, MPEG-4 Part 2, VC-1 and Motion JPEG. Embedded AAC, MP3 and PCM audio are normalized when present. Files without a supported audio stream remain playable as video and the Operator reports `NO AUDIO`. Native file resolution, frame rate and supported audio sample rate do not need to match the active production format exactly.

On Windows, the Media Foundation Source Reader uses advanced video processing to normalize decoded video to the RuntimeHost production format:

- 1920x1080 progressive at 50/1; or
- 1920x1080 progressive at 60000/1001.

Media Foundation now performs decode, scaling, frame-rate normalization and color conversion into RGB32 before the managed Runtime boundary. The decoder owns one reusable full-frame RGBA scratch buffer, copies RGB32 rows using the actual 2D-buffer pitch, normalizes channel order in place and reuses the same storage for the next decoded frame. The RuntimeHost then copies that payload into the already allocated source framebuffer instead of constructing another full-frame `RgbaFrameBuffer` for every boundary. A decoded video payload is therefore decoder-owned scratch data and is valid until the next decoder read; consumers must copy it if they need longer retention.

This removes the former steady-state NV12 managed per-pixel conversion plus repeated 1080p full-frame LOH allocations that could stall sustained playback. A single transient decoder read exception receives one bounded retry; repeated failures still fail closed and enter the normal media-deck error state.

When an audio track is present, it is requested as 48 kHz stereo PCM and then converted to the existing interleaved Float32 representation. When no supported audio track is available, decoded frames carry no audio buffer or audio payload.

This normalization keeps the existing Runtime/GPU production format invariant intact while allowing ordinary MP4 files with different native dimensions, frame rates and supported codecs to be imported. Decoder availability is determined by the Windows Media Foundation installation on the host. If Media Foundation cannot decode or normalize the selected stream, the file fails closed with an explicit `Failure` value.

### Expanded MP4 input envelope

The import path accepts a broad native MP4 input envelope before normalizing to the active RuntimeHost production format:

| Input property | Supported import envelope |
| --- | --- |
| Container | MP4 |
| Video codecs | H.264/AVC, HEVC/H.265, AV1, VP9, MPEG-4 Part 2, VC-1, Motion JPEG |
| Common sample entries | `avc1`, `avc3`, `hvc1`, `hev1`, `av01`, `vp09`, `mp4v`, `vc-1`, `jpeg` |
| Resolution | 48×48 through 4096×2304 |
| Native frame rate | up to 240 fps |
| Average video bitrate | up to 300 Mbit/s when Media Foundation reports `MF_MT_AVG_BITRATE` |
| H.264 decode-rate guard | Level 5.1 macroblock envelope remains enforced for H.264 |
| Runtime output | normalized to 1920×1080p50 or 1920×1080p59.94 |
| Audio | AAC, MP3 or PCM when decodable; absent/unsupported audio falls back to video-only playback |
| Pixel/audio runtime representation | RGBA8 video, optional 48 kHz stereo Float32 audio |

The decode-rate guard uses a maximum of 983,040 H.264 macroblocks per second and applies only to H.264. It intentionally permits common H.264 profiles such as 4K24/25/30, 1440p60, 1080p100/120 and 720p200/240 while rejecting H.264 combinations such as 4K60. Other admitted codecs use the general resolution, frame-rate and bitrate envelope and still depend on an installed Media Foundation decoder.

Variable-frame-rate MP4 files are admitted from their MP4 timing metadata and are normalized by Media Foundation frame-rate conversion to the fixed RuntimeHost production cadence. Bitrate is not used to alter production timing; it is an input decode-admission property only.

Acceptance by this software policy is not hardware-performance qualification. UHD/high-bitrate/high-frame-rate decoding remains `UNVERIFIED` on the reference hardware until dedicated sustained-load qualification evidence exists.

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

- sustained multi-cycle decoding of at least 250 normalized frames without entering decoder failure;
- contract metadata validation, including video-only probes;
- MP4/H.264/AAC reference probing;
- contract and policy coverage for HEVC, AV1, VP9, MPEG-4 Part 2, VC-1 and Motion JPEG;
- contract coverage for AAC, MP3 and PCM audio;
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
