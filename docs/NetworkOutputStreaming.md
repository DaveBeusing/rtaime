<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Network Output: SRT and NDI

## Purpose

Network output extends the existing governed Program/Aux output-role model. It is not a separate Program path, renderer, compositor, mixer or authority surface.

The implemented path is:

```text
ControlHost authoritative output-role state
  -> prepared Runtime execution
  -> committed Program/Aux media result
  -> RuntimeNetworkOutputBridge
  -> provider-neutral INetworkOutputSession
  -> RuntimeNetworkOutputProviderRegistry
     -> SRT provider
     -> NDI provider
```

ControlHost remains Production Authority. RuntimeHost owns output execution and lifecycle. Individual providers own only protocol/runtime-specific submission behavior.

## Provider-neutral configuration

Common network-output configuration owns:

- stable target identity;
- governed Program/Aux role association;
- Runtime video/audio format;
- bounded queue capacity;
- reconnect delay/attempt policy;
- protocol family;
- typed provider-specific settings.

SRT-only settings are represented by `SrtNetworkOutputSettings`. NDI-only settings are represented by `NdiNetworkOutputSettings`. A configuration whose protocol does not match its typed settings is rejected.

Stable Provider Contracts contain no NDI SDK/native types and no SRT transport implementation types.

## SRT reference path

The existing SRT path remains compatible and implements:

- SRT caller-mode output;
- H.264 video;
- AAC-LC stereo 48 kHz audio;
- 1920x1080 progressive at 50 fps and 60000/1001 fps;
- bounded sample queues;
- drop-oldest live backpressure;
- reconnect with bounded exponential delay and optional maximum attempts;
- optional passphrase loaded through an environment-variable reference;
- provider-confirmed connection/backpressure/statistics evidence.

Legacy Runtime descriptors that omit `protocol` continue to resolve as SRT.

Listener and rendezvous configuration values remain explicit but are not qualified by the first native SRT reference transport.

## NDI software path

`rtaime.Provider.Ndi` adds an NDI High Bandwidth software-output boundary for the current qualified software baseline:

- Windows x64;
- 1920x1080 progressive RGBA8 Runtime video;
- 50 fps;
- 60000/1001 fps;
- Program and Aux roles;
- final Runtime-owned stereo 48 kHz Float32 audio;
- stable NDI source name;
- bounded asynchronous submission;
- drop-oldest backpressure;
- explicit lifecycle/failure/statistics evidence.

The provider consumes the existing committed Program/Aux sample. It does not invoke a compositor, perform an independent GPU render/readback, choose a different source, or create an audio mix.

At the native representation boundary the provider converts the already selected interleaved Float32 stereo payload to planar Float32 required by the NDI send ABI. RGBA video is submitted from the retained Runtime payload. These are representation operations only; production routing/mixing authority remains upstream.

NDI HX is not claimed by this implementation.

## Runtime dependency

The NDI runtime is external and is not bundled with rtaime.

Runtime discovery is explicit:

- `RTAIME_NDI_LIBRARY_PATH` may identify the exact `Processing.NDI.Lib.x64.dll`;
- `NDI_RUNTIME_DIR_V6` may identify an NDI runtime directory;
- `NDI_RUNTIME_DIR_V5` may identify an NDI runtime directory.

If a compatible runtime cannot be located or required exports are unavailable, NDI becomes unavailable/faulted with bounded failure evidence. rtaime does not download the runtime and does not automatically fall back to SRT.

For licensing, redistribution and qualification boundaries see [NDI Runtime Dependency](NdiRuntimeDependency.md).

## Authority and routing

Network output is associated with an existing governed Program or Aux role identity.

Provider failure does not mutate authoritative routing and does not create a replacement source. ControlHost state remains authoritative while Runtime reports observational provider/output evidence.

Program and Aux may use different network-output providers. Each role currently admits at most one configured network-output target.

The Operator consumes synchronized Runtime evidence and must not infer LIVE state from configuration or operator intent.

## Media boundary

Video is handed to the network path through the existing owned Runtime readback lease. The bounded queue owns that lease until the sample is sent, dropped, rejected or the session shuts down. No network payload is transferred through management IPC.

Audio uses the final Runtime-owned governed audio bus selected by the output role as stereo 48 kHz Float32. That bus already contains the authoritative routing and processing result. Neither SRT nor NDI implements an independent mixer.

If the Runtime bridge has no materialized audio payload for a valid descriptor, it supplies bounded silence matching the descriptor timing rather than blocking Program continuity.

## Timing

Video and audio timestamps derive from Runtime media timing.

SRT converts that timing into the Media Foundation encoder timeline. NDI converts the same Runtime presentation timestamps into 100 ns timecode units at the native send boundary.

The provider never generates media presentation timing from wall clock. Wall clock is used only for observational fields such as last successful send.

At 50 fps, the authoritative Runtime audio cadence is preserved as supplied by the final audio bus. At 60000/1001, alternating sample-count cadence is likewise forwarded unchanged; the NDI provider does not resample or synthesize its own cadence.

## Backpressure

Runtime submission is synchronous, bounded and free of network I/O.

Each SRT or NDI session has a bounded complete-A/V queue. When a queue is full, the oldest complete sample is dropped and its owned video lease is released before the current live sample is admitted.

Network output must never indefinitely block Program execution.

Backpressure is represented through:

- accepted samples;
- sent samples;
- dropped samples;
- rejected samples;
- current queue depth;
- structured failure evidence.

## Reconnect and failure isolation

Connection/runtime/send failures move the provider through connecting, reconnecting or faulted observational lifecycle states.

Reconnect uses configured initial and maximum delays. A maximum-attempt value of zero means retry until shutdown where retry is meaningful; a positive value bounds attempts.

Runtime-unavailable failures such as a missing/incompatible NDI native library fail closed rather than spinning indefinitely.

A provider/runtime/encoder/secret/endpoint failure affects only its network-output session. It does not roll back Runtime committed execution and does not mutate Control authority.

Shutdown cancels reconnect work, disposes queued payload leases and releases provider/native resources.

## Configuration

RuntimeHost reads network targets from `--network-outputs=<json>` or `RTAIME_NETWORK_OUTPUTS`.

Common fields are:

- `roleId`: `program` or `aux`;
- `targetId`: stable local target identity;
- `protocol`: `srt` or `ndi`;
- `queueCapacity`;
- `reconnectInitialDelayMilliseconds`;
- `reconnectMaximumDelayMilliseconds`;
- `reconnectMaximumAttempts`.

SRT additionally accepts:

- `endpoint`: absolute `srt://` URI;
- `mode`: `caller`, `listener` or `rendezvous`;
- `latencyMode`: `low`, `normal` or `reliable`;
- `videoBitRate`;
- `audioBitRate`;
- `latencyMilliseconds`;
- `passphraseEnvironmentVariable`.

Example:

```json
[
  {
    "roleId": "program",
    "targetId": "program-srt",
    "protocol": "srt",
    "endpoint": "srt://127.0.0.1:9000/live"
  }
]
```

NDI accepts `sourceName` instead of SRT endpoint/codec/bitrate/latency fields:

```json
[
  {
    "roleId": "program",
    "targetId": "program-ndi",
    "protocol": "ndi",
    "sourceName": "rtaime Program",
    "queueCapacity": 8
  }
]
```

Mixed SRT/NDI fields are rejected. A configured NDI target never falls back to an SRT target.

## Offline preflight

Normal offline preflight remains independent of optional NDI:

```powershell
./tools/Invoke-OfflinePreflight.ps1 -BundlePath . -InstallPath C:\rtaime
```

For a deployment that will configure NDI:

```powershell
./tools/Invoke-OfflinePreflight.ps1 -BundlePath . -InstallPath C:\rtaime -RequireNdiRuntime
```

The offline bundle verifier rejects an accidentally bundled NDI runtime binary. Runtime presence is a prerequisite check, not interoperability certification.

## Health and Operator presentation

Runtime exposes network-output evidence with the governed output role.

The Operator presents a stream as live only when Runtime/provider evidence confirms the connected state.

Common evidence includes:

- provider and protocol;
- safe target identity;
- lifecycle and connected state;
- video/audio format;
- provider-specific codec/transport summary;
- queue depth;
- dropped/rejected samples;
- sent media/byte counters;
- reconnect count;
- last successful send;
- structured failure detail.

For SRT the Operator may show H.264/AAC bitrate. For NDI it shows NDI High Bandwidth / Float32 semantics rather than fabricating an encoded bitrate.

No SDK path, native library path or secret is projected into output snapshots.

## Qualification boundary

Repository tests and CI qualify:

- SRT configuration compatibility;
- typed NDI configuration validation;
- mixed-field rejection;
- Program/Aux provider selection;
- 1080p50 and 1080p59.94 software format admission;
- Runtime audio cadence preservation;
- bounded queue saturation/drop behavior;
- missing NDI runtime failure;
- send failure/recovery;
- clean shutdown/resource disposal;
- provider-neutral architecture;
- package/runtime/licensing guardrails.

Real NDI interoperability against a declared NDI runtime/peer environment remains **UNVERIFIED**. Physical-network throughput, switch behavior, multicast configuration, WAN behavior and external receiver compatibility require separate environment evidence.

The software implementation does not promote NDI connectivity into production authority and does not constitute NDI certification.

## Bounded wakeups and saturation observations

SRT and NDI signal the worker only when queue depth grows. Replacing an already queued oldest sample reuses its existing wakeup; it does not accumulate semaphore permits while a consumer is stalled. Both video lease and paired audio belong to one dropped A/V sample.

NetworkOutputStatistics additionally exposes MaximumQueueDepth and QueueCapacity; Backpressured means current depth is at capacity. The same observations are available in Runtime support snapshots per role. High-water marks are retained for the session. Optional constructor defaults keep older software observations representable without inventing capacity evidence.

Deterministic overload regressions stall each consumer, submit 10,002 samples at both supported media timebases, require a depth/high-water/wakeup bound of one, verify old leases are disposed, and release the consumer to verify drainage and resumed sends. This is software failure-isolation evidence, not NDI/SRT peer interoperability or hardware timing qualification.
