<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Production IPC & Remote API V1

Production IPC & Remote API introduces the first real cross-process control plane for rtaime while preserving the approved 28-project topology and host authority boundaries. Process Recovery & Supervision extends the Runtime snapshot payload with the committed authority reference required for process recovery without changing those ownership boundaries.

## Process topology

```text
Operator
  -> rtaime.Client
  -> local Named Pipe
  -> ControlHost
  -> local Named Pipe
  -> RuntimeHost

ControlHost or RuntimeHost
  -> local Named Pipe
  -> AIHost
```

Hosts never reference another host project. IPC implementations live at existing outer composition boundaries and operate only on contract/domain mappings already available to the owning project.

## V1 transport

The Windows V1 reference transport uses `System.IO.Pipes` with `PipeOptions.CurrentUserOnly`. Default endpoints are:

- `rtaime.v1.control.default`
- `rtaime.v1.runtime.default`
- `rtaime.v1.ai.default`

Endpoints are configuration values, not contract identities. Local Named Pipes are the qualified V1 reference transport. TCP, HTTP, gRPC, TLS, cluster discovery, NMOS and remote-network deployment remain UNVERIFIED.

## Framing and envelope

IPC protocol version is `1.0` and is independent of Control, Runtime, Media, Provider and AI contract versions.

Each message uses:

```text
uint32 big-endian JSON length
UTF-8 JSON envelope
```

Control frames are bounded to 1 MiB before payload allocation. The envelope contains protocol version, message type, request identity, correlation identity, host instance identity, UTC send time, state version, sequence and payload.

## Handshake

The first message on every connection is `client.hello`. A server rejects messages before the handshake, an unsupported IPC version, an invalid role or incompatible required contract versions. No silent downgrade is performed.

Roles used by the V1 control plane are:

- `OperatorClient`
- `ControlHost`
- `RuntimeHost`
- `AIHost`

Each server process creates a new `HostInstanceId`. Clients use that identity to distinguish reconnect from process replacement.

## Remote APIs

### ControlHost

Operator-facing messages:

- `control.ping`
- `control.snapshot.get`
- `control.preview.select`
- `control.output.route`
- `control.scene.activate`
- `control.program.cut`
- `control.program.dissolve`
- `control.audio.input.set`
- `control.audio.test_signal.set`
- `control.test_pattern.set`
- `control.media_deck.snapshot.get`
- `control.media_deck.open`
- `control.media_deck.transport`
- `control.media_deck.marker`
- `control.media_deck.close`

The `control.audio.input.set` and `control.audio.test_signal.set` messages validate that the selected audio slot belongs to the authoritative production, then delegate Runtime-owned audio state changes without advancing Production revision. Generated-audio requests carry only source identity, enabled state, mode, frequency and bounded peak level; raw samples never traverse management IPC.

The `control.test_pattern.set` message validates that the selected slot belongs to the authoritative production, then delegates the generated-source state change to RuntimeHost. Its bounded payload carries source identity, enabled state and the optional motion/timing mode flag. The Operator never addresses RuntimeHost directly.

The media-deck messages preserve the same authority direction. Operator intent enters ControlHost, which validates the selected production source slot, owns persisted IN/OUT and cue metadata, and proxies decode/transport execution to RuntimeHost. The Operator does not obtain direct RuntimeHost access.

A successful production-routing mutation crosses the existing boundary:

```text
validate command
-> stage proposed authoritative state
-> plan PreparedExecutionContract
-> remote Runtime apply
-> Runtime commit result
-> Control commit confirmation
```

Transport failure or Runtime rejection never promotes staged state to authoritative state.

`control.scene.activate` uses this same mutation pipeline. Its bounded command carries the Scene identity plus the normal Control command metadata and optimistic expected Production Revision. ControlHost resolves the Scene from the production specification, validates all declared routing dependencies plus the optional versioned compositing state, and stages routing and declared layer state together. The resulting `PreparedExecutionContract` carries the bounded compositing snapshot through the existing `runtime.execution.apply` request. RuntimeHost verifies required layer resources before prepare/commit and ControlHost promotes the complete authoritative state plus `ActiveSceneId` only after Runtime commit confirmation. The full Operator snapshot carries the Scene catalog with optional desired layer definitions, authoritative compositing state, Runtime-confirmed layer evidence and optional confirmed `ActiveSceneId`; local Scene selection is not transmitted as a mutation.

`control.output.route` also uses the same stage/plan/apply/confirm pipeline. V1 accepts it for governed non-Program roles such as Aux; Program continues to require the transition-aware CUT/DISSOLVE/Scene paths. The command carries only role identity and source identity in addition to normal optimistic Control metadata. Control snapshots carry authoritative role configuration, while Runtime snapshots carry bounded role-specific provider/target/format/timing/lifecycle/health evidence. ControlHost reconciles the two before publishing Operator evidence; stale, missing or source-mismatched Runtime evidence never becomes confirmed output truth.

Direct graphics and compositing messages continue through ControlHost rather than creating a Scene-specific transport. After Runtime confirms such a mutation, ControlHost incorporates the resulting bounded compositing snapshot into Production authority. If it no longer exactly matches the active Scene's governed state, `ActiveSceneId` is cleared before the updated authoritative snapshot is published.

### RuntimeHost

Control-facing messages:

- `runtime.ping`
- `runtime.providers.get`
- `runtime.snapshot.get`
- `runtime.execution.apply`
- `runtime.audio.input.set`
- `runtime.audio.test_signal.set`
- `runtime.test_pattern.set`
- `runtime.media_deck.snapshot.get`
- `runtime.media_deck.open`
- `runtime.media_deck.transport`
- `runtime.media_deck.close`

The server delegates normal production execution to `V1RuntimeHostService`. Prepared execution bindings may carry a stable output-role identifier; RuntimeHost uses those bindings to execute governed Program/Aux routes through the admitted provider resources and returns bounded output-role evidence in the normal Runtime snapshot. The `runtime.audio.test_signal.set` request configures the generated audio source for an existing audio input and returns the Runtime-confirmed mode, active identification channel, frequency and peak level through the normal audio-input snapshot. The `runtime.test_pattern.set` request selects static or motion/timing video generation for an existing source slot; snapshots separately identify active generated sources and those currently using motion/timing diagnostics. The media-deck slice delegates local-file decode and transport to the RuntimeHost-owned single-deck service, using a ControlHost-supplied `PreparedExecutionContract`. Raw video/audio payloads never cross this management IPC boundary.

A Runtime snapshot exposes two deliberately separate revision domains:

```text
ExecutionRevision
AuthorityStateId
AuthorityRevision
```

`ExecutionRevision` is Runtime-local transactional history. `AuthorityStateId` and `AuthorityRevision` identify the `AuthoritySnapshotReference` carried by the `PreparedExecutionContract` that produced the currently committed execution. The authority fields are absent when Runtime has no committed authority and are emitted as a pair when present. ControlHost fails closed on a malformed partial pair.

Process Recovery & Supervision process recovery compares the Runtime authority reference with Control authority; it never assumes that `ExecutionRevision == Production Revision`.

### AIHost

AI-facing messages:

- `ai.ping`
- `ai.capabilities.get`
- `ai.snapshot.get`
- `ai.inference.execute`

The endpoint delegates governed execution to `AIHostService`. The process boundary does not transfer production authority to AIHost.

## StateVersion and resynchronization

Remote `StateVersion` is separate from authoritative Production `Revision` and from Runtime `ExecutionRevision`.

Production Revision advances only when an authoritative production mutation is committed. Runtime ExecutionRevision advances according to Runtime-local commit history. StateVersion may also change because a host connects, disconnects, changes health or is resynchronized.

Clients establish synchronization from a full snapshot. Timed metadata deltas are valid only when their `BasedOnStateVersion` equals the client's current StateVersion. A gap or HostInstanceId change invalidates the delta stream and requires a new full snapshot.

## Runtime reconnect and restart

ControlHost may start before RuntimeHost and reports `Degraded`. Its binding loop retries without making an authoritative mutation.

When RuntimeHost becomes available for a fresh production:

```text
connect
-> handshake
-> immutable provider snapshot
-> plan initial state
-> Runtime prepare/commit
-> Control confirmation
-> Ready
```

For an existing authoritative production, ControlHost obtains `runtime.snapshot.get` and reconciles the Runtime committed `AuthorityStateId`/`AuthorityRevision` with the Control ProductionId/Production Revision. Matching authority is adopted even when Runtime ExecutionRevision differs. Missing or older matching authority is reapplied without advancing Control Production Revision. Newer or foreign committed authority fails closed and requires intervention.

## Idempotency

ControlHost, RuntimeHost and AIHost maintain bounded process-local request-result caches for mutation/execution requests. The same RequestId with the same canonical request returns the cached response. Reusing a RequestId with different request content fails closed.

The cache is intentionally not durable. After a server process restart, clients must obtain a full snapshot rather than blindly replay an uncertain mutation.

## Payload boundary

Management IPC may carry commands, state, provider descriptors, `PreparedExecutionContract`, media-deck metadata, frame/surface descriptors, opaque handles and governed AI metadata.

It must not carry raw video frames, RGBA byte arrays, audio sample arrays, segmentation mask pixels or GPU memory payloads. Bulk media remains on Media/Provider resource paths.

## Security baseline

The local-process security baseline includes:

- `PipeOptions.CurrentUserOnly`
- explicit roles and version checks
- bounded frame sizes
- bounded request caches
- strict message dispatch
- timeouts and cancellation
- no arbitrary runtime type activation
- no serializer type-name polymorphism
- no credential/secret payload requirement

Network authentication, TLS/PKI and RBAC are outside this local V1 IPC baseline.

## Failure behavior

Malformed, oversized, unknown, role-incompatible and version-incompatible requests fail closed. Runtime transport loss leaves Control authority unchanged and places ControlHost in `Degraded` until a successful resynchronization. A committed Runtime snapshot with a missing authority reference, a foreign AuthorityStateId or an AuthorityRevision ahead of durable Control authority is not overwritten automatically. AI transport loss cannot gain production authority.

## Evidence boundary

Managed CI can prove framing, mappings, local Named Pipe behavior, cross-process command paths, cancellation, failure isolation, process restart semantics and topology invariants. It does not prove remote-network latency, broadcast hard-real-time behavior, GPU cross-process memory sharing, professional hardware timing, distributed HA or security certification.


## recording control extension

Recording Operator Workflow adds recording control to the existing private management IPC without creating a new public Control contract or transferring media payloads over Named Pipes.

Operator-facing ControlHost messages:

- `control.recording.start`
- `control.recording.stop`

ControlHost-facing RuntimeHost messages:

- `runtime.recording.start`
- `runtime.recording.stop`

The normal ControlHost snapshot now carries the Runtime-observed recording lifecycle, elapsed time, configured destination/file name, final path, bounded recorder statistics and failure metadata.

Only commands and recording metadata cross management IPC. Program RGBA pixels and Float32 audio remain inside RuntimeHost and the Recording subsystem. Recording start/stop is serialized by ControlHost's existing mutation gate but does not create or advance an authoritative Production revision.


## Runtime Health & Performance HUD Runtime health snapshot extension

Runtime Health & Performance HUD extends the existing private RuntimeHost and ControlHost snapshot payloads with bounded observational health/performance metadata. No public Control or Runtime contract version changes are introduced.

RuntimeHost snapshot metadata now carries Runtime uptime, frame budget, the last observed Program-boundary processing duration, cumulative dropped-frame evidence, CPU identity/utilization, system-memory usage/capacity, GPU backend/physical-device identity and optional GPU utilization/VRAM measurements. CPU and system-memory measurements are qualified on Windows. NVIDIA GPU measurements are supplied by NVML when the installed driver exposes them; otherwise those optional fields remain absent/UNVERIFIED.

ControlHost combines this Runtime metadata with its authoritative-state availability, Runtime timing/execution state, media observations and cached Runtime provider descriptors to produce the Operator `PASS / FAIL / UNVERIFIED` health projection.

Only metadata crosses management IPC. Runtime Health & Performance HUD does not transport frame pixels, audio samples, GPU surfaces or telemetry histories, and it introduces no new polling transport or remote-monitoring API.


## Visible AI Showcase Integration AI showcase control and observation

Operator-facing ControlHost adds:

- `control.ai_showcase.set`

ControlHost-facing RuntimeHost adds:

- `runtime.ai_showcase.set`

The Runtime snapshot and Operator snapshot carry the bounded Person Segmentation Highlight state: enable flag, feature/status/provider, measured inference time, Person Regions count, source/application sequence, confidence, visible-effect flag and optional failure.

RuntimeHost communicates with AIHost using the existing `client.hello`, `ai.capabilities.get` and `ai.inference.execute` protocol as role `RuntimeHost`. No host project reference is introduced. The handoff contains the Program `FrameDescriptor` and inference metadata only; no Program RGBA payload crosses the management IPC path.
## Show Control management IPC

The existing ControlHost Operator management session exposes bounded Show Control commands for snapshot retrieval, cue-list save/selection, arm, GO, cancel and recovery acknowledgement.

Show Control uses the existing protocol negotiation, request-id idempotency cache and synchronized Operator snapshot. Cue-list definitions and execution metadata cross management IPC; media payloads do not.

Production-changing cue actions are dispatched through established ControlHost command paths. The Show Control IPC surface does not provide direct Runtime or provider mutation. Frame-wait progression observes Runtime frame sequence and host identity through ControlHost while authoritative action dispatch remains unchanged.

See `docs/ShowControlCueSequencing.md`.
