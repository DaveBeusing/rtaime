<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Media Pipeline Foundation

## Work package

Media Pipeline Foundation – Media Pipeline Foundation

Change classification: `REALTIME_CRITICAL`

This package replaces the provisional Virtual Media Runtime Slice Runtime frame executor with the reusable media-lifecycle foundation owned by `rtaime.Media`.

## Architectural placement

The approved production dependency graph remains unchanged.

- `rtaime.Media` owns media lifecycle, queueing, timing and backpressure semantics.
- `rtaime.Runtime` continues to own committed execution state and transactional activation only.
- concrete providers continue to depend only on Core and stable contracts.
- `RuntimeHost` and integration composition are the outer boundary where committed Runtime routing, Media lifecycle and concrete providers meet.

No Runtime → Media implementation reference and no Provider → Media implementation reference is introduced.

This preserves the rule that subsystem implementations meet at composition boundaries rather than depending outward on one another.

## Frame lifecycle

`MediaFrameLease` tracks the runtime ownership state of one `FrameDescriptor`:

```text
ProducerOwned
    ↓
Queued
    ↓
ConsumerOwned
    ↓
Released
```

A frame may transition to `Dropped` from any non-terminal state when policy or timing requires disposal.

`Released` and `Dropped` are terminal states.

The lifecycle state is intentionally separate from `SurfaceDescriptor.Ownership`. The contract descriptor describes the surface ownership model; the lease describes the current processing lifecycle of that descriptor inside one Media pipeline.

A surface already declared `ConsumerOwned` cannot enter the pipeline as a newly produced frame.

## Descriptor boundary and zero-copy readiness

The pipeline transports `FrameDescriptor` and its existing `OpaqueSurfaceHandle` reference.

It does not:

- copy pixel buffers,
- materialize bulk image payloads,
- marshal media payloads through IPC,
- introduce a managed byte-array media representation,
- reinterpret provider-specific surface handles.

The same descriptor instance is handed from producer-side submission to consumer-side processing.

This keeps the boundary compatible with future zero-copy or low-copy provider implementations where the descriptor carries a handle to externally owned host, device or shared storage.

## Bounded queues

`BoundedMediaFrameQueue` has a fixed capacity and never grows beyond that limit.

Supported backpressure policies:

### `Wait`

The producer waits for capacity. Cancellation is checked while waiting and terminates the blocked producer with `OperationCanceledException`.

### `RejectIncoming`

When full, the incoming frame is dropped and explicitly counted as both dropped and rejected.

### `DropOldest`

When full, the oldest queued frame is dropped and the incoming frame is accepted. This preserves the freshest bounded set for latency-sensitive paths.

Queue statistics expose:

- enqueued frames,
- dequeued frames,
- dropped frames,
- rejected frames,
- current depth,
- maximum observed depth.

## Sequence semantics

`MediaFramePipeline` validates the expected frame sequence before admission.

A gap or out-of-order frame fails closed with:

`media.pipeline.sequence_mismatch`

Dropped or backpressured frames still consume their sequence position. This makes loss observable without silently renumbering later frames.

## Timestamp and late-frame semantics

`MediaClockPosition` carries an explicit presentation timestamp and `Timebase`.

`MediaFrameTimingEvaluator` requires the current clock position and frame timing to use the same timebase.

A frame is late when:

```text
current presentation timestamp
>
frame presentation timestamp + configured tolerance
```

Late frames can be dropped either:

- before queue admission, or
- after queue residence before consumer delivery.

Both paths increment explicit late and dropped accounting.

No wall-clock conversion is required for this classification.

## Cancellation and shutdown

The bounded queue supports cancellation for blocked producers and consumers.

Ordered shutdown is:

```text
CompleteAdding
→ stop accepting new frames
→ drain already queued frames
→ Completed
```

Disposal is a fail-safe cleanup path. Any frame still queued during disposal is marked dropped rather than left in an ambiguous lifetime state.

## Media observations

Queue and pipeline observations use stable codes for lifecycle-relevant events, including:

- enqueue,
- dequeue,
- backpressure rejection,
- drop-oldest,
- late submit drop,
- late consume drop,
- sequence rejection,
- consumer failure,
- completion.

Observations intentionally contain descriptors/counters and failures rather than bulk media payloads.

## VirtualMedia integration

The Virtual Media Runtime Slice architecture-proof integration now routes VirtualMedia frames through `MediaFramePipeline`.

This means synthetic 1080p50 and 1080p59.94 frames use the same:

- lease lifecycle,
- sequence validation,
- timestamp rules,
- bounded queue semantics,
- consumer ownership transition,
- release semantics

that later provider compositions can reuse.

The provisional `CommittedMediaRuntime` implementation from Virtual Media Runtime Slice is removed. Its hardware-free resource reservation helper remains in Runtime as a separate component because it belongs to Runtime prepare/commit testing rather than Media processing.

## Test coverage

Media Pipeline Foundation adds qualification for:

- frame lifetime transitions,
- invalid ownership at pipeline entry,
- bounded queue limits,
- reject-incoming backpressure,
- producer-faster-than-consumer with drop-oldest,
- late frame before admission,
- frame becoming late while queued,
- dropped-frame accounting,
- ordered shutdown and drain,
- cancellation of a blocked producer,
- sequence-gap rejection,
- descriptor/opaque-handle identity preservation,
- existing Virtual Media Runtime Slice VirtualMedia E2E behaviour through the new pipeline,
- 50 fps long-run simulation,
- 59.94 fps long-run simulation.

## Performance qualification

The Performance test project runs descriptor-only long-run simulations for both V1 development rates:

- 30,000 frames for 50 fps, representing ten minutes of production time,
- 36,000 frames for 59.94 fps, representing approximately ten minutes of production time.

For each simulation the qualification requires:

- every submitted frame is consumed,
- zero late frames,
- zero dropped frames,
- zero rejected frames,
- queue maximum depth never exceeds configured capacity,
- the descriptor-only simulation completes within 15 seconds on the Windows reference CI runner.

The 15-second threshold is deliberately broad. It is a regression guard for the managed descriptor path, not a claim of professional hardware latency or deterministic OS scheduling.

Actual CI status is evidence only after the Windows reference workflow completes.

## Evidence boundary

This package provides managed reference evidence for Media lifecycle semantics.

It does not provide evidence for:

- real capture/output devices,
- DMA behaviour,
- GPU memory transfer,
- driver latency,
- genlock,
- OS hard-real-time scheduling,
- native interop cost,
- physical hardware queue depth.

Those remain `UNVERIFIED` until the relevant later work packages and hardware qualification exist.

`UNVERIFIED` is never treated as `PASS`.
