# Audio Follow Video

Status: AP-09 foundation

Change classification:

- `CONTRACT`
- `REALTIME_CRITICAL`

AP-09 introduces the minimal V1 audio path and couples it to the committed Program video selection through the `FOLLOW_VIDEO` policy.

## Scope

Implemented:

- independent audio stream contracts
- exact audio/video timing relationship
- embedded virtual audio for Input 1 and Input 2
- Audio Follow Video
- deterministic CUT follow behaviour
- per-input gain
- per-input mute
- basic peak-level state
- audio observations
- underrun reporting
- invalid-buffer/timing reporting
- long-run 50 fps and 59.94 fps timing evidence

Not implemented:

- full audio mixing console
- independent routing
- MIX
- CROSSFADE
- DUCK
- BREAKAWAY
- dynamics processing
- EQ
- network audio
- physical embedded-audio hardware qualification

## Audio is an independent timed media domain

Audio is not represented as bytes attached to a video frame.

`rtaime.Media.Contracts` now defines:

- `AudioStreamId`
- `AudioFormat`
- `AudioChannelLayout`
- `AudioSampleFormat`
- `AudioStreamDescriptor`
- `AudioBufferTiming`
- `AudioBufferDescriptor`
- `OpaqueAudioHandle`

The V1 reference virtual format is:

```text
48,000 Hz
Stereo
Float32
2 channels
```

The normal contract carries descriptors, timing and an opaque resource handle. It does not carry bulk sample arrays.

## Contract compatibility

The new audio types are an additive extension of the existing Media contract family.

`MediaContractVersion.Current` remains:

```text
1.0
```

No existing Media contract type or field is changed or removed. Unknown Media contract versions continue to fail closed.

Compatibility tests cover:

- stream/buffer round-trip
- canonical AudioStreamId representation
- invalid enum/channel combinations
- unknown contract version rejection
- absence of bulk sample fields

## Audio/video timing relationship

V1 uses exact rational timing rather than rounded samples-per-frame values.

For a video boundary `N`, audio sample positions are derived as:

```text
start = floor(N       * sampleRate * frameRate.denominator / frameRate.numerator)
end   = floor((N + 1) * sampleRate * frameRate.denominator / frameRate.numerator)
count = end - start
```

At 48 kHz and 50 fps:

```text
960 samples per video boundary
```

At 48 kHz and 60000/1001 fps:

```text
800/801 sample cadence
```

This avoids long-running drift caused by rounding 59.94 fps to a fixed sample count.

The audio timebase is:

```text
1/48000 seconds per sample tick
```

## FOLLOW_VIDEO semantics

Each V1 audio stream declares the video source it follows.

At every Program boundary the Audio Follow Video engine receives the committed Program video source.

If that source changed because of a committed CUT:

```text
Committed Program video source changes
        ↓
FOLLOW_VIDEO mapping resolves associated audio stream
        ↓
Audio active stream changes at the same boundary
        ↓
Audio buffer timing is validated
        ↓
Program audio result is emitted or a failure is observed
```

The media engine does not create new production authority. It follows the committed video source supplied by the Runtime/host composition path.

## Gain and mute

Each followed input has minimal processing state:

```text
Gain: linear 0..4
Mute: true/false
```

Gain/mute state is independent per input and does not change the FOLLOW_VIDEO mapping.

The AP-09 peak value is a minimal deterministic metering proof. The effective peak is:

```text
mute ? 0 : min(1, observedPeak * gain)
```

This is not a full metering or loudness implementation.

## Underrun and failure behaviour

If the committed CUT selects Source B but Source B has no audio buffer at that boundary:

```text
Program video remains Source B
Active FOLLOW_VIDEO audio remains Source B
Boundary is reported as audio.afv.underrun
Peak becomes 0 for that boundary
Next valid Source B buffer may recover normally
```

The engine does not silently fall back to Source A because doing so would redefine committed production intent.

Other fail-closed conditions include:

- sequence mismatch
- unmapped video source
- wrong audio stream
- audio format mismatch
- timing-domain mismatch
- sample-position/count/timestamp mismatch

## Observations

Representative stable observation codes:

- `audio.afv.initialized`
- `audio.afv.switched`
- `audio.afv.emitted`
- `audio.afv.underrun`
- `audio.afv.buffer_rejected`
- `audio.afv.sequence_rejected`
- `audio.afv.video_source_rejected`
- `audio.input.state_changed`

Observed state is not promoted to production authority.

## Virtual reference audio

`rtaime.Provider.VirtualMedia` contains a deterministic `VirtualEmbeddedAudioReferenceProvider`.

It provides two synthetic embedded audio streams corresponding to the existing two synthetic video inputs.

Both streams share:

- 48 kHz stereo Float32 format
- one deterministic timing-domain identity
- exact video/audio boundary arithmetic

Synthetic packets contain an opaque virtual handle and a deterministic test peak. They are reference behaviour and test infrastructure, not professional hardware evidence.

## Evidence obligations

AP-09 is both `CONTRACT` and `REALTIME_CRITICAL`.

Required evidence therefore includes:

- Contract compatibility evidence
- Unit evidence
- Integration evidence
- Behavioral evidence
- Failure evidence
- Performance evidence
- Architecture/dependency evidence

The long-run managed qualification exercises:

- 30,000 boundaries at 50 fps
- 36,000 boundaries at 59.94 fps
- periodic FOLLOW_VIDEO source switches
- zero expected underruns/rejections
- exact final sample position

The broad CI elapsed-time guard is a regression guard for the managed descriptor/timing path. It is not professional audio-hardware latency certification.

## Evidence boundary

Virtual/synthetic evidence proves deterministic contract and architecture behaviour.

It does not prove:

- professional media I/O embedded-audio qualification
- DMA behaviour
- hardware clock/genlock behaviour
- device-driver latency
- physical audio continuity under real hardware faults
- certified audio performance

Those remain `UNVERIFIED` until produced on the declared Reference Platform and professional I/O provider.
