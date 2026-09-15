<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Executable Host Lifecycle

AP-13 turns `rtaime.ControlHost`, `rtaime.RuntimeHost`, and `rtaime.AIHost` into long-lived executable processes without introducing production network IPC.

## Lifecycle

Each host follows the same operational sequence:

```text
Created
  -> Starting
  -> Ready | Degraded
  -> Draining
  -> Stopped
```

A startup, run-loop, or shutdown failure transitions the host to `Failed`. `Ctrl+C` and process-exit signals request cancellation; the host then drains owned resources before returning a structured exit code.

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
- `RTAIME_CONTROL_SHUTDOWN_TIMEOUT_MS`

ControlHost composes the authoritative Control service and bounded production journal. AP-13 intentionally uses an unbound Runtime transport seam by default, so the process reports `Degraded` until AP-14 provides production IPC. No Runtime execution is moved into ControlHost.

### RuntimeHost

Environment variables:

- `RTAIME_RUNTIME_SOURCE_A_ID`
- `RTAIME_RUNTIME_SOURCE_B_ID`
- `RTAIME_RUNTIME_FORMAT` (`1080p50` or `1080p59.94`)
- `RTAIME_RUNTIME_SHUTDOWN_TIMEOUT_MS`

RuntimeHost composes the existing transactional runtime, virtual media reference provider, managed GPU provider path, Audio Follow Video, Program output, and recording integration. Shutdown disposes recording, media pipelines, and GPU resources and rejects a clean exit if GPU surfaces remain retained.

### AIHost

Environment variables:

- `RTAIME_AI_COMPUTE_UNITS`
- `RTAIME_AI_VRAM_MIB`
- `RTAIME_AI_MAX_CONCURRENT`
- `RTAIME_AI_MAX_RATE`
- `RTAIME_AI_SHUTDOWN_TIMEOUT_MS`

AIHost composes the governed inference runtime, provider registry, and resource admission limits. Host shutdown cancels in-flight inference, waits for those executions to drain, and verifies that active admissions and reserved compute/VRAM return to zero.

## AP-13 boundary

This package does **not** implement production network IPC, endpoint discovery, protocol negotiation, reconnect, or remote command transport. Those remain AP-14 responsibilities. The AP-13 ControlHost transport seam exists only to preserve that future process boundary while allowing the executable lifecycle to be proved independently.
