<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Virtual Media Provider & Runtime Vertical Slice

## Status

Virtual Media Runtime Slice architecture proof implementation.

This package establishes the first executable rtaime production path that does not require external media hardware.

## Change classification

`ARCHITECTURE`

The package realizes the already approved provider/runtime boundaries and the existing Control → Planning → Transactional Runtime model. It does not introduce a new architectural direction and therefore does not add an ADR.

## Vertical path

```text
Production Specification
→ Desired State
→ Authoritative State
→ Logical Production Graph
→ Capability Resolution
→ Prepared Execution
→ Runtime Prepare
→ Runtime Commit
→ Committed Media Runtime
→ Synthetic Frames
→ Virtual Outputs
→ Runtime Observation
```

A Control CUT follows the same authority path:

```text
CutProgramCommand
→ new Authoritative Revision
→ re-plan
→ new Prepared Execution
→ prepare
→ commit
→ next media frame boundary observes the new committed execution
```

## Virtual media reference provider

`rtaime.Provider.VirtualMedia` now provides:

- a deterministic virtual timing provider,
- Source A and Source B synthetic video sources,
- deterministic frame/surface identity generation,
- virtual video outputs used as Program/Preview inspection points,
- capability advertisement for `media.route`,
- two deterministic reservable route resources,
- explicit support for the V1 development formats 1080p50 RGBA8 and 1080p59.94 RGBA8.

The synthetic provider does not allocate or transport bulk pixel payloads. A frame is represented through the existing `FrameDescriptor`, deterministic `SurfaceDescriptor`, timing metadata and an opaque virtual surface handle. This preserves the contract rule that normal boundaries exchange descriptors/handles rather than large media payloads.

The virtual implementation is Reference Behaviour and a deterministic test oracle. It is not evidence for professional capture/output hardware, GPU, DMA, genlock or external device qualification.

## Internal broadcast reference signal

The virtual source timing and frame-descriptor path is also reused by the internal broadcast reference signal documented in [BroadcastTestPattern.md](BroadcastTestPattern.md).

Enabling the reference signal replaces only the resolved RGBA content of a selected production source slot. The existing source identity, deterministic timing, media pipeline, GPU processing, monitoring and Program output path remain authoritative. The static generated frame is retained, so no full-frame pattern construction occurs on production boundaries.

The optional motion/timing diagnostic mode is documented in [MotionTimingTestSignal.md](MotionTimingTestSignal.md). It derives frame counter, media-time timecode and motion phase from the same virtual `FrameTiming`; no second clock or scheduling loop is introduced. Only its retained bounded dynamic region is redrawn on each active motion frame.

## Timing semantics

For a configured video format, one virtual timing tick equals exactly one frame duration:

```text
1080p50    → Timebase 1/50
1080p59.94 → Timebase 1001/60000
```

The frame sequence number is also used as the presentation-timestamp tick. This makes repeated virtual runs bit-for-bit comparable at the descriptor/timing level without wall-clock dependence.

## Runtime media execution

`rtaime.Runtime` now contains the minimal provider-neutral committed-media executor required by Virtual Media Runtime Slice.

The runtime:

- reads only the active `CommittedRuntimeExecution`,
- captures that execution once at the start of each frame boundary,
- resolves source/output endpoints from committed `PreparedExecutionBinding` identities,
- validates source/output format compatibility,
- requests the same global sequence number from every active route,
- stages all source frames before emitting outputs,
- records a Runtime observation for every successful or rejected boundary,
- advances the media sequence only after a successful boundary.

Runtime does not reference `rtaime.Provider.VirtualMedia`.

The concrete provider is composed at the outer boundary/test composition, preserving the approved dependency direction.

## CUT activation boundary

A CUT never mutates the current media execution directly.

The new Program source becomes visible only after:

1. Control accepts the CUT and advances Authoritative State,
2. planning produces a new Prepared Execution,
3. Runtime successfully prepares it,
4. Runtime successfully commits it.

`CommittedMediaRuntime` captures the active execution once per `ProcessNextFrameBoundary()` call. Therefore:

- a commit completed before a boundary is used by that boundary,
- a commit completed after the boundary snapshot can affect only a later boundary.

This is the Virtual Media Runtime Slice deterministic activation rule.

## Resource reservation

Virtual Media Runtime Slice adds an in-memory deterministic implementation of the Transactional Runtime Commit reservation abstraction for reference execution.

It:

- derives a reservation identity from PreparedExecution identity plus the ordered resource set,
- tracks active reservations,
- rejects reservation-identity conflicts,
- releases reservations when Runtime supersedes or aborts an execution.

It is not a physical device reservation mechanism.

## Format-authority evidence boundary

The current `ProductionSpecification` contract does not yet carry an explicit production `VideoFormat`.

Virtual Media Runtime Slice does not silently change that contract. The virtual reference environment is explicitly parameterized with one of the two already defined V1 development formats and proves execution behavior for both.

Therefore Virtual Media Runtime Slice proves:

- deterministic execution at 1080p50,
- deterministic execution at 1080p59.94,
- provider format behaviour and timing,

but it does **not** claim that production-format selection is already represented in authoritative Control state. Any future change that places format selection into Production Specification / Desired State is a separate contract-governed change.

## Provisional media-lifecycle boundary

This is intentionally the minimal virtual execution required before Media Pipeline Foundation.

Virtual Media Runtime Slice does not yet claim production-grade implementations for:

- bounded media queues,
- backpressure,
- dropped/late frame accounting,
- generalized frame lifetime/ownership enforcement,
- cancellation and ordered media shutdown,
- zero-copy hardware paths,
- atomic multi-output hardware presentation.

Those are owned by Media Pipeline Foundation Media Pipeline Foundation.

## Verification obligations

Virtual Media Runtime Slice integration evidence covers at least:

- Source A → Program,
- Source B → Program,
- A → CUT → B,
- frame sequence continuity,
- expected activation boundary,
- 1080p50,
- 1080p59.94,
- deterministic repeated execution,
- failed prepare leaves current Program intact,
- deterministic VirtualMedia capability/resource advertisement.

The complete repository Architecture, Contract, Unit, Integration, Behavioral, Failure and Performance baseline must remain green.

## Evidence claims

A passing virtual test proves software architecture behaviour in the CI environment only.

It does not certify:

- physical media I/O,
- professional GPU behaviour,
- driver timing,
- genlock/reference,
- DMA/zero-copy,
- broadcast hardware continuity.

Missing physical hardware evidence remains `UNVERIFIED`, never `PASS`.
