<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Advanced Audio Production

## Purpose

Advanced Audio Production extends the existing Audio Follow Video and explicit Breakaway model into one bounded Runtime-owned Program audio engine.

It does not add a second audio authority or a parallel recording/output mix path.

```text
ControlHost authoritative audio configuration
  -> RuntimeHost confirmed configuration
  -> bounded AudioProductionEngine
  -> final Program Float32 stereo bus
  -> metering
  -> Program recording
  -> physical/network output
```

ControlHost owns authoritative configuration. RuntimeHost owns sample processing and timing. Operator renders confirmed state and sends mutations through the existing client/control path.

## Qualified baseline

The initial advanced-audio baseline remains:

- 48 kHz;
- stereo;
- Float32;
- maximum 8 admitted source contributions;
- maximum 4 buses in the contract model;
- Program bus required;
- deterministic accumulation order;
- reusable Runtime buffers;
- hard clipping at [-1, +1];
- no steady-state per-block allocation in the mix engine.

Surround, immersive audio, arbitrary DSP graphs, plugins, EQ/compressor/reverb suites, network audio and sample-rate conversion are outside this capability.

## Source and bus model

Every admitted audio source has a stable `MediaSourceId`.

Each source configuration contains:

- source identity;
- linear gain in the range 0..4;
- mute state;
- whether contribution follows the currently routed AFV/Breakaway source;
- one or more bounded bus assignments.

Every bus contains:

- stable `AudioBusId`;
- master gain in the range 0..4;
- master mute state.

`program` is mandatory.

The legacy-compatible configuration assigns every admitted source to Program with `FollowRoutedSource=true`. Only the currently routed AFV/Breakaway source contributes, preserving existing behavior until advanced mixing is explicitly configured.

## Authority and revisions

`AudioProductionConfiguration.Revision` is monotonically increasing.

Operator mutations are created from the latest confirmed audio-production snapshot and advance the revision by exactly one. ControlHost rejects stale expected revisions. Runtime validates the complete configuration before applying it.

Local UI edit state never becomes audio truth.

## Gain, accumulation and clipping

Input and master gain are represented as finite linear multipliers in the inclusive range 0..4.

For each output sample:

```text
source sample
  * source gain/mute
  * crossfade contribution
  * ducking gain when targeted
  -> deterministic sum
  * bus master gain/mute
  -> hard clip to [-1, +1]
```

The engine also records pre-clip peak and clipped sample-value count so overload is observable rather than accidental.

Non-finite source values are treated as zero for accumulation and are never propagated as healthy audio output.

## Crossfade

Crossfades are explicit configuration objects with:

- target bus;
- from source;
- to source;
- absolute start sample position;
- duration in audio samples;
- law.

The default production law is equal-power:

```text
from = cos(progress * pi / 2)
to   = sin(progress * pi / 2)
```

Linear crossfade remains an explicit supported contract option.

Progress is computed from absolute Runtime audio sample position. No wall-clock or UI timer participates in transition timing.

A newer configuration revision replaces the previous crossfade deterministically.

## Ducking

Ducking is one bounded deterministic rule, not a general-purpose DSP graph.

Configuration contains:

- Program/bus identity;
- enabled state;
- sidechain source;
- bounded target-source set;
- threshold;
- attenuation target gain;
- attack samples;
- hold samples;
- release samples.

The detector uses the maximum absolute stereo sample value of the sidechain at each frame.

Attack and release advance by fixed per-sample steps derived from the configured sample counts. Hold is measured in audio samples.

If the sidechain becomes unavailable, the engine reports `SidechainAvailable=false` and releases toward unity rather than fabricating sidechain activity.

## AFV and Breakaway interoperability

AFV and Breakaway continue to determine the routed audio source.

A source with `FollowRoutedSource=true` contributes only when its identity matches the currently routed source. A source with `FollowRoutedSource=false` may contribute continuously when assigned to the target bus.

This means:

- legacy AFV remains unchanged by default;
- explicit Breakaway remains explicit;
- advanced always-on contributions can be added without creating another routing engine;
- crossfade and ducking operate within the same Runtime mix stage.

## Program media truth

Runtime creates one final Program audio buffer per boundary.

The final mixed Program bus is reused by:

- Program metering;
- Program recording descriptor/payload staging;
- Program network output;
- Aux network output where the current output contract intentionally reuses Program audio.

Recording and output do not implement independent audio mixing logic.

## Metering and diagnostics

Runtime publishes confirmed advanced-audio evidence including:

- Program left/right peak;
- pre-clip peak;
- clipping state;
- clipped sample-value count;
- ducking gain/reduction;
- sidechain availability;
- crossfade progress;
- active source count;
- missing source count;
- authoritative configuration revision.

Operator updates these values from synchronized Runtime/Control snapshots. No sample analysis is performed by WPF.

## Operator mixer

The Audio workspace uses the existing custom rtaime control system.

The operator can:

- retain FOLLOW VIDEO or explicit BREAKAWAY;
- edit selected source gain/mute;
- switch a source between routed-only and always-in-mix contribution;
- edit Program master gain/mute;
- start an equal-power crossfade using absolute sample start and duration;
- configure and enable bounded ducking with the selected source as sidechain;
- observe mix status, reduction, missing sources and clipping evidence.

UI changes are requests only. Confirmed Runtime state remains authoritative.

## Failure semantics

Missing source payloads contribute silence for that source and increment missing-source evidence.

An unavailable ducking sidechain is reported explicitly and releases the ducking envelope toward unity.

Invalid or stale configuration is rejected before becoming Runtime audio truth.

Runtime restart reconstructs audio production from authoritative Control configuration and the durable show-project state.

Audio processing failure must not be converted into a healthy state.

## Performance qualification

Hosted qualification covers:

- sample-level deterministic output;
- legacy AFV compatibility;
- two-source summing;
- input gain and mute;
- Program master gain;
- hard clipping;
- equal-power crossfade endpoints/midpoint;
- ducking attack/hold/release;
- sidechain loss;
- configuration replacement;
- zero steady-state mix-engine allocations;
- repeated maximum-input 48 kHz stereo blocks with crossfade and ducking enabled.

Hosted elapsed-time guards are regression evidence only. They are not physical audio-hardware latency or certification claims.
