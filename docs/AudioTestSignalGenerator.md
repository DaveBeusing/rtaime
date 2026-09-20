<!--
Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>
All rights reserved.
-->

# Audio Test Signal Generator

## Purpose

rtaime provides an internal deterministic generated audio source for validating the normal audio path without external audio files, codecs or capture hardware.

The generated signal is attached to an existing production audio input and continues through the established RuntimeHost path:

```text
authoritative audio sample window
-> generated audio samples
-> Audio Follow Video
-> input gain / mute
-> Program metering
-> recording / qualified output payload
```

It does not create a second audio engine, an independent routing authority or a UI-driven media clock.

## Supported modes

The generated source supports these modes:

| Mode | Behaviour |
| --- | --- |
| Silence | Writes exact zero samples while retaining a valid timed audio buffer. |
| Tone | Continuous sine wave on every channel. |
| Stereo Identification | Repeats LEFT -> RIGHT -> BOTH, one second per segment. |
| Channel Identification | Activates one channel at a time in the order defined by the active channel-layout contract. |
| Pulse | Emits a short 10 ms sine burst once per exact audio second. |

The current V1 audio layouts are Mono and Stereo. Therefore Channel Identification currently resolves to MONO for mono and LEFT -> RIGHT for stereo. No 5.1, 7.1 or other channel order is invented before those layouts exist in the media contracts.

## Timing and phase

The generator uses `AudioBufferTiming.SamplePosition`, `SampleCount` and the active audio sample rate.

No wall clock, WPF timer or independent scheduler is used.

The sine phase is calculated from the absolute sample position:

```text
phase = 2 * pi * frequency * absoluteSample / sampleRate
```

This makes adjacent buffers phase-continuous and makes repeated runs deterministic for the same configuration and sample timing.

Stereo/channel identification and pulse periods are also derived from absolute sample position. A RuntimeHost restart naturally begins from the new Runtime audio timeline; enabling or changing a generated mode does not introduce another clock.

## Default frequency and level

The default tone and identification frequency is:

```text
1000 Hz
```

The default generated peak level is:

```text
0.25 linear ~= -12.04 dBFS peak
```

The configuration rejects generated peak levels above:

```text
0.50 linear ~= -6.02 dBFS peak
```

This leaves deliberate headroom before the normal Runtime input gain stage. The existing operator gain remains authoritative after generation, so a deliberately high input gain can still produce Runtime clipping evidence. The generator itself does not use full-scale output in its supported configuration range.

## Sample rate and sample format

The generator reads sample rate and channel layout from the existing `AudioFormat`.

The current qualified V1 Runtime payload path is:

```text
48,000 Hz
Float32
Mono or Stereo as defined by AudioFormat
```

The Media contract also defines `PcmS16`, but the current generated Runtime payload implementation intentionally fails closed for non-Float32 formats because the established Runtime metering/output path is Float32. This does not change or remove the existing sample-format contract.

## Runtime integration

RuntimeHost owns generated-audio configuration per production source.

When a generated signal is enabled:

- the existing audio stream identity is retained;
- the existing `AudioBufferDescriptor` and exact AFV sample window are retained;
- generated samples replace the source payload for that input only;
- normal AFV, gain, mute, metering, recording and output processing remain in use;
- the underlying external input queue and observation state are preserved.

When the generated signal is disabled, the source returns to its underlying audio input on the next normal Runtime boundary.

Generated Silence remains a valid available audio source and therefore reports `SILENCE`, not an underrun.

## Operator workflow

The existing **AUDIO / AFV** panel shows the generated test state for each input and the active identification channel.

For the selected input, **NEXT TEST SIGNAL** cycles:

```text
OFF
-> SILENCE
-> TONE
-> STEREO ID
-> CHANNEL ID
-> PULSE
-> OFF
```

The Operator uses the existing Client -> ControlHost -> RuntimeHost command path. Mode changes do not advance the authoritative Production revision.

The UI only displays Runtime-confirmed state and meter observations. It does not synthesize audio samples, channel timing or meter animation.

## Lifecycle and transition behaviour

Generated signal state is Runtime-owned and process-local. A RuntimeHost restart clears that generated state unless a higher-level workflow explicitly reapplies it.

Start, mode change and stop occur on the existing Runtime processing boundary. No additional fade or de-click processor is introduced in this package. Software tests prove deterministic boundary/sample behaviour; physical click-free switching on professional output hardware remains unverified.

## Allocation behaviour

The generator writes into caller-owned `Span<float>` storage and does not allocate sample arrays in its fill loop.

RuntimeHost still materializes the Program audio byte payload required by the existing recording/output path. The generated path reuses that one payload buffer for the gain/mute stage rather than creating an additional generated-audio copy.

Performance tests guard against ongoing managed allocations inside repeated generator fills.

## Health and metering

Generated samples use the existing Runtime audio meters and AFV health projection.

Expected normal states are:

- Tone / active identification / active pulse window: `HEALTHY`;
- Silence or pulse quiet interval: `SILENCE`;
- operator mute: `MUTED`;
- post-gain full-scale reach: `CLIPPING`.

Generated audio availability is independent of the underlying external input while the test source is active. Disabling the generated source exposes the underlying input health again.

## Boundaries

This feature does not provide:

- A/V synchronization coupling;
- audio codec encoding;
- file export;
- loudness certification;
- acoustic calibration;
- a new mixer or routing engine;
- professional hardware audio qualification;
- new surround channel-layout contracts.

The periodic pulse is intentionally only a deterministic basis for later A/V synchronization diagnostics.
