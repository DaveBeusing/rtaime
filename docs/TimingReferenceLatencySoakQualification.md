<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Timing, Reference, Latency & Soak Qualification

## Scope

Timing/Reference/Latency/Soak Qualification qualifies the temporal behavior of the V1 execution path without changing production authority. ControlHost remains authoritative, RuntimeHost remains the committed execution owner, and timing/reference evidence is observational.

The qualification model separates evidence that can be proven deterministically in generic CI from evidence that requires the physical AJA reference system and an independent physical-latency instrument.

## Layer 1: deterministic Runtime timing evidence

`RuntimeTimingQualificationProbe` is the common bounded evidence collector for committed Program boundaries. It records only timing metadata:

- committed boundary sequence number;
- monotonic observation time;
- interval from the preceding boundary;
- absolute interval jitter relative to the configured frame period;
- processing duration for the boundary;
- sequence continuity;
- jitter-budget result;
- processing-budget result.

The retained sample set is fixed-capacity and chronological. Long-running operation cannot create an unbounded timing history.

The state model is deliberately conservative:

- `Healthy`: no observed sequence, jitter or processing-budget violation;
- `Degraded`: at least one jitter or processing-budget violation;
- `Unstable`: a sequence discontinuity or three consecutive timing violations;
- `Lost`: no boundary has been observed for more than three expected frame periods.

The probe does not stop execution, change routing, mutate desired state or create production truth. It exists only to produce evidence and health observations.

## RuntimeHost live wiring

RuntimeHost owns the live scheduler evidence because the `PeriodicTimer` is the cadence boundary. The process creates one `RuntimeTimingQualificationProbe` from the active V1 frame rate, records the scheduler observation time plus the actual committed-boundary processing duration, and maps the resulting state into `V1RuntimeHostSnapshot.TimingHealth`.

`V1RuntimeHostService` therefore starts timing health as `Recovering`; it is no longer hardcoded to `Healthy`. Only measured scheduler observations can move it to `Healthy`, `Degraded`, `Unstable` or `Lost`.

The default V1 software thresholds are intentionally conservative:

- expected period: exact active frame period;
- absolute jitter budget: 25% of one frame period;
- processing budget: one complete frame period;
- retained observations: 2,048 committed boundaries in the RuntimeHost process.

These defaults are operational health thresholds, not a physical-platform certification claim.

## Layer 2: external reference observation

Timing/Reference/Latency/Soak Qualification does not add a new Media I/O contract or native ABI. The existing output `MediaIoPortStatus` is sufficient for live reference observation.

When the native AJA Program-output session was opened with external reference required:

- the adapter keeps `NTV2_REFERENCE_EXTERNAL` selected;
- `GetReference` must still report the external source;
- `GetReferenceVideoFormat` must be present and match the requested 1080p50 or 1080p59.94 rate;
- otherwise Program output status becomes `Lost`;
- Program submission returns explicit backpressure while reference is unavailable rather than silently switching reference/provider or terminating authority;
- after a matching external reference returns, output status becomes `Locked` and submission can resume.

This recovery behavior is non-authoritative. It does not modify Control state, prepared execution or routing.

## Layer 3: physical reference, host-cycle and soak evidence

`TimingReferenceHardwareQualificationTests.Reference_loss_relock_host_cycle_and_soak_must_pass_when_explicitly_enabled` is hardware-gated and never runs merely because normal CI executes the integration test assembly.

A qualifying run must provide:

- the exact expected AJA adapter identity;
- the exact repository-pinned `libajantv2` commit;
- one V1 video format;
- external reference enabled;
- at least 1,800 seconds (30 minutes) of soak;
- an explicit reference loss/re-lock exercise;
- a declared host-cycle p95 threshold.

During the soak, the test requires:

- both SDI inputs to finish `Locked`;
- Program output to finish `Locked`;
- at least 85% of the theoretical frame count to be captured on each input and accepted on Program output;
- zero capture failures;
- zero hard output rejections;
- initial external-reference lock;
- a subsequently observed `Lost` or `Unstable` reference state;
- a later observed re-lock;
- host-cycle p95 at or below the declared threshold.

Reference-loss backpressure is allowed because it is the explicit bounded recovery behavior under test; it is not treated as a hard output failure.

## Latency terminology

Timing/Reference/Latency/Soak Qualification distinguishes two latency classes and does not conflate them:

1. **Host pipeline / host-cycle latency**: monotonic time spent inside defined Runtime/Media-I/O software observation points. Software can measure this directly.
2. **Physical end-to-end latency**: measured from physical SDI input presentation to physical Program output presentation using an independent external measurement method.

Host-side timing can never be relabeled as physical end-to-end latency.

## Independent physical end-to-end latency evidence

The full Timing/Reference/Latency/Soak Qualification runner `build/qualification/Invoke-TimingReferenceSoakQualification.ps1` requires a runner-local JSON file produced by an independent latency measurement instrument or measurement workflow. The accepted evidence schema is `1.0` and requires at least:

- `schemaVersion`: `1.0`;
- non-empty `measurementMethod`;
- `sampleCount` of at least 30;
- `p95Milliseconds`;
- `maximumMilliseconds`.

The wrapper rejects non-finite values, a maximum below p95, too few samples or a p95 above the explicitly declared acceptance threshold. Only after both the AJA soak/reference test and independent latency evidence pass does the wrapper write `physicalEndToEndLatency.status = PASSED` into the retained Timing/Reference/Latency/Soak Qualification evidence.

No repository test can synthesize this external evidence.

## Dedicated qualification workflow

`.github/workflows/timing-reference-qualification.yml` is manual-only and runs on the self-hosted Windows x64 runner labeled `rtaime-media-io-reference`.

It:

1. checks out the exact source candidate;
2. verifies .NET 10.0.401 plus CMake/Git;
3. resolves the repository-pinned AJA SDK source and verifies the exact commit;
4. builds the native `rtaime_media_io.dll` from that exact source;
5. builds the integration qualification target;
6. invokes the fail-closed Timing/Reference/Latency/Soak Qualification runner with the declared soak/timing/E2E thresholds;
7. requires an explicit external-reference loss/re-lock exercise;
8. uploads only the resulting combined qualification JSON as retained workflow evidence.

The default workflow requests a 3,600-second soak, while the runner itself enforces a hard minimum of 1,800 seconds.

## Evidence semantics

Normal Required Gates prove the implementation, state machine, build graph and policy only. They do not prove AJA hardware, physical reference, real SDI continuity, physical latency or long-duration behavior.

The dedicated hardware workflow may emit `PASSED` only when every requested Timing/Reference/Latency/Soak Qualification dimension is evidenced. A missing independent E2E-latency file, missing loss/re-lock event, short soak, mismatched adapter/SDK, continuity failure or threshold violation is a failed qualification rather than an implicit pass.

## Current evidence state

- deterministic Runtime timing probe: **IMPLEMENTED**;
- bounded timing retention and state-machine regression: **IMPLEMENTED**;
- RuntimeHost live timing-health wiring: **IMPLEMENTED**;
- AJA external-reference live status/recovery mechanism: **IMPLEMENTED**;
- physical reference-loss/re-lock qualification mechanism: **IMPLEMENTED**;
- host-cycle latency qualification mechanism: **IMPLEMENTED**;
- independent physical end-to-end latency evidence validation: **IMPLEMENTED**;
- long-soak qualification mechanism: **IMPLEMENTED**;
- retained physical reference-loss/re-lock evidence: **UNVERIFIED**;
- retained host-cycle hardware evidence: **UNVERIFIED**;
- physical end-to-end latency qualification: **UNVERIFIED**;
- long soak qualification: **UNVERIFIED**.

No Timing/Reference/Latency/Soak Qualification physical-hardware claim is valid until `.github/workflows/timing-reference-qualification.yml` emits retained `PASSED` evidence for the exact tested source, AJA adapter, driver and pinned SDK identity together with the independent physical-latency evidence.
