<!--
Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>
All rights reserved.
-->

# Reference Media Regression

## Purpose

The reference-media suite exercises the local-file decoder and playback path with media whose logical content is derived from the same internal video, timing and audio generators used by RuntimeHost diagnostics.

The comparison boundary is intentional:

`Internal Generated Source -> Render/Output`

versus

`Generated Reference MP4 -> Windows Media Foundation Decoder -> Runtime/Render`

This isolates file/container/decoder/playback failures from the generated-source path while keeping frame timing, visible frame identity and A/V event semantics controlled.

## Required reference profiles

The required regression matrix is:

| Profile | Encoded video | Native frame rate | Audio | Duration | Required in CI |
| --- | --- | ---: | --- | ---: | --- |
| hd25 | 1920x1080 H.264/AVC | 25/1 | AAC, stereo, 48 kHz | 6 s | yes |
| hd50 | 1920x1080 H.264/AVC | 50/1 | AAC, stereo, 48 kHz | 6 s | yes |
| hd5994 | 1920x1080 H.264/AVC | 60000/1001 | AAC, stereo, 48 kHz | 6 s | yes |

The current local-media Runtime output contract supports 1920x1080p50 and 1920x1080p59.94. It does not expose 2160p50 as a production output format, so UHD50 is not a required reference profile yet.

HEVC/H.265 remains in the admitted local-file codec envelope, but Media Foundation HEVC decoder availability depends on the Windows host installation. It is therefore not a mandatory CI reference until the repository has a pinned, qualified HEVC decode environment. No codec support is added solely for this regression suite.

## Signal construction

The test-media generator uses production signal code directly:

- `BroadcastTestPatternGenerator` supplies the stable visual reference background;
- `MotionTimingTestSignalGenerator` supplies frame counter, timecode, motion indication and synchronized event flashes;
- `GeneratedAudioTestSignalGenerator` in `Pulse` mode supplies sample-accurate stereo sync pulses at 48 kHz.

For generation efficiency, the source signal is rendered on a 640x360 deterministic generation canvas and nearest-neighbor scaled by the test encoder to the 1920x1080 encoded reference profile. Frame rate, frame identity, timecode progression and event timing remain native to the selected profile.

The generated MP4 is therefore not arbitrary sample footage and contains no third-party media.

## Encoding and reproducibility

FFmpeg is a test-only encoder/muxer dependency. It is not a production Runtime dependency.

Reference CI pins FFmpeg 7.1.1. Generation uses:

- raw RGBA video input;
- raw 48 kHz stereo Float32 audio input;
- H.264/AVC video through `libx264`;
- AAC stereo audio;
- 1920x1080 YUV420p encoded output;
- stripped source metadata and a fixed creation timestamp;
- fast-start MP4 layout.

The generated manifest records:

- profile identifier;
- native frame rate;
- encoded resolution;
- codecs;
- audio rate and channel count;
- requested duration;
- generated frame count;
- generated audio sample count;
- source signal descriptions;
- SHA-256 of the generated MP4.

The SHA-256 is an integrity value for a concrete generated artifact. Byte-identical output is not claimed across different FFmpeg/x264 builds. The pinned CI encoder provides the canonical reproducibility environment; semantic verification additionally checks container, codecs, duration, decoded output and playback behavior.

## Local generation

With FFmpeg available on `PATH`:

```powershell
./build/testmedia/Generate-ReferenceMedia.ps1
```

The default output is:

```text
artifacts/testmedia/reference/
```

A custom encoder path may be supplied:

```powershell
./build/testmedia/Generate-ReferenceMedia.ps1 -FfmpegPath C:\Tools\ffmpeg\bin\ffmpeg.exe
```

Generated MP4 files are build/test artifacts and are not committed to the repository.

## Regression execution

Run the complete generated-media regression locally with:

```powershell
$env:RTAIME_REFERENCE_MEDIA_REGRESSION = "1"
./build/testmedia/Test-ReferenceMediaRegression.ps1
```

The dedicated GitHub workflow installs the pinned test encoder, builds the integration graph, generates all required profiles and executes the targeted regression suite.

Normal repository test runs do not require FFmpeg and therefore do not introduce an encoder dependency into ordinary development or production builds.

## Decoder and playback coverage

The generated-media regression verifies that:

- every required MP4 opens through `LocalMediaFileProvider`;
- H.264 and AAC are recognized;
- audio is normalized to 48 kHz stereo Float32;
- 25 fps input decodes into the existing 1080p50 Runtime output;
- 50 fps input decodes into 1080p50;
- 60000/1001 input decodes into 1080p59.94;
- decoded video timestamps progress monotonically;
- decoded audio remains available;
- the visible synchronized flash survives encode/decode;
- the audio pulse survives encode/decode near the corresponding event;
- Stop, restart, Close and reload remain valid;
- playback remains `Playing` beyond five seconds;
- looping across repeated full-file cycles does not enter `Error`;
- retained managed memory remains bounded after warm-up.

The >5 second assertion is the explicit regression boundary for the previously observed failure mode where local MP4 playback could freeze and subsequently appear unavailable/offline.

## Internal-generator versus decoder interpretation

When the internal generated source is correct but a generated reference MP4 fails, investigate the file/decoder path first:

- MP4 probing and metadata;
- Media Foundation decoder availability;
- frame conversion and reusable RGBA buffering;
- audio decode/normalization;
- transport state;
- decoder retry/error handling.

When both the generated source and the decoded reference fail at the same timing point, investigate shared Runtime timing, Program routing, GPU processing or output behavior instead.

A planned A/V event offset caused by frame quantization must not be interpreted as decoder drift. The A/V sync diagnostics continue to distinguish planned media-time quantization from observed internal pipeline timing.

## CI artifact policy

Generated MP4 files remain transient and are not uploaded as normal CI evidence. The workflow uploads only the small JSON manifests containing technical metadata and SHA-256 values.

The regression script rejects generated files larger than 20 MiB to prevent accidental growth of CI artifacts or test inputs.

## Future profiles

Add UHD50 only after 2160p50 becomes an explicitly supported Runtime output profile.

Promote HEVC to the required matrix only after the Windows reference environment has a deterministic, qualified HEVC decoder. Until then, HEVC policy/contract coverage remains separate from the required H.264 playback reference matrix.


## Long local-file playback baseline

The exact-source reference workflow also runs an actual ControlHost/RuntimeHost/Client
local-file baseline. The existing RuntimeHost Program loop is the only cadence owner;
the observer samples every 250 ms and does not drive media boundaries. The default
asset is ten minutes of 1080p50 H.264 High/yuv420p and stereo 48-kHz AAC. Manual
workflow dispatch can select 60 minutes. The existing 25/50/59.94 six-second matrix
continues independently.

Long files encode repetitions of the existing deterministic six-second encoded signal
with pinned test-only FFmpeg 7.1.1, explicit 50-fps CFR and audio sample normalization. Container timestamps advance; the visible six-second
counter intentionally repeats. This is long-container/decode/host execution coverage,
not ten minutes of unique source material or byte-identical encoding across builds.
SHA-256 is streamed. Raw intermediates remain six seconds, final files are bounded
to 2 GiB, and only JSON reports/manifests are uploaded. MP4 files are transient.

To reproduce after the pinned canonical Release build:

```powershell
$env:RTAIME_REFERENCE_MEDIA_REGRESSION = "1"
$env:RTAIME_REFERENCE_MEDIA_OUTPUT = "$PWD/artifacts/testmedia/local-playback"
$env:RTAIME_LOCAL_PLAYBACK_MINUTES = "10" # explicit opt-in: "60"
$env:RTAIME_SOURCE_COMMIT = "<exact tested source SHA>"
$env:RTAIME_BASELINE_SDK = "10.0.401"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~ReferenceMediaPlaybackBaselineTests" --logger "console;verbosity=detailed"
```

The baseline requires actual decoded frames through the last media timestamp and
committed Program boundaries. Its completion limit is twice the media duration plus
two minutes, so a source that advances below real time is measured rather than silently
called 50-fps stable. Host startup is bounded to 30 seconds; both hosts must stop,
join and release Runtime resources. The decoder worker uses asynchronous MF callbacks and generation cancellation with
acknowledged flush. The observer does not call ReadSample or drive playback cadence;
workflow termination remains an independent last-resort boundary for a native hang.

### Evidence schema and interpretation

`local-playback-baseline.json` schema 1.0 records exact source/SDK, generated asset
manifest/hash/size, requested duration, elapsed wall time, OS/architecture, actual
transport outcome, decoded video/audio counts, native video/audio ReadSample CPU wall
totals/maxima, decode/seek/open/first-frame observations, retries/failures, timestamp
regressions, first/last video timestamp and timebase, maximum interval, forward gaps
against the declared normalized frame interval, audio gaps and maximum A/V distance. Seek resets the continuity
comparison without clearing cumulative findings.

The existing bounded Runtime pipeline probe supplies admission, scheduler start and
presentation lateness, render/output stages and its retained/dropped sample accounting.
These percentiles describe its retained ring, not an unlimited whole-run history.
Lifetime local-read totals/maxima use value types with no per-frame logging/history.

At most 128 minute samples retain process private/managed/heap/fragmented bytes,
generation collection counts, cumulative allocations, handles and process I/O counters.
Post-two-minute private growth is bounded to 512 MiB and handles to 64; these hosted
regression ceilings are not a physical production memory SLA. Process I/O is aggregate
and includes journals and IPC; it is not source-file byte attribution or SSD device
latency. No forced GC or cache flush establishes a false plateau/cold-cache result.

`collectionStatus` PASS means the actual reproduction and cleanup assertions passed.
`audioStatus` independently reports FAIL for missing audio, discontinuities or A/V
distance above 40 ms. Normal full-playback acceptance now requires this audio check to pass. Such findings
are never production readiness, and failed reports remain retained. Physical GPU,
installed MFT identity, SSD class, cold-cache/device latency and adverse-storage
behavior remain UNVERIFIED. Run on the designated reference SSD with retained adapter,
driver, decoder and storage inventory before making those claims.

`local-playback-faults.json` distinguishes missing/corrupt/truncated-header,
exclusive sharing denial, and removal before reload at the probe stage. An exclusive
lock is not slow-SSD emulation, and deleting a closed file does not qualify device
disappearance during decoding. The existing short tests retain full-file loops and
Stop/restart/reload; the long harness adds seeks around frame 60's GOP, pause/resume,
Stop and reload after uninterrupted playback.

### Asynchronous admission qualification and next dependency

Program admission now dequeues owned frames from the bounded worker. The Runtime
thread never calls ReadNext/ReadSample, native seek, worker join or file access.
The original synchronous call chain is the comparison baseline from PR272, not the
current architecture. Native decoder work and copy/normalization still incur their
measured cost on the worker; moving them does not make native decoding faster.

AsynchronousLocalMediaDecodeTests instruments the actual Runtime session admission
against a controlled decoder. It proves bounded queue/scratch ownership, cancellation
while a read is blocked, generation-safe rapid seeks/Stop, worker-fault isolation
and early VFR timestamp holds without replaying audio. These are controlled software
faults, not poor-SSD or physical-removal acceptance. Existing real MF 25→50, 50→50,
59.94→59.94, 250-frame cycles, full-file loops, GOP seeks and A/V event checks remain.
Test-side waits synchronize on preroll; production dequeue never waits.

The long JSON additionally records the final bounded decode snapshot (generation,
queue depth/capacity/peak, discarded frames and starvation count), alongside the
existing admission/scheduler timing probe. Compare exact-source admission timing
with PR272; neither hosted run proves physical storage latency or scan-out.
Resource/lifetime optimizations are the next dependency, followed by reference
storage/hardware acceptance. No new codec/container or multi-deck claim is added.

### Audio packet cadence regression

The first ten-minute baseline decoded all 30,000 video frames but observed 1,874
audio-missing frames and a maximum 37,478.6666-ms A/V timestamp distance. Reading one
native AAC packet for every video frame advanced audio at the packet's independent
sample count instead of the video clock.

The existing Media Foundation decoder now retains at most one native PCM packet,
copies only the samples belonging to the current video time window and keeps the
remainder for the next frame. Signed native sample positions discard negative
codec preroll rather than shifting it into time zero; timestamp rounding respects
the audio sample grid. Seek clears packet/EOS state. Genuine gaps return missing
audio, never synthetic silence. Packet scanning has a 1,024-iteration bound; the
native reads use cancellable asynchronous callbacks on the worker.

Real 50/59.94 regressions require contiguous sample positions, matching video/audio
timestamps, no missing audio through the reference tail, fewer native packet reads
than video frames, and valid audio after a GOP-adjacent seek. The repeated ten-minute
host baseline must also pass its audio check. The earlier failing source-bound report
is retained on the PR for comparison; it is not reused as corrected-source PASS.

### Managed allocation diagnosis with continuous Replay enabled

The first asynchronous ten-minute run on source
`2df6447c810dc05a3c8a861fad20858947f84546` decoded all 30,000 frames and 28,800,000
audio samples but FAILED the unchanged 512-MiB post-warmup private-memory guard
(actual growth 545,296,384 bytes). Seven queue underruns and 94 discarded frames
were recorded; decoded-audio continuity alone did not qualify Program output.

Source-bound resource samples showed LOH growth until Gen2 collection. A separate
60-second real-host allocation probe identified repeated approximately 3,110,400-byte
byte arrays. Default continuous Replay creates a new MP4 writer every two seconds;
each writer previously allocated one NV12 conversion array, about 93 MB/minute
of LOH allocation. The writer now pools that scratch and passes only its exact
logical extent to native samples, returning it on finalize, abort and initialization
failure. Replay remains enabled, memory/handle ceilings stay unchanged and no forced
GC conceals the result. Repeated writer regressions include oversized rentals,
independent MP4 playback and storage-failure cleanup.

The optional short allocation observer retains at most 33 sampled type/size buckets,
runs in a separate diagnostic job and does not qualify ten-minute or physical
playback. The normal long acceptance does not enable that observer.

Playback evidence uses a compact summary and a separate complete timing-ring file,
each limited to 256 KiB before writing. The summary binds the ring by literal file
identity and SHA-256; the verifier checks source, SDK, capacity, counts and stage
summaries. Raw timing samples remain available without inflating the primary report.
A synthetic 60-minute-format regression retains all 2,048 timing samples and 61
resource observations, and rejects tampering, missing files, incorrect provenance,
invalid capacity and oversized evidence. It qualifies report handling only, not a
60-minute playback run. The temporary historical-artifact inspection job was removed
after resource diagnosis; current-source acceptance remains mandatory.

## Decoder lifecycle safety regression

LocalMediaDecoderSafetyTests runs in the required and generated-reference workflows.
It exercises the production progress guard (gaps/EOS/flags, cancellation, deadline,
bounded seek scanning), terminal source fencing, pre-allocation PCM limits and the
actual RGB32 copy/swizzle for padded positive/negative pitch. A controlled clock
qualifies deadlines without sleeping thirty seconds. Controlled malformed flags are
not a claim that a physical damaged file or driver has been reproduced.

The real Windows reader test performs 32 open/read/seek/close cycles, excludes eight
warmup cycles, records allocations/read P99 and asserts the existing hosted ceilings
of 512 MiB private growth and 64 handles. It forces no GC. The existing long-container
test remains the actual ControlHost/RuntimeHost pressure qualification, with Replay
enabled, fixed evidence bounds and unchanged limits. Short repeated lifetimes do not
establish an hour-long or physical SSD/driver plateau.

AsynchronousLocalMediaDecodeTests additionally covers concurrent Close, an injected
noncooperative reader, repeat cleanup after late return and deck Error/reopen blocking.
The test deadline is shortened through an internal constructor; production remains
ten seconds. No physical device removal, installed MFT/driver recovery, adversarial
storage latency or 60-minute reference result is inferred from these controlled tests.
