<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Broadcast file interchange profiles and MXF evidence

## Scope and authority

This inventory is a **provider-bound, bounded interchange profile projection**; it is not a generic MXF/IMF decoder, a compliance certificate or a recording support override.

Source of truth: \`RecordingProfileCatalog.DescribeFileInterchange()\`, which calls \`BroadcastFileInterchangeProfiles.Snapshot()\` on the *actual registered* Runtime writer profiles. Existing \`RecordingProfileId\`, \`RecordingWriterProviderRegistry.Resolve\`, \`ProgramRecorder\`, ControlHost production authority, prepared execution, A/B decoder and Program cadence are unchanged. The inventory adds no recording provider and no new Native/Codec SDK.

Edition: **EBU R 154 v2.0, June 2023** (UHD/HDR programme file interchange). References to EBU Tech 3293 **EBUCore v1.10** and EBU R 123 are metadata/audio-order guidance, not proof of EBU/IMF/MXF conformance.

## Exactly implemented output tuples (not R 154)

The following two Program rates correspond to the existing explicitly admitted Program source formats. Every output has a single picture stream and one stereo stream. Existing writer admission remains the only authority; the table does not enable an unprovisioned provider.

| Format and profile ID | Video output | Rate | Audio output | Recording status | Local source import |
| --- | --- | --- | --- | --- | --- |
| \`mp4-h264-aac\` | MP4, H.264/AVC Main, 1920×1080, 8-bit 4:2:0 | 50/1; 60000/1001 | 48 kHz AAC-LC stereo, sample depth not externally asserted | Source Runtime catalog; Windows Media Foundation dependency | CONDITIONAL_PROBE per asset; native MF codec negotiation still required |
| \`mov-2vuy-pcm\` | QuickTime MOV, uncompressed 2vuy, 1920×1080, 8-bit 4:2:2, signalled BT.709 SDR | 50/1; 60000/1001 | 48 kHz PCM S16LE stereo (\`sowt\`) | Source Runtime catalog; managed writer | NOT_SUPPORTED for 2vuy MOV. The decoder's restricted MOV/H.264 input is a different tuple |
| \`mxf-op1a-uncompressed-pcm\` | MXF OP1a, uncompressed UYVY 4:2:2, 1920×1080, 8 bit; transfer UNVERIFIED | 50/1; 60000/1001 | One MXF sound track, 2 PCM16 stereo channels at 48 kHz; ordering UNVERIFIED | UNAVAILABLE by default; IMPLEMENTED when explicitly provisioned BMX/FFprobe available and enabled | NOT_SUPPORTED: no MXF decoder or Media Library admission |

\`Implemented\` means an executable provider/known tuple, not that every host or file has passed a software/hardware qualification. \`SoftwareQualified\` comes only from a qualifying catalog evidence state. \`IndependentReopenRequired\` is true for every existing output, while \`InterchangeFileQualified\` remains false in a *catalogue row* because it does not represent an actual file.

MXF recording enablement is unchanged:

\`\`\`text
RTAIME_MXF_OP1A_ENABLED=1
RTAIME_MXF_RAW2BMX=C:\path\to\raw2bmx.exe
RTAIME_MXF_FFPROBE=C:\path\to\ffprobe.exe
\`\`\`

BBC BMX is not downloaded or bundled. FFprobe must be independently supplied; its binary build/license, hash and patch status remain deployment responsibilities. The existing \`mxf-op1a-uncompressed-pcm\` independent structural and FFprobe checks run before atomic publication.

## Explicit EBU R 154 v2.0 baseline — NOT_SUPPORTED

These are **desired broadcast exchange tuples**, not production capabilities, and are never inserted as executable recording-provider entries:

| Guidance target | Exact characteristic | Missing rtaime evidence |
| --- | --- | --- |
| UHD/HDR picture | 3840×2160 progressive; 25/1, 50/1 or 60000/1001; 10-bit 4:2:2; ITU-R BT.2100 HLG, BT.2020 primaries | No admitted HDR/10-bit UHD recording and no independently qualified conversion |
| XAVC MXF OP1a | XAVC 4K Intra Class 300, SMPTE RDD 32, 25/50/60000/1001 | No licensed/qualified XAVC encoder or MXF ingest decoder |
| Conventional audio (Option A) | 48 kHz PCM, 24-bit; minimum eight **separate mono MXF audio tracks** with EBU R 123:8a allocation | Current writer produces **one two-channel 16-bit audio stream**; no order, language or channel-role metadata carriage |
| Metadata-guided audio (Option B) | S-ADM/ADM and prescribed track carriage / mapping | No admitted MXF ADM/S-ADM, multi-track or 16-channel production template |
| IMF Application #6 | IMF package + CPL/AssetMap/PKL, AVC essence, virtual tracks | No IMF package resolver, sequencer, encoder or importer |
| IMF Application DPP | IMF package + CPL/AssetMap/PKL, ProRes essence | No admitted ProRes encoder/decoder or IMF DPP package implementation |
| DNxHD/DNxHR | Not a qualified rtaime file I/O tuple; not an R 154 baseline encoder specified above | No encoder/decoder admission or approved license. Unknown sample entries are rejected |
| Subtitle/timecode metadata | EBU-TT-D / IMSC where applicable; explicit source/edit-rate and Timecode semantics | Separate timed-text work does not imply MXF/IMF carriage; no source timecode preservation or track-order serialization |

The current 1080p/8-bit/PCM16 MXF profile has **no** implied R 154 conformance even when a generated file passes its existing independent probes. 1080p sourced material can be used under R 154's specifically signalled exceptional cases, but the present writer does not provide the required UHD/HLG/10-bit and audio delivery attributes.

## MXF header and essence evidence path

1. The existing \`MxfOp1aStructureProbe\` scans the actual KLV file with a finite element bound, confirms OP1a partition/header/footer/index/RIP topology and rejects inconsistent structural lengths/offsets.
2. The separately configured \`MxfIndependentMediaProbe.InspectValidatedAsync\` runs FFprobe with an explicit path and ArgumentList (no shell), a **two-minute** total budget, bounded 256-Ki-character output/16-Ki-character error capture and 20-level JSON depth. Cancellation/timeout or bounded-output overflow aborts the probe; no invalid file is published.
3. The independent probe checks exactly one video and one stereo audio stream; raw UYVY/8-bit 1920×1080 progressive video at exact 50/1 or 60000/1001, decoded frame count, exact PCM16 48-kHz stereo sample duration, and rejects contrary reported field order, bit-depth or non-stereo layout.
4. The resulting \`MxfValidatedInterchangeEvidence\` preserves **observed** video and audio timebases and optional FFprobe colour, language and timecode tags. It does not synthesize HDR metadata, native audio roles, rights or original source timecode. Source timecode, even if FFprobe reports a label, is *not* claimed to survive recording.
5. The caller independently hashes the completed, stable MXF file with SHA-256. Only then can its verified observation be projected into the existing bounded \`rtaime.media-interchange.ebucore-1.10.v1\` JSON profile. Validation enforces exact rational rate, UTC evidence and DF/NDF timecode. Any unknown audio language remains visible in \`UNMAPPED\`, not invented as a language.
6. An actual MXF recording qualification exercises **record → partial validation → publish → FFprobe reopen → SHA-256-bound metadata projection**. This does *not* become Media Library import or native Playback; \`.mxf\` remains explicitly rejected with existing failure code \`media.file.container_unsupported\` and a specific \`NOT_SUPPORTED\` explanation.

The metadata projection does not write EBUCore XML into MXF header metadata, add language tracks, change the immutable Media Library asset identity or automatically persist an unbound sidecar. Typed sample/frame totals and exact source byte/timebase observations stay associated with the independent file validation; only the bounded, representable mapping is serialized. Roundtrip of original source language/timecode, alpha, rights, captions, audio ordering and UHD colour remains **UNVERIFIED or UNSUPPORTED**.

## Compatibility, performance and security

No new serialization fields are added to existing Control/Runtime/Media IPC, no SQLite schema migration, no new codec, recording writer, audio mapping or Program callback I/O. This inventory is additive in the Recording assembly; old recording selections and older Media Library assets retain their existing behavior.

The independent FFprobe subprocess is never invoked from Program frame or audio callbacks. Its bounded output and timeout do not extend native decoder admission. External executable versions, redistribution rights and any physical throughput/latency qualification must be independently recorded; no third-party compliance seal is claimed.

## Tests and qualification

Run the canonical \`.NET SDK 10.0.401\` Release build before these tests:

\`\`\`powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter "FullyQualifiedName~BroadcastFileInterchangeProfileTests|FullyQualifiedName~MxfValidatedInterchangeEvidenceTests|FullyQualifiedName~MxfOp1aRecordingCatalogTests|FullyQualifiedName~BmxOp1aRecordingWriterTests"
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~LocalMediaContainerProbeTests|FullyQualifiedName~BmxOp1aRecordingIntegrationTests"
dotnet test rtaime.slnx --configuration Release --no-build -m:1
\`\`\`

The real MXF two-rate integration test requires the existing explicitly provisioned \`RTAIME_MXF_QUALIFICATION=1\`, \`RTAIME_RAW2BMX\` and \`RTAIME_FFPROBE\` environment variables. If absent, it does **not** establish actual MXF file qualification. The dedicated MXF Actions workflow and all five Required Gates must be checked against the *exact* PR head; physical hardware is **UNVERIFIED** until reproducible evidence says otherwise.

## Next dependent slice

An explicit MXF **input** provider requires a licensed, qualified decoder with complete Essence/Track/Timecode/Alpha/Audio-order handling, track mapping, resource admission, bounded workers, source fingerprint and separate Preview/Program paths. R 154 XAVC/IMF additionally requires UHD/HDR/10-bit and 24-bit multitrack audio evidence. These are new vendor/provider admission decisions, not a feature-flag change or repurposing of the writer.

## References

- [EBU R 154 v2.0 (June 2023)](https://tech.ebu.ch/publications/r154)
- [EBU Tech 3293 v1.10](https://tech.ebu.ch/publications/tech3293)
- [EBU R 123](https://tech.ebu.ch/publications/r123)
- [Existing MXF recording qualification](MxfOp1aRecordingQualification.md)
- [Recording Profile Catalog](RecordingProfileCatalog.md)
- [Media Interchange Metadata](MediaInterchangeMetadata.md)
- [Local Media Decoder Capabilities](LocalMediaDecoderCapabilities.md)
