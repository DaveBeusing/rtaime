<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Show Control and Cue Sequencing

## Purpose

Show Control provides a bounded, operator-authored cue stack for repeatable production sequences. It orchestrates existing governed production commands without creating a second production authority.

ControlHost remains authoritative for production state. RuntimeHost executes only work prepared through the established Control boundary, and the Operator presents synchronized state rather than optimistic local production truth.

## Supported action set

The version 1 contract supports the following explicit actions:

- Activate Scene
- Set Preview
- CUT
- DISSOLVE
- Jump Media Cue
- Media Play
- Media Pause
- Media Stop
- Set Layer Visibility
- Start Recording
- Stop Recording
- bounded production-frame Wait

The action union is intentionally closed. Unknown action values are rejected by contract validation and unsupported actions are never serialized as valid execution work.

Cue lists, cues and actions all use stable identities. Rename and reorder operations preserve those identities.

## Authority and execution path

The cue sequencer is owned by ControlHost and is an orchestration coordinator, not a production authority.

Production-changing actions reuse the same established paths as manual operation:

- Scene, Preview, CUT and DISSOLVE use the existing Control mutation pipeline and expected authoritative revision.
- media actions use the existing Media Deck control service and marker/transport semantics.
- compositing visibility changes use the existing compositing-layer command path.
- recording actions use the existing recording command path.
- frame waits are coordinated by Show Control but observe Runtime timing rather than issuing direct Runtime production mutations.

A cue action must not bypass Control validation, transactional Runtime preparation/commit, or existing subsystem validation.

## Execution state machine

The execution state is explicit and persisted:

`Idle -> Armed -> Executing -> Waiting -> Executing -> Armed -> ... -> Completed`

Failure and recovery states are:

- `Failed` — a production-relevant action failed and sequence progression stopped.
- `Cancelled` — the operator cancelled execution.
- `RecoveryRequired` — the previous action outcome cannot be proven safe after restart or timing authority changed.

`GO` is valid only while `Armed`. Actions inside one cue execute in deterministic order. When the cue completes, the cursor advances to the next cue and returns to `Armed`, requiring the next explicit `GO`. Completing the final cue produces `Completed`.

Selecting a cue in the Operator never executes it.

## Timing model

Production waits are expressed in production frames.

When a frame wait begins, ControlHost records:

- the RuntimeHost instance identity;
- the current Runtime frame sequence;
- the bounded target frame sequence.

The wait completes only when the observed Runtime frame sequence reaches the target. UI timers never advance production state.

A wall-clock deadline exists only as a bounded failure timeout for unavailable or stalled timing. It is not the progression authority. A RuntimeHost instance change during an active frame wait moves execution to recovery instead of silently continuing against a different timing domain.

## Failure and recovery

Action failure stops automatic progression and records the failed action and failure detail.

After a ControlHost restart, a persisted `Executing` or `Waiting` state is restored as `RecoveryRequired`. The system does not silently replay an uncertain action.

Operator acknowledgement may resume only when the current action has explicit replay-safe semantics. Non-replay-safe production actions such as CUT, DISSOLVE and recording start cannot be automatically resumed after uncertain completion; the operator must cancel and establish a known production state.

Cancellation clears active wait state and prevents later wait completion from advancing the sequence.

## Persistence and journal

Cue-list definitions and the minimum execution cursor required for safe recovery are stored inside the versioned durable show project using the established management-state infrastructure. Show Control keeps its own optimistic logical storage version within that project, so graphics or Scene persistence changes do not create false editor conflicts.

On first project creation, an existing legacy `show-control.workspace` document is imported with its logical storage version intact. Cue-list, cue, action and execution identities are preserved. After migration, the show project is the canonical durable owner; the legacy document is no longer the active writer.

Structured production-journal events cover:

- cue-list save and selection;
- arm;
- GO;
- action start;
- action completion;
- wait start and completion;
- action/wait failure;
- cancellation;
- recovery acknowledgement.

Journal detail remains bounded and includes stable cue/action causation identities where available.

## IPC and client model

Show Control extends the existing Control management IPC and version negotiation. The client surface supports:

- retrieve synchronized Show Control state;
- save a bounded cue list;
- select a cue list;
- arm;
- GO;
- cancel;
- acknowledge recovery.

The IPC surface carries contracts and state only. It does not transport bulk media.

## Operator workflow

Show Control is integrated into the existing SCENES & CUES workflow.

The Operator provides:

- a RUN surface with cue-list selection, current cue/action evidence, ARM, GO and CANCEL;
- explicit recovery acknowledgement controls;
- an EDIT surface for bounded cue-list, cue and action authoring;
- stable identity preservation across rename and reorder;
- `F9` as the deliberate Show Control GO shortcut.

The editor exposes only the supported action union and typed bounded fields. It is not a scripting environment. Server-side validation remains authoritative even when the Operator performs immediate local shape validation.

The Operator renders confirmed Show Control snapshots delivered through the same synchronization flow used by the production workspace. Local row selection does not mutate production state.

## Operational behavior

Show Control performs no per-frame work while idle. A frame wait starts a bounded timing observer only for the duration of the active wait.

Cue-list limits, cue limits, action limits, duration limits and persisted workspace limits are contract- or persistence-bounded to prevent unbounded scheduler behavior and uncontrolled journal/state growth.

## Non-goals

Show Control does not provide:

- arbitrary scripts or executable user code;
- unbounded loops or condition expressions;
- a generic scheduler;
- direct provider or hardware control;
- direct Runtime production mutation;
- a second Program authority;
- timeline NLE editing;
- network-distributed show control;
- external automation protocol integration as part of this capability.

## Validation expectations

Required validation includes:

- deterministic contract serialization and version rejection;
- stable identity behavior;
- execution-state transitions and GO progression;
- wait completion and cancellation;
- action failure stop behavior;
- restart recovery and unsafe replay prevention;
- existing authoritative command integration;
- Operator selection isolation and recovery affordances;
- Release build and Required Gates;
- architecture checks preventing direct Runtime/provider production bypass.

Show Control must not materially affect the real-time media path.
