<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Local input decoder capability inventory

Inventory baseline: `7bd13506272dec071bccda23beb767da44562fac`, reviewed 2026-10-17.
This is an inventory of existing policy and evidence, not a codec support expansion.
See [decoder dependency decision](ProfessionalDecoderDecision.md) before proposing a backend.

## Interpretation and source of truth

A recognized enum or sample entry is not a successful decode. Admission requires
the existing container probe, native subtype match and RGB32/PCM negotiation.
`LocalMediaFileProvider.Descriptor` declares output formats and generic route/decode
resources; Windows availability does not enumerate or certify individual MFTs.
The metadata seam `ILocalMediaProbe` is internal version 1; `ILocalMediaDecoder`
owns read/seek/dispose. Public `LocalMediaProbe` reports normalized formats, not
an exhaustive source profile, selected MFT identity or professional capability.

Source: [provider](../src/Media/rtaime.Media/LocalMediaFileSource.cs),
[container reader](../src/Media/rtaime.Media/IsoBaseMediaMetadataReader.cs),
[AVC sequence checks](../src/Media/rtaime.Media/H264LocalMediaSequenceMetadata.cs),
[input envelope](../src/Media/rtaime.Media/LocalMediaInputPolicy.cs),
[native decoder](../src/Media/rtaime.Media/WindowsMediaFoundationLocalMediaDecoder.cs).
New declarations must follow an approved slice; this inventory adds no contract.

## Container, video and native negotiation matrix

All admitted rows remain conditional on the exact asset and Windows installation.
The output is always existing 1920x1080 progressive RGBA8 at 50/1 or 60000/1001.
Ten-bit input is not ten-bit Program output. Audio rules are in the next table.

| Container / required brand | Sample entry | Codec/profile checked by source | Source chroma / depth checked | Native availability and evidence |
| --- | --- | --- | --- | --- |
| MP4 / admitted MP4 brand | avc1, avc3 | H.264 profile IDs 66, 77, 88, 100; each SPS geometry/early VUI checked | 4:2:0 / 8; High extension checked when present | MF codec must match; real generated Baseline and High coverage, not every entry/profile tuple |
| QuickTime MOV / qt | avc1, avc3 | Same H.264 checks | Same 4:2:0 / 8 | Same provider; generated MOV High with AAC and PCM; avc3/Extended and all rates not individually qualified |
| MP4 | hvc1, hev1 | HEVC profile IDs 1, 2 (Main/Main10) in hvcC | 4:2:0; equal luma/chroma depth 8 or 10; Main requires 8 | Host-dependent transform; generated Main/Main10 may report UNAVAILABLE; no pinned decoder |
| MP4 | av01 | AV1 recognized; no codec-specific profile parser | Not checked by a codec-specific parser | Native subtype/normalization required; real profile matrix UNVERIFIED |
| MP4 | vp09 | VP9 recognized; no codec-specific profile parser | Not checked by a codec-specific parser | Same conditional negotiation; real profile matrix UNVERIFIED |
| MP4 | mp4v | MPEG-4 Part 2 recognized; no codec-specific profile parser | Not checked by a codec-specific parser | Same conditional negotiation; real profile matrix UNVERIFIED |
| MP4 | vc-1 | VC-1 recognized; no codec-specific profile parser | Not checked by a codec-specific parser | Same conditional negotiation; real profile matrix UNVERIFIED |
| MP4 | jpeg | Motion JPEG recognized; no codec-specific profile parser | Not checked by a codec-specific parser | Same conditional negotiation; real profile matrix UNVERIFIED |
| MOV | hvc1, hev1, av01, vp09, mp4v, vc-1, jpeg | Rejected, even if a matching Windows decoder exists | No admitted tuple | media.file.video_codec_unsupported before native open |
| MP4 or MOV | apco, apcs, apcn, apch, ap4h, ap4x | ProRes not admitted | No admitted profile, chroma, alpha or depth | media.file.video_codec_unsupported before native open |
| MP4 or MOV | AVdn, AVdh | DNxHD/DNxHR not admitted | No admitted profile, chroma or depth | media.file.video_codec_unsupported before native open |
| MP4 or MOV | Other unknown entries, including encrypted encv | Rejected | None | media.file.video_codec_unsupported before native open |
| MXF (.mxf) or any other extension | Any essence | No local input probe | None | media.file.container_unsupported for an existing path; MXF recording proves no input capability |

The exact MP4 brand set is isom, iso2..iso9, mp41, mp42, avc1, M4V-space
and MSNV. Any qt brand selects QuickTime identity, which must agree with the
extension-selected probe. An ftyp must precede moov. No extension-only acceptance,
legacy brandless MOV or fallback decoder exists.

General limits are 48x48..4096x2304, positive rational input rate <=240 fps,
and <=300,000,000 bit/s when native average bitrate is reported. H.264 also
has <=983,040 macroblocks/s (Level 5.1 envelope); the guard is not a promise
that every signaled H.264 level or throughput is qualified. HEVC hvcC fields
are checked; full HEVC sequence/VUI validation is not implemented.

## Audio compatibility matrix

| Container | Source audio description | Existing behavior | Evidence limit |
| --- | --- | --- | --- |
| MP4 | No first supported audio stream | Video-only / NO AUDIO | Existing compatibility; not proof that requested audio was decoded |
| MP4 | Any audio sample entry in container | No sample-entry allowlist or container/native audio cross-check | Native AAC, MP3 or PCM maps to supported audio; unknown subtype maps to None |
| MP4 | Native AAC, MP3 or PCM | Requests PCM16 stereo 48 kHz, converts to Float32 | AAC real reference; other codec/layout tuples not exhaustively exercised |
| MOV | No audio track | Video-only allowed | Cannot satisfy a required-audio request |
| MOV | mp4a version 0 | AAC must match native AAC | Generated AAC evidence; arbitrary AAC profiles/channel maps not separately certified |
| MOV | mp4a version 1 | Requires 1024 samples/packet, 0 bytes/packet, 0 bytes/frame, 2 bytes/sample; native AAC match | Existing qualified packet metadata only |
| MOV | sowt version 0 | PCM16, 2 channels, 48 kHz; native PCM match | Generated PCM evidence |
| MOV | Other entries (including twos, lpcm, alac, ac-3), other versions/layouts | Rejected before native open | audio_codec_unsupported or audio_layout_unsupported |

MP4's historical unsupported-audio-to-video-only behavior is not suitable as
professional required-audio proof. It is preserved here for compatibility.
A future approved professional slice must declare expected audio and reject a
missing/mismatched track; it must not inherit that fallback silently. Explicit
[asset preparation](LocalMediaFileSource.md#non-program-asset-preflight) can require
actual decoded audio; a conditional observation does not change playback admission.

## Timing, color and hostile metadata boundaries

- One video track, at most one audio track, exactly one sample description per
  inspected track and one self-contained data reference. At most 16 tracks,
  32 MiB moov, 4096 boxes per sibling/top-level scan, 65536 timing runs/table.
- Positive stts runs produce a rational average; multiple runs mark VFR.
  Native timestamps remain the playback timing source. ctts v0/v1 decode and
  composition runs are merged without per-frame expansion; cslg is rejected.
- An absent edit list or one nonempty, nonnegative, unit-rate edit within
  movie/media presentation extent is accepted. Other edit programs are rejected.
  Existing generated GOP seeks do not prove every B-frame/edit combination.
- Video track matrix must be identity. Square pixels only, no clap cropping.
  nclc/nclx primaries/matrix 1,2,5,6 and transfer 1,2,6 are allowed;
  ICC/other color types are rejected. Missing color is unspecified.
  AVC early VUI is checked; native transfer >=9, non-square pixels and rotation
  are rejected. This is not end-to-end colorimetry or HDR qualification.
- Source field order/interlace is not exposed as a qualified input capability.
  RGB32 output is explicitly progressive. Timecode tracks are not imported as
  a timecode contract; generic track parsing is not a validated timecode path.
  These are open professional qualification requirements.
- Malformed/truncated metadata uses probe_failed; dedicated policy failures retain
  their media.file.* codes. Wrong native video codec gives codec_mismatch;
  0xC00D5212 during video negotiation gives decoder_unavailable, other native
  negotiation failures normalization_failed. Failures elsewhere in native open
  may instead return probe_failed. Do not normalize every failure to “missing codec.”

## Evidence and acceptance procedure

Existing software tests: LocalMediaContainerProbeTests, LocalMediaInputPolicyTests,
LocalMediaDecoderSafetyTests, AsynchronousLocalMediaDecodeTests,
ReferenceMediaRegressionTests and MultiDeckLocalMediaIntegrationTests.
The generated reference workflow retains asset SHA-256, encoder identity,
probe metadata, source SHA and SOFTWARE_DECODED or UNAVAILABLE. Returned test
success with an UNAVAILABLE HEVC row is negative evidence, not decoded support.
Decoder binary identity and physical qualification in format outcomes remain
UNVERIFIED; asset preflight's bounded OS-module fingerprint is not selected-MFT proof.

For an approved new tuple require: exact container/sample entry/profile/chroma/depth/
scan/color/audio/track layout; pinned build and malformed/truncated fixtures;
independent metadata/content checks; real open, preroll, GOP/edit/VFR seeks, loop,
audio continuity, close/reopen, cancellation, decoder loss and quarantined cleanup;
two-deck budget and Program continuity. Preserve source timestamps and generation
fences. Do not run disk I/O, decode, waits or joins in Program admission.

Use the repository-pinned SDK and canonical Release build, targeted container
tests first, then full solution tests and all five Required Gates. Run the existing
reference-media workflow at the final source; do not substitute enum/policy tests
for decode. Physical acceptance additionally needs the exact Windows build,
selected MFT/binary hashes, driver/GPU/SSD identity, A/B profiles, source/output
latency and A/V evidence, resource deltas, sustained load and device/storage faults.
SATA/NVMe throughput, 60-minute/8-hour endurance and physical acceptance remain
UNVERIFIED until actual matching evidence exists.
