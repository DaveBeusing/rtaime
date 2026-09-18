# V1 Contract Foundation

## Scope

This document records the V1 Contract Foundation V1 contract foundation. It is subordinate to the binding Project Architecture Context, accepted ADRs, V1 Product Definition, Technology Baseline, Subsystem & Solution Architecture, V1 Project & Dependency Map, Initial Solution Bootstrap & Project Reference Map, and Project Development & Product Governance.

Change classification: `CONTRACT`.

V1 Contract Foundation establishes the first stable, transport-neutral V1 vocabulary across:

```text
Control -> Planning -> Runtime -> Media / Provider / AI
```

It does not implement command processing, planning, runtime execution, provider discovery, IPC, GPU processing, media transport, persistence, or host behavior.

## Contract version policy

Each contract family exposes an explicit V1 compatibility marker:

```text
Control   1.0
Runtime   1.0
Media     1.0
Provider  1.0
AI        1.0
```

The current policy is deliberately conservative and fail-closed:

- only the explicitly supported `1.0` version is accepted;
- unknown versions are rejected;
- same-major forward compatibility is not implied;
- broader compatibility may only be introduced through the established contract/versioning governance.

This keeps compatibility policy out of `rtaime.Core`, which continues to provide only the policy-neutral `CompatibilityVersion` primitive.

## Control contracts

`rtaime.Control.Contracts` defines:

- stable `ProductionId`, `ProductionSourceId`, and `CommandId` wrappers;
- logical `ProductionSourceSpecification`;
- `ProductionSpecification`;
- explicit `ProductionRoutingState`;
- `DesiredProductionState`;
- `AuthoritativeProductionState` with `Revision`;
- command metadata with expected revision;
- typed preview-selection and program-cut commands;
- immutable `ControlValidationReport`.

Control contracts intentionally do not reference Media, Runtime, Provider, AI, Persistence, hosts, UI, or transport technologies.

Desired state and authoritative state are distinct public contract types. Control Domain Foundation owns the actual domain validation and transition rules.

## Media contracts

`rtaime.Media.Contracts` defines:

- `MediaSourceId`, `MediaSinkId`, and `SurfaceId`;
- `VideoFormat`;
- exact `FrameRate` and `Timebase` use through Core primitives;
- `FrameTiming`;
- surface storage, ownership, and lifetime descriptors;
- opaque surface-handle descriptors;
- `FrameDescriptor`.

The V1 reference formats are represented explicitly:

```text
1920x1080 progressive RGBA8 @ 50/1
1920x1080 progressive RGBA8 @ 60000/1001
```

Contracts contain descriptors only. They do not carry bulk pixel buffers.

`OpaqueSurfaceHandle` is intentionally transport- and vendor-neutral. Its interpretation belongs to the provider/runtime boundary that owns the resource.

## Provider contracts

`rtaime.Provider.Contracts` defines:

- stable provider, capability, and resource identities;
- provider availability with explicit degraded/unavailable semantics;
- capability descriptors;
- resource descriptors with capacity and reservation semantics;
- provider descriptors;
- capability requirements for later deterministic planning and admission.

No concrete GPU, capture-card, inference-runtime, SDK, IPC, or operating-system type is present.

## Runtime contracts

`rtaime.Runtime.Contracts` defines:

- prepared execution identity;
- authority snapshot reference using only Core identity/revision semantics;
- prepared execution bindings to provider resources and optional media endpoints;
- prepared execution contract;
- prepare result;
- commit request;
- commit result;
- runtime execution state;
- runtime observation.

Runtime contracts do not reference Control contracts. The authority boundary is represented by an opaque state identity plus revision, preserving the approved dependency direction.

Prepare/commit behavior is not implemented in V1 Contract Foundation. Transactional Runtime Commit owns transactional runtime semantics.

## AI contracts

`rtaime.AI.Contracts` defines:

- inference capability identity and descriptor;
- inference request identity;
- governed inference request;
- deterministic parameter ordering;
- governed inference result;
- AI observation;
- explicit success/failure/timeout/cancellation/unavailable states.

AI contracts may carry an optional Media `FrameDescriptor` as an input reference. They contain no command, authoritative-state, or runtime-commit capability. AI therefore gains no production authority through this contract layer.

## Immutability and deterministic representation

Public collection-bearing contracts snapshot input collections before exposing them as read-only views.

Where unordered name/value inputs are accepted, V1 Contract Foundation canonicalizes them by ordinal name before exposing or serializing them. Identifiers, revisions, timestamps, frame rates, and timebases reuse the deterministic Core semantics introduced by Core Domain Semantics.

Enum-backed contract values reject undefined values at construction boundaries rather than silently accepting unknown meanings.

## Validation and evidence expectations

`rtaime.Tests.Contracts` covers:

- supported and unsupported contract versions;
- construction and boundary validation;
- immutable collection snapshots;
- JSON serialization round-trips;
- enum/value compatibility;
- representative fixtures spanning all five contract families;
- absence of bulk media payloads in frame descriptors.

The existing architecture suite remains responsible for the approved ProjectReference graph and for preventing contract-to-implementation dependencies and vendor/runtime leakage.

A build or test outcome is recorded as PASS only after actual execution. `UNVERIFIED` is never treated as PASS.
