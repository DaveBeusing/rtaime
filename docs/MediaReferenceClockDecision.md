<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# ADR: Provider Media Reference Clock versus Production Authority

**Status:** proposed for acceptance with the provider-reference-clock PR.
**Change class:** `CONTRACT`, `REALTIME_CRITICAL`, provider qualification.
**Scope:** RuntimeHost and provider timing observation; no new scheduler.

## Context and decision

rtaime has one established `RationalFrameSchedule`, one Runtime-owned Program
frame sequence and an exact 48 kHz Program audio sample cadence. Its
`RuntimeTimingQualificationProbe` measures scheduler progress/jitter;
that **does not** prove external reference sync. The separate native AJA
reference observation already has its own fail-closed loss behavior when
`RequireExternalReference` is selected. None of these facts implies
PTP/Grandmaster capture from a NIC or qualified Genlock.

We introduce a **versioned provider-observation contract** in
`Media.Contracts` and `Provider.Contracts`. A provider publishes
`MediaReferenceClockObservation` with source, identity, domain, clock
epoch, lock state, evidence class, optionally GM/domain, monotonic
holdover elapsed, source-measured offset/drift, last sync time and an independent `TimeQuality` state; hardware LOCKED requires `Traceable` and hardware HOLDOVER requires `Holdover`.

- `ControlHost` **alone** authorizes production changes. RuntimeHost
  evaluates its opt-in synchronized reference requirement before
  `TransactionalRuntime.Prepare`; a rejected clock cannot reserve a
  planned graph or advance the Control revision.
- `RuntimeHost` remains the exclusive **execution and timestamp owner**.
  The existing rational frame index is the canonical source for media
  PTS/48-kHz PCM and future RTP timestamp projections; PTP/Genlock
  providers deliver **evidence and reference mapping**, not another
  Program scheduler, audio clock or WallClock.Sleep loop.
- Provider clock epochs are immutable between GM/domain changes.
  Grandmaster failover or discontinuity creates an explicit new epoch.
  A stale, unknown, unqualified or simulated reference is not hardware
  lock, regardless of elapsed Windows monotonic time.
- Existing asynchronous Program, NDI, SRT and local-file paths retain
  their current admission by default (`asynchronous`). An opt-in
  `hardware-locked` or `qualified-holdover` policy fails **closed**
  before committing a new Runtime execution when required evidence
  is missing. Future ST 2110 admission must use these same facts and
  add transport-specific physical output fencing; no ST 2110 sender
  or certified PTP adapter is introduced here.
- Health Center reads *only* the confirmed snapshot from
  RuntimeHost → ControlHost → Client. It never infers clock lock from
  scheduler health, GPU FPS, wall time or synthetic test signals.

## Interoperability and migration

`MediaReferenceClockObservation` format v1 is appended as a nullable
field at every existing management snapshot hop. Old readers ignore
additional data; new readers accept absent fields as **UNVERIFIED**.
No existing serialized production graph, `PreparedExecutionContract`
shape, Control command, Resource ID, SQLite migration or audio/video
sample format changes. The supported `RuntimeContractVersion` and
`VersionPrefix` stay unchanged.

## Alternatives rejected

A second PTP-slaved Program loop and independent audio clock would
split ownership and create drift/backpressure hazards. Treating NTP or
`DateTimeOffset.UtcNow` as a PTP lock would manufacture reference
evidence. Making the existing sample-peak, timing-health or Genlock
software test classify itself as `hardware-qualified` would bypass
exact-source hardware qualification. These options are not adopted.

## Follow-up requirements

A real provider for supported NIC/SDI interfaces must supply driver
and hardware timestamping evidence, device/network topology,
`grandmasterIdentity`, `domainNumber`, supported SMPTE ST 2059
profile, measured offset/error/drift, holdover behavior and
loss/relock counters under bounded non-realtime observation. Its
admission and physical submission must be fenced during lock loss,
not merely at initial Prepare. Until that adapter, physical sensors,
and an exact-source hardware test are accepted, physical PTP/Genlock
remains **UNVERIFIED** and the hardware-required profiles fail closed.
