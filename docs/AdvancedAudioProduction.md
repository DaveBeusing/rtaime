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

Surround, immersive audio, arbitrary DSP graphs, plugin hosting, dynamic/linear-phase EQ, compression/reverb suites, network audio and sample-rate conversion are outside this capability. The bounded three-band per-source parametric EQ described below is part of the qualified baseline.

## Source and bus model

Every admitted audio source has a stable `MediaSourceId`.

Each source configuration contains:

- source identity;
- linear gain in the range 0..4;
- mute state;
- whether contribution follows the currently routed AFV/Breakaway source;
- one or more bounded bus assignments;
- an optional bounded three-band equalizer configuration.

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
  -> optional low-shelf -> bell-mid -> high-shelf EQ
  * crossfade contribution
  * ducking gain when targeted
  -> deterministic sum
  * bus master gain/mute
  -> hard clip to [-1, +1]
```

The engine also records pre-clip peak and clipped sample-value count so overload is observable rather than accidental.

Non-finite source values are treated as zero for accumulation and are never propagated as healthy audio output.

## Bounded per-source equalizer

The initial production equalizer is optional per source and contains exactly three ordered bands:

1. low shelf;
2. parametric bell mid;
3. high shelf.

It is deliberately not a generic DSP graph or plugin host. A missing equalizer is bypass. Enabled bands at exactly 0 dB are also treated as mathematical bypass so the legacy sample path remains byte-equivalent.

Qualified parameter bounds at the 48 kHz baseline are:

- frequency: 20..20,000 Hz inclusive;
- shelf gain: -18..+18 dB inclusive;
- bell gain: -18..+18 dB inclusive;
- bell Q: 0.1..10 inclusive.

Shelf slope is fixed to the RBJ S=1 interpretation. The bell uses the configured Q. Coefficients are normalized biquad coefficients and are rejected if any value is non-finite or the normalized denominator fails the second-order stability checks.

For all three sections:

```text
A  = 10^(gainDb / 40)
w0 = 2*pi*frequency/48000
```

The bell uses:

```text
alpha = sin(w0) / (2*Q)
b0 = 1 + alpha*A
b1 = -2*cos(w0)
b2 = 1 - alpha*A
a0 = 1 + alpha/A
a1 = -2*cos(w0)
a2 = 1 - alpha/A
```

The shelves use the established RBJ low-/high-shelf equations with S=1 and `beta = 2*sqrt(A)*alpha`. All coefficients are divided by `a0` before processing.

Runtime uses transposed direct form II independently for left and right channels:

```text
y  = b0*x + z1
z1 = b1*x - a1*y + z2
z2 = b2*x - a2*y
```

Filter history is Runtime execution state keyed by stable source identity. It is retained when unrelated source settings change and the equalizer configuration is unchanged. Changing the equalizer configuration resets that source's filter history deterministically. Multiple buses rendering the same absolute sample window replay the same source block from the same pre-block filter state, so a source assigned to several buses does not advance its IIR history multiple times.

After Runtime process loss, ControlHost and durable show-project state reconstruct the authoritative equalizer configuration. Filter delay/history is not persisted and is explicitly restarted from zero state. The system does not claim that historical filter memory survived process loss.

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
- EQ bypass and 0 dB parity;
- low/mid/high frequency-response direction;
- finite behavior at all allowed EQ boundaries;
- left/right and source-identity state isolation;
- deterministic EQ reset/retention behavior;
- block-splitting invariance;
- multi-bus EQ state replay;
- zero steady-state mix-engine allocations with active EQ;
- repeated maximum-input 48 kHz stereo blocks with crossfade, ducking and bounded EQ enabled.

Hosted elapsed-time guards are regression evidence only. They are not physical audio-hardware latency or certification claims.

## Governed multi-bus output routing

The production audio model executes between one and four configured buses through the single `AudioProductionEngine`. `program` remains mandatory. Sources may be assigned to multiple buses or to no bus, and every configured bus is processed against the same absolute 48 kHz sample window. Runtime retains bounded per-bus mix buffers and per-bus peak, pre-clip, clipping, active-source, missing-source and master-state evidence.

Governed Program/Aux output roles carry an authoritative audio-bus identity. Legacy roles without an explicit mapping resolve to `program`. Aux may select another confirmed bus; an invalid or missing bus reference fails closed before committed Runtime state changes. Providers consume the selected final bus payload and never remix it.

Recording remains intentionally bound to the `program` bus even when a Program or Aux output role selects another bus. This does not add another mixer, wall-clock scheduler, unbounded queue, surround layout, generic DSP/plugin host, compression or limiting.
