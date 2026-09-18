<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
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

Runtime Health & Performance HUD extends the existing observational plane rather than introducing a separate metrics system. RuntimeHost now exposes one bounded `V1RuntimePerformanceSnapshot` containing Runtime uptime, the active frame budget, the most recently observed Program-boundary processing duration, cumulative dropped-frame evidence and GPU telemetry evidence.

The dropped-frame counter is intentionally O(1). It retains only the previous scheduler-boundary timestamp and one cumulative count. It does not retain per-frame history, allocate a diagnostic collection, write files, call a remote endpoint or otherwise change Program scheduling.

Dropped-frame evidence combines missed scheduler frame periods with native Program-output backpressure/rejection counters when those counters exist. The definition is therefore explicit and does not reinterpret the monitoring-plane's intentionally lossy preview-frame drops as production dropped frames.

The Runtime support snapshot reuses this same performance snapshot and adds:

- `performance.frameBudgetMs`;
- `performance.lastFrameProcessingMs`;
- `performance.uptime`;
- `runtime.droppedFrames`;
- GPU device/hardware-acceleration evidence;
- optional GPU utilization and VRAM evidence.

GPU utilization and used VRAM remain `UNVERIFIED` when the active backend cannot provide a reliable measured value. A missing measurement must never be translated into zero utilization, zero VRAM use or a healthy PASS conclusion.

ControlHost derives the Operator health projection from existing Runtime snapshots, provider availability, Media observations and authoritative Control availability. The projection uses `PASS / FAIL / UNVERIFIED` evidence semantics and carries only metadata through management IPC.

The Operator consumes this projection on the existing bounded 200 ms management refresh. Runtime Health & Performance HUD adds no per-frame UI callback, no second telemetry polling task and no local GPU probing. No per-frame disk write is introduced.
