<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Recording Foundation

Status: AP-10 implementation foundation, extended by AP-38 reference payload closure.

## Change classification

- `CONTRACT`
- `REALTIME_CRITICAL`

The package introduces an explicit Recording subsystem API and attaches a bounded enqueue path to committed Program output. It does not introduce a new architectural direction and therefore does not require a new ADR.

## Architecture boundary

The approved project graph remains unchanged:

```text
rtaime.Runtime ─────┐
rtaime.Media ───────┼─> rtaime.RuntimeHost ─> composition
rtaime.Recording ───┘
```

`rtaime.Recording` depends only on Core, Media.Contracts and Provider.Contracts. It does not depend on Runtime or Media implementations.

`RuntimeRecordingBridge` lives in `rtaime.RuntimeHost`, the existing composition root. It observes the currently committed Runtime execution and only accepts a video frame when its source matches the committed binding for the configured Program sink.

Recording never creates, changes, commits or rolls back production authority.

## Recording contract

The subsystem-local contract is versioned independently through `RecordingContractVersion` (`1.0`). The contract contains:

- `RecordingSessionId`
- `RecordingOutputId`
- `RecordingOutputDescriptor`
- `RecordingStartRequest`
- `RecordingProgramSample`
- lifecycle/start/stop/enqueue result types
- recording observations and statistics

No 29th managed project is introduced. The contract intentionally remains in the already approved `rtaime.Recording` subsystem because V1 has no approved cross-process `Recording.Contracts` project.

A `RecordingOutputDescriptor` explicitly ties a stable recording output identity to one Program `MediaSinkId`.

## Lifecycle

Normal lifecycle:

```text
Idle / Completed
	↓ StartAsync
Recording
	↓ StopAsync / ShutdownAsync
Finalizing
	↓ queue drained + writer finalized
Completed
```

Writer/open/write/finalize failures move the session to `Failed` and create stable observations.

A later new recording session may be started with a new session/output identity after a completed or failed session once the previous worker has terminated. A concurrent second start is rejected fail-closed.

## Program-path isolation

`ProgramRecorder.TryEnqueue` is deliberately synchronous and storage-I/O-free.

It only:

1. validates recorder state and monotonically increasing Program sequence,
2. checks a bounded in-memory queue,
3. enqueues a descriptor-level sample or drops/rejects it,
4. returns immediately.

Storage work executes on the recorder worker.

If the queue is full:

```text
recording.backpressure.dropped
recording.backpressure.queue_full
```

is reported and the sample is dropped. Program execution is not blocked.

If the writer fails, the recording becomes `Failed`, pending recording samples are discarded, and the Runtime committed execution remains unchanged.

This implements the V1 failure-isolation rule: recorder backpressure or storage failure must not synchronously block RT-critical Program execution.

## Descriptor boundary

A `RecordingProgramSample` transports existing `FrameDescriptor` and optional `AudioBufferDescriptor` objects. It does not add bulk pixel/audio arrays or expose vendor/device pointers.

The stable backend interface remains:

```text
IProgramRecordingWriter
	OpenAsync
	WriteAsync
	FinalizeAsync
	AbortAsync
```

AP-38 adds an optional subsystem-local capability:

```text
IProgramRecordingPayloadWriter : IProgramRecordingWriter
	StagePayload
	DiscardPayload
```

This capability does not change `RecordingProgramSample`, Media contracts, Runtime contracts or IPC contracts. The RuntimeHost composition root may provide already-materialized Program/AFV reference bytes to a capable writer while the normal recorder queue continues to carry descriptor-level samples.

A future qualified encoder/device implementation can resolve opaque media handles behind the writer boundary without changing Runtime, Control or the recording lifecycle.

## Local architectural-proof artifacts

`LocalRecordingManifestWriter` remains the descriptor-level architectural-proof writer. It persists Program sample identity, timing and opaque-handle references only.

AP-38 adds `ReferenceRecordingPayloadWriter`, a deterministic software-only payload artifact for V1 functional proof. It persists:

- actual post-composite Program RGBA8 bytes supplied by the RuntimeHost reference path;
- deterministic Stereo 48 kHz Float32 AFV sample bytes for the exact Program boundary;
- exact video/audio timing and format metadata;
- video/audio sample counts;
- SHA-256 integrity over persisted media payloads.

Both protected local artifact paths use the same lifecycle concept:

```text
<RecordingOutputId>.partial
	↓ orderly FinalizeAsync
<RecordingOutputId>.rtaime-recording
```

`CreateNew` semantics prevent silent overwrite. The reference payload artifact is promoted only after queued samples are written, footer counts and integrity data are emitted, and the writer flushes/finalizes successfully.

`ReferenceRecordingPayloadReader` validates the file magic/version, sample structure, payload lengths, footer counts, trailing-data absence and SHA-256 integrity.

The AP-38 reference container is intentionally uncompressed and CI-verifiable. It is **not** a qualified professional codec/container. Professional storage throughput, DMA/device-surface resolution, codec interoperability, hardware encoding and long-duration media integrity remain `UNVERIFIED` until measured on the declared production environment.

## Storage exhaustion and recovery

`ReferenceRecordingPayloadWriter` supports a deterministic payload-byte quota for failure evidence. Exhausting the quota raises a writer-side storage failure on the asynchronous recorder worker rather than blocking Program execution.

AP-38 verifies that:

- storage exhaustion moves Recording to `Failed`;
- no valid final artifact is published;
- Runtime remains committed;
- Program and AFV continue on later boundaries;
- finalization failure is isolated;
- a later recording session can recover after a failed finalization.

## Observability

Representative codes include:

- `recording.started`
- `recording.finalizing`
- `recording.finalized`
- `recording.backpressure.dropped`
- `recording.sequence.rejected`
- `recording.writer.failed`
- `recording.abort.failed`
- `recording.shutdown`

Statistics track accepted, written, dropped, rejected and writer-failure counts.

RuntimeHost also emits a non-authoritative `recording.payload.stage.failed:<ExceptionType>` observation if optional reference-payload staging itself cannot be materialized. Such a staging error is not thrown into Program execution; the recorder worker subsequently owns recording failure semantics.

## Verification obligations

AP-10 verifies at minimum:

- start
- stop
- repeated recording
- invalid start
- output unavailable
- Runtime shutdown
- recording failure without production loss
- committed Program binding integration
- orderly local finalization
- bounded/nonblocking enqueue under slow storage
- architecture graph remains unchanged

AP-38 adds verification for:

- actual reference Program video payload bytes;
- actual reference AFV audio payload bytes;
- both 1080p50 and 1080p59.94 development formats;
- deterministic reference-container parsing and integrity validation;
- storage quota exhaustion;
- Program continuity after storage failure;
- finalize failure;
- later-session recovery.

Hardware/codec/storage qualification must remain `UNVERIFIED` unless executed on the declared production environment.
