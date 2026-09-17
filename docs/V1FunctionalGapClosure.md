<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# V1 Functional Gap Closure

**Work package:** AP-38  
**Change classification:** `CONTRACT`, `REALTIME_CRITICAL`

## Purpose

AP-38 closes the remaining software-only functional gaps in the V1 allowlist before physical hardware qualification. It does not expand the V1 feature scope and it does not convert hardware, codec, timing, latency, soak, security/compliance or release evidence to `PASS`.

The implementation preserves the existing authority model:

```text
Operator / Headless Automation
	↓
rtaime.Client
	↓
ControlHost transport
	↓
ControlHost production authority
	↓
Prepared Execution
	↓
RuntimeHost committed execution
```

Automation never calls Runtime, Persistence, Media or AI implementations directly and never owns Production Truth.

## Automation semantic equivalence

The existing transport-neutral `IOperatorControlTransport` and `OperatorControlClient` are the V1 automation API. AP-38 deliberately does not create a second command stack or another managed project.

`AutomationClientSemanticEquivalenceTests` exercises the same client path without WPF and proves:

- authoritative snapshot synchronization;
- Preview selection;
- CUT;
- DISSOLVE;
- optimistic-concurrency rejection of a stale revision;
- disconnect;
- reconnect and authoritative resynchronization.

A competing headless client cannot commit a mutation against an obsolete revision. After reconnect it must fetch current ControlHost truth before issuing another command.

## Reference recording payload

The stable `RecordingProgramSample` contract remains descriptor-only. Bulk video/audio bytes are not added to normal Media, Runtime or cross-process contracts.

AP-38 adds the optional subsystem-local `IProgramRecordingPayloadWriter` capability. `V1RuntimeHostService` detects that capability at composition time and supplies media payload only while recording is active.

The software-only reference path persists:

- actual post-composite Program RGBA8 bytes from the existing GPU readback;
- actual deterministic Stereo 48 kHz Float32 AFV sample bytes for the exact audio window associated with that Program boundary;
- video format, exact frame rate and timebase;
- video sequence and timestamp;
- audio stream/timing metadata;
- sample counts;
- SHA-256 integrity over persisted media payloads.

The deterministic audio payload reflects the effective AFV result, including gain/mute behavior, and is generated only for the reference recording backend.

The binary reference container is deliberately uncompressed. It is not a broadcast/professional codec claim.

## Atomic artifact lifecycle

`ReferenceRecordingPayloadWriter` retains the protected lifecycle:

```text
<RecordingOutputId>.partial
	↓ successful queue drain + writer finalization
<RecordingOutputId>.rtaime-recording
```

The final artifact is promoted only after footer counts and the payload integrity hash have been written and flushed successfully. Abort/failure removes the partial artifact where possible. Existing output identities remain protected from silent overwrite.

`ReferenceRecordingPayloadReader` validates:

- format/version magic;
- sample record structure;
- complete video/audio payload lengths;
- footer sample counts;
- absence of trailing data;
- SHA-256 payload integrity.

## Failure isolation and storage exhaustion

The synchronous Program path still performs no storage I/O. Runtime only stages already-materialized reference payload into bounded recording-side state and then uses the existing `ProgramRecorder.TryEnqueue` path.

Payload staging failure is observed but not thrown into Program execution. Storage writing remains on the recorder worker.

AP-38 evidence covers:

- output/open failure;
- asynchronous write failure;
- finalization failure;
- deterministic storage exhaustion through a reference-writer byte quota;
- continued committed Runtime/Program execution after recording failure;
- later recording-session recovery after a failed finalization.

A storage-exhausted recording transitions to `Failed`; no apparently valid final artifact is published. Program and AFV continue at the next boundary and Runtime remains committed.

## Development-format evidence

`ReferenceRecordingPayloadTests` runs the actual RuntimeHost path for both V1 development formats:

- 1920x1080p50 RGBA8;
- 1920x1080p59.94 RGBA8.

For each format the test compares the persisted video payload byte-for-byte with `V1ProgramBoundaryResult.ProgramPixels`, validates the exact frame-rate/timebase metadata, validates the AFV sample window and verifies persisted Float32 audio payload.

## V1 functional audit

The following software capabilities are considered AP-38 closure candidates. A candidate is `PASS` only when the latest-head Required Gates covering its implementation are green. A stale or earlier workflow run is not evidence for a later commit.

| V1 capability | Repository evidence | AP-38 classification |
|---|---|---|
| Two video inputs | V1 end-to-end proof | software evidence |
| Preview / Program | V1 end-to-end proof | software evidence |
| CUT | V1 end-to-end proof | software evidence |
| DISSOLVE | V1 end-to-end proof | software evidence |
| Static RGBA layer | V1 end-to-end proof | software evidence |
| Dynamic RGBA layer | V1 end-to-end proof | software evidence |
| 1080p50 | V1 end-to-end + AP-38 recording payload test | software evidence |
| 1080p59.94 | V1 end-to-end + AP-38 recording payload test | software evidence |
| Audio Follow Video | V1 end-to-end + AP-38 recording payload test | software evidence |
| Program recording lifecycle | Recording foundation tests | software evidence |
| Actual reference recording video payload | `ReferenceRecordingPayloadTests` | software evidence |
| Actual reference AFV audio payload | `ReferenceRecordingPayloadTests` | software evidence |
| Recording write failure isolation | `RecordingFailureIsolationTests` | software evidence |
| Recording storage exhaustion | `RecordingStorageExhaustionIntegrationTests` | software evidence |
| Recording finalize failure/recovery | `RecordingFinalizeRecoveryTests` | software evidence |
| Operator client | existing Client/Operator evidence | software evidence |
| Headless automation client | `AutomationClientSemanticEquivalenceTests` | software evidence |
| Automation semantic equivalence | `AutomationClientSemanticEquivalenceTests` | software evidence |
| Operator reconnect/resync | existing IPC/client evidence plus AP-38 headless resync | software evidence |
| Lost-input fallback | V1 end-to-end/failure evidence | software evidence |
| Governed Person Segmentation | V1 end-to-end AI evidence | software evidence |
| AI unavailable fallback | V1 end-to-end AI evidence | software evidence |
| Durable production state | existing persistence/recovery evidence | software evidence |
| Production journal/recovery | existing persistence/journal evidence | software evidence |

## Explicitly UNVERIFIED after AP-38

AP-38 does not provide evidence for:

- qualified professional capture/output hardware;
- SDI electrical/output qualification;
- real genlock/reference/PTP behavior;
- hardware fill/key output;
- physical DMA, zero-copy or GPUDirect interoperability;
- professional GPU/VRAM deadline qualification;
- physical input-to-Program latency;
- professional recording codec/container quality or interoperability;
- hardware encoder qualification;
- long-duration soak qualification;
- broader release-security/compliance acceptance not otherwise evidenced;
- `1.0.0 STABLE`, `VALIDATED` or `CERTIFIED` status.

The `.rtaime-recording` artifact introduced here is a deterministic reference container for software proof only. It must not be described as a qualified broadcast recording format.

## Architecture result

AP-38 adds no Production Authority, no new host, no new cross-process contract project and no external codec dependency. The existing managed project topology remains unchanged.

The V1 software feature allowlist is closed when the final AP-38 head passes all Required Gates. Remaining V1 work then moves from feature implementation to physical/reference-platform qualification and release evidence.

> No evidence, no claim.

> UNVERIFIED is not PASS.
