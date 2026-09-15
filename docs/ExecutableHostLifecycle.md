<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Executable Host Lifecycle

AP-13 established `rtaime.ControlHost`, `rtaime.RuntimeHost`, and `rtaime.AIHost` as long-lived executable processes. AP-14 added the local Windows production IPC control plane without moving authority or execution ownership between hosts. AP-15 adds bounded durable ControlHost journal/checkpoint composition while keeping SQLite outside RT-critical media execution.

## Lifecycle

Each host follows the operational sequence:

```text
Created
  -> Starting
  -> Ready | Degraded
  -> Draining
  -> Stopped
```

A startup, run-loop, or shutdown failure transitions the host to `Failed`. `Ctrl+C` and process-exit signals request cancellation; the host then drains owned IPC and subsystem resources before returning a structured exit code.

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

ControlHost composes the authoritative Control service, bounded production journal, Operator-facing Named Pipe endpoint and RuntimeHost transport. It may start while RuntimeHost is absent and reports `Degraded`. The background binding loop performs handshake, provider refresh, initial Runtime commit and later RuntimeHost-instance resynchronization. A Runtime process replacement never advances authoritative Production Revision by itself.

AP-15 additionally composes two independent SQLite-backed durability lanes under the configured durability root:

```text
<DurabilityRoot>/<ProductionId>-<ControlEndpoint>/management.db
<DurabilityRoot>/<ProductionId>-<ControlEndpoint>/production-journal.db
```

The default root is `%LOCALAPPDATA%/rtaime/data` on Windows, with an application-directory fallback when a local application-data folder is unavailable.

`management.db` stores management/configuration documents and controlled production checkpoints. `production-journal.db` is the purpose-built append-only causal journal. The journal and checkpoint writers use independent bounded background queues. Authoritative commits and Program/media execution never synchronously wait for SQLite. Confirmed authoritative revisions are checkpointed asynchronously; journal/checkpoint pressure or storage failures remain observable and are drained/fail-closed during orderly shutdown.

AP-15 does not restore authority from those files at startup. Automatic process/production recovery remains a later recovery responsibility.

### RuntimeHost

Environment variables:

- `RTAIME_RUNTIME_SOURCE_A_ID`
- `RTAIME_RUNTIME_SOURCE_B_ID`
- `RTAIME_RUNTIME_FORMAT` (`1080p50` or `1080p59.94`)
- `RTAIME_RUNTIME_ENDPOINT`
- `RTAIME_RUNTIME_SHUTDOWN_TIMEOUT_MS`

RuntimeHost composes the existing transactional runtime, virtual media reference provider, managed GPU provider path, Audio Follow Video, Program output, recording integration and a Control-facing Named Pipe server. Shutdown stops IPC before disposing recording, media pipelines and GPU resources and rejects a clean exit if GPU surfaces remain retained.

### AIHost

Environment variables:

- `RTAIME_AI_COMPUTE_UNITS`
- `RTAIME_AI_VRAM_MIB`
- `RTAIME_AI_MAX_CONCURRENT`
- `RTAIME_AI_MAX_RATE`
- `RTAIME_AI_ENDPOINT`
- `RTAIME_AI_SHUTDOWN_TIMEOUT_MS`

AIHost composes the governed inference runtime, provider registry, resource admission limits and an IPC endpoint for health, capabilities, snapshot and governed inference. Host shutdown stops IPC, cancels in-flight inference, waits for executions to drain and verifies that active admissions and reserved compute/VRAM return to zero.

## AP-14 IPC lifecycle

The V1 reference transport is local Windows Named Pipes with `PipeOptions.CurrentUserOnly`.

```text
Operator -> ControlHost
ControlHost -> RuntimeHost
ControlHost/RuntimeHost -> AIHost endpoint foundation
```

Every connection performs Protocol/Role/Contract handshake before application messages. Each server process exposes a fresh HostInstanceId. ControlHost uses RuntimeHost HostInstanceId changes to distinguish reconnect from process replacement and resynchronize current authority.

The IPC plane transports commands, descriptors, state, capabilities, opaque handles and observations only. Bulk media payloads remain outside management IPC.

Detailed wire, StateVersion, idempotency, security and recovery semantics are documented in `docs/ProductionIpcRemoteApi.md`. Durable storage semantics are documented in `docs/DurablePersistenceAndJournal.md`.
