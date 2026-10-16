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

Long files remux repetitions of the existing deterministic six-second encoded signal
with pinned test-only FFmpeg 7.1.1. Container timestamps advance; the visible six-second
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
join and release Runtime resources. Native synchronous ReadSample remains
non-cancelable by this observer; workflow termination is the final hang boundary.

### Evidence schema and interpretation

`local-playback-baseline.json` schema 1.0 records exact source/SDK, generated asset
manifest/hash/size, requested duration, elapsed wall time, OS/architecture, actual
transport outcome, decoded video/audio counts, native video/audio ReadSample CPU wall
totals/maxima, decode/seek/open/first-frame observations, retries/failures, timestamp
regressions, audio gaps and maximum A/V timestamp distance. Seek resets the continuity
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
distance above 40 ms. Such findings are never production readiness. Physical GPU,
installed MFT identity, SSD class, cold-cache/device latency and adverse-storage
behavior remain UNVERIFIED. Run on the designated reference SSD with retained adapter,
driver, decoder and storage inventory before making those claims.

`local-playback-faults.json` distinguishes missing/corrupt/truncated-header,
exclusive sharing denial, and removal before reload at the probe stage. An exclusive
lock is not slow-SSD emulation, and deleting a closed file does not qualify device
disappearance during decoding. The existing short tests retain full-file loops and
Stop/restart/reload; the long harness adds seeks around frame 60's GOP, pause/resume,
Stop and reload after uninterrupted playback.

### Confirmed boundaries and next dependency

Source inspection confirms synchronous decoder open/seek/read work on the Runtime
admission path, including native read loops with no explicit cancellation contract.
This is a measured-baseline prerequisite, not compliance with a no-blocking cadence
target. No second scheduler, decoder backend or expanded codec/container support is
introduced here. Imported/probed MP4 or the wider admitted codec policy is not a
playback-qualified profile. Asynchronous bounded decode/admission, native-read
cancellation and audio packet/cadence reconciliation are the next dependent work,
guided by the retained measurements and failure evidence.
