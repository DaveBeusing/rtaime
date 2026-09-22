<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Recording Foundation

Status: implementation foundation with deterministic reference evidence and Windows Media Foundation MP4 delivery.

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

V1 Functional Gap Closure adds an optional subsystem-local capability:

```text
IProgramRecordingPayloadWriter : IProgramRecordingWriter
	StagePayload
	DiscardPayload
```

This capability does not change `RecordingProgramSample`, Media contracts, Runtime contracts or IPC contracts. The RuntimeHost composition root may provide already-materialized Program/AFV reference bytes to a capable writer while the normal recorder queue continues to carry descriptor-level samples.

Qualified delivery writers remain behind the same writer boundary. `WindowsMediaFoundationMp4RecordingWriter` consumes the already-materialized Program payload supplied by RuntimeHost without changing Runtime, Control or the recording lifecycle.

## Local architectural-proof artifacts

`LocalRecordingManifestWriter` remains the descriptor-level architectural-proof writer. It persists Program sample identity, timing and opaque-handle references only.

V1 Functional Gap Closure adds `ReferenceRecordingPayloadWriter`, a deterministic software-only payload artifact for V1 functional proof. It persists:

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

The V1 Functional Gap Closure reference container is intentionally uncompressed and CI-verifiable. It is **not** a qualified professional codec/container. Professional sustained-storage throughput, DMA/device-surface resolution, hardware-encoder behavior and long-duration physical-platform media integrity remain `UNVERIFIED` until measured on the declared production environment.

## Professional MP4 delivery path

The default Windows RuntimeHost recording writer is `WindowsMediaFoundationMp4RecordingWriter`. The deterministic `.rtaime-recording` writer remains available as a separate test/evidence lane and is not replaced.

The qualified software delivery contract is exposed through `ProfessionalRecordingFormats.Mp4H264Aac` and defines:

- container: ISO Base Media File Format (MP4);
- file extension: `.mp4`;
- video codec: H.264/AVC Main Profile at 20 Mbit/s;
- audio codec: AAC-LC at 192 kbit/s;
- accepted Program video: 1920x1080 progressive RGBA8 at 50 fps or 60000/1001 fps;
- accepted Program audio: stereo 48 kHz Float32;
- encoder input conversion: RGBA8 to NV12 and Float32 to signed 16-bit PCM on the asynchronous recording worker;
- Media Foundation clock: exact Program timestamps converted to 100 ns units with a shared A/V origin;
- finalization: `<name>.partial.mp4` is finalized first and promoted to `<name>.mp4` only after Media Foundation finalization succeeds;
- collision protection: an exclusive `<name>.mp4.lock` reservation prevents two sessions from publishing the same target.

The writer rejects unsupported formats, missing Program audio, non-monotonic timestamps and incompatible target extensions. Abort/failure cleanup removes partial and reservation artifacts without altering committed Program execution.

No third-party codec package is introduced. H.264 and AAC-LC encoding use the Windows Media Foundation components shipped with the supported Windows platform. Availability therefore fails closed outside Windows. Hardware-encoder selection, professional storage-throughput guarantees and long-duration physical-platform qualification remain separate evidence obligations and must not be inferred from the software codec/container qualification.

## Storage exhaustion and recovery

`ReferenceRecordingPayloadWriter` supports a deterministic payload-byte quota for failure evidence. Exhausting the quota raises a writer-side storage failure on the asynchronous recorder worker rather than blocking Program execution.

V1 Functional Gap Closure verifies that:

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

Recording Foundation verifies at minimum:

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

V1 Functional Gap Closure adds verification for:

- actual reference Program video payload bytes;
- actual reference AFV audio payload bytes;
- both 1080p50 and 1080p59.94 development formats;
- deterministic reference-container parsing and integrity validation;
- storage quota exhaustion;
- Program continuity after storage failure;
- finalize failure;
- later-session recovery.

Professional MP4 qualification additionally verifies:

- exact machine-readable container/codec/profile/audio/input-format capability;
- writer-authoritative safe `.mp4` target normalization;
- both 1080p50 and 1080p59.94 Program formats;
- finalized MP4 readability through the independent local-media decoder;
- decoded H.264/AAC identity and A/V timestamp alignment;
- repeated MP4 recordings in one RuntimeHost lifecycle;
- controlled writer/storage failure while committed Program continues;
- a 250-frame / five-second 1080p50 Program timeline with bounded recording backlog and zero recording drops/writer failures.

The sustained software sequence validates writer lifecycle/resource behavior; it is deliberately not a physical storage-throughput or hardware-encoder benchmark.

Targeted qualification commands:

```powershell
dotnet test tests/rtaime.Tests.Unit/rtaime.Tests.Unit.csproj -c Release --filter FullyQualifiedName~ProfessionalRecordingFormatTests
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj -c Release --filter FullyQualifiedName~ProfessionalRecordingMp4IntegrationTests
```

The repository Required Gates remain the authoritative broader build/test/packaging validation.

Physical hardware-encoder, sustained storage-throughput and long-duration platform qualification remain `UNVERIFIED` unless executed on the declared production environment. The software MP4/H.264/AAC path is qualified separately by Windows integration tests and independent Media Foundation decode evidence.


## Operator recording workflow

Recording Operator Workflow promotes the existing recording foundation into an explicit Operator workflow without moving recording authority or storage execution into WPF.

The command path is:

```text
Operator
  -> rtaime.Client
  -> ControlHost
  -> RuntimeHost
  -> ProgramRecorder
  -> WindowsMediaFoundationMp4RecordingWriter
```

The Operator exposes explicit **START REC** and **STOP REC** actions plus confirmed recording state, elapsed time, destination directory, file name, final output path, sample statistics and failure detail. Recording commands do not advance the authoritative Production revision.

RuntimeHost remains the recording execution owner. The normal Program boundary stages the already-produced post-transition/post-graphics RGBA Program pixels and the post-AFV/post-gain Float32 Program audio payload before the bounded `ProgramRecorder` enqueue. Storage remains on the recorder worker and never moves into the Program hot path.

### Destination and naming

Recording writers implement the optional `IConfigurableProgramRecordingWriter` capability. The writer owns target normalization and returns the authoritative file name to RuntimeHost. The production MP4 writer accepts only a single safe file-name component and appends `.mp4` when no extension is supplied; the deterministic reference writer independently retains `.rtaime-recording`. Existing output-id naming remains the fallback for callers that do not configure a target.

The default RuntimeHost process composes `WindowsMediaFoundationMp4RecordingWriter` under the current user's local application-data `rtaime/recordings` directory. Operator-selected destinations override that default per recording. Tests and deterministic evidence workflows may explicitly inject `ReferenceRecordingPayloadWriter`.

Final publication retains create-new semantics. An existing target is rejected rather than overwritten. The professional writer promotes `.partial.mp4` only after Media Foundation finalization succeeds; the reference writer independently retains its deterministic footer/hash finalization.

### Validation and evidence boundary

The Recording Operator Workflow result is externally readable through `ReferenceRecordingPayloadReader`. Acceptance evidence validates:

- at least one Program video sample;
- matching audio sample presence;
- checksum-valid finalization;
- post-graphics Program pixels in the persisted payload;
- repeated recordings with distinct names in one RuntimeHost lifecycle;
- controlled storage failure propagated back to the Operator while Runtime Program remains committed.

The `.rtaime-recording` artifact remains an uncompressed architectural-proof container and is still validated by the deterministic reader. The production Windows recording path now publishes MP4/H.264/AAC and is independently reopened through the existing Media Foundation local-media decoder in integration qualification. MOV/MXF delivery, ISO input recording, replay, segment recording, cloud upload, hardware-encoder guarantees and physical storage-throughput guarantees remain outside this capability.
