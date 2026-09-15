# Governed AI Inference Foundation

## Status

AP-11 implements the governed V1 inference path on branch `governed-ai-inference`.

Change classification:

- `CONTRACT`
- `REALTIME_CRITICAL`

The AI workload itself remains `RT_SOFT`. The `REALTIME_CRITICAL` classification is applied because this change must prove that AI admission, timeout, cancellation and failure cannot compromise the RT-critical Program path.

No new architectural direction is introduced and no ADR is required.

## Authority boundary

AI may:

- advertise inference capabilities,
- accept governed inference requests,
- analyze timed media descriptors,
- return structured inference results,
- expose availability and observations.

AI may not:

- mutate Desired or Authoritative Production State,
- issue authoritative production commands,
- prepare or commit Runtime execution,
- become Program authority,
- directly control production hardware.

`rtaime.AI`, `rtaime.Provider.Inference` and `rtaime.AIHost` have no production reference to Control or Runtime implementations. AI failure therefore cannot directly mutate committed production state.

## V1 reference capability

The V1 reference capability is:

```text
ai.person-segmentation
```

`ManagedReferencePersonSegmentationProvider` is a deterministic managed reference implementation used for architecture, contract, behavior and CI evidence.

It returns a timed `SegmentationMask` descriptor and opaque resource handle. It does not return bulk mask pixels through the AI contract.

The reference provider is explicitly not evidence of:

- trained model quality,
- production segmentation accuracy,
- TensorRT/ONNX Runtime/DirectML behavior,
- qualified GPU inference performance.

## Controlled model package identity

The inference provider advertises controlled model metadata including:

- ModelId,
- ModelVersion,
- CapabilityId,
- runtime requirements,
- input contract,
- output contract,
- resource requirements,
- provider compatibility,
- artifact SHA-256,
- provenance.

The V1 reference package is deliberately marked as a synthetic CI/reference package rather than a trained production model.

## Request model

The AP-02 `GovernedInferenceRequest` remains compatible and is wrapped by `GovernedInferenceExecutionRequest` with explicit execution context.

The governed request path carries concepts equivalent to:

```text
RequestId
CapabilityId
SourceFrameId
TimingDomainId
Production / Presentation Time
Deadline
ResourceBudget
InputResourceHandle
```

`InferenceResourceBudget` contains bounded compute, VRAM and target inference-rate information. The input remains a descriptor/opaque-handle relationship rather than bulk media payload.

## Admission and scheduling

`GovernedInferenceRuntime` selects providers deterministically:

1. matching capability,
2. not Unavailable,
3. Ready before Degraded,
4. stable ProviderId order.

Admission is fail-closed when:

- deadline is already expired,
- no provider advertises the capability,
- all matching providers are unavailable,
- AI compute/VRAM/concurrency budgets cannot be reserved.

The in-process V1 governor reserves only the declared AI budget. It does not claim to enforce physical GPU partitioning against the Program compositor; hardware-level shared-GPU arbitration remains qualification evidence.

## Deadline and cancellation

After admission the runtime links:

- caller cancellation,
- request deadline timeout.

The outcomes remain distinct:

```text
Cancelled
TimedOut
Failed
Unavailable
Succeeded
```

All admitted resource leases are released on success, timeout, cancellation, provider failure and provider exception.

AI execution never waits indefinitely beyond the declared deadline.

## Result normalization and provenance

Successful results are normalized with:

- ResultId,
- RequestId through the enclosing result,
- CapabilityId,
- SourceFrameId,
- ModelId,
- ModelVersion,
- ProviderId,
- ObservationTime,
- ProductionTime,
- Freshness,
- Confidence,
- Uncertainty,
- PayloadDescriptor,
- ResourceHandle.

The result is an observation. It does not become Authoritative State merely because it exists.

## Multi-rate result use and fallback

`AIResultUsePolicy` evaluates a normalized result separately from inference execution.

Fallback occurs when:

- execution did not succeed,
- result belongs to a different source frame,
- freshness exceeds the configured budget,
- confidence is below the configured threshold.

This separation permits video to run at 50 or 59.94 fps while AI executes at a lower rate. A late/stale/missing AI result does not force the media path to wait.

Effect policy remains outside the inference provider. Person Segmentation produces a mask descriptor; a later compositing/effect decision consumes it through governed production semantics.

## Availability and degradation

AI runtime state is exposed as:

```text
Ready
Degraded
Unavailable
```

- `Ready`: at least one usable provider and no observed provider execution degradation.
- `Degraded`: degraded provider is selected or a provider execution failure has been observed.
- `Unavailable`: no usable provider remains.

This state describes intelligence availability, not Program authority.

## Failure isolation

The required continuity rule is:

```text
AI failure
  -> inference result unavailable / invalid
  -> fallback
  -> committed Program continues
```

Automated failure evidence verifies that a provider/model execution failure:

- returns a defined failed inference result,
- selects fallback,
- releases AI resource reservations,
- leaves the committed Runtime execution and revision unchanged,
- does not prevent the next deterministic Program frame.

A real operating-system termination/restart of the `AIHost` process over a production IPC transport is not simulated as hardware/process evidence in AP-11 and remains `UNVERIFIED` until that process boundary is exercised directly.

## Test and evidence obligations

AP-11 includes:

- Contract evidence for additive governance contracts and descriptor-only transport,
- Unit evidence for capability advertisement, admission, deadline, timeout, cancellation, resource recovery and result-use policy,
- Integration evidence for VirtualMedia 1080p50/59.94 through AIHost Person Segmentation,
- Behavioral evidence for AI available -> unavailable -> fallback while clean Program frames continue,
- Failure evidence for provider/model failure without committed production loss,
- Performance evidence for combined virtual Program frames plus lower-rate managed reference inference.

## Evidence boundary

PASS evidence may establish:

- managed orchestration correctness,
- contract/version behavior,
- deterministic reference capability advertisement,
- governed admission and deadline handling,
- resource accounting and release,
- result provenance/freshness/confidence semantics,
- fallback behavior,
- managed reference combined-workload regression bounds,
- architecture dependency isolation.

The following remain `UNVERIFIED` unless separately qualified:

- TensorRT, ONNX Runtime or DirectML execution,
- trained Person Segmentation model accuracy/quality,
- production model package signing and supply-chain trust,
- physical GPU/VRAM arbitration against the RT-critical compositor,
- real GPU inference latency/jitter,
- actual AIHost process crash/restart over production IPC,
- visible production compositing effect driven by the segmentation result,
- professional hardware validation/certification.

`UNVERIFIED` is never treated as PASS.
