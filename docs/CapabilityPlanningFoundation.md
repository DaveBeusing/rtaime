<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Capability Resolution and Execution Planning Foundation

## Scope

This document records Capability Planning Foundation, the first deterministic planning path from an authoritative Control state to a `PreparedExecutionContract`.

The package implements the already defined architecture path:

```text
Authoritative State
→ Logical Production Graph
→ Capability Requirements
→ Capability Resolution
→ Resource Admission
→ Execution Plan
→ Prepared Execution Contract
```

No Runtime prepare/commit, media-frame processing, persistence, provider discovery, hardware access, GPU execution, IPC, or host behavior is introduced.

The implementation remains in `rtaime.Control`. This preserves the approved 28-project solution topology and uses the already approved inward contract dependencies from Control to Control, Runtime, Media, and Provider contracts. No production `ProjectReference` topology changes are required.

Change classification: `ARCHITECTURE`.

Capability Planning Foundation realizes architecture boundaries already established by the binding project context. It does not introduce a new architectural boundary or replace an accepted decision, so no ADR is added.

## Planning input boundary

Planning requires:

- the validated `ProductionSpecification`,
- the current `AuthoritativeProductionState`,
- one immutable provider-capability snapshot supplied through `IProviderCapabilityRegistry`.

The planner validates the authoritative input again before graph compilation. A different production identity, incompatible state/specification version, or routing to a source not declared by the specification fails closed before a graph is exposed.

Planning does not mutate the authoritative state.

## Logical production graph

The V1 graph contains stable logical identities for:

- each declared source endpoint,
- the Preview route,
- the Program route,
- the Preview sink,
- the Program sink,
- each graph edge.

For the current V1 routing model the active graph is:

```text
Preview Source  → Preview Route  → Preview Sink
Program Source  → Program Route  → Program Sink
```

All declared sources remain represented as source endpoints, while only the authoritative Preview and Program selections are connected to the two active route nodes.

Route nodes carry both a media source identity and media sink identity. This is deliberate: the existing V1 Contract Foundation `PreparedExecutionBinding` can therefore express an executable route without extending or changing the Runtime contract in Capability Planning Foundation.

Graph validation is fail-closed for:

- duplicate node identities,
- duplicate edge identities,
- unknown edge endpoints,
- self-edges,
- invalid endpoint semantics,
- duplicate production-source endpoints,
- missing or duplicated required Preview/Program route/sink nodes.

## Stable planning identities

Planner-owned graph, edge, requirement, sink, and prepared-execution identities are derived deterministically from canonical semantic inputs using SHA-256 and the first 128 bits as the opaque `Identity` value.

The deterministic input includes only stable semantic values such as:

- production identity,
- logical role,
- source identity,
- graph endpoint identities,
- authoritative revision,
- selected capability/resource identities.

No wall-clock time, random GUID, process state, registry enumeration order, or machine-specific value participates in planning identity generation.

This means the same authoritative state and equivalent provider snapshot produce the same graph identities, selection, execution plan, and `PreparedExecutionId`.

## Capability requirement

The current V1 route requirement kind is:

```text
media.route
```

A Preview route and Program route each require one unit of this capability.

The current Control specification does not yet declare an exact video format, so Capability Planning Foundation-generated route requirements intentionally carry no video-format constraint. An empty accepted-format set means that no format constraint was declared by the requirement; it does not mean that an unknown declared format may be silently accepted.

When a requirement does declare accepted video formats, `CapabilityRequirementMatcher` requires an exact `VideoFormat` intersection. A capability with no advertised format cannot satisfy a requirement that explicitly declares formats.

## Provider capability registry

`IProviderCapabilityRegistry` is a snapshot abstraction only. Capability Planning Foundation does not implement discovery, polling, lifecycle management, device probing, or vendor integration.

One planning snapshot must have unique:

- `ProviderId`,
- `CapabilityId`,
- `ProviderResourceId`.

Conflicting identities fail planning closed.

Only providers whose availability is explicitly `Available` participate in selection. `Degraded` and `Unavailable` providers are not silently promoted to usable resources by the planner.

## Deterministic capability selection

Provider input order is not selection priority.

Candidate selection is deterministic and ordered by:

1. `ProviderId`,
2. `CapabilityId`,
3. `ProviderResourceId`.

All comparisons use canonical identifier text with ordinal ordering.

The result is stable even when an equivalent provider snapshot is returned in a different enumeration order.

## Resource admission

Capability matching and resource admission are distinct checks.

A candidate resource must:

- belong to the selected provider,
- have the same exact capability/resource kind,
- be explicitly reservable,
- meet or exceed the requirement's `RequiredCapacityUnits`,
- not already be bound by another requirement in the same prepared execution.

The final rule is intentionally conservative. The V1 Contract Foundation Runtime contract carries a resource descriptor but no fractional allocation quantity. Capability Planning Foundation therefore does not invent hidden partial-reservation semantics. One resource descriptor is bound at most once within one plan. Resource subdivision can only be introduced later with an explicit contract and architecture decision if required.

If matching capability exists but no eligible unused resource can admit a requirement, planning fails with resource exhaustion and no execution plan or prepared execution is emitted.

## Fail-closed behavior

Representative planning failures include:

```text
planning.authority.version_mismatch
planning.authority.production_mismatch
planning.authority.preview_source_unknown
planning.authority.program_source_unknown
planning.graph.node_identity_duplicate
planning.graph.edge_identity_duplicate
planning.graph.edge_endpoint_unknown
planning.graph.self_edge
planning.registry.provider_identity_duplicate
planning.registry.capability_identity_duplicate
planning.registry.resource_identity_duplicate
planning.capability.missing
planning.capability.incompatible
planning.resource.exhausted
```

A failed plan may expose a validated logical graph and/or rejected admission result for diagnostics, but it never exposes an `ExecutionPlan` or `PreparedExecutionContract`.

There is no fallback to unknown capability or unavailable resources.

## Execution plan and prepared execution

A successful admission is converted to an immutable `ExecutionPlan` containing:

- an `AuthoritySnapshotReference`,
- a deterministic `PlanGeneration`,
- ordered logical-node bindings,
- selected capability identity,
- selected provider resource,
- route media source and sink identities.

For Capability Planning Foundation, `PlanGeneration` follows the authoritative revision value. A new authoritative revision therefore produces a new generation while repeated planning of the same authoritative revision remains stable.

The `PreparedExecutionContract` is generated entirely from the validated plan. Its `PreparedExecutionId` is deterministic over the authority snapshot, plan generation, and ordered bindings.

Creation of a prepared execution is not a Runtime prepare operation and is not evidence that any resource has actually been reserved or any media path has been activated.

## Tests

Capability Planning Foundation unit coverage verifies:

- complete capability coverage produces a prepared execution,
- missing capability fails closed,
- incompatible video-format capability does not match,
- deterministic provider choice with multiple valid providers,
- resource exhaustion fails closed,
- invalid graph edge endpoints are rejected,
- planning output is stable across provider snapshot order,
- duplicate provider identities fail the registry snapshot,
- authoritative production mismatch fails before graph compilation,
- a new authoritative revision produces a different deterministic prepared execution and generation.

Existing contract and architecture tests continue to protect the approved dependency graph and contract boundaries.

## Evidence boundary

Capability Planning Foundation establishes deterministic planning semantics only.

It does **not** prove:

- real resource reservation,
- Runtime prepare/commit,
- frame processing,
- timing behavior,
- media I/O,
- provider discovery,
- GPU or hardware support,
- production failover.

Those capabilities belong to later work packages, beginning with Transactional Runtime Commit Transactional Runtime Commit Foundation.
