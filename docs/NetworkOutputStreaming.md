<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Network Output and SRT Streaming

## Purpose

Network output extends the existing governed Program/Aux output-role model. It is not a separate Program path, renderer, compositor or authority surface.

The implemented reference path is:

```text
ControlHost authoritative output-role state
  -> prepared Runtime execution
  -> committed Program/Aux media result
  -> RuntimeNetworkOutputBridge
  -> provider-neutral NetworkOutputProgramSample
  -> SrtNetworkOutputSession
  -> H.264/AAC MPEG-TS encoder
  -> SRT transport
```

ControlHost remains Production Authority. RuntimeHost owns execution and lifecycle. The SRT provider owns protocol and transport details.

## Implemented capability

The reference provider implements:

- SRT caller-mode output;
- H.264 video;
- AAC-LC stereo 48 kHz audio;
- 1920x1080 progressive RGBA8 Runtime video at 50 fps and 60000/1001 fps;
- bounded sample queues;
- drop-oldest live backpressure;
- reconnect with bounded exponential delay and optional maximum attempts;
- optional SRT passphrase loaded through an environment-variable reference;
- Runtime/provider health evidence including connection state, bitrate, queue depth, sent bytes/packets, dropped/rejected samples and reconnect count.

Listener and rendezvous values exist in the provider-neutral configuration model but are not qualified by the first native reference transport. They fail closed rather than silently behaving as caller mode.

NDI, RIST, RTMP/RTMPS, WebRTC, SMPTE ST 2110, network ingest, adaptive bitrate and CDN management are not implemented by this foundation.

## Authority and routing

Network output is associated with existing governed Program or Aux role identity.

Provider failure does not mutate authoritative routing and does not create a replacement source. ControlHost state remains authoritative while Runtime reports observational provider/output evidence.

Program and Aux use the same output-role model. The Operator consumes synchronized Runtime evidence and must not infer LIVE state from configuration or button intent.

## Media boundary

Network output consumes the already committed Runtime media result.

Video is handed to the network path through an owned Runtime readback lease. The queue owns the lease until the sample is sent, dropped, rejected or the session shuts down. No network payload is transferred through management IPC.

Audio uses the final Runtime-owned governed Program audio bus as stereo 48 kHz Float32. That bus already includes AFV/breakaway routing plus any confirmed advanced source mix, crossfade, ducking and Program master state. Network output does not implement a separate mix. If the Runtime bridge has no materialized audio payload for a valid descriptor, it supplies bounded silence matching the descriptor timing rather than blocking Program continuity.

The reference encoder converts:

- RGBA8 -> NV12 for Media Foundation H.264 input;
- Float32 -> signed PCM16 for Media Foundation AAC input.

The encoder writes H.264/AAC into an in-memory MPEG-2 transport stream. Network transport is not coupled to recording-file naming or publication semantics.

## Timing

Video and audio timestamps derive from Runtime media timing.

The Media Foundation encoder converts the existing timebase into 100 ns units and establishes one shared A/V origin from the first sample. Video and audio timestamps must remain non-negative and strictly monotonic.

Wall-clock time is used only for observational fields such as last successful send; it does not generate media presentation timestamps.

## Backpressure

Runtime submission is synchronous, bounded and free of network I/O.

Each SRT session has a bounded queue. When the queue is full, the oldest complete A/V sample is dropped and its owned video lease is released before the current live sample is admitted.

This policy intentionally favors current live output over unbounded latency growth.

Backpressure is represented through:

- accepted samples;
- sent samples;
- dropped samples;
- rejected samples;
- current queue depth;
- structured failure evidence.

Network output must never indefinitely block Program execution.

## Reconnect and failure isolation

Connection and send failures move the provider through connecting, reconnecting or faulted observational lifecycle states.

Reconnect uses configured initial and maximum delays. A maximum-attempt value of zero means retry until shutdown; a positive value bounds attempts and produces explicit fault evidence when exhausted.

A provider, encoder, secret or endpoint failure affects only the network output session. It does not roll back Runtime committed execution and does not mutate Control authority.

Shutdown cancels connection/reconnect work, disposes queued payload leases and drains/releases provider resources.

## Configuration

RuntimeHost reads network targets from `--network-outputs=<json>` or `RTAIME_NETWORK_OUTPUTS`.

The value is a JSON array. Each entry supports:

- `roleId`: `program` or `aux`;
- `targetId`: stable local target identity;
- `endpoint`: absolute `srt://` URI with host and port;
- `mode`: `caller`, `listener` or `rendezvous`;
- `latencyMode`: `low`, `normal` or `reliable`;
- `videoBitRate`;
- `audioBitRate`;
- `latencyMilliseconds`;
- `queueCapacity`;
- `passphraseEnvironmentVariable`;
- `reconnectInitialDelayMilliseconds`;
- `reconnectMaximumDelayMilliseconds`;
- `reconnectMaximumAttempts`.

The current reference codec configuration is H.264 + AAC-LC.

A target URI must not contain user-info credentials. Secret values are not stored in source-controlled configuration. When encryption is used, `passphraseEnvironmentVariable` names the environment variable containing the SRT passphrase.

The native library can be resolved through `RTAIME_SRT_LIBRARY_PATH`. The reference transport requires SRT 1.5.7 or newer.

## Health and Operator presentation

Runtime exposes network output evidence with the governed output role.

The Operator presents a network stream as live only when Runtime/provider evidence confirms the connected state. Configuration without provider confirmation remains unverified.

Relevant evidence includes:

- provider and protocol;
- safe target identity without secret material;
- lifecycle and connected state;
- video/audio format and codec;
- encoded bitrate;
- queue depth;
- dropped/rejected samples;
- packets/bytes sent;
- reconnect count;
- last successful send;
- structured failure detail.

## Qualification boundary

Repository tests and CI can verify contract validation, bounded queue behavior, failure isolation, deterministic timestamp conversion, Runtime/Operator projection and fake/reference provider behavior.

Native SRT availability, real network path quality, sustained physical-network throughput, WAN behavior and deployment-specific firewall/NAT behavior require separate environment evidence.

Software-only 1080p50/59.94 measurements must not be presented as physical-network certification.

## Output-role audio bus selection

Network output consumes the final Runtime-owned audio payload selected by its governed output role. Program defaults to the `program` bus; Aux may select another configured authoritative bus. The SRT/provider layer receives that already mixed stereo 48 kHz payload and does not perform an independent remix.

Legacy output-role configurations without an explicit audio-bus mapping preserve Program-audio behavior. Missing or invalid configured bus references fail closed before confirmed execution. Recording remains bound to the Program bus independently of Aux/network bus selection.

## Advanced-audio selected-bus qualification

Advanced Audio Processing qualification treats Program/Aux audio-bus selection as part of the final authoritative Runtime bus materialization path. A governed output role consumes the selected already-processed bus payload; the network provider does not perform an independent remix.

The retained network-output backpressure/failure tests remain part of the qualification boundary: transport pressure may drop bounded output work or surface failure evidence, but it must not redefine Program production authority or create an unbounded queue.

See [Advanced Audio Processing Qualification](AdvancedAudioProcessingQualification.md). Network transport evidence does not qualify physical embedded-audio hardware, device-driver latency, hardware clocks/genlock or certified audio performance; those remain **UNVERIFIED**.

