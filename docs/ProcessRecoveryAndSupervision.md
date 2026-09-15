<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Process Recovery & Supervision

## Purpose

AP-16 defines how the V1 multi-process reference system behaves when Operator, AIHost, RuntimeHost, ControlHost or their local IPC connection disappears and later returns.

The recovery model preserves the existing ownership rules:

- ControlHost owns authoritative production state.
- RuntimeHost owns committed execution.
- AIHost owns governed inference only and never gains production authority.
- Operator is presentation/client state and never owns production truth.

AP-16 builds on the durable checkpoints and causal Production Journal introduced by AP-15.

## Failure matrix

| Failure | Continuing responsibility | Recovery action | Authority rule |
| --- | --- | --- | --- |
| Operator process lost | ControlHost and RuntimeHost continue | restarted Operator requests a full authoritative snapshot | Operator state is disposable and never authoritative |
| Operator/Control IPC interrupted | production hosts continue | retry connection; a ControlHost HostInstanceId change requires a full snapshot before another mutation | stale client state cannot mutate new authority session |
| AIHost process lost | core Control/Runtime production continues | optional local supervisor restarts AIHost; inference/fallback policy reconnects independently | AIHost never advances Production Revision |
| RuntimeHost process lost | ControlHost keeps last committed authority and becomes degraded | optional local supervisor restarts RuntimeHost; ControlHost queries and reconciles execution | mutations pause while Runtime is unavailable; recovery does not advance authority revision |
| RuntimeHost replacement | ControlHost detects a new HostInstanceId | compare Runtime execution revision with durable/in-memory authority and reconcile | same revision is adopted, behind/idle Runtime is reapplied, Runtime-ahead conflict fails closed |
| ControlHost process lost | already committed Runtime execution may continue independently | OS/service manager restarts ControlHost; checkpoint and journal integrity are verified; durable authority is restored | Control restart never silently creates revision 0 when durable authority exists |
| ControlHost replacement | Operator sees a new HostInstanceId | full snapshot is mandatory before another mutation | old StateVersion/session continuity is discarded |
| Control/Runtime recovery disagreement | no automatic authority rewrite | remain degraded and emit `recovery.runtime.conflict` | Runtime revision ahead of durable Control authority is never overwritten automatically |

## Durable ControlHost restart

Before accepting recovered authority, ControlHost performs:

```text
management SQLite integrity check
-> latest production checkpoint read
-> checkpoint format validation
-> ProductionId validation
-> Control contract version validation
-> checkpoint/payload revision equality
-> Preview/Program source validation
-> Production Journal SQLite + hash-chain integrity check
-> authoritative state restore
-> Runtime query/reconciliation
```

Any malformed, unsupported or contradictory checkpoint fails startup instead of falling back to a fresh revision.

### Runtime reconciliation

For durable Control authority revision `C` and observed Runtime execution revision `R`:

```text
Runtime committed and R == C
    -> bind existing execution
    -> no execution replacement
    -> no Production Revision change

R < C, or Runtime is Idle/Prepared/Faulted at R <= C
    -> refresh providers
    -> prepare current authoritative state
    -> apply to Runtime
    -> require Runtime commit at exactly C
    -> no Production Revision change

R > C
    -> recovery conflict
    -> remain Degraded
    -> disconnect mutation transport
    -> emit recovery.runtime.conflict
    -> require operator/administrative intervention
```

The Runtime-ahead case is intentionally fail-closed because AP-16 has no evidence that an automatically chosen side would preserve production truth.

## Local process supervision

ControlHost can optionally supervise RuntimeHost and AIHost by endpoint without taking a project reference on either host.

Configuration:

```text
RTAIME_RUNTIME_EXECUTABLE
RTAIME_AI_EXECUTABLE
RTAIME_AI_ENDPOINT
RTAIME_SUPERVISION_PROBE_TIMEOUT_MS
RTAIME_SUPERVISION_PROBE_INTERVAL_MS
RTAIME_SUPERVISION_RESTART_BACKOFF_MS
RTAIME_SUPERVISION_MAX_START_ATTEMPTS
```

If an endpoint is already present, the supervisor adopts it and does not launch a duplicate process. Only a process launched by the current supervisor instance is terminated during orderly supervisor disposal.

A hard ControlHost process loss does not automatically terminate an already-running RuntimeHost or AIHost. Top-level ControlHost restart is therefore deliberately assigned to the operating system/service manager. This prevents ControlHost supervision ownership from becoming a hidden Runtime continuity dependency.

Restart attempts are bounded. Exhausting the configured start budget reports `Failed` and continues endpoint probing without an unbounded crash loop.

## Operator reconnect

The Client SDK tracks ControlHost `HostInstanceId` and remote `StateVersion`.

A HostInstanceId replacement resets remote synchronization continuity and marks a full snapshot as required. Mutations are refused until `control.snapshot.get` has established the new authoritative session.

The normal Operator UI can resynchronize through the Client SDK. The executable also exposes a `--headless` recovery/probe mode used by process-failure qualification:

```text
--headless
--control-endpoint=<pipe>
--ready-file=<path>
```

The headless mode repeatedly performs ordinary full snapshot synchronization; it has no alternate authority path.

## AIHost continuity boundary

AIHost loss must not transfer authority to ControlHost, RuntimeHost or Operator. Core preview/program authority and Runtime execution are independent of AIHost process availability.

Where an effect has a qualified fallback path, that fallback remains subject to the existing governed inference/fallback contracts. AP-16 process supervision only restores process availability; it does not change inference semantics.

## Observability

Recovery-relevant journal codes include:

```text
control.authoritative.restored
recovery.runtime.aligned
recovery.runtime.reapplied
recovery.runtime.conflict
runtime.connection.degraded
```

ControlHost additionally exposes `Fresh`, `Recovered` or `Conflict` recovery state in-process for qualification and diagnostics.

## Claims and non-claims

AP-16 qualifies process-level recovery semantics. It does **not** claim:

- zero-frame Program interruption after RuntimeHost termination,
- frame-identical continuation after process crash or power loss,
- preservation of in-flight DISSOLVE phase across RuntimeHost death,
- automatic resolution when Runtime durable/execution truth is ahead of Control durable authority,
- distributed HA/failover across machines,
- operating-system service installation or deployment policy,
- hardware I/O continuity during device/driver reset.

Those remain UNVERIFIED unless a later package provides direct evidence.
