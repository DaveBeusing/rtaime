<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Timing, Reference, Latency & Soak Qualification

## Scope

AP-34 qualifies the temporal behavior of the V1 execution path without changing production authority. ControlHost remains authoritative, RuntimeHost remains the committed execution owner, and timing evidence is observational.

The qualification model separates evidence that can be proven deterministically in generic CI from evidence that requires the physical AJA reference system.

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

## V1 baseline thresholds

AP-34 derives the expected period from the active V1 frame rate. Exact physical acceptance thresholds for the reference platform are evidence items, not assumptions. They must be declared in the hardware qualification workflow and retained with the resulting evidence.

Generic tests therefore validate the state machine, monotonicity, bounded retention and threshold semantics without claiming physical latency or jitter performance.

## Layer 2: physical reference evidence

The AJA reference path must extend the common evidence with measured physical facts before AP-34 can be described as hardware-qualified:

- selected reference source;
- detected reference format;
- reference present/locked state;
- reference-loss observation;
- re-lock observation and recovery time;
- sustained input/output frame continuity;
- Program output acceptance/backpressure counters;
- host pipeline latency observations;
- externally measured end-to-end latency when a qualified loopback/measurement method is available;
- soak duration and total accepted frame count;
- driver, adapter, SDK revision, source commit and video format identity.

A normal GitHub-hosted Required-Gates run is not physical reference, SDI, latency or soak evidence.

## Latency terminology

AP-34 distinguishes two latency classes and does not conflate them:

1. **Host pipeline latency**: monotonic time spent inside the measured Runtime/Media-I/O software path between defined host-side observation points.
2. **Physical end-to-end latency**: measured from physical SDI input presentation to physical Program output presentation using an independently qualified measurement method.

Host pipeline latency may be collected by software instrumentation. It must never be presented as physical end-to-end latency.

## Reference loss and recovery

Reference state is observational and non-authoritative. Loss of external reference must become visible in Runtime timing health and retained qualification evidence, but must not rewrite Control authority or silently select another Media I/O provider.

The physical AP-34 qualification must prove an explicit loss/re-lock scenario before reference recovery can be marked `PASSED`.

## Soak qualification

Soak qualification is separate from unit/integration testing. The physical workflow must declare a minimum duration and minimum accepted frame count and must fail closed on:

- sequence discontinuity;
- capture failure;
- hard Program output rejection;
- reference loss outside the explicit recovery scenario;
- timing thresholds exceeding their declared limits;
- missing or mismatched adapter/driver/SDK identity.

Short generic CI runs verify only the qualification mechanism.

## Current evidence state

- deterministic Runtime timing probe: **IMPLEMENTED**;
- bounded timing retention and state-machine regression: **IMPLEMENTED**;
- RuntimeHost live timing-health wiring: **IN PROGRESS**;
- physical reference-state ABI/evidence: **UNVERIFIED**;
- reference-loss/re-lock qualification: **UNVERIFIED**;
- host pipeline latency qualification: **UNVERIFIED**;
- physical end-to-end latency qualification: **UNVERIFIED**;
- long soak qualification: **UNVERIFIED**.

No AP-34 hardware claim is valid until the dedicated reference workflow emits retained evidence for the exact tested source and hardware identity.
