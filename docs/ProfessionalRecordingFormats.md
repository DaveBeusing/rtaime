<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Professional Recording Formats

## Decision

The first recording-format expansion beyond MP4 uses a managed QuickTime/MOV writer implemented inside the Recording provider boundary.

The selected production profile is:

| Field | Value |
| --- | --- |
| Profile | `mov-2vuy-pcm` |
| Provider | `managed-quicktime` |
| Container | QuickTime Movie (MOV) |
| Extension | `.mov` |
| Video essence | Uncompressed 8-bit YUV 4:2:2, QuickTime `2vuy` sample entry |
| Audio essence | Signed 16-bit little-endian PCM, QuickTime `sowt` sample entry |
| Program video input | 1920x1080 progressive RGBA8 at 50 fps or 60000/1001 fps |
| Program audio input | Stereo 48 kHz Float32 |
| Acceleration | Software |
| Finalization | `<name>.partial.mov` is promoted atomically to `<name>.mov` only after a complete `moov` atom is written and independently probed |

RGBA-to-2vuy conversion and Float32-to-PCM16 conversion execute on the asynchronous Recording writer path. The video conversion is the bounded BT.709 limited-range mapping declared by the MOV `nclc` 1/1/1 color atom. Program execution does not perform file I/O or recording-specific pixel conversion.

## Dependency and licensing assessment

No new native library, executable, NuGet package, codec SDK or vendor runtime is introduced.

This avoids the redistribution, binary provenance, security-update lifecycle and runtime discovery concerns that would accompany a bundled FFmpeg/libav build or a vendor codec SDK. The writer emits the MOV container and uncompressed/PCM sample payloads directly from managed code. Release packaging therefore does not gain an external runtime binary, and the existing package/SBOM mechanisms continue to describe the shipped managed assemblies without an added third-party media dependency.

The implementation does not claim ProRes, DNxHR, AVC-Intra or another proprietary codec. It does not invoke user-installed encoders or expose command strings.

## Why MOV first

MOV provides a concrete professional interchange container while allowing the first provider to use codec-independent, legally simple uncompressed video and PCM audio. The resulting files are intentionally high bandwidth; this profile is a software interoperability capability, not evidence for sustained physical-disk throughput.

The profile is useful for:

- deterministic container and timestamp validation;
- lossless recording-path qualification;
- downstream ingest/interchange checks;
- provider/catalog integration without a proprietary codec dependency.

## MXF boundary

MXF is not advertised by this implementation.

A production MXF profile requires an explicit operational pattern and essence mapping, including KLV partition/index behavior, timecode/edit-rate semantics and independent conformance evidence. OP1a or a named professional codec must not be claimed until that implementation exists and is validated independently.

## Validation boundary

A dedicated MOV probe is independent from the writer and validates the finalized file structure and sample tables. It verifies:

- QuickTime/MOV file type;
- `2vuy` video sample entry;
- `sowt` audio sample entry;
- resolution;
- frame rate/edit rate;
- 48 kHz stereo PCM16 audio;
- sample counts, fixed essence sizes and durations;
- progressive field metadata and BT.709 `nclc` 1/1/1 color metadata;
- monotonic chunk/sample layout;
- A/V start offsets and duration alignment, including 60000/1001 edit-list offsets;
- complete finalization with no partial artifact published as valid.

This repository qualification is software evidence. Sustained storage throughput, long-duration capture and physical hardware behavior remain separate qualification obligations.
