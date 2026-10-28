<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# ST 2110 RTP software loopback reference and qualification

## Implementation status and evidence boundary

**Status:** Software implementation awaiting exact-head test evidence. No physical ST 2110 qualification or broadcast interoperability claim.

The reference path is opt-in on the existing governed Runtime network-output graph. It consumes committed ControlHost-authorized Program/Aux media timing through typed Provider Contracts, finite queues, and existing network-output health. It does not add a Program renderer, authority, independent clock or NMOS registration. Its receiver is a software loopback test harness, **not** an admitted live source.

| Area | Implemented | Qualification boundary |
| --- | --- | --- |
| ST 2110-20 video | RFC 4175 RTP YCbCr 4:2:2 10-bit, progressive, scan-line fragments, extended sequence, 90 kHz timestamps | Software-reference path; external interoperability UNVERIFIED |
| ST 2110-30 audio | L24 big-endian stereo, 48 kHz, 48 sample frames per 1 ms packet, exact Runtime sample positions | AES67/physical interoperability UNVERIFIED |
| Runtime output formats | 1920x1080p50 and 1920x1080p60000/1001 | Offline 25p reference wire profile only; RuntimeHost does not produce 25p |
| SDP and receiver | Typed canonical SDP (4096-byte bound), PT96/97; SSRC, loss, reorder, jitter and bounded frame reassembly | Broad third-party SDP and NMOS enrolment UNVERIFIED |
| Network | IPv4 loopback only; video/audio separate even RTP ports; single leg | Multicast, external NIC, WAN and ST 2022-7 UNSUPPORTED |
| Clock | 90 kHz and 48 kHz timestamps projected from the same committed rational Runtime frame | Physical PTP/ST 2059 and Genlock UNVERIFIED |
| ST 2110-21 pacing | Asynchronous provider worker sends RTP/UDP separately from Program hotpath | Sender shaping and physical throughput UNVERIFIED; no -21 compliance claim |
| Admission | Explicit typed loopback opt-in and exact format, finite provider slots | Unknown format or external destination rejected |
| Input | Receiver loopback has bounded video and PCM queues and packet health | Does not produce a routable ControlHost media source |

A separate [ST 2022-7 software packet-merge reference](St2022SoftwareProtectionQualification.md)
provides bounded dual-leg packet selection and impairment tests **without** enabling
physical protection, dual network endpoints, a new Runtime source, or Operator admission.
The production ST 2110 transport remains single-leg loopback; ST 2022-7 is
UNSUPPORTED for physical deployment.

The Runtime RGBA8 format may have unknown source colorimetry. The deterministic software BT.709/YCbCr conversion is not independent evidence of calibrated source colour or broadcast compliance.

## Opt-in configuration

Use the existing RuntimeHost --network-outputs= JSON or RTAIME_NETWORK_OUTPUTS environment setting:

~~~json
[
  {
    "roleId": "program",
    "targetId": "st2110-reference-program",
    "protocol": "st2110",
    "softwareLoopback": true,
    "address": "127.0.0.1",
    "videoPort": 5004,
    "audioPort": 5006,
    "videoSsrc": 2233,
    "audioSsrc": 2234,
    "queueCapacity": 2
  }
]
~~~

Only the literal IPv4 destination 127.0.0.1 is accepted; other 127/8 aliases are rejected because the reference receiver binds 127.0.0.1. Missing softwareLoopback, DNS names, nonlocal addresses, unqualified PCM formats, unknown mixed settings or non-SSRC-separated streams fail closed. Old descriptors without a protocol field still default to SRT. Program/Aux role authority and provider reservations are unchanged.

This test/reference output is not intended for deployment at production shows: uncompressed 1080p RTP requires substantial per-frame memory/CPU and network throughput. No ST 2110-21 shaping, bounded latency or real-time guarantee has been established.

## Software reference and negative tests

1. Instantiate a test-only matching St2110SoftwareLoopbackReceiver before sending any UDP.
2. Select the committed Program/Aux role through the existing ControlHost/Runtime prepare and commit protocol.
3. Verify canonical SDP and reassembly, audio sample count, 90/48 kHz timing, per-stream packet loss/reorder/jitter, queue caps and explicit sender health. Do not assume sender send evidence means peer delivery.
4. Inject malformed RTP/SDP, dropped/duplicated/reordered packets, sequence and timestamp wrap, source clock discontinuities, transport failures and receiver queue saturation.
5. Preserve a distinct negative result for any unsupported hardware clock/NIC/multicast format.

Software test commands (Windows x64; SDK 10.0.401):

~~~powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --filter FullyQualifiedName~St2110SoftwareTransportTests
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --filter FullyQualifiedName~St2110NetworkOutputConfigurationIntegrationTests
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
~~~

Only exact-run evidence can be marked PASS. Required CI gates remain mandatory.

## Separate physical qualification (not yet satisfied)

Qualify dedicated NIC, PTP Grandmaster and hardware timestamp/frequency discipline; observed ST 2110-21 shaping on a switch; multicast join/leave; actual external sender/receiver interoperability; A/V lip-sync and drift at 25/50/59.94; color test pattern accuracy; soak under CPU/NIC pressure, receiver reordering and failover; timing loss and recovery. Do not market hardware or full standard compliance before this evidence is independently captured.

NMOS registration and IS-05 activation remain with IntegrationHost's existing observer/virtual boundary. Production ST 2022-7 protection and ST 2110-22 compression remain unsupported.
A non-admitted ST 2022-7 software merger and qualification matrix are documented separately.

## References

- [RFC 4175 video RTP](https://www.rfc-editor.org/rfc/rfc4175)
- [EBU Tech 3371](https://tech.ebu.ch/publications/tech3371)
- [AMWA NMOS specifications](https://specs.amwa.tv/nmos/by-type/)
- [Media Reference Clock Qualification](MediaReferenceClockQualification.md)
- [Output Routing and Health](OutputRoutingHealth.md)


## HDR source precision and colour restriction

The reference software sender **starts with legacy 1080p RGBA8** and
derives 10-bit packet words through a BT.709 software conversion. This
is not native 10-bit source preservation, HLG metadata carriage or
3840×2160 HDR output. The new `HdrNative422Packing` off-line
pgroup/planar roundtrip exercises a distinct original 10-bit
signal contract; it does not upgrade the existing ST 2110 session,
SDP or network clock. Exact HDR 10-bit/CICP source and physical
ST 2110 HDR receiver/SDI output remain UNSUPPORTED or UNVERIFIED.
See [HDR Contribution Color Decision](HdrContributionColorPipelineDecision.md).
