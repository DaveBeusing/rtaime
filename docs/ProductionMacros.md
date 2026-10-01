<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Bounded Production Macros

## Purpose

Production Macros provide a declarative, reusable sequence of bounded production actions. They are designed for repeatable operator and integration workflows such as preparing a source, taking it to Program, changing a supported layer, routing an AUX output, waiting a known number of production frames, or starting/stopping recording.

ControlHost remains Production Authority. A Macro is orchestration metadata, not executable user code and not an alternative production state machine.

The execution path is:

`Operator / authorized external integration -> rtaime.Client -> ControlHost ProductionMacroCoordinator -> existing governed ControlHost action paths -> RuntimeHost/subsystem execution`

The Macro coordinator never calls RuntimeHost or a provider directly.

## Domain and bounds

The Macro contract is versioned independently and uses stable identities:

- `ProductionMacroId`
- `ProductionMacroActionId`
- `ProductionMacroExecutionId`

A Macro contains between 1 and `ShowControlCue.MaximumActions` actions. The persisted Macro library is bounded to 128 definitions. Names, descriptions, references, transition durations and frame waits are bounded by contract.

Macro definitions preserve action identity when renamed or reordered.

## Closed action model

Production Macros reuse the governed `ShowControlAction` union rather than defining a second generic action framework.

The closed action set is:

- Activate Scene
- Set Preview
- CUT
- DISSOLVE
- Jump Media Cue
- Media Open
- Media Play
- Media Pause
- Media Stop
- Set Layer Visibility
- Start Recording
- Stop Recording
- bounded production-frame Wait
- Set Audio Routing
- Route Output Role

`RouteOutputRole` is restricted to an existing non-Program output role and a known production source. Program routing continues to use the established Scene/Preview/CUT/DISSOLVE authority model.

Unknown action values and action payloads containing fields that do not belong to their action type fail validation.

## Explicit non-goals

Production Macros do not provide arbitrary executable code, C#/JavaScript/Python/Lua/PowerShell scripting, shell/process execution, reflection/type activation, arbitrary HTTP requests, expressions, arbitrary conditions, recursion, nested Macros, unbounded loops, user plugins, autonomous AI execution, direct Runtime/provider calls, or transactional rollback fiction.

Already committed Production mutations remain committed when a later action fails or the Macro is cancelled.

## Validation

ControlHost performs pre-execution validation before a Macro is saved or executed.

Validation includes:

- contract version and bounded definition shape;
- stable and unique identities;
- known Scene/source/output-role references;
- persistent Media Library asset identity where media actions are used;
- action-specific payload legality;
- bounded transition and wait values;
- supported audio-routing modes;
- unsupported/unknown action rejection.

The validation API returns structured bounded issues. Validation is advisory for authoring convenience; ControlHost still validates again at save/execute time and remains authoritative.

## Persistence

Macro definitions and the minimum execution cursor needed for safe recovery are stored in the durable show-project document.

Definitions and execution state use independent optimistic logical storage versions inside the show project. A stale authoring save is rejected rather than overwriting newer Macro definitions.

The execution snapshot stores only bounded metadata:

- execution and Macro identity;
- current action index/identity;
- last confirmed completed action;
- execution revision;
- active Runtime-frame wait target and RuntimeHost identity;
- recovery acknowledgement and failure evidence.

Media payloads are never persisted in Macro state.

## Deterministic execution

One Macro is adapted to one existing Show Control cue containing the Macro's governed action list. `ShowControlExecutionMachine` supplies the bounded sequential cursor and established failure/recovery semantics.

For each action ControlHost:

1. persists the current execution cursor;
2. journals action start;
3. invokes the same underlying governed action executor used by Show Control/manual production;
4. records success or failure;
5. advances only after confirmed success.

Action failure stops execution immediately. Later actions do not run.

Macro execution does not claim atomicity across production mutations and never rolls back already committed actions.

## Production-frame waits

`WaitFrames` uses the existing Show Control frame-observation seam.

ControlHost records the RuntimeHost instance identity, current production frame sequence and bounded target frame sequence. Completion occurs only when the observed Runtime production frame reaches the target.

Wall-clock time exists only as a bounded failure timeout for missing/stalled timing. It never advances a Macro.

If RuntimeHost identity changes during an active wait, the Macro enters `RecoveryRequired`.

## Cancellation

Cancellation:

- stops admission of later Macro actions;
- cancels an active frame-wait observer;
- persists `Cancelled`;
- retains the last confirmed completed action;
- retains already committed Production state.

A cancelled/stale wait cannot later advance the Macro.

## Restart and recovery

Execution state is persisted before each production-relevant action.

If ControlHost restarts while execution is `Executing` or `Waiting`, the restored state becomes `RecoveryRequired`. The uncertain action is not automatically replayed.

An operator may explicitly resume only when the current governed action is replay-safe according to the existing Show Control semantics. Unsafe uncertain actions such as CUT, DISSOLVE and recording start require cancellation and re-establishment of a known production state.

Recovery never infers success from elapsed wall-clock time.

## Journal

Bounded Production Journal events cover:

- Macro save/delete;
- execution start;
- action start/completion/failure;
- frame-wait start/completion/failure;
- cancellation;
- recovery required;
- recovery acknowledgement.

Stable Macro/action/execution identities are retained as causation evidence where available.

## Client, IPC and external control

The local Named Pipe and optional secure gRPC/TLS external boundary expose:

- snapshot/list;
- get by stable Macro identity;
- save;
- delete;
- validate;
- execute;
- cancel;
- recovery acknowledgement.

Read-only snapshot/validation operations may be admitted for authenticated Observer clients. Definition mutation and execution require Operator authority through the existing external-control RBAC model.

No bulk media crosses either transport.

## IntegrationHost

`IntegrationHost` exposes `ProductionMacroExecute` as a bounded mapping action.

OSC, MIDI, GPI/GPIO-style and Companion-compatible adapters map a trigger to one stable Macro identity and then call `OperatorControlClient.ExecuteProductionMacroAsync`. Adapter configuration does not contain or reproduce the Macro action chain.

Existing bounded queues, debounce/rate limits and authentication remain in force.

## Operator workflow

The `SCENES & CUES` workspace includes a `MACROS` tab.

The Operator surface provides:

- Macro selection/list;
- stable action list and deterministic order;
- name/description editing;
- typed action kind and bounded parameter fields;
- add/remove/reorder;
- ControlHost validation;
- save/delete;
- RUN/CANCEL;
- current action and last confirmed action evidence;
- explicit recovery acknowledgement.

The surface uses rtaime custom controls and contains no code editor. Local selection or unsaved editing never mutates Production.

## Operational behavior

Production Macros do no per-frame work while idle. Runtime-frame polling exists only while a bounded wait is active.

Definitions, action counts, library count, payload size, wait duration, external request size/rate, IntegrationHost queues and journal entries all remain bounded.

## Validation expectations

Required automated coverage includes:

- contract/version/serialization bounds;
- stable Macro/action identity and reorder;
- invalid/unknown actions and references;
- deterministic action order;
- stop-on-failure;
- frame wait;
- cancellation and stale-wait invalidation;
- ambiguous restart recovery;
- IntegrationHost Macro-ID trigger;
- custom Operator controls and Client-only presentation boundary;
- no direct Runtime/provider dependency;
- Release build and all Required Gates.
