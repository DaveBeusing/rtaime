# GPU Lifecycle and Recovery

## Purpose

GPU processing is production-critical. RuntimeHost must fail closed when the qualified CUDA execution path can no longer prove safe ownership or context validity, while failures isolated to monitoring must not mutate Program authority.

This document defines the V1 lifecycle, failure classification, resource cleanup, operator evidence and recovery procedure.

## Authority boundaries

ControlHost remains production authority. RuntimeHost owns committed execution, frame timing and production surfaces. The GPU provider executes the prepared Runtime work only.

Recovery does not introduce a second renderer, compositor, scheduler, state store or authority. Stable contracts remain provider-neutral and do not expose CUDA pointers, D3D handles or raw pixel payloads.

The managed reference backend is software evidence only. It must not be selected silently as a live-production fallback after a CUDA failure.

## Lifecycle

The provider exposes a bounded lifecycle snapshot with state, reason code, optional failure and monotonically increasing successful-start generation.

Allowed operational progression is:

~~~text
Unavailable -> Starting -> Ready
Stopped     -> Starting -> Ready

Ready -> Degraded -> Ready
Ready -> Failed -> Recovering -> Ready
Degraded -> Recovering -> Ready
Failed -> Stopped
Recovering -> Failed
Ready/Degraded -> Stopped
Stopped/Unavailable -> Disposed
~~~

### Unavailable

Capability detection cannot establish the configured backend. No production GPU work is accepted.

### Starting

Backend context/module initialization is in progress. Program GPU work is not accepted until Ready.

Partial-start failure invokes backend cleanup and becomes Failed with explicit failure evidence. A cleanup failure is not discarded.

### Ready

The backend is available for production work. Successful start or recovery rotates the provider lifecycle generation and the monitoring provider instance identity.

### Degraded

Program execution may continue only when the failed subsystem is explicitly isolated from Program correctness. Current examples are bounded readback-pool pressure, monitoring export unavailability, monitoring resource-release failure and failed surface release that remains tracked for cleanup.

The existing ProviderDescriptor health path projects Degraded and its failure detail to ControlHost/Operator.

### Failed

The provider rejects further production work until controlled recovery or shutdown. CUDA upload, composite and Program readback failures are fail-closed because the continued validity of device/context state cannot be assumed.

### Recovering

Existing surfaces and monitoring resources are drained using tracked ownership. Backend stop is completed before a new start. Successful recovery rotates generation and monitoring instance identity, invalidating stale resources.

### Stopped

No new work is accepted. Stop is idempotent. Successful stop requires tracked provider resources to be drained and backend cleanup to complete.

## Resource ownership and cleanup

### CUDA allocations

A CUDA surface remains in the backend allocation map until its release operation succeeds. Failed direct frees remain in provider-owned accounting so a later Stop or Recover can retry them.

Pooled device allocations are inspected before removal: ownership is removed from the pool only after the CUDA free succeeds.

Context/module handles are cleared only after corresponding cleanup succeeds. Partial cleanup failure remains explicit rather than presenting a clean stop.

### Program readback leases

Readback memory remains bounded by the configured pool. Pool exhaustion does not allocate around the bound; it produces a Degraded state and explicit reason evidence.

A backend Program readback failure on CUDA is production-critical and moves the provider to Failed. The rented host buffer is returned before the failure escapes.

### Shared monitoring resources

Monitoring resources have independent bounded ownership. Lease release callbacks remain installed until the backend release succeeds, making release retryable rather than converting a thrown release into an untracked resource.

Monitoring export failure is isolated from Program. The CUDA backend performs one bounded recreation attempt of the D3D11 interop object on the same CUDA device. If recreation/export still fails, monitoring remains unavailable/degraded and CPU/other established fallback presentation may continue where already supported; Program state is not changed by the monitoring failure.

Provider restart/recovery rotates monitoring instance identity, so descriptors from an older generation are rejected.

## Failure classes

| Failure | Lifecycle result | Program behavior |
| --- | --- | --- |
| Backend unavailable before start | Unavailable | no GPU production work |
| Start/context/module initialization failure | Failed | fail closed |
| CUDA upload failure | Failed | fail closed |
| CUDA composite failure | Failed | fail closed |
| CUDA Program readback failure | Failed | fail closed |
| Readback pool exhausted | Degraded | no bound bypass; caller receives failure |
| Surface release failure | Degraded while tracked | surface remains cleanup evidence |
| Monitoring export/interoperability failure | Degraded | Program authority/output not mutated |
| Monitoring release failure | Degraded while tracked | lease remains retryable |
| Stop/cleanup failure | Failed | do not claim Stopped |

Managed-reference operation failures remain software-test behavior and do not constitute physical CUDA failure evidence.

## Recovery procedure

### Monitoring-only degradation

1. Keep Program execution on the existing qualified GPU path.
2. Observe GPU Provider health and failure detail in the existing Operator health projection.
3. Allow the bounded same-device monitoring interop recreation attempt.
4. Confirm monitoring resource count returns to its expected bound.
5. A successful later monitoring export returns the provider from monitoring-related Degraded state to Ready.

If monitoring does not recover, continue only according to the established operational policy for degraded observability. Do not restart or replace production authority merely to restore a monitor surface.

### Production-critical GPU failure

1. Treat GPU Provider status Failed as fail-closed; do not substitute the managed reference backend.
2. Stop/drain the failed provider using tracked resource ownership.
3. Resolve the physical/driver/device condition before recovery.
4. Execute controlled provider recovery or restart RuntimeHost through the established process-supervision path.
5. Require Ready plus a new lifecycle generation before accepting new GPU production work.
6. Reject all monitoring resources/surfaces from the old generation.
7. Re-establish authoritative committed execution through the normal ControlHost -> RuntimeHost path.
8. Confirm resource counts and timing evidence before declaring the production path healthy.

If cleanup cannot release all tracked resources, retain Failed evidence and restart the owning process/device environment as required. Do not label the provider Stopped or Ready while cleanup is incomplete.

## Operator evidence

No new Operator authority or UI polling loop is introduced. RuntimeHost already publishes provider descriptors; ControlHost already projects provider availability and failure detail into the Operator GPU Provider health indicator.

Lifecycle projection is:

- Ready hardware backend -> Available / PASS through existing health evaluation.
- Degraded -> Degraded / UNVERIFIED with the reason detail.
- Failed or Unavailable -> Unavailable / FAIL with the reason detail.
- managed reference -> Degraded / UNVERIFIED because software execution is not hardware qualification.

## Software qualification

Required Gates enforce the lifecycle policy and unit fault-injection coverage for:

- start failure and explicit recovery generation;
- CUDA-classified composite fail-closed behavior;
- bounded readback-pool exhaustion;
- monitoring export isolation and recovery;
- retryable monitoring release;
- tracked surface/allocation cleanup semantics.

Existing Program-memory ownership, deterministic Runtime boundary and monitoring tests continue to protect bounded leases, one Program cadence and isolation from Operator presentation.

## Physical qualification boundary

Physical NVIDIA device removal, CUDA context loss, TDR/reset behavior, CUDA/D3D11 device-loss recreation and long-duration recovery on the approved reference platform are **UNVERIFIED** until immutable evidence is captured for the exact source revision on approved hardware.

Software CI, managed-reference execution and source-level fault injection must not be presented as physical GPU recovery qualification.

## Acceptance invariants

A recovery-capable build must preserve all of the following:

- no untracked GPU surface or monitoring lease after successful Stop;
- cleanup failure remains explicit and prevents a false Stopped/Ready state;
- old-generation monitoring resources are rejected after restart/recovery;
- Program authority is unchanged by monitoring failure;
- no silent managed-reference live-production fallback;
- no unbounded allocation or retry queue;
- repeated Stop/Start/Recover operations remain deterministic and idempotent where applicable.
