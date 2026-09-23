<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>
# Observability, Diagnostics and Support Snapshot

## Scope

Observability & Diagnostics introduces a non-authoritative diagnostics plane for supportability and production operations. Diagnostics observe existing host, runtime, provider and persistence state; they do not create production truth and they do not participate in command acceptance, Runtime commit, media timing, inference admission or recording continuity.

The support snapshot format is intentionally metadata-only. It must never contain raw video/audio payloads, GPU surface contents, credentials, signing material, access tokens or connection secrets.

## Common support snapshot

`rtaime.Core` provides the dependency-neutral diagnostics foundation:

- `BoundedDiagnosticBuffer` is a fixed-capacity in-memory ring buffer;
- `DiagnosticEvent` carries sequence, UTC timestamp, severity, category, code, message, optional failure and sanitized dimensions;
- `DiagnosticRedactor` redacts secret-bearing keys and common inline credential forms;
- `SupportSnapshotBuilder` creates a stable `1.0` support snapshot envelope;
- `SupportSnapshotSerializer` emits deterministic JSON with fixed top-level ordering and ordinal key ordering.

Snapshot creation is on demand. No per-frame disk write is introduced.

## Structured host logs

All executable hosts write the same structured JSON Lines format through `rtaime.Core.HostLog`:

- `AppHost`
- `ControlHost`
- `RuntimeHost`
- `AIHost`
- `Operator`

A normal AppHost launch creates one log session and publishes `RTAIME_LOG_SESSION_ID` plus `RTAIME_LOG_ROOT` to the child-process environment. ControlHost-supervised RuntimeHost and AIHost processes therefore inherit the same session identifier. The Operator started by AppHost does the same. A single application run can consequently be reconstructed across process boundaries by sorting all records in the session directory by `timestampUtc`.

Interactive runs default to `%LOCALAPPDATA%\rtaime\logs\<session-id>\`. Windows service or externally managed engine runs default to `<state-root>\logs\<session-id>\`, where the default state root is under `%PROGRAMDATA%\rtaime`. `--log-root` or `RTAIME_LOG_ROOT` can override the root explicitly.

Each `.jsonl` record carries schema version, UTC timestamp, per-process sequence, severity, host, process ID, managed thread ID, session ID, optional instance ID, category, stable event code, message, sanitized dimensions and optional sanitized exception type/message/detail. Files remain readable while the process is running.

The default file limit is 16 MiB per segment. A new segment is opened before the next record would exceed that limit. Session directories whose newest JSONL file is older than 14 days are removed on a later host start. Both values are bounded and configurable:

- `RTAIME_LOG_LEVEL` / `--log-level`: `Trace`, `Information`, `Warning`, `Error`, `Critical`;
- `RTAIME_LOG_MAX_FILE_MB` / `--log-max-file-mb`: 1–256 MiB;
- `RTAIME_LOG_RETENTION_DAYS` / `--log-retention-days`: 1–90 days;
- `RTAIME_LOG_SESSION_ID` / `--log-session-id`: optional stable session identifier;
- `RTAIME_LOG_ROOT` / `--log-root`: explicit log root.

Logging is best-effort. A directory, permission or file-system failure does not become a production failure; the logger disables the file writer temporarily, emits a minimal stderr diagnostic where possible and retries later. Unhandled process exceptions and unobserved task exceptions are captured through common process-level hooks. Host entry points additionally log configuration admission, lifecycle/readiness transitions, managed-child supervision where applicable, graceful stop signals and terminal failures.

The Operator keeps its human-readable crash report for immediate support workflows, but stores it in the active structured-log session directory and applies the shared redaction policy. The structured `operator.dispatcher-unhandled-exception` record is the machine-readable correlate.

Structured host logs remain outside the real-time execution path. Runtime frame processing, media callbacks, GPU work and inference execution must never write a file log per frame. High-rate state remains in existing bounded counters/snapshots; host files record lifecycle, configuration, transition, recovery and failure evidence only.

For a local debugging session, start with the newest session directory, inspect `AppHost` for launch/lifecycle context, then correlate `ControlHost`, `RuntimeHost`, `AIHost` and `Operator` records by `timestampUtc`, `sessionId`, process ID and stable event code. Exceptions retain bounded stack detail after redaction, so the original process and failure boundary can be identified without relying only on a final crash file.

## Host projections

### ControlHost

`ControlHostDiagnostics` projects:

- process lifecycle and health;
- durable recovery state;
- Control IPC instance and state version;
- Runtime connectivity and provider count;
- authoritative production revision and Preview/Program source identity when authority is present;
- bounded production-journal health and counters;
- up to the latest 64 already-retained journal observations.

The production journal remains the observation source of truth; diagnostics do not duplicate or persist a second authority log.

### RuntimeHost

`RuntimeHostDiagnostics` projects:

- process lifecycle and IPC/monitoring readiness;
- committed Runtime execution status/revision;
- timing and input-signal state;
- active GPU surface count;
- monitoring capture/drop/subscriber counters;
- recording lifecycle and write/drop/failure counters;
- current reference video format;
- the latest Runtime observations, capped in the exported support view.

No RGBA monitoring frame or other bulk media payload is serialized into a support snapshot.

### AIHost

`AIHostDiagnostics` projects:

- process lifecycle and IPC readiness;
- governed inference availability;
- active requests/executions;
- compute and VRAM reservations;
- completed/rejected/timed-out/cancelled/failed counters;
- provider/capability counts;
- the latest governed inference observations, capped in the exported support view.

## Security and redaction

Configuration and event data passes through `DiagnosticRedactor`. Keys containing password, secret, token, API key, authorization, credential, cookie, connection-string, SAS/signature or private-key semantics are replaced with `[REDACTED]`. Common inline assignment and Bearer credential forms are also removed. Diagnostic values are length-bounded.

Support snapshots are not a secret-storage mechanism. New diagnostic fields must be reviewed under the same fail-closed assumption: if a value might contain credentials or media payloads, do not add it unless a deterministic sanitizer and regression test exist.

## Realtime and boundedness rules

Diagnostics must remain subordinate to production continuity:

- no synchronous file or network I/O is permitted in the media hot path;
- diagnostic buffers are bounded;
- support JSON is generated only on demand;
- monitoring/media byte arrays are never embedded in support snapshots;
- slow support consumers must not affect Runtime scheduling, Program output, AI execution or recording;
- host diagnostics read existing immutable/snapshot state rather than taking ownership of it.

## Verification

Observability & Diagnostics verification includes:

- ring-buffer boundedness and chronological retention;
- secret-key and inline credential redaction;
- deterministic support-snapshot serialization;
- explicit assertions that media-payload field names are absent from generated support JSON;
- structural Quality-gate policy checking for schema marker, bounded diagnostics, redaction, host projections and absence of byte payload fields.

The existing Required Gates remain authoritative for architecture, contracts, unit/integration behavior, security, provider smoke and packaged end-to-end qualification.


## Runtime Health & Performance HUD Runtime health and performance projection

Runtime Health & Performance HUD extends the existing observational plane rather than introducing a separate metrics system. RuntimeHost exposes one bounded `V1RuntimePerformanceSnapshot` containing Runtime uptime, the active frame budget, the most recently observed core render duration, measured Program output cadence, cumulative dropped-frame evidence, CPU identity/utilization, system-memory usage/capacity and GPU identity/utilization/VRAM evidence. Windows CPU and memory measurements use OS APIs; qualified NVIDIA measurements use NVML. Hardware sampling is cached for 500 ms and is read only from the management snapshot path, never from Program frame processing. A transient hardware read miss retains the most recent qualified sample for up to three seconds instead of clearing the UI immediately; expired or never-qualified evidence still resolves to `UNVERIFIED`.

The Program-cadence observation is intentionally O(1). `RuntimeFrameDropCounter` retains only the previous scheduler-boundary timestamp, one cumulative dropped-frame count and one exponentially smoothed Output-FPS scalar. It does not retain per-frame history, allocate a diagnostic collection, write files, call a remote endpoint or otherwise change Program scheduling. Support snapshots expose the measured rate as `performance.outputFramesPerSecond` when available.

Dropped-frame evidence combines missed scheduler frame periods with native Program-output backpressure/rejection counters when those counters exist. The definition is therefore explicit and does not reinterpret the monitoring-plane's intentionally lossy preview-frame drops as production dropped frames.

The Runtime support snapshot reuses this same performance snapshot and adds:

- `performance.frameBudgetMs`;
- `performance.lastFrameProcessingMs` (core Runtime render/composite duration; full pipeline timing remains owned by the timing qualification probe);
- `performance.uptime`;
- `runtime.droppedFrames`;
- GPU device/hardware-acceleration evidence;
- optional GPU utilization and VRAM evidence.

GPU utilization and used VRAM remain `UNVERIFIED` when the active backend cannot provide a reliable measured value. A missing measurement must never be translated into zero utilization, zero VRAM use or a healthy PASS conclusion.

ControlHost derives the Operator health projection from existing Runtime snapshots, provider availability, Media observations and authoritative Control availability. The projection uses `PASS / FAIL / UNVERIFIED` evidence semantics and carries only metadata through management IPC.

The Operator consumes this projection on the existing bounded 200 ms management refresh. Runtime Health & Performance HUD adds no per-frame UI callback, no second telemetry polling task and no local GPU probing. No per-frame disk write is introduced.


## Operator lifecycle projection

The Operator derives its compact engine lifecycle and Program-safety presentation from the existing authoritative management snapshot. It does not add another telemetry collector.

The projection uses the existing Control, Runtime, Media, Provider and GPU Provider `PASS / FAIL / UNVERIFIED` evidence together with Runtime readiness, connection freshness and the existing AI showcase state. Unknown evidence remains unverified and is never converted to healthy.

The existing bounded 200 ms management refresh is also the reconnect mechanism. When Control synchronization becomes stale, mutations are blocked while the same refresh continues requesting a full snapshot. Recovery is complete only after a new authoritative snapshot is accepted. Repeated failures update one bounded status/error presentation rather than accumulating modal errors or an unbounded event list.
