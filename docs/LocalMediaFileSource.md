<!--
Copyright (c) 2026 Dave Beusing
David Beusing <david.beusing@gmail.com>
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

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

`LocalMediaRuntimeSession` accepts only a matching `PreparedExecutionContract`, qualifies preroll, commits it through `TransactionalRuntime`, and admits already decoded frames through `MediaFramePipeline` without performing file I/O or native decoding on the Program boundary. The UI does not own or synthesize playback state and there is no side-channel WPF playback path.

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

## Generated reference-media matrix

The original committed `reference-1080p50-h264-aac*.mp4` fixtures remain as small bootstrap coverage for ordinary integration tests.

A separate generated regression matrix now exercises native 25/1, 50/1 and 60000/1001 H.264/AAC inputs for six seconds each. These files are produced on demand from the internal broadcast test pattern, motion/timing overlay and generated pulse-audio signal, then decoded through the real Windows Media Foundation path.

The generated suite specifically covers the sustained-playback failure boundary beyond five seconds, repeated loop cycles, A/V event preservation, restart/reload behavior and bounded retained managed memory. Generated MP4 files remain transient test artifacts; per-run JSON manifests record technical metadata and SHA-256 integrity evidence.

See `ReferenceMediaRegression.md` for generation, CI use and interpretation.

## Asynchronous decode, ownership and preroll

One session-owned background thread serializes decoder reads, conversion and seek.
Media Foundation uses its asynchronous Source Reader callback: all native ReadSample
output pointers are null; read/flush completion arrives through a single-slot mailbox.
A cancelled generation wakes the reader wait, flushes on the decode thread and waits
up to five seconds for OnFlush before any subsequent read/seek. A read has a 30-second
deadline. Decoder exceptions become local-media failures; they do not escape Program
admission. Close cancels, joins the worker within ten seconds and releases its reader
on the worker, never concurrently with a read. Unresponsive native cancellation is a
typed lifecycle failure, not a claim that a blocked driver has been terminated.
An unacknowledged flush permanently fences that reader and fails cleanup; it is
never retried against a new generation or reported as a successful shutdown.

Ten exact-size RGBA slots and ten 32-KiB audio slots are allocated during session
creation, outside the Program admission lock. Eight frames may wait in the queue; one
slot may be decoding and one admitted. At 1080p this reserves 82,944,000 video bytes
and 327,680 audio bytes per session, excluding decoder scratch/native allocations.
The initial capacity is a conservative 160-ms queue at 50 fps (133.47 ms at 59.94);
it is not a measured optimal latency or a hardware guarantee. Three complete frames
qualify initial/recovery preroll; refill wakes at seven queued frames and backpressures
at eight. Queue, worker and admitted-frame leases return every slot on replacement,
failure, seek or close. An explicitly retained consumer lease keeps its slot alive
until release; lack of free slots backpressures decode rather than allocating more.

The decoder retains its reusable RGB32-to-RGBA scratch buffer. Only the worker copies
it and the audio slice into independent session slots before reading again.
Runtime's handoff retains its own lease across concurrent transport/close operations,
then copies into the existing Runtime input buffer. No large managed allocation,
file access, native read, seek or worker join occurs in Program admission.

Decode diagnostics distinguish Prerolling, Ready, Seeking, Starved, Ended, Failed
and Stopped. Existing transport state remains Runtime-owned playback intent.
Deck Loading projects unqualified preroll/rebuffering while retaining asset/marker
identity; Error carries the actual asynchronous failure. No wire/schema version
changes or new production authority are introduced.

### Timestamp admission and underrun policy

The existing rational Program slot index advances an integer media-frame ordinal.
Missed scheduler opportunities advance the PTS target without catch-up rendering;
late queued video/audio is released and counted, and the admitted media sequence
advances once. Play/resume/seek reanchor the existing slot index; decoder starvation
freezes media progression instead of silently treating missing data as a late frame.
Its target is derived with rational arithmetic from the seek origin and declared
normalized output rate. Admission selects the newest queued PTS at or before that
target, examining at most eight frames. Older due frames are released and counted;
future PTS remains queued. Video PTS must be nonnegative and strictly increasing
within a generation; extent and timestamp validation runs on the worker.

An explicitly queued future PTS may repeat the last admitted video without replaying
its audio. No initial image is invented before the first due PTS. Empty/unqualified
queues freeze media progression and project Loading while Program continues its
own cadence. Runtime marks that input Lost and uses the existing input-fallback
policy with zero/unavailable audio; stale pixels are not advertised as valid.
On recovery, fresh queued timestamps resume the frozen media timeline. This policy
preserves media continuity at the cost of wall-clock delay; it is not catch-up or
an audio concealment policy. Program's output sequence stays authoritative.

Seek, Stop, step, restart and Loop invalidate the old generation synchronously and
enqueue repositioning; they perform no native work on the calling boundary. The
command acknowledges the requested transport position; Loading persists until the
new generation has preroll. Pause retains bounded preread and stops admission.
Open/replacement probes, allocates and prepares off the admission lock. Close and
shutdown detach under the lock and join outside it. An in-flight admission retains
its own payload until the existing boundary completes.

## Playback measurement boundary

Local source observations now expose constant-space open/first-frame, decode/native
read and seek durations, decoded counts, retries/failures and timestamp/audio
discontinuities through the Runtime-owned deck. These implementation diagnostics do
not change wire contracts or supported formats. Native read wall time includes
decoder and I/O waiting; actual source-file bytes, SSD latency and installed decoder
qualification are not inferred. See [long playback evidence](ReferenceMediaRegression.md#long-local-file-playback-baseline)
for source-bound ten-minute / opt-in hour reproduction, actual host cadence and
separate collection/audio/physical statuses.


AAC packet duration is independent of video frame duration. Audio is now sliced onto
the decoded video's sample-clock window with one pending native PCM packet retained;
remainders survive the next frame, and seek clears the packet cache. Negative codec
preroll is discarded by signed sample position. Actual gaps stay observable rather
than being filled with manufactured silence. This fixes long-playback audio runahead
and early audio exhaustion within the existing Media Foundation backend.

The initial six-frame/two-frame-watermark long run observed seven underruns with
a 118.8308-ms maximum decode call. Refill now starts after the first consumed frame,
with eight queued slots as the bounded initial reserve. This changes preread depth,
not playback authority or a promised physical I/O deadline. Qualification must repeat
the long run; raising the existing memory/handle ceilings is not permitted.

The native callback mailbox owns two reusable AutoResetEvents (read and flush),
released after reader disposal and on unsuccessful open. It does not allocate a
blocking Task/monitor chain for each sample. A direct cross-thread mailbox regression
performs 1,024 blocked deliveries after warmup with a bounded handle delta; cancellation
and abandoned/late delivery are checked separately. The isolated native-reader probe
records handle types without Runtime or Replay, and never substitutes for Program acceptance.

## Decoder safety and recovery boundary

One decoded-frame operation shares a 30-second managed progress deadline across
video, audio and key-frame scanning, with at most 4,096 native reads and 128
consecutive empty non-EOS callbacks. Every iteration checks generation cancellation.
A stream tick is a gap, not EOF; an EOS callback carrying a sample retains that final
sample. Seek scanning requires advancing timestamps. An asynchronous read waits only
for the remaining operation budget. These limits cannot interrupt synchronous native
open, ReadSample submission, Flush, seek or COM release.

Reader ERROR, dynamic layout/effect changes and unknown flags fail closed before
payload access. The existing normalized layout is never silently reinterpreted after
a change. Stable implementation failure codes include:

| Code | Meaning and recovery |
| --- | --- |
| media.file.no_progress | Empty callbacks or sample scan exhausted; close/reopen after reviewing the asset |
| media.file.read_timeout | Managed operation/read deadline expired |
| media.file.timestamp_discontinuity | Seek scan stopped advancing |
| media.file.decoded_type_changed | Stream/layout/effect changed; explicit reopen required |
| media.file.stream_flags_invalid | Unknown native status flags |
| media.file.decoder_lost | Native reader error/failing non-Win32 callback status |
| media.file.storage_failure | Failing Win32 callback status; inspect HRESULT and storage/access |
| media.file.audio_payload_invalid | Empty, misaligned or excessive decoded PCM/native extent |
| runtime.media_deck.cleanup_failed | Owner could not be safely released; supported RuntimeHost restart required |

These terminal source failures are latched: neither another read nor Seek can call
the damaged reader. Ordinary nonterminal decoder exceptions retain the existing
single retry. A native PCM packet is bounded to 192,000 bytes (one second of the
configured 48-kHz stereo PCM16), validated before allocation or copy. Locked buffer
lengths must fit their reported native capacity.

MFStartup ownership transfers only with a successfully constructed decoder.
Unsuccessful open, including unexpected metadata/construction exceptions, releases
native media types, reader, callback events and the matching MFShutdown in nested
finally scopes. After transfer, the decode thread alone owns read/seek/flush/release;
callback threads only deliver or release mailbox samples. Close callers serialize
join/cleanup outside Program admission. Pipeline resources are released even when
native-owner cleanup fails.

An owner that misses the ten-second join remains quarantined with its cancellation
and wake resources intact; subsequent cleanup can join it once it returns. The deck
retains the failed session, stays Error and blocks replacement/probing so repeated
Open cannot accumulate stuck readers. Close/Dispose continue reporting cleanup failure,
including after a late native return. Recovery uses the supported RuntimeHost/AppHost
lifecycle, with its existing process supervision boundary. No thread abort, forced COM
release concurrent with read, automatic retry loop or global process termination is
introduced. A permanently blocked driver still requires external lifecycle escalation;
managed cancellation is not proof of native termination.

The actual RGB32 extraction/swizzle path remains scalar and reusable. Direct parity
coverage exercises padding, positive/negative pitch, top-row orientation, channels and
opaque alpha. A full-frame profile records allocation and copy P99 without changing
the conversion algorithm. Audio Float32 windows/native packets retain their existing
ownership and allocation strategy: no audio-specific GC attribution yet justifies
pooling or a longer consumer lifetime.
