<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# SRT live transport and encoder qualification

## Transport contract

The SRT provider uses caller mode, live transmission and the Message API with a
1316-byte payload limit. Native `srt_send` accepts one complete message per call.
The provider packetizes the Media Foundation MPEG-TS output in its existing
network worker, independently of the Program cadence thread.

Each message contains between one and seven complete 188-byte transport packets.
The packetizer preserves byte order across encoder drains and retains at most
187 trailing bytes until the next drain completes their packet. An empty drain
is permitted while the encoder buffers media. It does not trigger a reconnect
or manufacture a successful send. Packet starts must contain the TS sync byte;
invalid alignment faults the output session instead of silently resynchronizing.

The native boundary rejects oversized or unaligned messages before invoking
SRT. The Message API's all-or-error result is checked for every message. A
positive short result is treated as a transport failure; the uncertain message
is never retransmitted by the provider. Successful earlier messages retain
their byte and packet accounting even when a later message in the same encoder
drain fails. Connection teardown clears residual packet bytes before a new
encoder/connection epoch starts.

The mux `IStream.Stat` advertises `STGM_READWRITE`; leaving the access mode at its
zero default incorrectly describes a read-only stream. After the native MF
wrapper is created, the encoder reads back `IMFByteStream.GetCapabilities` and
requires readable and writable access before creating the sink writer. Factory
failures retain the operation, HRESULT and actual native capability mask, so a
stream-access failure can be distinguished from a codec or container failure.
The [`STATSTG` contract](https://learn.microsoft.com/en-us/windows/win32/api/objidl/ns-objidl-statstg)
defines the stream access mode. The previous URL-based factory,
[`MFCreateSinkWriterFromURL`](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-mfcreatesinkwriterfromurl),
also required a writable byte stream.

The mux is selected explicitly with `MFCreateMuxSink` and
`MFStreamFormat_MPEG2Transport`. Its MFT output attributes set
`MF_MT_MPEG2_TIMECODE=0` and `MF_MT_MPEG2_CONTENT_PACKET=0`, requesting transport
packets without additional timecode or Content Packet headers. The generic
`MFTranscodeContainerType_MPEG2` selection produced MPEG Program Stream in the
[native run](https://github.com/DaveBeusing/rtaime/actions/runs/40136536356) at
`396e25f45f635435d7714540602f11bb7aab7618`: the first nonempty drain
was 1,645,057 bytes and began with pack/system/program-map/PES start codes
`000001BA`, `000001BB`, `000001BC` and `000001E0`. That output is not accepted as
188-byte transport packets. No bytes are stripped or resynchronized to disguise
the format mismatch. The explicit mux still requires a successful native run for
both formats before qualification can be claimed.

The [mux factory](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-mfcreatemuxsink)
accepts the MFT output subtype and attributes. Microsoft documents the
[timecode](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mt-mpeg2-timecode)
and [Content Packet](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-mt-mpeg2-content-packet)
zero values. The encoder owns the resulting media sink, releases the sink writer
before calling the sink's `Shutdown`, and attempts all remaining cleanup even if
a stage fails. Initialization errors retain secondary cleanup errors, and normal
disposal reports finalization or shutdown failure. This follows the ownership
contract of
[`MFCreateSinkWriterFromMediaSink`](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-mfcreatesinkwriterfrommediasink).

`PacketsSent` counts messages accepted by the native transport, and `BytesSent`
counts their payload bytes. `SentSamples` advances for a processed encoder drain
that emits at least one message and finishes without failure. Codec buffering
means that it is not an independent receiver-decoded frame count.
`LastSuccessfulSendUtc` advances only after actual message acceptance. These
statistics do not claim receipt by a remote peer.

The upstream API contract is documented in Haivision's
[socket options](https://github.com/Haivision/srt/blob/master/docs/API/API-socket-options.md#srto_messageapi)
and [send functions](https://github.com/Haivision/srt/blob/master/docs/API/API-functions.md#srt_send).

## Deterministic regressions

After the canonical pinned-SDK Release build:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj `
    --configuration Release --no-build `
    --filter "FullyQualifiedName~SrtTransportPacketizationTests|FullyQualifiedName~SrtMuxStreamTests|FullyQualifiedName~SrtMediaFoundationCleanupTests"
```

The packetization suite covers large drains at 50 and 60000/1001 fps, splits
before/at/after TS and live-message boundaries, empty encoder drains, exact byte
order, nonfabricated send counters, partial failure accounting, invalid sync and
reconnect isolation. Existing network backpressure/cleanup fixtures emit complete
TS null packets so that they continue to exercise the actual send boundary.

These tests use deterministic transports. Their success alone does not establish
native SRT or Media Foundation interoperability.

## Explicit native loopback profile

Run on Windows x64 with .NET SDK 10.0.401 and a supplied SRT runtime version 1.5.7
or newer. The runtime and its dependencies must already be installed; the
qualification command does not download them.

```powershell
./build/qualification/Invoke-SrtNativeQualification.ps1 `
    -LibraryPath C:/Srt/bin/srt.dll `
    -OutputDirectory artifacts/qualification/srt-native-current
```

Use a clean checkout and a fresh output directory. The command enables the
existing test-suite opt-in pattern, builds the Unit test graph and runs
`SrtNativeLoopbackQualificationTests`. It fails on a missing/incompatible runtime,
encoder or native-call error, test timeout, absent evidence, stale source identity
or mismatched runtime hash. The five existing Required Gates remain unchanged.

The profile processes 90 full 1080p RGBA/Float32 Program samples at each supported
nominal frame rate through the actual Media Foundation H.264/AAC encoder,
production session packetizer and native caller transport. A local receiver uses
the real SRT listening/receive API. For both rates it requires:

- at least one encoder drain larger than 1316 bytes;
- complete TS alignment and bounded messages at the receiver;
- exact byte totals and matching SHA-256 hashes between encoder output and receiver input;
- no dropped samples or reconnects during the qualification;
- successful native/encoder teardown before writing PASS evidence.

The 90 samples are submitted inputs, not a receiver-decoded or end-of-stream
complete frame count. Byte totals and hashes cover only bytes actually returned
by `EncodeAsync` during live submission. The current stop path calls the sink
writer's `Flush` and discards encoder tail output; complete graceful final drain
is a separate unresolved issue. This profile does not establish delivery of
encoder-delayed terminal samples, even when finalization and shutdown succeed.

The retained JSON files bind source commit, actual native runtime version and DLL
SHA-256 to each format's encoder/message statistics and payload hash. The TRX
captures the test result. Ordinary CI leaves the opt-in profile disabled and
prints **UNVERIFIED**; a successful ordinary test run is not native loopback PASS.
Only the explicit command's complete source/runtime-bound evidence establishes
that narrow result.

External receiver decoding, WAN behavior, encryption, physical network latency
and long-duration hardware qualification remain separate evidence obligations.
No provider capability or release-readiness status is promoted by adding this
profile.

## Automated Windows loopback

`.github/workflows/srt-native-qualification.yml` runs the same explicit profile
on `windows-2022` for relevant pull requests to `master`, pushes to `master`,
and manual dispatch. It checks out the exact PR head rather than the synthetic
merge commit. Generated evidence is ignored by Git; native sources and build
outputs live under the runner's temporary directory, preserving the clean-source
requirement.

The job builds the official [Haivision SRT v1.5.7 source](https://github.com/Haivision/srt/tree/899348d8318eb9a3c5a5b6ec43c4a1114288773a)
at commit `899348d8318eb9a3c5a5b6ec43c4a1114288773a` using Visual Studio 2022 x64
and the `srt_shared` Release target. Encryption is disabled and C++ standard
library synchronization is enabled, so this plaintext loopback requires neither
OpenSSL nor pthread DLLs. The hosted Windows system and MSVC runtime supply the
remaining dependencies; the job retains the DLL import list, CMake configuration,
build logs and a native-build identity containing the source pin and DLL SHA-256.

The job checks `Server-Media-Foundation`, installs that Windows feature if needed,
and records its state, runner image and runtime DLL versions. Unavailable features,
a required reboot or missing core MF DLLs fail the job. Feature presence is only
a prerequisite: the real H.264/AAC encoder, MPEG-TS sink and SRT listener must all
complete the existing two-format qualification. Unsupported codecs or containers
fail the test; they are never converted to a skip or qualification PASS.

The retained artifact `rtaime-srt-native-<run-id>-<attempt>` contains both format
JSON files and the TRX only when produced by the tests, alongside build and
prerequisite diagnostics even on failure. A green result establishes source-bound
software loopback on that runner. It does not establish encrypted transport,
external decoding, WAN behavior, physical latency or hardware performance, and it
does not replace any of the five Required Gates.
