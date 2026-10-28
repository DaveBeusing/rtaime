<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# ST 2022-7 dual-leg RTP merge software reference and qualification

## Evidence and admission boundary

**Implementation:** a deterministic software-only dual-leg RTP packet-selection and
impairment-test reference in `rtaime.Provider.St2110`.
**Physical support:** UNSUPPORTED / HARDWARE UNVERIFIED. No dual-NIC, PTP, multicast,
switch, latency, ST 2022-7 interoperability or standard-conformance claim.
A green managed test qualifies only the exact tested software source revision.

The existing ST 2110 sender and test receiver remain **single-leg IPv4 loopback**
(127.0.0.1), not two independently routed networks. The new merge adapter is
**not connected to a Runtime production input/output or to ControlHost admission**.
There is deliberately no Operator protection toggle, no ST 2022-7 provider
capability advertisement, no fabricated physical link state and no change
to the existing transactional Program/Aux authority.

## Packet and timing contract

- The two logical inputs must carry byte-identical copies of the same ST 2110
  RTP datagram, with matching PT, SSRC, RTP sequence, timestamp and payload.
  The first valid copy wins; a conflicting copy cannot overwrite it.
- Video reuses the existing RFC 4175-style packet format and its 32-bit
  extended sequence (high 16 bits in the video payload header). Audio reuses
  the existing 48 kHz L24 1-ms RTP packet format and unwraps the 16-bit RTP
  sequence across its rollover relative to the latest accepted packet.
- The packet merger does **not** encode, decode, alter timestamps, synthesize
  missing audio, choose a new media clock, or infer synchronized PTP.
  Emitted datagrams are consumed by the existing ST 2110 receivers.
- `St2022SoftwareMergeOptions` explicitly bounds pending reordering to
  2..1024 RTP datagrams, with a configured holdback (0..250 ms) and finite
  pressure threshold. At most another 1024 recent output datagrams are
  retained for late-copy comparison. At the 1200-byte wire limit, packet
  payload retention is bounded to approximately 2.4 MB at the maximum
  capacity, excluding dictionary/object overhead.
- A gap is released as observed loss only after the software holdback expires
  or the pressure threshold is reached. A packet arriving after release is
  late, not replayed. Jumps outside the bounded sequence window are rejected;
  the caller must explicitly open a new epoch, not silently re-anchor the
  active RTP timebase.
- All arrivals and explicit `Flush` calls take monotonic timestamps.
  Snapshots preserve per-leg accepted packet, outstanding gap, reorder,
  duplicate/late and jitter observations plus post-merge gap loss,
  selected-leg counts, malformed/conflicting datagrams, late and
  out-of-window counts. These are software observations, not NIC counters.
  A trailing loss without later traffic cannot be inferred from RTP sequence
  numbers alone.

## State and quality semantics

| Software-observed state | Meaning | Physical qualification |
| --- | --- | --- |
| UNKNOWN | Neither leg has supplied a valid software RTP packet | UNVERIFIED |
| HEALTHY | Both logical legs recently supplied accepted packets | UNVERIFIED |
| DEGRADED | Exactly one logical leg is recently observed | UNVERIFIED |
| FAILED | Previously observed legs are both past the configured timeout | UNVERIFIED |

These states do not assert independent physical paths, delivered Program
continuity, packet recovery on external equipment, or PTP time quality.
`SelectedSecondary` means the second software copy was used, not proof
that a physical primary path was interrupted. A simultaneous loss on
both legs is counted as a merged gap when a later packet establishes
the missing sequence. Neither a single NIC nor two loopback sockets
qualify for ST 2022-7. The test adapter must remain unadvertised as a
production protection route until real provider admission is available.

The RuntimeHost configuration rejects `seamlessProtection` and
`protectionMode` on the single-leg ST 2110 reference, even when set to
false. These are deliberately **not** deployable configuration options.
SRT and NDI are not relabelled as ST 2022-7 protected transport.

## Deterministic software qualification matrix

| Scenario | Asserted software behavior | Not proven |
| --- | --- | --- |
| Complementary loss on A/B | Sequence-ordered merge, selected leg and zero merged gaps | Physical disjoint network delivery |
| Common sequence absent on both | Bounded wait, post-merge observed gap, no synthesized PCM | End-to-end protection against common-mode failure |
| Duplicate and altered RTP copy | Identical copy classified, conflicting data rejected | Cryptographic transport authenticity |
| High skew, out-of-order and late | Holdback/pressure release, stale packets not replayed | Configured wire/network latency |
| 16-bit audio / 32-bit video rollover | Continuous logical sequence, unchanged RTP timestamps | PTP frequency accuracy |
| Single-leg blackout/recovery | Software HEALTHY/DEGRADED/FAILED/UNKNOWN observation | Program no-black / audio sample-exact failover |
| Invalid SSRC/PT, oversized and jump | Reject without unbounded allocation or session re-anchor | Malicious packet filtering by production NIC |
| Existing ST 2110 receiver integration | Reassembled video and PCM from merged software packets | External device interoperability |

Test on Windows x64 with the repository-pinned .NET SDK 10.0.401:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --filter FullyQualifiedName~St2022SoftwarePacketMergeTests
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --filter "FullyQualifiedName~St2022SoftwareMergeIntegrationTests|FullyQualifiedName~St2110NetworkOutputConfigurationIntegrationTests"
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
```

The existing `st2110-software-reference.yml` workflow includes these
checks alongside the original ST 2110 tests; the five Required Gates
remain mandatory. Record the exact SHA, run ID, test counts, negative
cases and any baseline failures before declaring SOFTWARE QUALIFIED.
No physical test can be marked passed by this harness.

## Requirements before a real dual-NIC Runtime provider can be admitted

1. Independently qualified ST 2110 single-leg send/receive, versioned SDP,
   real RTP capture and receive queues, both with documented PTP time quality.
2. Physically independent NICs and independent addressing/flows/switch
   paths for A and B, genuine identical RTP copies, explicit bounded
   joining and resource admission. A loopback-only or shared-NIC
   configuration must fail closed.
3. Real dual-leg send/receive adapters hosted below Runtime authority;
   ControlHost still prepares and commits one authoritative Program/Aux
   graph. No new direct Operator-to-runtime mutation path.
4. Measured per-leg packet loss, timing gap/jitter, reordering, link health,
   pre/post-merge loss and sink continuity. Timeouts must not be
   mistaken for hardware timestamps.
5. Independent loss/jitter/blackout impairment on each switch path,
   simultaneous loss, severely skewed legs, packet corruption, link
   reconnect, wrap and prolonged pressure; bounded memory and CPU
   under maximum admitted bitrate.
6. PTP/ST 2059 clock lock, 5+ minutes measured holdover under loss of
   reference, separate from packet redundancy; A/V synchronization,
   program-clock continuity and real external sender/receiver captures.
7. Versioned profile/NIC/driver/switch/firmware, test-platform identity,
   evidence provenance and operator-visible UNVERIFIED/DEGRADED/FAILED
   separation before hardware qualification is granted.

A real two-port sender or receiver must not be introduced by merely
duplicating a software loopback socket. Until the above requirements
are satisfied, protection is unavailable in deployment and must not
appear in product capability or marketing matrices.

## Related documents

- [ST 2110 software interop qualification](St2110SoftwareInteropQualification.md)
- [Network output streaming](NetworkOutputStreaming.md)
- [Media reference clock qualification](MediaReferenceClockQualification.md)
- [Provider-qualified resource admission](ProviderQualifiedCapabilityAdmission.md)
- [Production deployment baseline](ProductionDeploymentBaseline.md)
- [EBU Tech 3371](https://tech.ebu.ch/publications/tech3371)
- [EBU LIST monitoring and testing](https://tech.ebu.ch/list)
