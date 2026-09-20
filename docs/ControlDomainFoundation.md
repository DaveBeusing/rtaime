<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Control Domain Foundation

## Scope

This work package establishes the first executable Control-domain semantics above the V1 Control contracts.

The flow is intentionally limited to:

```text
Production Specification
        ↓
Domain Validation
        ↓
Authoritative Command Path
        ↓
Desired Production State
        ↓
Authoritative Production State
```

It does not implement Runtime preparation or commit, provider discovery, media execution, persistence, transport, host orchestration, operator UI, command deduplication, audit history, or AI-driven authority.

Change classification: `ARCHITECTURE`.

No new architecture decision is introduced. The implementation realizes already binding architecture invariants, so no ADR is required for this package.

## State boundaries

Control Domain Foundation keeps the following states distinct:

- `ProductionSpecification` is declarative production intent and topology.
- `DesiredProductionState` is the validated state requested by one accepted command.
- `AuthoritativeProductionState` is the Control plane's committed state after the domain commit boundary.

Execution, observed, derived, and semantic AI state remain outside this package.

An authoritative Control commit in Control Domain Foundation does **not** claim that physical media execution has occurred. Runtime preparation, deterministic execution contracts, and Runtime commit semantics are later work packages.

## Initialization

`ControlDomainEngine.Initialize` validates the production specification before any state is exposed.

V1 initialization rules:

- initial preview source must exist in the specification;
- initial program source must exist in the specification;
- invalid specifications fail closed and expose no desired or authoritative state;
- valid specifications create authoritative revision `0`;
- initial desired state is explicitly based on authoritative revision `0`;
- desired and authoritative state remain separate immutable contract objects.

## Authoritative command path

The V1 command path supports the two commands already defined by the Control contract foundation:

```text
SelectPreviewCommand
CutProgramCommand
```

Every command is validated against the same production specification and the current authoritative state before a mutation can commit.

Validation includes:

- specification validity;
- Control contract-version consistency;
- authoritative-state production identity;
- authoritative routing references;
- command production identity;
- command expected revision;
- command target source identity;
- authoritative revision exhaustion.

A rejected command:

- produces validation issues;
- produces no desired mutation;
- returns the exact current authoritative state;
- does not increment the authoritative revision.

A successful command:

1. derives a new immutable desired routing state from the current authoritative state;
2. records the current authoritative revision as `BasedOnAuthoritativeRevision`;
3. revalidates the derived desired routing;
4. crosses the deterministic Control-domain commit boundary;
5. creates a new authoritative state with revision advanced exactly once.

The transition is pure and deterministic for equal specification, current state, and command inputs.

## Mutation semantics

### Select preview

`SelectPreviewCommand` changes only the preview source.

The current program source is preserved.

### Cut program

`CutProgramCommand` changes only the program source.

The current preview source is preserved.

No media switch, frame operation, transition effect, provider call, or Runtime action occurs in Control Domain Foundation.

## Fail-closed validation codes

The package introduces stable domain validation codes for expected rejection conditions:

```text
control.specification.preview_source_unknown
control.specification.program_source_unknown
control.state.version_mismatch
control.state.production_mismatch
control.state.preview_source_unknown
control.state.program_source_unknown
control.state.revision_exhausted
control.command.version_mismatch
control.command.production_mismatch
control.command.revision_conflict
control.command.source_unknown
```

These codes describe Control-domain failures only. They do not define transport status, UI messages, persistence policy, retry policy, or Runtime failure semantics.

## Revision semantics

Control Domain Foundation uses the Core `Revision` primitive as the monotonic authoritative Control revision.

Rules:

- initialization starts at `Revision.Initial` (`0`);
- every accepted Control-domain mutation advances exactly once;
- rejected commands do not advance;
- stale expected revisions fail closed;
- `UInt64.MaxValue` fails closed before `Next()` can overflow.

Command replay/idempotency by `CommandId` is intentionally not implemented here because it requires an authoritative retained command history or persistence boundary. That belongs to a later durability/idempotency package.

## Dependency boundaries

Control Domain Foundation adds behavior only to `rtaime.Control`.

The Control implementation continues to obey the approved inward dependency graph. The new domain implementation uses only Core and V1 Control-contract semantics even though the bootstrap project currently carries the wider approved Control reference set.

The implementation contains no direct dependency on:

- Runtime implementation;
- Media implementation;
- AI implementation;
- Persistence;
- concrete providers;
- hosts;
- operator UI;
- vendor SDKs;
- IPC or network transports.

## Verification

Unit coverage added by Control Domain Foundation verifies:

- valid initialization;
- fail-closed invalid initial routing;
- preview mutation semantics;
- program mutation semantics;
- exact one-step authoritative revision advancement;
- stale revision rejection;
- production identity rejection;
- unknown source rejection;
- invalid authoritative routing rejection;
- revision exhaustion rejection;
- deterministic equal-input state transitions.

Repository architecture tests remain responsible for enforcing the established Control dependency boundaries.

A successful managed build/test run is evidence only for the managed Control-domain semantics above. It is not evidence for real-time execution, frame accuracy, hardware support, provider behavior, persistence durability, failover, or production qualification.
