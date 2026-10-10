# MXF OP1a Uncompressed/PCM Recording Qualification

## Implementation and activation

The constrained profile `mxf-op1a-uncompressed-pcm` now has an executable writer backed by the **BBC BMX v1.6** `raw2bmx` OP1a muxer and a separately configured **FFprobe** inspection executable. There is **no automatic Runtime download, plugin discovery, or bundled BMX/FFprobe executable**. The profile stays unavailable by default.

RuntimeHost activates the provider only when **all three** variables are configured and both executable files exist:

```text
RTAIME_MXF_OP1A_ENABLED=1
RTAIME_MXF_RAW2BMX=C:\path\to\raw2bmx.exe
RTAIME_MXF_FFPROBE=C:\path\to\ffprobe.exe
```

The default `mp4-h264-aac` recording profile remains unchanged. The Operator receives the MXF entry and its enabled/disabled state from the Runtime-confirmed provider-neutral catalog and never starts a missing writer.

## Locked supported scope

- Container: MXF OP1a, one progressive picture stream plus one two-channel sound stream.
- Program input: RGBA8 1920×1080 progressive at 50/1 or 60000/1001 fps, committed stereo 48 kHz Float32 Program audio.
- Output video: 8-bit packed UYVY 4:2:2, software-converted using the established MOV conversion path.
- Output audio: signed little-endian PCM16, 48 kHz stereo, using the established MP4 conversion path.
- Conversion and file I/O occur on the Runtime-owned asynchronous Program Recorder worker. Program execution has no encoding/file-I/O work.
- The writer spools bounded per-session raw video/audio essence under a private session directory. On finalize, BMX constructs the OP1a metadata, material/source package topology, track/descriptor graph, essence KLV, indexes, footer and random index pack.
- All final output is partial-first: `<name>.partial.mxf` is generated and independently validated before no-overwrite atomic publication to `<name>.mxf`. Target paths use `.lock` reservation, and session work files are cleaned after finalize or abort.
- A failed/prohibited format, wrong Program frame size, discontinuous sequence/audio positions, quota exhaustion, missing tools or failed independent probe cannot publish a final `.mxf`.

## Actual validation

`MxfOp1aStructureProbe` independently reads the file's SMPTE KLV and MXF partition/primer structures, validates OP1a operational pattern, header/body/footer/index consistency, previous-partition chains and RIP offsets. It handles legal fixed-width BER lengths.

`MxfIndependentMediaProbe` invokes the separately provisioned FFprobe binary and requires:

- MXF container, exactly one video and one audio stream;
- 1920×1080 uncompressed YUV422 video, exact 50 or 60000/1001 edit rate;
- exact number of independently decoded frames;
- signed stereo PCM16 audio at 48000 Hz;
- independently reported audio stream duration/sample-frame count equal to the written payload.

At fractional rate, the writer preserves rational PCM accumulation and may add **at most one** terminal stereo PCM silence sample frame to cover the final fractional video boundary; no already committed audio is trimmed or resampled.

Real Windows GitHub Actions qualification uses `.github/workflows/mxf-op1a-qualification.yml` and the MXF job in `reference-media-regression.yml`. It installs FFprobe 7.1.1, downloads BBC BMX v1.6 directly from the official release URL, and verifies its archive SHA256 against:

```text
e36096e8a02a5a767e27a4c9794b03f11dbd7af3ccd9679104489b89dca4f9ce
```

The targeted `BmxOp1aRecordingIntegrationTests` exercise genuine 1080p50 and 1080p60000/1001 files, read back and verified by both probes. **GitHub Actions MXF OP1a qualification run 38042667329 completed successfully for both rates on commit 5f0eebba.** Later changes require requalification on their exact SHA.

## Format and dependency boundary

The BMX muxer owns SMPTE MXF serialization; rtaime does not invent undocumented ST 384/ST 382 UL constants. Structural/essence validation is independent from muxer serialization.

Normative context: SMPTE ST 377-1, ST 378, ST 379-2, ST 384, ST 382 and ST 330. Independent FFprobe verification is **software interoperability evidence**, not certification of every SMPTE metadata field by a separately authored semantic MXF decoder.

BBC BMX is a third-party native executable (BSD-3-Clause upstream; confirm exact artifact license and runtime redistributable terms before bundling). FFprobe's redistribution obligations depend on the actual build and dependency license selection. Both remain **user-provisioned**; neither executable is included in rtaime packaging by this change. Pinning SHA256 protects reproducibility of the qualified CI archive but is not a vendor digital signature.

## Remaining qualification limits

No claim of generic MXF, OP-Atom, AS-11, XDCAM, AVC-Intra, ProRes, DNx, JPEG XS, multichannel audio, ancillary data, hardware acceleration or proprietary broadcast interchange certification. Short software recordings do **not** prove sustained physical storage throughput, long-duration capture, system failure recovery during muxing or third-party NLE/vendor compatibility. The external process increases finalization latency and temp-disk space needs. Native package inclusion requires a separate licensing/security/SBOM and redistribution review.

The evidence state is `Implemented` when the backend is explicitly provisioned, **not** `Qualified` or a general hardware interchange claim. Runtime remains authoritative for configured availability; the default catalog and reference manifest remain fail-closed.
