<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Audio Follow Video

Status: foundation + operator workflow

Change classification:

- `CONTRACT`
- `REALTIME_CRITICAL`

Audio Follow Video introduces the minimal V1 audio path and couples it to the committed Program video selection through the `FOLLOW_VIDEO` policy.

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

The Audio Follow Video peak value is a minimal deterministic metering proof. The effective peak is:

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

Audio Follow Video is both `CONTRACT` and `REALTIME_CRITICAL`.

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

## Audio Operator Workflow operator audio workflow

Audio Operator Workflow promotes the existing V1 FOLLOW_VIDEO foundation into a bounded production-operator workflow without creating a second audio engine.

The RuntimeHost remains the owner of:

- FOLLOW_VIDEO source selection from confirmed Program routing;
- per-input linear gain (0..4);
- per-input mute;
- stereo L/R peak measurement;
- Program master peak;
- clipping, silence, underrun and error health;
- actual Program audio payload selection.

The Operator receives snapshots through **Operator → rtaime.Client → ControlHost → RuntimeHost** and can change the existing Runtime input gain/mute state plus the internal generated audio diagnostic source documented in [AudioTestSignalGenerator.md](AudioTestSignalGenerator.md). Audio mutations are serialized by ControlHost and confirmed by RuntimeHost. They do not fabricate or advance Preview/Program routing revisions.

Generated audio diagnostics reuse the same `AudioBufferDescriptor`, exact sample window, FOLLOW_VIDEO selection, gain/mute stage, Program metering and recording/output payload path. They do not introduce a second audio engine or a UI timer.

### Stereo metering

`AudioMetering.MeasureInterleavedStereoFloat32` measures actual interleaved Float32 samples without allocating on the measurement path. RuntimeHost applies input gain/mute to the observed L/R peaks and reports clipping when the pre-clamped post-gain level reaches or exceeds full scale.

The Operator polls confirmed management snapshots at a bounded 200 ms cadence. WPF does not animate or synthesize meter values.

### Live and local-media payloads

Physical Media I/O and the local Media Deck feed decoded/captured Float32 samples into a bounded RuntimeHost audio queue. RuntimeHost consumes the required sample window for the committed Program source, applies the same AFV gain/mute state and exposes one Program audio payload.

That payload is then reused for:

- Program recording payload staging;
- physical Program output when a qualified Media I/O output is active.

When no external source is active, deterministic virtual reference audio remains the V1 fallback/reference source. Paused/stopped local media is represented as intentional silence; unavailable external audio fails as an AFV underrun rather than silently borrowing audio from another video source.

The external queue is bounded to approximately 500 ms per source and drops oldest samples if a producer outruns Program consumption. It is not an unbounded media queue.

### Health semantics

Audio Operator Workflow exposes these Runtime-owned states:

- `HEALTHY`
- `MUTED`
- `SILENCE`
- `CLIPPING`
- `UNDERRUN`
- `ERROR`

AFV still follows only the committed Program video source. CUT/DISSOLVE routing remains authoritative in Control; audio does not implement an independent BREAKAWAY path.

### Scope boundary

Audio Operator Workflow does not add EQ, compression, limiter configuration, aux buses, a routing matrix, multichannel mixing, loudness normalization or a loudness-compliance suite. Those remain outside the V1 showcase audio workflow.
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
