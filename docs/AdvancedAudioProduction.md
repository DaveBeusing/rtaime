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

Surround, immersive audio, arbitrary DSP graphs, plugin hosting, dynamic/linear-phase EQ, multiband dynamics, gates/expanders, reverb suites, true-peak oversampling, loudness normalization, network-audio transport and sample-rate conversion are outside this capability. The bounded three-band per-source parametric EQ and bounded per-bus compressor/sample-peak limiter described below are part of the qualified baseline.

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
- master mute state;
- optional bounded dynamics: exactly one compressor followed by exactly one sample-peak limiter.

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
  -> deterministic source sum
  * bus master gain/mute
  -> optional bounded compressor
  -> optional sample-peak limiter
  -> final hard-clamp safety to [-1, +1]
  -> bus output
```

Ducking remains source-contribution control before bus accumulation. Runtime records both the pre-dynamics peak and the post-dynamics/pre-safety-clamp peak. `SafetyClippedSampleValues` aliases the existing clipped-sample counter so final safety clipping cannot be confused with compressor or limiter action.

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

## Bounded per-bus dynamics

Each configured bus may carry exactly one typed compressor followed by one typed sample-peak limiter. This is a fixed production path, not an arbitrary processor order or plugin chain.

Qualified compressor bounds are:

- threshold: -60..0 dBFS inclusive;
- ratio: 1..20 inclusive;
- attack: 0.1..200 ms inclusive;
- release: 5..5,000 ms inclusive;
- makeup gain: 0..24 dB inclusive;
- explicit enabled/bypass state.

Qualified sample-peak limiter bounds are:

- ceiling: -24..0 dBFS inclusive;
- release: 5..5,000 ms inclusive;
- explicit enabled/bypass state.

All values must be finite. dB gain conversion uses `10^(dB/20)`. Compressor detection is stereo-linked: the larger absolute left/right sample drives one gain value applied identically to both channels, preserving stereo balance. Above threshold, the target compression reduction is:

```text
overDb = inputDb - thresholdDbFs
reductionDb = overDb * (1 - 1/ratio)
targetGain = 10^(-reductionDb/20)
```

Attack and release use deterministic one-pole per-sample smoothing. For a configured time in milliseconds at the fixed 48 kHz baseline:

```text
coefficient = exp(-1 / (timeMs * 0.001 * 48000))
gain = targetGain + coefficient * (previousGain - targetGain)
```

Makeup gain is applied after compressor gain reduction.

The limiter operates after compressor makeup. It is a **sample-peak limiter**: when the stereo-linked instantaneous sample peak exceeds the configured ceiling, gain is reduced immediately so the current sample does not exceed that ceiling; release returns toward unity with the same sample-domain one-pole form. There is no look-ahead, oversampling or true-peak claim.

Per-bus compressor and limiter envelopes are Runtime execution state keyed by stable `AudioBusId`. Contiguous sample blocks retain state. Re-applying an equivalent dynamics configuration preserves state, while changing that bus's dynamics configuration, removing the bus, a discontinuous sample-position jump, or Runtime process restart resets its envelope state deterministically to unity. Authoritative configuration is reconstructed after Runtime restart; historical envelope memory is intentionally not persisted.

Confirmed dynamics evidence includes pre-dynamics peak, compressor gain reduction, limiter gain reduction, limiter hit count, post-dynamics/pre-safety-clamp peak, final output peak and final safety-clipped sample count.

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

Runtime materializes each configured bus once per boundary after its complete master/dynamics/safety path.

The final Program bus is reused by Program metering and Program recording descriptor/payload staging. Governed physical/network output roles consume their selected final materialized bus. No recording or output path branches from a pre-dynamics signal and no output provider implements independent audio mixing logic.

## Metering and diagnostics

Runtime publishes confirmed advanced-audio evidence including:

- Program and per-bus left/right output peak;
- pre-dynamics peak;
- post-dynamics/pre-safety-clamp peak;
- compressor gain reduction;
- sample-peak limiter gain reduction and hit count;
- final safety-clipping state and clipped sample-value count;
- ducking gain/reduction;
- sidechain availability;
- crossfade progress;
- active source count;
- missing source count;
- authoritative configuration revision.

Operator updates these values from synchronized Runtime/Control snapshots. No sample analysis is performed by WPF.

## Operator mixer

The OUTPUTS workspace hosts one bounded professional audio mixer projection using only the existing rtaime custom-control system. It does not create another audio workspace, mixer engine, sample-analysis path or production-state store.

The mixer projects at most eight confirmed source strips and four confirmed bus strips from the synchronized Client/Runtime snapshot. Source strips show identity, routed-only versus always-in-mix contribution, production gain/mute, confirmed bus assignments, Runtime-owned L/R source peaks and missing/underrun health. They also expose the fixed typed low-shelf → bell-mid → high-shelf EQ as validated draft values.

Bus strips show stable bus identity, master gain/mute, Runtime-owned L/R output peaks, missing-source and final safety-clipping evidence, compressor gain reduction, sample-peak limiter reduction/hits and the confirmed Program/Aux output roles consuming that bus. Each bus exposes the one typed bounded compressor followed by the one typed sample-peak limiter; there is no arbitrary processor order or plugin surface.

The source-to-bus matrix is bounded by the same 8×4 contract maximum. A routing cell changes presentation to PENDING while the complete next AudioProductionConfiguration crosses Operator → rtaime.Client → ControlHost → RuntimeHost. The cell becomes confirmed only after the authoritative snapshot returns. A rejected or stale revision is not retried blindly: the Operator refreshes authoritative state and restores drafts from the confirmed configuration.

Editable EQ/dynamics values are UI drafts only. Ordinary 200 ms meter refreshes preserve dirty drafts while updating Runtime evidence. Successful or rejected mutations explicitly refresh the draft from authoritative state so desired values cannot silently become production truth.

The mixer reuses the existing bounded 200 ms management snapshot cadence. WPF performs no sample analysis and runs no additional audio polling or animation loop. Runtime currently exposes peak/reduction/clipping evidence but no bounded per-bus RMS field, so RMS is deliberately shown as not exposed rather than synthesized locally. No LUFS, true-peak, loudness-standard or other certification claim is made.

Existing FOLLOW VIDEO/BREAKAWAY, equal-power crossfade, bounded ducking and generated diagnostic-signal controls remain available from the same surface. UI changes are requests only; ControlHost remains configuration authority and RuntimeHost remains signal-processing and metering authority.

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
- compressor threshold/ratio, attack/release and makeup behavior;
- sample-peak limiter ceiling and stereo-linked behavior;
- per-bus dynamics continuity, reset and bus isolation;
- dynamics-processed recording/output payload reuse;
- zero steady-state mix-engine allocations with active EQ and active dynamics;
- repeated maximum-input 48 kHz stereo blocks across up to four buses.

Hosted elapsed-time guards are regression evidence only. They are not physical audio-hardware latency or certification claims.

## Governed multi-bus output routing

The production audio model executes between one and four configured buses through the single `AudioProductionEngine`. `program` remains mandatory. Sources may be assigned to multiple buses or to no bus, and every configured bus is processed against the same absolute 48 kHz sample window. Runtime retains bounded per-bus mix buffers plus master, pre-dynamics, dynamics-reduction, post-dynamics/pre-safety-clamp, output-peak, safety-clipping, active-source and missing-source evidence.

Governed Program/Aux output roles carry an authoritative audio-bus identity. Legacy roles without an explicit mapping resolve to `program`. Aux may select another confirmed bus; an invalid or missing bus reference fails closed before committed Runtime state changes. Providers consume the selected final bus payload and never remix it.

Recording remains intentionally bound to the `program` bus even when a Program or Aux output role selects another bus. This does not add another mixer, wall-clock scheduler, unbounded queue, surround layout, generic DSP/plugin host, arbitrary dynamics ordering, multiband compression or true-peak processing.
