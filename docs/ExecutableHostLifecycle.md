<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Executable Host Lifecycle

Executable Host Lifecycle established `rtaime.ControlHost`, `rtaime.RuntimeHost`, and `rtaime.AIHost` as long-lived executable processes. Production IPC & Remote API added the local Windows production IPC control plane without moving authority or execution ownership between hosts. Durable Persistence & Journal added bounded durable ControlHost journal/checkpoint composition while keeping SQLite outside RT-critical media execution. Process Recovery & Supervision adds process supervision, HostInstanceId-aware reconnect and durable ControlHost authority recovery/reconciliation.

## Lifecycle

Each service host follows the operational sequence:

```text
Created
  -> Starting
  -> Ready | Degraded
  -> Draining
  -> Stopped
```

A startup, run-loop, or shutdown failure transitions the host to `Failed`. `Ctrl+C` and process-exit signals request cancellation; the host then drains owned IPC and subsystem resources before returning a structured exit code.

A hard process termination does not execute graceful drain. Process Recovery & Supervision therefore treats restart/reconnect as a separate recovery path rather than pretending graceful shutdown semantics occurred.

## Product startup layer

The canonical product entry point is `rtaime.exe`, implemented by `rtaime.AppHost`. AppHost is intentionally outside the service-host authority graph:

```text
rtaime.exe / AppHost
├── starts or adopts ControlHost
│   ├── supervises RuntimeHost
│   └── supervises AIHost
└── opens Operator after qualified readiness
```

AppHost does not take project references on the service hosts and does not directly supervise RuntimeHost or AIHost. It observes product lifecycle state and reuses the existing ControlHost readiness evidence and Named Pipe readiness checks.

Lifecycle ownership is explicit. Ephemeral local sessions may stop a ControlHost lifecycle they own; interactive persistent sessions and externally managed sessions do not tear the engine down when Operator exits. The persistent Windows-service path runs the same AppHost lifecycle in HeadlessEngine mode. A top-level ControlHost loss fails that service process so Windows Service Control Manager recovery can restart the persistent lifecycle and require fresh qualified readiness.

See `docs/ApplicationStartup.md` and `docs/WindowsProductionLifecycle.md` for startup profiles, ownership, Windows service operation and qualification semantics.

## Exit semantics

| Exit code | Meaning |
| ---: | --- |
| `0` | Clean shutdown |
| `2` | Invalid configuration |
| `3` | Subsystem/composition startup failure |
| `4` | Shutdown/drain failure or timeout |
| `10` | Unexpected run-loop failure |

## Configuration

Configuration is loaded from command-line values first, then environment variables, then V1 defaults. Command-line values use `--key=value`.

### ControlHost

Environment variables:

- `RTAIME_CONTROL_PRODUCTION_ID`
- `RTAIME_CONTROL_SOURCE_A_ID`
- `RTAIME_CONTROL_SOURCE_B_ID`
- `RTAIME_CONTROL_PRODUCTION_NAME`
- `RTAIME_CONTROL_JOURNAL_CAPACITY`
- `RTAIME_CONTROL_JOURNAL_RETAINED_CAPACITY`
- `RTAIME_CONTROL_CHECKPOINT_CAPACITY`
- `RTAIME_CONTROL_DURABILITY_ROOT`
- `RTAIME_CONTROL_ENDPOINT`
- `RTAIME_RUNTIME_ENDPOINT`
- `RTAIME_CONTROL_CONNECT_TIMEOUT_MS`
- `RTAIME_CONTROL_REQUEST_TIMEOUT_MS`
- `RTAIME_CONTROL_RUNTIME_RETRY_MS`
- `RTAIME_CONTROL_SHUTDOWN_TIMEOUT_MS`

Optional local child supervision additionally uses:

- `RTAIME_RUNTIME_EXECUTABLE`
- `RTAIME_AI_EXECUTABLE`
- `RTAIME_AI_ENDPOINT`
- `RTAIME_SUPERVISION_PROBE_TIMEOUT_MS`
- `RTAIME_SUPERVISION_PROBE_INTERVAL_MS`
- `RTAIME_SUPERVISION_RESTART_BACKOFF_MS`
- `RTAIME_SUPERVISION_MAX_START_ATTEMPTS`

ControlHost composes the authoritative Control service, bounded production journal, Operator-facing Named Pipe endpoint and RuntimeHost transport. It may start while RuntimeHost is absent and reports `Degraded`. The background binding loop performs handshake, provider refresh, initial Runtime commit and later RuntimeHost-instance reconciliation. A Runtime process replacement never advances authoritative Production Revision by itself.

Two independent SQLite-backed durability lanes live under the configured durability root:

```text
<DurabilityRoot>/<ProductionId>-<ControlEndpoint>/management.db
<DurabilityRoot>/<ProductionId>-<ControlEndpoint>/production-journal.db
```

The default root is `%LOCALAPPDATA%/rtaime/data` on Windows, with an application-directory fallback when a local application-data folder is unavailable.

`management.db` stores management/configuration documents and controlled production checkpoints. `production-journal.db` is the purpose-built append-only causal journal. The journal and checkpoint writers use independent bounded background queues. Authoritative commits and Program/media execution never synchronously wait for SQLite. Confirmed authoritative revisions are checkpointed asynchronously; journal/checkpoint pressure or storage failures remain observable and are drained/fail-closed during orderly shutdown.

Process Recovery & Supervision verifies both durability lanes before activating recovered authority. A valid latest checkpoint restores the exact persisted Production Revision and routing, after which Runtime execution is queried and reconciled against the committed Runtime `AuthoritySnapshot`, not against Runtime-local `ExecutionRevision`. A Runtime execution whose AuthoritySnapshot matches the restored Control ProductionId and Production Revision is rebound as-is even when its ExecutionRevision differs. A Runtime with no committed authority or an older matching AuthorityRevision is reapplied at the same Control authority revision. A newer or foreign Runtime authority is a fail-closed recovery conflict.

Optional RuntimeHost/AIHost supervision is endpoint-driven. Existing reachable endpoints are adopted without duplicate process launch. Restart attempts are bounded. The ControlHost executable may launch configured Runtime/AI executables. Product startup may start or adopt ControlHost through AppHost, while Windows Service Control Manager may own the persistent top-level lifecycle. ControlHost does not supervise itself; when the persistent service process fails, configured Service Control Manager recovery starts a new top-level lifecycle that must restore qualified readiness.

### RuntimeHost

Environment variables:

- `RTAIME_RUNTIME_SOURCE_A_ID`
- `RTAIME_RUNTIME_SOURCE_B_ID`
- `RTAIME_RUNTIME_FORMAT` (`1080p50` or `1080p59.94`)
- `RTAIME_RUNTIME_ENDPOINT`
- `RTAIME_RUNTIME_SHUTDOWN_TIMEOUT_MS`

RuntimeHost composes the existing transactional runtime, virtual media reference provider, managed GPU provider path, Audio Follow Video, Program output, recording integration and a Control-facing Named Pipe server. The primary Control-facing listener is a first-class RuntimeHost dependency: `Ready/Healthy` is valid only while that listener remains actively accepting. A terminal primary-listener failure immediately transitions RuntimeHost to `Failed/Unhealthy`, cancels the controlled runtime path and returns `UnexpectedFailure` (`10`). The original listener failure remains the primary outcome even when later resource cleanup observes the already-faulted listener; it is not reclassified as `ShutdownFailure` (`4`).

Short-lived clients, disconnects before handshake completion, malformed per-connection protocol input and normal connection teardown are isolated to the affected connection. Recoverable listener-level I/O failures recreate the server stream with bounded retry/backoff; an unknown failure or repeated listener-level I/O failure is terminal and observable. Normal cancellation still drains IPC before disposing recording, media pipelines and GPU resources and rejects a clean exit if GPU surfaces remain retained.

The monitoring listener uses the same bounded listener lifecycle implementation. Monitoring-plane listener failure is reported as observability degradation only and does not take Runtime authority or independently fail the production RuntimeHost process.

If RuntimeHost is killed, ControlHost retains the last committed authoritative revision but becomes degraded and pauses authoritative mutations until a Runtime instance is available and reconciled. Process Recovery & Supervision does not claim zero-frame output continuity while RuntimeHost itself is absent.

### AIHost

Environment variables:

- `RTAIME_AI_COMPUTE_UNITS`
- `RTAIME_AI_VRAM_MIB`
- `RTAIME_AI_MAX_CONCURRENT`
- `RTAIME_AI_MAX_RATE`
- `RTAIME_AI_ENDPOINT`
- `RTAIME_AI_SHUTDOWN_TIMEOUT_MS`

AIHost composes the governed inference runtime, provider registry, resource admission limits and an IPC endpoint for health, capabilities, snapshot and governed inference. Host shutdown stops IPC, cancels in-flight inference, waits for executions to drain and verifies that active admissions and reserved compute/VRAM return to zero.

AIHost process loss never transfers production authority. Optional local supervision may restore AIHost availability independently from Control/Runtime authority.

### Operator

Operator remains a presentation/client process. It never owns production truth. On ControlHost HostInstanceId replacement, the Client SDK discards remote state continuity and requires a full authoritative snapshot before another mutation. The UI resynchronizes and requires the user to repeat the requested operation instead of auto-replaying stale intent.

For process-recovery qualification, Operator supports a headless mode that uses the normal Client SDK and repeatedly obtains full snapshots:

```text
--headless
--control-endpoint=<pipe>
--ready-file=<path>
```

This mode is diagnostic/test composition only; it does not introduce an alternate control path.

## IPC lifecycle

The V1 reference transport is local Windows Named Pipes with `PipeOptions.CurrentUserOnly`.

```text
Operator -> ControlHost
ControlHost -> RuntimeHost
ControlHost/RuntimeHost -> AIHost endpoint foundation
```

Every connection performs Protocol/Role/Contract handshake before application messages. Each server process exposes a fresh HostInstanceId. HostInstanceId changes distinguish connection interruption from process replacement and trigger the corresponding recovery policy.

The IPC plane transports commands, descriptors, state, capabilities, opaque handles and observations only. Bulk media payloads remain outside management IPC.

Detailed wire/StateVersion/idempotency semantics are documented in `docs/ProductionIpcRemoteApi.md`. Durable storage semantics are documented in `docs/DurablePersistenceAndJournal.md`. Process-failure, reconnect and reconciliation semantics are documented in `docs/ProcessRecoveryAndSupervision.md`.
