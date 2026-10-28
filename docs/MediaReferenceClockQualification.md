<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Broadcast media reference clock and PTP/Genlock qualification

**Implementation status:** media-clock contracts, virtual simulation and
management observations: `IMPLEMENTED` pending exact-head CI.
**Physical PTP, SMPTE ST 2059 and Genlock:** `UNVERIFIED`.
**Decision:** [Provider versus authority clock ADR](MediaReferenceClockDecision.md).

## Existing production timing remains authoritative

The existing `RationalFrameSchedule` uses a monotonic elapsed-time epoch
and rational `FrameRate`; Runtime continues to own all `ProgramBoundaryResult`
timestamps. The existing `AudioFollowVideoEngine` supplies the single
48 kHz PCM window for a given Program boundary. This change does **not**
replace that scheduler, modify the audio renderer, allocate a second
Program cadence timer, add a new media producer or synthesize a
wall-clock PTP lock.

`MediaReferenceClockObservation` has format version 1 and publishes:

- qualified provider ID, stable clock-domain ID and explicit nonempty
  `EpochId` for the current reference epoch;
- source `Unknown/Virtual/HardwarePtp/ExternalGenlock`, lock
  `Unverified/Locked/Holdover/Lost`, independent evidence
  `Unverified/Simulated/HardwareQualified`;
- optional PTP `domainNumber` (0..127) and exact 16-hex GM identity;
- provider observation time and last physical/simulated synchronization
  time in UTC, measured offset in nanoseconds, drift in ppb and explicit `TimeQuality` (`Unverified`, `Simulated`, `Traceable`, `Holdover`);
- bounded holdover duration and maximum, provenance/diagnostic detail.

All untrusted values are bounded and validated. Invalid or absent
snapshots are not interpreted as a healthy clock. An unmatched
provider ID/source, a falsely hardware-qualified virtual provider,
an unsupported holdover, reversed observation timestamp, changed
GM/domain within one epoch, missing physical timing measurements or
nonfinite drift fails closed. Monotonic scheduler timing alone is not
hardware evidence.

## Derived RTP/timebase mapping (no transport feature)

The following **derived timestamps** use the *same* Runtime frame
index `n`, exact rational production frame rate `N / D`
and an explicit caller-owned RTP epoch:

```text
videoMediaTicks90k(n) = floor(n × D × 90000 / N)
audioSamplePosition(n) = floor(n × D × 48000 / N)
videoRtpTs(n)         = (epoch90k + videoMediaTicks90k(n)) mod 2^32
audioRtpTs(n)         = (epoch48k + audioSamplePosition(n)) mod 2^32
```

These are not a new ST 2110/RTP sender or proof of an absolute PTP
alignment. They define the common relative edit-boundary timebase.
Decoded PCM **actual window** remains the authoritative sample owner;
the mapping only predicts its boundary position. Network sender
adaptation must use the actual committed timestamp and one explicit
clock epoch, not a separate clock with rounded per-frame accrual.

| Edit rate | `n` | Video ticks (90 kHz) | Audio samples (48 kHz) |
| --- | ---: | ---: | ---: |
| 25/1 | 25 | 90000 | 48000 |
| 50/1 | 50 | 90000 | 48000 |
| 60000/1001 | 60 | 90090 | 48048 |

For fractional rates, the first 90-kHz video increments alternate
between 1501 and 1502 ticks as determined by exact rational flooring.
There is no repeated integer frame-period addition or long-term drift.
When the provider changes grandmaster or resets, its epoch changes;
an interop sender must treat that as a discontinuity and requalify
timestamp origin. It may not silently reuse an old RTP-to-PTP mapping.

## Test-only virtual provider

`VirtualMediaReferenceClock` is a deterministic `IMediaReferenceClockProvider`:
all monotonic time/UTC inputs are supplied explicitly by tests. It
does not open NICs, read Windows system time as PTP or claim a
physical clock. It exercises:

- no sync → `UNVERIFIED`;
- synthetic sync → `LOCKED` for at most two seconds without another sync;
- sync interruption → `HOLDOVER` for **five simulated minutes** after
  the lock-observation interval;
- expiry → `LOST`, without minting new last-sync evidence;
- explicit Grandmaster change → new epoch and `UNVERIFIED`;
- next synthetic sync → new `LOCKED`, with no retroactive claim
  that the old epoch was continuous.

A virtual `LOCKED` never passes `HardwareLocked` admission because
its evidence is `Simulated`, not `HardwareQualified`.
`MediaReferenceAdmission` also requires a current provider observation
(maximum three seconds old), source evidence, matching time quality (`Traceable` for `Locked`, `Holdover` for qualified finite holdover), actual last sync and a
valid matching lock or allowed bounded holdover. A real hardware
adapter must independently qualify these facts.

## Start, loss and recovery policy

RuntimeHost accepts `--clock-requirement=asynchronous` (default),
`hardware-locked` or `qualified-holdover` and corresponding
`RTAIME_RUNTIME_CLOCK_REQUIREMENT` values.

- `asynchronous`: existing local/NDI/SRT Program routes are not
  blocked by absence of a physical reference.
- `hardware-locked`: new execution admission requires fresh,
  hardware-qualified LOCKED evidence. Unverified/virtual/holdover
  sources reject at Runtime Prepare, before a Control commit.
- `qualified-holdover`: adds a **bounded**, provider-measured HOLDOVER
  allowance of at most five minutes. Hardware-qualified provenance,
  a last-sync timestamp and freshness are still mandatory.
- All rejected new plans preserve the previously committed Program
  execution and return `runtime.clock.reference_unverified`.
- RuntimeHost retains clock facts as optional read-only snapshot fields.
  Control forwards only fresh Runtime evidence; legacy payloads without
  them become `UNVERIFIED`. Health Center exposes lock/provider/GM,
  domain/epoch, offset/drift and last sync/holdover.

The policy gates **new Runtime Prepare**. It is not a substitute for
continuous reference validation on a future ST 2110/SDI provider output
hotpath. Existing native AJA external-reference submission gating
continues unchanged; no claim is made that this new opt-in predicate
continuously fences a real hardware device in isolation.

## Hardware qualification matrix (pending)

Before promoting any hardware source or ST 2110 profile from
`UNVERIFIED`, an exact-source qualification record must include:

| Evidence | Required physical proof | Current status |
| --- | --- | --- |
| NIC/SDI adapter | Exact NIC/device model, driver and firmware, HW timestamps, isolated clock domain | UNVERIFIED |
| Grandmaster | Exact 1588 clock identity/domain, profile, announce/BMCA changes and failover | UNVERIFIED |
| Lock | Independently measured offset/error, drift, accuracy and stability | UNVERIFIED |
| Holdover | At least 300 seconds of *physical* packet loss and bounded drift, recovery and GM switch | UNVERIFIED |
| Media phase | Frame-boundary and PCM sample relationship to measured PTP time (25/50/60000:1001) | UNVERIFIED |
| Transport | RTP presentation timestamps, packet capture, wrap/recovery and PTP ST 2059 interoperability | UNVERIFIED |
| Operations | Sustained Program/Audio continuity, packet/performance budgets, device error & restart | UNVERIFIED |

This follows the reference platform's source-bound
`TIMING_REFERENCE_LOCK`, `AUDIO_VIDEO_SYNCHRONIZATION`,
`SUSTAINED_FRAME_CADENCE`, `PHYSICAL_END_TO_END_LATENCY` and
`LONG_SOAK_STABILITY` evidence model rather than fabricating
a physical PASS.

## Verification

Run the pinned Windows-x64 .NET SDK 10.0.401 build and the narrow
regressions against the **exact PR head**:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj --configuration Release --no-build --filter FullyQualifiedName~MediaReferenceClockTests
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter FullyQualifiedName~MediaReferenceClockAdmissionTests
dotnet test tests/rtaime.Tests.Architecture/rtaime.Tests.Architecture.csproj --configuration Release --no-build --filter FullyQualifiedName~MediaReferenceClockArchitectureTests
```

Then inspect all required CI/Quality/Security/Provider Smoke/Packaged E2E.
No GitHub-hosted test, Windows Stopwatch observation or virtual holdover
counts as physical PTP/Genlock hardware evidence.

Reference: EBU Tech 3371 v3 (2023), EBU LIST, IEEE 1588 and
SMPTE ST 2059 (external interoperability needs independent verification;
their title references are not a certification claim).
