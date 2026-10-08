# Backend-Neutral Resource and Synchronization Semantics

## Status

- Baseline source: master at `25d4d5080b7e3b43bd6e83dba25c8ad868babf83`
- Technology baseline: .NET SDK 10.0.401
- Media contract version: 1.0, unchanged
- Provider contract version: 1.0, unchanged
- Monitoring contract version: 1.3, unchanged
- Wire-layout change: none
- New steady-frame allocation or synchronization requirement: none
- Physical CUDA / D3D11 synchronization qualification for this source: UNVERIFIED

This document defines the V1 backend-neutral meaning of resource identity, placement, ownership, lifetime, capability compatibility and publication completion. It does not add a second GPU abstraction, renderer, scheduler or transport.

## Architecture boundary

ControlHost remains production authority. RuntimeHost owns committed execution and production surfaces. Provider implementations adapt concrete hardware/runtime APIs behind provider boundaries.

Control, Runtime planning and stable provider contracts must not depend on CUDA, D3D11, Vortice, WPF, device pointers or vendor device identities.

The existing Windows graphics shared handle remains a deliberately narrow monitoring-presentation transport descriptor in `rtaime.Media.Contracts`. Its concrete creation and consumption stay at the GPU-provider and Operator presentation edges. It is not a planning primitive and must not become production authority.

## Surface identity and storage

`SurfaceId` is an opaque logical resource identity. Consumers must not derive backend, device, address or allocation semantics from its value.

`SurfaceStorageDomain` describes placement only:

- `Host`: host-resident memory.
- `Device`: backend/device-resident memory.
- `Shared`: a resource exposed through an explicit shared lifetime/transport boundary.

A storage domain is not a GPU API identifier. Device-resident surfaces may be implemented by different backends without changing consumer semantics.

## Ownership and lifetime

`SurfaceOwnership` remains the V1 ownership model:

- `ProducerOwned`: the producer controls lifetime and consumers may only observe/use the surface while the producer keeps it valid.
- `ConsumerOwned`: ownership has been transferred to the consumer according to the enclosing subsystem contract.
- `SharedLease`: producer and consumer coordinate lifetime through an explicit lease identity.

A `SharedLease` is consumable only when `SurfaceLifetimeDescriptor.LeaseId` is present and non-empty.

`Generation` is part of resource validity. A consumer that is bound to a specific generation must reject a descriptor from any other generation. Provider restart/recovery generation changes therefore invalidate stale resources without exposing backend-specific context identities.

The additive `SurfaceContractSemantics` helper centralizes these checks without changing `SurfaceDescriptor` layout or serialization.

## Completion and synchronization

V1 uses one contract-level completion model:

`ProducerCompletedBeforePublication`

A producer must complete all writes required for the advertised consumer use before publishing the descriptor across the stable contract boundary. Publication is therefore the completion boundary visible to backend-neutral consumers.

CUDA events, D3D fences, keyed mutexes, native events and similar primitives remain backend/adapter implementation details. They are not transported through Control or Planner.

This rule codifies the existing Runtime publication behavior; it does not add a new wait, fence, allocation or synchronization operation to the steady Program frame path.

If a future backend cannot satisfy completion-before-publication without transporting an asynchronous completion primitive across a stable process/API boundary, that is a new contract requirement and requires an ADR plus versioned migration rather than an opaque pointer escape hatch.

## Format and capability compatibility

`ProviderCapabilityDescriptor.VideoFormats` is authoritative for advertised provider compatibility.

`ProviderCapabilityDescriptor.SupportsVideoFormat` provides the single provider-contract comparison rule. Control capability matching uses that rule rather than maintaining a separate interpretation.

Format matching is exact for V1. A capability that advertises 1080p50 and 1080p59.94 does not implicitly advertise 720p, another pixel format, another scan mode or another color description.

Low-level managed-reference test fixtures may use reduced frame sizes for deterministic software tests. That does not expand the production capability descriptor and must never be represented as qualified hardware support.

## Monitoring interop

The monitoring transport has one intentional platform-specific metadata seam:

`MonitoringSharedResourceInteropKind.WindowsGraphicsSharedHandle`

Rules:

1. The descriptor remains read-only monitoring metadata.
2. The resource stays device/shared resident and carries an explicit lease.
3. Provider instance identity and generation define validity.
4. The GPU provider owns creation/release and lifecycle recovery.
5. Operator presentation may adapt the handle to the Windows graphics stack.
6. Control, Planner and provider-neutral resource admission must not inspect or branch on this interop kind.
7. A monitoring failure must not mutate Program authority.
8. CPU fallback presentation, where already available, is an observability fallback and not a silent production-renderer fallback.

No CUDA pointer, D3D object pointer or vendor device identity is added to stable contracts.

## Fallback classification

The managed reference GPU backend remains software/reference evidence. Its provider availability is `Degraded` with `gpu.backend.reference_only`; it must not masquerade as a qualified hardware backend.

A hardware backend may advertise shared monitoring only when that capability is actually available. Absence or degradation of monitoring interop does not synthesize a production capability.

## Architecture audit result

The current project reference graph keeps:

- Media and Provider contracts inward-facing and implementation-independent.
- Control dependent on contracts, not GPU/provider implementations.
- Runtime dependent on contracts, not concrete GPU/provider implementations.
- GPU implementation dependencies such as Vortice/D3D11 inside `rtaime.Provider.Gpu`.
- WPF presentation dependencies outside Core and stable contracts.

Architecture regression tests additionally prohibit CUDA/D3D/Vortice/WPF/shared-handle leakage into Control, Runtime and non-media stable contracts, while allowing the existing narrow monitoring descriptor in `MonitoringContracts.cs`.

## Validation expectations

Required Gates must continue to cover:

- contract tests for ownership/lifetime/format/generation semantics;
- capability matching rejection for unadvertised formats;
- managed-reference versus CUDA-structural semantic parity;
- repository architecture boundaries;
- existing GPU lifecycle/recovery tests;
- existing Program memory-ownership and deterministic Runtime boundary policies.

Managed-reference and structural software tests are not physical CUDA qualification.

## ADR and migration decision

No ADR or contract-version increment is required for this change because:

- no existing constructor or serialized field is removed or changed;
- no wire header changes;
- no process boundary changes;
- no provider/backend interface shape changes;
- the additions formalize semantics already required by current ownership and publication behavior.

A future cross-boundary fence/event contract, raw native resource transport, new storage-domain meaning or incompatible ownership rule requires a dedicated ADR and versioned migration.
