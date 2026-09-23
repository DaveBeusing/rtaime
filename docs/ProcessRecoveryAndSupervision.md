<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>
# Process Recovery & Supervision

## Purpose

Process Recovery & Supervision defines how the V1 multi-process reference system behaves when Operator, AIHost, RuntimeHost, ControlHost or their local IPC connection disappears and later returns.

The recovery model preserves the existing ownership rules:

- ControlHost owns authoritative production state.
- RuntimeHost owns committed execution.
- AIHost owns governed inference only and never gains production authority.
- Operator is presentation/client state and never owns production truth.

Process Recovery & Supervision builds on the durable checkpoints and causal Production Journal introduced by Durable Persistence & Journal.

The product-level `rtaime.exe` AppHost does not change these authority rules. It may start or adopt ControlHost and observe recovery/readiness state, but RuntimeHost and AIHost supervision remains owned by ControlHost. AppHost never performs Runtime authority reconciliation itself.

## Failure matrix

| Failure | Continuing responsibility | Recovery action | Authority rule |
| --- | --- | --- | --- |
| Operator process lost | ControlHost and RuntimeHost continue | restarted Operator requests a full authoritative snapshot | Operator state is disposable and never authoritative |
| Operator/Control IPC interrupted | production hosts continue | retry connection; a ControlHost HostInstanceId change requires a full snapshot before another mutation | stale client state cannot mutate new authority session |
| AIHost process lost | core Control/Runtime production continues | optional local supervisor restarts AIHost; inference/fallback policy reconnects independently | AIHost never advances Production Revision |
| RuntimeHost process lost | ControlHost keeps last committed authority and becomes degraded | optional local supervisor restarts RuntimeHost; ControlHost queries and reconciles execution | mutations pause while Runtime is unavailable; recovery does not advance authority revision |
| RuntimeHost replacement | ControlHost detects a new HostInstanceId | compare Runtime committed AuthoritySnapshot with durable/in-memory Control authority and reconcile | matching authority is adopted, missing/older authority is reapplied, newer/foreign authority fails closed |
| ControlHost process lost | already committed Runtime execution may continue independently | AppHost reports application failure; an explicit application/administrative or operating-system lifecycle restart starts ControlHost, verifies checkpoint/journal integrity and restores durable authority | Control restart never silently creates revision 0 when durable authority exists |
| ControlHost replacement | Operator sees a new HostInstanceId | full snapshot is mandatory before another mutation | old StateVersion/session continuity is discarded |
| Control/Runtime recovery disagreement | no automatic authority rewrite | remain degraded and emit `recovery.runtime.conflict` | Runtime authority ahead of or foreign to durable Control authority is never overwritten automatically |

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
-> optional ActiveSceneId validation against the current production specification
-> Production Journal SQLite + hash-chain integrity check
-> authoritative state restore
-> Runtime query/reconciliation
```

Any malformed, unsupported or contradictory checkpoint fails startup instead of falling back to a fresh revision. A recovered `ActiveSceneId` is accepted only when that Scene still exists in the current specification; recovery never infers an active Scene from routing coincidence.

### Runtime reconciliation

Runtime and Control use two independent revision spaces:

- Control `Production Revision` identifies authoritative production state.
- Runtime `ExecutionRevision` identifies Runtime-local transactional execution history.
- Every committed Runtime execution additionally retains the `AuthoritySnapshot.StateId` and `AuthoritySnapshot.Revision` from the `PreparedExecutionContract` that produced it.

`Runtime ExecutionRevision` is therefore never compared numerically with Control `Production Revision` during recovery.

Let `C` be the durable/in-memory Control authority revision and `A` the AuthorityRevision attached to the currently committed Runtime execution:

```text
Runtime committed
and AuthorityStateId == Control ProductionId
and A == C
    -> bind existing execution
    -> Runtime ExecutionRevision may differ from C
    -> no execution replacement
    -> no Production Revision change

Runtime has no committed authority
or matching AuthorityStateId with A < C
    -> refresh providers
    -> prepare current authoritative state
    -> apply to Runtime
    -> verify the resulting Runtime snapshot references Control ProductionId and C
    -> Runtime ExecutionRevision may advance independently
    -> no Production Revision change

A > C
or committed AuthorityStateId != Control ProductionId
or committed execution lacks an AuthoritySnapshot reference
    -> recovery conflict
    -> remain Degraded
    -> disconnect mutation transport
    -> emit recovery.runtime.conflict
    -> require operator/administrative intervention
```

The Runtime-authority-ahead/foreign case is intentionally fail-closed because Process Recovery & Supervision has no evidence that an automatically chosen side would preserve production truth.

### Scene compositing recovery

When authoritative Production state contains a versioned Scene compositing snapshot, Runtime alignment requires more than a matching authority revision. The confirmed Runtime layer set must also match the authoritative layer identities, kinds, order, visibility, opacity, transforms and content identities.

A replacement RuntimeHost starts without the retained bitmap/CG resources. ControlHost therefore restores the resource content retained for the current ControlHost process before reapplying the already committed `PreparedExecutionContract`. The reapply uses the same authoritative revision and must not create a new Scene activation or Production Revision. Missing required resources reject prepare and leave Control authority unchanged.

Legacy checkpoints without compositing state remain compatible. When an older checkpoint cannot prove a Scene's newly declared compositing state, recovery keeps the valid routing authority but does not restore `ActiveSceneId` evidence for that Scene.

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

A hard ControlHost process loss does not automatically terminate an already-running RuntimeHost or AIHost. The product AppHost observes that loss as a failed application lifecycle but does not automatically create a replacement ControlHost. Recovery therefore remains an explicit application/administrative or operating-system lifecycle action. This prevents ControlHost supervision ownership from becoming a hidden Runtime continuity dependency.

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

Where an effect has a qualified fallback path, that fallback remains subject to the existing governed inference/fallback contracts. Process Recovery & Supervision process supervision only restores process availability; it does not change inference semantics.

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

Process Recovery & Supervision qualifies process-level recovery semantics. It does **not** claim:

- zero-frame Program interruption after RuntimeHost termination,
- frame-identical continuation after process crash or power loss,
- preservation of in-flight DISSOLVE phase across RuntimeHost death,
- automatic resolution when Runtime committed authority is ahead of or foreign to Control durable authority,
- distributed HA/failover across machines,
- operating-system service installation or deployment policy,
- hardware I/O continuity during device/driver reset.

Those remain UNVERIFIED unless a later package provides direct evidence.


## Operator recovery experience

The normal interactive Operator now turns the existing reconnect semantics into an explicit lifecycle presentation:

```text
HEALTHY
  -> DEGRADED / RECOVERING
  -> HEALTHY
```

A stale Control session immediately blocks production mutations while preserving read-only Program/monitoring visibility where the independent monitoring plane is still available. The existing bounded management refresh continues attempting `control.snapshot.get`; it does not stop merely because the prior session became stale. A ControlHost replacement therefore returns through the mandatory full-snapshot path before mutation controls can become active again.

The startup overlay is used only until the first qualified authoritative synchronization. After that point the main production workspace remains visible during degradation and recovery so the operator can understand what is happening.

AI effect failure is kept separate from core production authority. When an enabled governed AI effect reports `UNAVAILABLE`, `TIMEOUT` or `FAILED` while core Control/Runtime evidence remains valid, the engine presentation is `DEGRADED` but Program safety remains valid or cautionary according to the core evidence. The UI does not claim that AI loss is a Program failure.

`FAILED` is reserved for explicit recovery-exhaustion evidence. The Operator does not infer terminal failure merely from missing metrics or from an arbitrary local retry count.
