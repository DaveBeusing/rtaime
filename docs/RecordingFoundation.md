# Recording Foundation

Status: AP-10 implementation foundation.

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

A later new recording session may be started with a new session/output identity after a completed session. A concurrent second start is rejected fail-closed.

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

The backend interface is:

```text
IProgramRecordingWriter
  OpenAsync
  WriteAsync
  FinalizeAsync
  AbortAsync
```

A future qualified encoder/device implementation can resolve opaque media handles behind this boundary without changing Runtime, Control or the recording lifecycle.

## Local architectural-proof artifact

`LocalRecordingManifestWriter` provides a protected local artifact lifecycle for the hardware-free architectural proof:

```text
<RecordingOutputId>.partial
    ↓ orderly FinalizeAsync
<RecordingOutputId>.rtaime-recording
```

It uses `CreateNew` semantics so an existing output identity is never silently overwritten. The final artifact is promoted only after the queue drains and the writer flushes/finalizes successfully.

The current local writer persists Program sample identity, timing and opaque-handle references. It does **not** claim to be a qualified encoded video/audio container. Real codec payload writing, professional storage throughput, DMA/device-surface resolution and long-duration media integrity require separate measured backend evidence and remain `UNVERIFIED` until such a backend/environment exists.

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

Hardware/codec/storage qualification must remain `UNVERIFIED` unless executed on the declared production environment.
