<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Runtime Readiness

## Purpose

Runtime Readiness provides one persistent application-level view of whether rtaime can produce, whether required components are healthy, and whether the current Runtime performance evidence remains qualified.

The Operator does not own or invent this state. `RuntimeReadinessService` in `rtaime.Client` consumes the existing authoritative Control snapshot and Runtime-derived health/performance projection and keeps one thread-safe `RuntimeReadinessSnapshot`.

## Global states

The canonical global states are:

- `Initializing` — the first authoritative Control snapshot has not been established yet.
- `Ready` — required Runtime/Control/media/provider evidence is healthy and Runtime performance is verified.
- `Degraded` — production can continue, but one or more non-blocking health or performance conditions require attention.
- `NotReady` — at least one required component has blocking failure/readiness evidence.
- `Recovering` — automatic authoritative-state recovery is active.
- `Failed` — bounded automatic recovery is exhausted.

`IsProductionReady` is true only for `Ready` and `Degraded`.

A degraded snapshot retains every active reason. The titlebar uses the first reason for its compact label while the diagnostic surface and tooltip expose the complete reason set.

## Single source and presentation

`IRuntimeReadinessService` exposes `Current` and the `Changed` event. The Operator keeps one service instance for its lifetime. Workspace changes, view rebinding and Output/Settings view recreation consume that same snapshot instead of recomputing a global readiness state.

The existing lifecycle labels remain compatibility presentation over the centralized snapshot. They are not a second lifecycle authority.

## Performance verification persistence

Runtime performance verification is independent from WPF view lifetime.

A fresh qualified Runtime measurement verifies performance when the Runtime performance snapshot has a valid observation timestamp, frame budget is positive, frame processing time is present, and the active output format is known.

The verification remains valid for the same bounded two-second Runtime observation-retention window used by ControlHost. A transient Runtime refresh miss therefore does not immediately erase a still-valid performance result.

The verification is invalidated when:

- the verified measurement exceeds its validity window;
- a relevant Runtime/Engine health failure occurs;
- a previously qualified CPU/GPU identity changes;
- the active output format changes, representing a relevant pipeline change;
- an explicit caller invalidates performance.

Hardware identity enrichment from `UNVERIFIED` to a concrete device name does not count as a hardware replacement.

Explicit invalidation requires a newer Runtime observation before verification can become `Verified` again.

No independent polling loop or background thread is introduced. Expiry is evaluated when the existing Operator synchronization path supplies a new observation or a repeated stale synchronization event.

## Titlebar and diagnostics

The dark production titlebar permanently hosts `RtaimeGlobalStatusButton`.

The control shows a semantic status indicator, the global readiness label, the first affected component when attention is required, a concise detail line, and a tooltip with all active readiness reasons and performance-verification detail.

Selecting the control opens the existing `SETTINGS` workspace. The Settings/System Status surfaces expose both global readiness and performance-verification state without introducing another Health Center state model.

## Failure and recovery

Blocking required-component failures produce `NotReady`.

Non-blocking evidence gaps, governed AI fallback and performance-verification gaps produce `Degraded` while production remains available.

Control synchronization loss produces `Recovering`. The existing synchronization path continues to evaluate performance validity so stale measurements cannot remain verified indefinitely.

Once the affected subsystem publishes healthy authoritative evidence again, the same service automatically returns the global state to `Ready` when no other reason remains.

## Concurrency and lifecycle

`RuntimeReadinessService` serializes state transitions under one private gate and raises `Changed` after releasing the gate.

Consumers must unsubscribe during disposal. The Operator owns and disposes the service it creates, while an injected service remains externally owned.

The service retains state independently from subscribers, so removing and recreating UI bindings cannot reset readiness or performance verification.
