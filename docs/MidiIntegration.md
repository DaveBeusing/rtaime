<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# MIDI Integration

The MIDI adapter supports Windows MIDI through the WinMM short-message API and a deterministic virtual backend used by CI/test environments.

Supported trigger keys are:

```text
note:<channel>:<note>
cc:<channel>:<controller>
program:<channel>:<program>
```

Channels are 1-16 and data values are 0-127. Note velocity and Control Change values are normalized to 0.0-1.0 before command mapping. A Note Off, including Note On with zero velocity, produces zero/false input.

Device selection accepts an exact Windows MIDI device name or numeric device id. The Windows backend runs a bounded reconnect loop so unavailable/hot-removed devices can be reopened without blocking Production. Device-specific SysEx protocols and vendor quirks are not implemented.

Feedback targets support `note:<channel>:<note>` and `cc:<channel>:<controller>`. Observed rtaime state is projected to a bounded 0-127 value. Use the virtual backend for deterministic mapping and feedback tests without hardware.
