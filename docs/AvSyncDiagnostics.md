<!--
Copyright (c) 2026 Dave Beusing <david.beusing@gmail.com>
All rights reserved.
-->

# Audio Video Sync Diagnostics

## Purpose

The internal A/V sync diagnostic mode provides a repeatable reference for detecting scheduling drift and relative pipeline timing between video and audio. Video flashes and audio pulses are derived from one exact media-time event timeline rather than independent timers.

The mode is intended for internal pipeline validation. It does not measure physical display latency, loudspeaker latency, cable delay, external device delay, or acoustic/optical roundtrip latency.

## Activation

A source participates in synchronized A/V diagnostics when all of the following are true:

- the source uses the internal broadcast test pattern,
- the broadcast test pattern is in `MotionTiming` mode,
- the same source uses the generated audio test signal in `Pulse` mode,
- the source is the active Program source.

The existing Operator controls remain authoritative for selecting the video test pattern and generated audio mode. No additional production-authority path is introduced.

## Common media-time event model

The default event period is exactly one second.

Each event has one monotonically increasing event identifier and one exact rational expected media time:

`event time = event id × 1 second`

The video target frame is the first frame whose presentation time is not earlier than the event time. The audio target sample is the first sample whose presentation time is not earlier than the same event time.

The calculations use integer/rational arithmetic for target selection. Event positions are not calculated by accumulating rounded frame durations or sample durations.

This matters for fractional rates such as 30000/1001 and 60000/1001. At 60000/1001, for example, the one-second event maps to video frame 60 at approximately 1.001 seconds while the 48 kHz audio event maps exactly to sample 48000 at one second. The diagnostic output reports this planned video quantization offset explicitly instead of treating it as drift.

## Video signal

The Motion/Timing test pattern continues to display:

- frame counter,
- timecode,
- motion marker,
- frame indicator.

For each synchronized event it also:

- renders a one-frame visible flash in the dynamic timing region,
- displays `EVENT` with the event identifier.

The visual event is derived from the frame `PresentationTimestamp`, `Timebase`, sequence number, configured frame rate, and the shared event timeline.

## Audio signal

Generated audio `Pulse` mode produces a short pulse once per second from the absolute audio sample position. At 48 kHz, event 1 starts at sample 48000, event 2 at sample 96000, and so on.

The RuntimeHost inspects the actual audio buffer sample window against the same event timeline used by the video timing signal. This allows the video flash and audio pulse to be correlated by event identifier even when fractional frame-rate quantization places them in adjacent runtime boundaries.

## Diagnostics

When the synchronized configuration is active on Program, RuntimeHost exposes:

- diagnostic state,
- event identifier,
- expected media time,
- target video frame,
- target audio sample,
- scheduled video offset relative to the exact audio event time,
- observed internal submit offset,
- drift from the first observed internal submit offset.

The values are available in RuntimeHost support snapshots and are projected into the Operator Frame Timing health section.

`UNAVAILABLE` means the synchronized source configuration is not active or no valid measurement is currently available.

`PARTIAL` means one side of an event has been observed and the paired video/audio boundary has not yet been observed.

`MEASURED` means both internal event boundaries were observed for the same event identifier.

## Measurement limits

The scheduled offset is a deterministic media-time value. It describes frame/sample quantization relative to the shared event time.

The internal submit offset is based on monotonic RuntimeHost timestamps at the observable internal video and audio pipeline boundaries. It is useful for detecting changes, stalls, and drift inside the current pipeline.

Neither value is a physical end-to-end latency measurement. A monitor, video output adapter, audio interface, amplifier, loudspeaker, camera, or microphone may add latency that the internal diagnostic cannot observe.

External sync measurement equipment remains the correct method when physical output timing must be certified.

## Restart and recovery behavior

Changing either the video test-pattern mode or generated audio test-signal mode resets the internal correlation baseline. Switching the active Program source also resets the correlation baseline.

After reactivation, the first fully observed synchronized event establishes a new internal submit-offset baseline. Later events report drift relative to that baseline.

No historical measurement is retained and presented as current after the synchronized configuration becomes inactive.

## Validation coverage

Regression coverage verifies:

- exact shared event identifiers,
- 25 fps and 50 fps integer-rate mapping,
- 30000/1001 and 60000/1001 fractional-rate mapping,
- correct audio sample position mapping,
- long-running scheduling without cumulative rounding drift,
- deterministic restart behavior,
- RuntimeHost activation, measurement, and reset behavior,
- separation between planned media-time offset and observed internal pipeline offset.
