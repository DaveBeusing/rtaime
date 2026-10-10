# MXF OP1a Uncompressed/PCM Recording Qualification

## Capability gate

The profile `mxf-op1a-uncompressed-pcm` is **NOT IMPLEMENTED** and must not be advertised, made available in the confirmed Runtime profile catalog, or selected in Operator until all mandatory writer/probe qualification below is satisfied. No generic MXF claim is permitted.

## Locked scope

- Operational pattern: MXF OP1a, one Material Package and one Source Package, one video track and one stereo audio track.
- Video input: committed Program RGBA8 1920×1080 progressive at 50/1 and 60000/1001.
- Video essence: uncompressed 8-bit YUV 4:2:2, with packing, component ordering, descriptor fields, coding UL and GC element key verified against the selected normative mapping before code is merged.
- Audio essence: 48000/1 Hz, two channels, signed 16-bit PCM mapped to an appropriate MXF Generic Container audio element.
- Execution: the existing asynchronous Runtime-owned Program recorder worker, not Program's media hot path.
- Output: `<name>.partial.mxf` until close/finalization, independent verification and atomic publication to `.mxf`.
- No native or third-party dependencies without a separate licensing/security/packaging decision.

## Normative implementation references

1. SMPTE ST 377-1 — MXF file format, structural metadata and partition rules.
2. SMPTE ST 378:2004 — OP1a (Single Item, Single Package), operational pattern and package topology.
3. SMPTE ST 379-2 — MXF Generic Container essence item/element mapping.
4. SMPTE ST 384 — mapping uncompressed pictures into the MXF Generic Container. Confirm the exact 8-bit 4:2:2 byte packing, essence container UL and picture descriptor fields from the controlled standard before selecting constants.
5. SMPTE ST 382:2023 — AES3/Broadcast Wave audio mapping into the MXF Generic Container; select and document the exact valid PCM16 mapping/UL, not an invented audio key.
6. SMPTE ST 330 — unique material identifiers, as applicable.

Normative references above identify the documents to validate; they do **not** assert that a particular unverified UL or pixel arrangement is compliant. Copy no unverified ULs from examples.

## Required implementation evidence before profile activation

- Writer must construct consistent header partition, metadata primer/local sets, Preface/ContentStorage/Package/Track/Sequence/SourceClip, sound/picture descriptors, essence container references, body essence KLV, footer partition, index table and random index pack as required by the selected mapping.
- All BER-encoded KLV lengths, offsets and partition byte counts are bounded, checked and independently parseable. Unknown/unsupported formats fail closed.
- Exact rational edit-rate and audio cadence are preserved: 960 PCM sample-frames per picture at 50 fps; at 60000/1001 the 48 kHz audio boundary must use rational accumulation rather than a rounded constant per video frame.
- Convert RGBA8 to the selected YUV422 packing and Float32 audio to PCM16 on the recording worker only; use final Program audio, never a separate mixer.
- Finalization writes and flushes complete header/index/footer metadata; standalone `MxfOp1aProbe` independently reads the final bytes without reusing writer serialization methods.
- Probe checks OP1a package topology, one picture and one audio track, exact ULs/descriptors, frame rates, 48 kHz stereo PCM16, monotonic essence positions, frame and sample counts, aligned durations, index/partition consistency, and rejects truncated/malformed/partial files.
- Writer performs independent probe before publication; failure preserves explicit incomplete/failure semantics and must never publish a `.mxf` final artifact.
- Catalog availability is enabled only with a registered provider and passing targeted unit, integration, failure, performance and architecture qualifications. Operator selection remains exclusively Runtime-confirmed.
- Qualify both 1080p50 and 1080p59.94; software testing is not a claim of sustained physical storage throughput or certified third-party interchange.

## Scope limits

Do not claim generic MXF, OP-Atom, AS-11/DPP, XDCAM, AVC-Intra, ProRes, DNx, JPEG XS, multichannel audio, ancillary data or hardware acceleration. No commercial interoperability claim without explicit independent evidence.

## Status

Specification / qualification boundary only. The writer, independent parser, operational profile registry entry and tests remain outstanding. The shipping MP4 and MOV profiles must not be affected.
