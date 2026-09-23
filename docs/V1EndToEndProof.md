<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# V1 End-to-End Architecture Proof

**Status:** V1 End-to-End Proof software architecture proof  
**Change classification:** `CONTRACT`, `REALTIME_CRITICAL`  
**Branch:** `v1-end-to-end-proof`

## Purpose

This package composes the V1 foundations into one reproducible managed reference production path. It proves software architecture and failure behavior for the constrained V1 workload. It does **not** by itself qualify rtaime 1.0.0 STABLE, VALIDATED or CERTIFIED.

The reference path is:

```text
Operator / Client
      ↓
ControlHost
      ↓
Production Specification
      ↓
Command + Validation
      ↓
Staged Authoritative State
      ↓
Logical Production Graph
      ↓
Capability Resolution / Resource Admission
      ↓
Prepared Execution Contract
      ↓
Runtime Prepare + Commit
      ↓
ControlHost Commit Confirmation
      ↓
Authoritative State
      ↓
RuntimeHost
      ↓
Virtual Timing + Bounded Media Lifecycle
      ↓
Managed Reference GPU Processing
      ↓
Program Output + Audio Follow Video
      ├────────→ Failure-isolated Recording
      └────────→ Observation / Bounded Journal
```

AIHost remains optional to continuity. The V1 visible AI proof is composed as:

```text
Timed Program Frame Descriptor
      ↓
Governed Person Segmentation
      ↓
Timed Segmentation Result
      ↓
Result-use / fallback policy
      ↓
Independent visual-effect policy
      ↓
Dynamic RGBA layer
      ↓
GPU composite
```

AI never owns Program state or Runtime commit.

## Architecture classification

The package realizes architecture already approved by the Project Architecture Context, V1 Product Definition, Technology Baseline and Subsystem/Solution Architecture. It does not introduce a new architectural direction, so no new ADR is required.

`CONTRACT` applies because V1 adds explicit DISSOLVE command/transition semantics at stable Control and Runtime boundaries.

`REALTIME_CRITICAL` applies because the package composes timing, media, GPU, audio, Program output and failure-isolated side workloads into the production reference path.

## Production authority and transactional commit

The final V1 End-to-End Proof host boundary is deliberately two-phase.

1. The Operator or another client creates a versioned production Command against the current authoritative revision.
2. `ControlHostService` validates the command and computes the proposed production state.
3. Capability planning produces a `PreparedExecutionContract` for that proposed state.
4. The proposed state remains **staged**, not authoritative.
5. `RuntimeHost` performs Runtime `Prepare` and `Commit`.
6. Only a matching successful Runtime commit is confirmed back to ControlHost.
7. ControlHost then advances the authoritative production revision.
8. A rejected Runtime commit discards the staged mutation and leaves the previous authoritative state unchanged.

This avoids Authoritative/Execution drift. RuntimeHost never becomes Production Authority, and ControlHost does not call a RuntimeHost implementation directly through its production dependency graph.

## Operator and control seam

`rtaime.Client` now provides a transport-neutral `IOperatorControlTransport` and `OperatorControlClient`.

The client:

- synchronizes from authoritative snapshots,
- sends Preview, CUT and DISSOLVE production commands,
- uses the current authoritative revision for optimistic concurrency,
- contains no Runtime implementation dependency,
- contains no production authority.

The WPF Operator is an MVVM presentation client over that seam. Production access remains `Operator -> Client` only; the Operator also references dependency-neutral `rtaime.Core` exclusively for shared cross-cutting diagnostics.

The V1 End-to-End Proof automated end-to-end tests use an **in-process test adapter** to compose ControlHost, RuntimeHost and AIHost. That adapter is test infrastructure and is not evidence for a production network/IPC implementation.

## CUT and DISSOLVE

### CUT

A CUT is represented by the same authoritative command path as other production mutations. Runtime receives a transition intent and anchors activation to the next production frame boundary after a successful replacement commit.

### DISSOLVE

DISSOLVE carries:

- source media identity,
- destination media identity,
- duration in production frames.

RuntimeHost anchors the transition start to the next production boundary after commit. The managed reference compositor derives deterministic blend weights from production frame sequence. No UI timer or wall-clock animation owns transition timing.

The reference three-frame proof produces deterministic weights `85`, `170`, `255` and completes on the third boundary.

## Video formats and timing

The same V1 reference path is exercised for:

- 1080p50,
- 1080p59.94.

Virtual timing preserves the exact rational timebases already established by VirtualMedia. Media processing passes through the bounded `MediaFramePipeline`; bulk pixel payloads remain provider-local rather than being added to normal media contracts.

## Program GPU composition

`V1RuntimeHostService` composes the existing managed reference GPU provider with:

- two deterministic video background inputs,
- CUT,
- DISSOLVE,
- one RGBA key/compositing layer,
- static RGBA source,
- dynamic RGBA source,
- Program readback probe for observable reference evidence.

The static and dynamic sources use the same compositor path. GPU surface lifecycle is checked after each completed reference boundary through the RuntimeHost snapshot.

This is managed reference evidence. It is not physical GPU/driver qualification.

## Audio Follow Video

Each logical video input has an embedded V1 audio stream descriptor. Program boundary processing uses the existing `AudioFollowVideoEngine` and exact video/audio timing relationship.

The end-to-end proof verifies that Program source transitions select the corresponding followed audio source and that audio emission continues through CUT, DISSOLVE, AI fallback and Operator disconnect.

Per-input gain, mute and peak-meter seams remain available through the previously implemented audio foundation.

## Recording isolation

Program recording remains an independent bounded asynchronous path through `ProgramRecorder` and `RuntimeRecordingBridge`.

The end-to-end proof starts Program recording, emits committed Program video/audio samples, stops cleanly and verifies sample delivery. Recording is never placed in the synchronous Program output dependency chain.

Existing recording-failure evidence remains applicable: recording may degrade or stop without stopping Program.

## Governed AI visible reference effect

The managed reference Person Segmentation provider now emits deterministic reference region metadata in addition to its existing segmentation descriptor and opaque mask handle.

This metadata is explicitly synthetic architecture-proof data; it is **not** a model-quality claim.

The end-to-end proof:

1. submits a timed Program frame to governed inference,
2. validates freshness/confidence/source-frame policy,
3. converts the accepted result into a dynamic RGBA region through an outer effect policy,
4. verifies a visible Program pixel change,
5. makes the AI provider unavailable,
6. observes a fallback decision,
7. removes the AI-derived layer,
8. verifies clean Program and audio continue.

The model therefore supplies analysis data only. It does not decide authoritative composition state.

## Input signal failure

V1 End-to-End Proof defines an observable reference fallback for a lost input signal:

```text
VALID → LOST → black reference fallback
```

The reference failure test verifies:

- Program execution remains committed,
- output continues at the next sequence,
- Program becomes deterministic black for the lost active source,
- an `input.fallback.black` observation is emitted.

The black fallback is a V1 managed reference behavior, not professional input-hardware qualification.

## Operator continuity and resynchronization

The in-process architecture proof explicitly disconnects the Operator client while Runtime remains committed and continues processing Program + audio. Re-synchronization fetches the current authoritative snapshot and restores client visibility without creating state in the UI.

This proves the software authority property. A real OS process kill/restart over production IPC remains separately unverified.

## Production Journal

`BoundedProductionJournal` is a bounded asynchronous append-only causal journal reference implementation.

It records production-relevant events such as:

- staged control mutations,
- prepared execution,
- Runtime commit success/failure,
- authoritative commit confirmation,
- subsystem observations.

`TryAppend` is nonblocking. Capacity pressure drops journal work rather than blocking Program execution.

The current implementation is an in-memory reference journal. Durable SQLite-backed management/journal recovery is not claimed by this package.

## Cross-host failure proof

A dedicated failure test injects Runtime commit rejection after ControlHost has staged a valid replacement.

Expected and verified semantics are:

```text
Current Authoritative State
      ↓
Valid Command
      ↓
Staged Proposed State + Prepared Execution
      ↓
Runtime Commit REJECTED
      ↓
Staged state discarded
      ↓
Current Authoritative State unchanged
```

The journal records the Runtime rejection. No later revision is marked authoritative.

## Combined managed-reference performance evidence

V1 End-to-End Proof includes a combined performance regression test for both V1 development formats. Each run composes:

- two timed inputs,
- media lifecycle/queues,
- managed reference GPU composition,
- static RGBA layer,
- Audio Follow Video,
- Program recording,
- a four-frame DISSOLVE,
- governed Person Segmentation at a lower inference rate.

The test calculates P50, P95, P99 and worst-observed wall-clock boundary duration and counts executions exceeding the **nominal** frame interval. It also checks continuous Program sequence, recording completion and zero retained GPU/AI resources after completed work.

These measurements are CI regression evidence only. The managed reference backend intentionally copies full RGBA frames and is not a qualified real-time hardware backend. Nominal-frame-budget exceedance in this test does not establish or invalidate the professional Production Envelope; hardware timing/latency qualification requires the reference hardware environment.

A generous runaway guard exists only to catch pathological regression. It is not a production latency SLA.

## Automated evidence obligations

The V1 End-to-End Proof branch requires the complete managed solution to pass on the Windows reference CI runner:

- Architecture tests,
- Contract tests,
- Unit tests,
- Integration tests,
- Behavioral tests,
- Failure tests,
- Performance tests.

The integration proof includes both 1080p50 and 1080p59.94 and verifies the complete software composition described above.

## Evidence boundary

The following may be marked PASS only when directly supported by final latest-head CI:

- V1 managed project/dependency graph remains valid,
- CUT and DISSOLVE contract/version behavior,
- two-phase Control/Runtime commit behavior,
- two-input Preview/Program reference routing,
- deterministic frame-boundary CUT/DISSOLVE,
- 1080p50 and 1080p59.94 virtual timing path,
- bounded media lifecycle integration,
- managed reference GPU composite,
- static/dynamic RGBA layer path,
- Audio Follow Video,
- protected Program recording reference path,
- governed Person Segmentation reference result and visible effect separation,
- AI unavailable fallback with Program continuity,
- Operator-client disconnect/resynchronization at the in-process seam,
- lost-input black reference fallback,
- asynchronous bounded causal journal behavior,
- rejected Runtime commit preserving authoritative state,
- managed combined-workload regression bounds.

## Explicitly UNVERIFIED after V1 End-to-End Proof

Unless separate evidence exists, V1 End-to-End Proof does **not** mark the following PASS:

- qualified professional capture/output hardware provider,
- real genlock/reference/PTP hardware behavior,
- hardware fill/key output,
- physical DMA / zero-copy / GPUDirect interoperability,
- physical GPU/VRAM utilization and professional real-time deadline qualification,
- real input-to-Program hardware latency,
- production network/IPC transport between Operator, ControlHost, RuntimeHost and AIHost,
- production remote API endpoint/version negotiation,
- Automation client semantic-equivalence proof,
- real OS-level Operator crash/restart/reconnect,
- real OS-level AIHost termination/restart,
- real ControlHost process loss over IPC,
- durable SQLite management persistence and restart reconstruction,
- durable journal checkpoints/replay recovery,
- trained Person Segmentation model accuracy,
- TensorRT/ONNX Runtime/DirectML production inference,
- production Model Package signing/supply-chain trust,
- physical recording hardware-encoder and sustained-storage qualification,
- long-duration soak qualification for release,
- security/compliance/release-readiness gates not otherwise evidenced,
- `rtaime 1.0.0 STABLE`, `VALIDATED` or `CERTIFIED` status.

Simulation and managed reference providers prove contracts and behavior only. They do not certify unavailable physical hardware.

## Completion interpretation

V1 End-to-End Proof is complete when the final branch demonstrates the required V1 **software architecture proof** reproducibly on the Windows reference CI runner and all remaining unsupported claims are explicitly left `UNVERIFIED`.

This distinction is intentional:

> No evidence, no claim.

> UNVERIFIED is not PASS.

> V1 proves the production architecture, not the size of the feature list.

## V1 Functional Gap Closure functional-gap closure addendum

V1 Functional Gap Closure closes two software gaps that were intentionally still listed as `UNVERIFIED` by the original V1 End-to-End Proof evidence boundary. The historical V1 End-to-End Proof list above remains unchanged because it describes what V1 End-to-End Proof itself proved; later work packages provide additional evidence rather than retroactively changing V1 End-to-End Proof.

### Headless automation

The existing `OperatorControlClient` and `IOperatorControlTransport` are reused as the headless V1 automation surface. `AutomationClientSemanticEquivalenceTests` exercises this path without WPF and verifies authoritative synchronization, Preview, CUT, DISSOLVE, stale-revision rejection, disconnect and reconnect/resynchronization.

No parallel automation authority, direct Runtime command path or second command model is introduced.

When final latest-head V1 Functional Gap Closure Required Gates are green, the V1 End-to-End Proof item `Automation client semantic-equivalence proof` has separate V1 Functional Gap Closure software evidence and no longer remains an unproven functional V1 gap.

### Actual reference recording payload

V1 Functional Gap Closure adds `ReferenceRecordingPayloadWriter` and `ReferenceRecordingPayloadReader` without changing the descriptor-only `RecordingProgramSample` contract. The RuntimeHost composition root supplies actual post-composite RGBA8 Program bytes and deterministic AFV Stereo 48 kHz Float32 bytes to this optional writer capability while storage I/O remains on the recorder worker.

The payload proof covers both V1 development formats, validates exact timing/format metadata and verifies SHA-256 media-payload integrity. A deterministic quota exercises storage exhaustion; separate failure evidence covers finalization failure and later-session recovery while Runtime remains committed and Program continues.

The deterministic reference payload remains the exact software-evidence lane. A separate Windows Media Foundation MP4 writer now qualifies MP4/H.264/AAC software interoperability through independent decode evidence; physical hardware-encoder selection, professional sustained-storage throughput and long-duration platform evidence remain `UNVERIFIED`.

### Functional-scope interpretation

After final latest-head V1 Functional Gap Closure Required Gates pass, no known **software-only functional allowlist gap** remains for V1. Remaining work is qualification/evidence work, including physical media I/O, timing/reference, hardware GPU deadlines, physical latency, physical recording throughput/encoder behavior, soak and release acceptance.

The detailed V1 Functional Gap Closure audit and retained evidence boundary are documented in `V1FunctionalGapClosure.md`.
