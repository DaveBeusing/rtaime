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

Surround, immersive audio, arbitrary DSP graphs, plugin hosting, dynamic/linear-phase EQ, multiband dynamics, gates/expanders, reverb suites, true-peak limiting, automatic loudness normalization, network-audio transport and sample-rate conversion are outside this capability. The bounded three-band per-source parametric EQ and bounded per-bus compressor/sample-peak limiter described below are part of the qualified baseline.

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

The source-to-bus matrix is bounded by the same 8×4 contract maximum. A routing cell changes presentation to PENDING while the complete next AudioProductionConfiguration crosses Operator → rtaime.Client → ControlHost → RuntimeHost. The cell becomes confirmed only after the authoritative snapshot returns. A rejected or stale revision is not retried blindly: the Operator refreshes authoritative state and retains the rejected local draft separately from the confirmed configuration. Rejection remains visible across meter refreshes; Discard Draft resets only that strip.

Editable EQ/dynamics values are UI drafts only. Ordinary 200 ms meter refreshes preserve dirty drafts while updating Runtime evidence. A successful source or bus Apply resets only the submitted strip from authoritative state. Other dirty source/bus drafts survive the confirmation. Rejected edits retain their local values and show REJECTED until another action or explicit discard. Configuration labels always use confirmed values; local contribution/EQ/dynamics drafts do not change those labels.

Source and bus gain controls include the existing custom slider and numeric editor over 0..4x. Arrow keys adjust the draft by 0.01x; Page Up/Down adjust by 0.1x. Linear and dB gain are displayed separately from the confirmed gain. Zero gain is −∞ dB. Editing never submits automatically, and pending strip editors are disabled until completion. Apply and Discard remain accessible in both presentation modes.

Compact and Advanced are two session-only presentations over the **same**
source/bus and configuration owners. Compact is the default: each bounded
source/Program/Aux bus shows current Runtime sample peaks and status, confirmed
gain/routing, separately marked local gain/mute drafts, and explicit Apply/Discard.
The top strip retains governed Follow Video/Breakaway with confirmed routing.
Advanced opens the existing complete bus matrix, EQ, dynamics and diagnostic
surface. Visible compact sample-peak confidence is never equivalent to a
qualified physical output, and stale, missing, invalid or clipping evidence
remains explicit. The current Mixer does not acquire additional DSP, a second
poller, a new signal clock or independent Program authority.

Both modes preserve all local drafts, source selection, pending/rejected
mutation state and controller revision requirements. Dirty source and bus
drafts now retain their original production/configuration revision and
connection session. A changed revision or interrupted synchronization shows
REVIEW REQUIRED and blocks Apply until the operator explicitly rebases that
strip against current authoritative evidence, or discards it. Rebase itself
never sends a command or changes Program; an additional Apply is required.
Compact bus cards now expose the existing compressor/limiter and missing-source
evidence; this adds no Runtime samples or processing path. Neither preset changes
audio configuration, stores a mix, recalls production state or creates another
layout-persistence mechanism. Existing workspace Save Layout/Reset Layout
remains the presentation persistence path. A production Solo command is not
exposed because the current audio contract has no such operation.

Absent bus observations show UNAVAILABLE rather than healthy zero meters or idle processors. Retained observations are marked STALE/OFFLINE/UNVERIFIED when the Control connection or Runtime readiness cannot support current evidence. Source meter data remains unavailable when its input descriptor is missing. Source routing labels describe confirmed mute, zero gain, bus inclusion and routed-source eligibility; they do not claim a sample was executed merely from configuration. Bus role labels describe confirmed Program/Aux bus mapping; current execution is separate Runtime meter evidence. Ducking identifies its confirmed bus/sidechain and reports unavailable sidechain explicitly. Peak text is dBFS; limiter hits are retained counters rather than new transient events.

The mixer reuses the existing bounded 200 ms management snapshot cadence. WPF performs no sample analysis and runs no additional audio polling or animation loop. Management presentation delivery retains at most one pending latest update and one posted callback, replacing obsolete telemetry while a UI consumer is slow. Starting a governed command invalidates prior poll work, so a delayed pre-command projection cannot overwrite the confirmation. Disposal discards retained work. This delivery is a current-state projection, not an event journal. Source, bus, routing-cell and audio-input collections reconcile stable identities instead of clearing/recreating rows on every meter refresh; bounded 8×4 topology avoids unbounded visual work. Runtime currently exposes peak/reduction/clipping evidence but no bounded per-bus RMS field, so RMS is deliberately shown as not exposed rather than synthesized locally. The separately opt-in programme loudness monitor projects advisory LUFS and 4× interpolated True Peak without altering these sample-peak paths; it is not yet qualified as standards-conformant.

Existing FOLLOW VIDEO/BREAKAWAY, equal-power crossfade, bounded ducking and generated diagnostic-signal controls remain available from the same surface. UI changes are requests only; ControlHost remains configuration authority and RuntimeHost remains signal-processing and metering authority.

## Failure semantics

Missing source payloads contribute silence for that source and increment missing-source evidence.

External PCM input has an explicit sample-backed mode, separate from the
meter-only reference input and generated diagnostic signals. A PCM source never
falls back to a waveform derived from its last peak. At each Program boundary,
Runtime consumes the available stereo frames for that exact window and fills
the missing suffix with zeros. A partial packet is consumed immediately; it is
not retained until a later complete window or replayed after recovery. Input
unavailability discards queued PCM before another packet can re-enable the
source. The existing half-second input queue bound remains in effect.

A partial block can contribute captured audio while also reporting one missing
source in each eligible bus. A completely empty input contributes no active
source. Routed partial and complete starvation both report AFV underrun, and
source meters describe the samples actually consumed in the current window.
Healthy silence remains distinct from missing PCM. The final mixed payload has
the full window length even when every sample is zero, so physical Program,
recording, replay and network consumers receive the same clock-aligned silence
and do not need to synthesize an unrelated signal.

`ExternalPcmUnderrunIntegrationTests` exercises 50 and 59.94 fps windows with a
packet crossing into a partial next block, complete starvation and recovery.
It compares final Program PCM with persisted reference-recording bytes and the
actual replay/network provider submission payloads. This is deterministic
software payload evidence; it does not qualify a native encoder or physical
audio output.

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

Recording remains intentionally bound to the `program` bus even when a Program or Aux output role selects another bus. This does not add another mixer, wall-clock scheduler, unbounded queue, surround layout, generic DSP/plugin host, arbitrary dynamics ordering, multiband compression or true-peak limiting.

## Integrated qualification

The complete advanced-audio path is qualified as one bounded Runtime-owned system in [Advanced Audio Processing Qualification](AdvancedAudioProcessingQualification.md).

That retained software-evidence matrix covers the canonical processing-order golden vector, governed Client/Control/Runtime configuration, 8-source/4-bus sustained processing, exact 50/59.94 sample continuity, recording/output final-bus reuse, restart semantics and failure isolation.

Hosted software qualification does not convert physical embedded-audio continuity, driver/DMA latency, hardware clock/genlock behavior, analog performance, loudness compliance or oversampled true-peak behavior into verified claims. Those properties remain **UNVERIFIED** until dedicated reference-platform evidence exists.


## Mixer interaction regression evidence

`AudioMixerOperatorSurfaceTests` covers bounded topology, retained drafts, source-loss/stale meter evidence, stable strip/cell selection, persistent rejection, confirmed versus draft processing, local presentation presets and 5,000 slow-consumer updates. `AudioMixerMutationTests` exercises the existing Client command seam with delayed confirmation, rejection and reconnect, including preservation of independent drafts. `AudioMixerInteractionTests` verifies named controls, custom fader command increments and evidence-aware meters. Existing AFV/breakaway, engine continuity, allocation and output-backpressure suites remain required.

These deterministic software checks do not qualify physical mixed-monitor interaction, hardware audio latency or sustained hardware continuity; those remain UNVERIFIED.


## Optional programme loudness and reconstructed true peak (analysis only)

This additional observation capability is **OFF by default**. It is opt-in at
RuntimeHost launch, not a Control mutation, Gain change, limiter setting or
automatic loudness normalization. The existing 48 kHz, Float32, interleaved
stereo, maximum eight sources/four buses, and single `AudioProductionEngine`
remain unchanged. The analyser reads each final bus buffer **after** its
compressor, sample-peak limiter and final safety clamp, and therefore measures
the bytes actually offered to that bus's Program/Aux consumers. Recording is
still bound to `program`; a separately routed Program output bus is identified
by its confirmed output-role binding.

### Explicit local monitoring profiles

| RuntimeHost environment setting | Meaning |
| --- | --- |
| absent or `RTAIME_PROGRAMME_LOUDNESS_PROFILE=off` | No analyser created and no per-boundary work |
| `RTAIME_PROGRAMME_LOUDNESS_PROFILE=ebu-r128-linear` | -23 LUFS ±1 LU advisory integrated target; -1 dBTP advisory reconstructed peak maximum |
| `RTAIME_PROGRAMME_LOUDNESS_PROFILE=custom-monitor` | Requires `RTAIME_PROGRAMME_LOUDNESS_TARGET_LUFS`, `RTAIME_PROGRAMME_LOUDNESS_TOLERANCE_LU`, `RTAIME_PROGRAMME_TRUE_PEAK_CEILING_DBTP` as invariant-culture numbers |

For custom profiles, admitted ranges are -70..0 LUFS, 0..10 LU
(nonzero), and -20..0 dBTP. An absent, malformed or unknown non-off
configuration is rejected at startup rather than silently assigning an OTT
platform its own target. Both profiles are observation only. A warning never
changes the Program audio mix or its Production revision. The policy is
evaluated only after ten seconds of current material; it must not interpret
a partial programme reading as final programme compliance.

### Measurement parameters and provenance

The `ProgrammeLoudnessAnalyzer` works on sequential samples without WPF
sampling, asynchronous media callbacks, independent transport polling, file
I/O or dynamic memory allocation in the hot path. Its version-one snapshot is
an **additive, nullable** field on each existing Audio Production Bus evidence
record, forwarded via the current RuntimeHost → ControlHost → Client → Operator
200-ms management cadence. Older wire payloads lacking it remain readable;
new readers treat a missing field as OFF / UNAVAILABLE, never zero.

The source is `runtime:post-bus-dynamics-and-safety-clamp:<bus-id>`.
The snapshot includes profile, state, measurement duration in seconds,
monotonically advancing reset generation, M/S/I/LRA, reconstructed maximum
dBTP, integrated relative gate, advisory policy result and explicit
qualification state. The available states are WARMING, CURRENT, SILENCE and
UNVERIFIED; neither a stale UI nor interrupted audio is a valid measurement.

The implemented stereo K-weighting filter stages use 48 kHz
BS.1770 coefficients (a0=1):

- Stage 1 shelf: b=(1.53512485958697, -2.69169618940638,
  1.19839281085285), a1=-1.69065929318241, a2=0.73248077421585.
- Stage 2 RLB high-pass: b=(1, -2, 1), a1=-1.99004745483398,
  a2=0.99007225036621.
- Left and right both carry channel energy weight 1.0. The loudness
  mapping is -0.691 + 10 log10(weighted mean square). Surround and
  arbitrary sample-rate conversion are **not** supported.

Internal energy windows are 100 ms. M uses four windows (400 ms), S uses
30 (3 s), integrated gating uses complete 400-ms windows with 75%
overlap, first excluding below -70 LUFS, then applying a -10 LU gate
relative to absolute-gated loudness. LRA uses 3-s windows at 1-s hops
with the -70 LUFS and -20 LU relative-to-integrated gates and the
10th/95th percentiles. LRA does not become available until at least
30 seconds of usable material. Histograms use fixed 0.1-LU bins over
a bounded loudness domain and energy-weighted aggregates; they do not
retain unbounded programme frames. The quantization introduces
approximately 0.1 LU bin granularity; only independent reference-vector
tests can establish end-to-end measurement tolerance.

True peak reconstructs inter-sample extrema using an allocation-free
4× 33-tap Blackman-windowed, DC-normalized polyphase sinc interpolator.
It also includes direct sample extrema, and the published peak is the
maximum since the last reset. **This is an implementation-specific 4×
reconstructed peak**, not evidence of passing the official BS.1770
Annex 2 / EBU Tech 3341 conformance vectors. Both numerical
interpolation error and potentially higher physical/codec downstream
peaks remain **UNVERIFIED**. A sample-peak limiter ceiling must not be
relabeled as a measured or guaranteed true-peak ceiling.

### Boundaries and reset semantics

A change of routed source, noncontiguous audio sample position,
explicit Audio Production configuration change or any missing/underrun
source invalidates the integrated history. On recovery the analyzer starts
new M/S/I/LRA/TP windows. The prior values are not retained as fresh.
Silence has a defined SILENCE state and nullable LUFS, not a fabricated
zero LUFS reading. Invalid/nonfinite PCM invalidates observation without
modifying the Runtime buffer.

The analyzer never constructs an audio buffer or crosses the Prepare/Commit
authority boundary. The off profile creates no analyzer, consumes no
additional per-boundary processing, and leaves existing PCM bit identity
unchanged. The custom mixer displays evidence, qualification status,
current duration and reset generation; its pre-existing Apply/Discard
commands remain the only configuration mutations.

### Qualification scope and gaps

Source-anchored generated sine, silence, relative gating, channel swap,
intersample-peak and interruption tests are included. These are
**implementation regressions**, not independent certification vectors.
The required third-party EBU Tech 3341 measurement test set, Tech 3342
LRA comparisons, CPU and allocation profiling on the Windows-x64
reference platform, several-hour capture and physical output evidence
must still be qualified before any `software-qualified` or
`hardware-qualified` claim. For now the wire qualification field remains
**UNVERIFIED**. No normative compliance or release gate is bypassed.

Standards referenced for future validation: ITU-R BS.1770-5 (2023),
EBU R 128 v5 (2023), EBU Tech 3341, EBU Tech 3342 and EBU Tech 3343.

## ADM audio-definition metadata is not an immersive mix engine

An independent Media Library metadata-only interchange path accepts a
bounded ITU-R BS.2076-3 / EBU Tech 3393-derived ADM subset with
Audio Programme/Content/Object/Track UID references and explicit
R 123 `4b/8a/8b` legacy file-exchange track descriptions. The XML
is byte-preserved as an optional local source-bound sidecar.

**The Runtime AudioProductionEngine is unchanged:** only the
currently admitted 48 kHz Float32 stereo PCM processing and existing
Program/Aux routing are implemented. Reading metadata for 5.1,
object audio, HOA or any other immersive layout cannot allocate an
immersive renderer, auto-downmix content, enable an unqualified
decoder or create a new broadcast output profile. See
[ADM Audio Track Metadata](AdmAudioTrackMetadata.md) for supported
metadata validation, R 123 assignment, evidence and limitations.
