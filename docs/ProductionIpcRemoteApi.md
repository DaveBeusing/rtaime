<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Production IPC & Remote API V1

AP-14 introduces the first real cross-process control plane for rtaime while preserving the approved 28-project topology and host authority boundaries. AP-16 extends the Runtime snapshot payload with the committed authority reference required for process recovery without changing those ownership boundaries.

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
- `control.program.cut`
- `control.program.dissolve`
- `control.media_deck.snapshot.get`
- `control.media_deck.open`
- `control.media_deck.transport`
- `control.media_deck.marker`
- `control.media_deck.close`

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

### RuntimeHost

Control-facing messages:

- `runtime.ping`
- `runtime.providers.get`
- `runtime.snapshot.get`
- `runtime.execution.apply`
- `runtime.media_deck.snapshot.get`
- `runtime.media_deck.open`
- `runtime.media_deck.transport`
- `runtime.media_deck.close`

The server delegates normal production execution to `V1RuntimeHostService`. The media-deck slice delegates local-file decode and transport to the RuntimeHost-owned single-deck service, using a ControlHost-supplied `PreparedExecutionContract`. Raw video/audio payloads never cross this management IPC boundary.

A Runtime snapshot exposes two deliberately separate revision domains:

```text
ExecutionRevision
AuthorityStateId
AuthorityRevision
```

`ExecutionRevision` is Runtime-local transactional history. `AuthorityStateId` and `AuthorityRevision` identify the `AuthoritySnapshotReference` carried by the `PreparedExecutionContract` that produced the currently committed execution. The authority fields are absent when Runtime has no committed authority and are emitted as a pair when present. ControlHost fails closed on a malformed partial pair.

AP-16 process recovery compares the Runtime authority reference with Control authority; it never assumes that `ExecutionRevision == Production Revision`.

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


## AP-53 recording control extension

AP-53 adds recording control to the existing private management IPC without creating a new public Control contract or transferring media payloads over Named Pipes.

Operator-facing ControlHost messages:

- `control.recording.start`
- `control.recording.stop`

ControlHost-facing RuntimeHost messages:

- `runtime.recording.start`
- `runtime.recording.stop`

The normal ControlHost snapshot now carries the Runtime-observed recording lifecycle, elapsed time, configured destination/file name, final path, bounded recorder statistics and failure metadata.

Only commands and recording metadata cross management IPC. Program RGBA pixels and Float32 audio remain inside RuntimeHost and the Recording subsystem. Recording start/stop is serialized by ControlHost's existing mutation gate but does not create or advance an authoritative Production revision.
