<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Audio mixer workspace

The Mixer uses the existing Operator audio projection and Client -> ControlHost -> RuntimeHost
configuration path. Eight admitted source strips and four admitted buses remain the current DSP
contract bounds. Horizontally virtualized, scrollable strip banks expose every admitted source/bus;
the number fitting the current viewport does not alter the configuration.

## Compact live operation

The Mixer starts in **COMPACT** with one bounded, scrollable panel of the
existing authoritative Program/Aux bus and source-strip projections. Bus cards
display the current bus role assignment, explicitly labeled L/R Runtime
sample-peak bars, confirmed gain, local draft state, clipping/missing-signal
warnings and gain/mute **draft → Apply / Discard** controls. Source cards
present actual source identity, confirmed contribution/routing, input L/R
peaks, gain/mute draft and the same explicit Apply/Discard boundary.
An **UNASSIGNED** bus or **NO BUS** source is not claimed to produce output.

The top strip retains **FOLLOW VIDEO / BREAKAWAY SELECTED** using the
existing governed routing commands. It distinguishes the **confirmed**
routed source from the locally **selected Breakaway target**; each
compact source card includes an explicit SELECT/SELECTED control. Selection
itself makes no ControlHost mutation and cannot change Program audio.
The consolidated signal label distinguishes current sample-peak evidence
from CLIPPING, PARTIAL SIGNAL and stale/offline/unavailable readings. It
does not report physical output, LUFS, RMS or true peak. All source and
bus cards are projections of the same maximum eight/four retained owners:
there is no new meter producer, DSP session or poller.

**ADVANCED** reveals the original full-size, horizontally virtualized
source/bus controls, 8×4 assignment matrix, three-band EQ, compressor,
sample-peak limiter, crossfade, ducking and diagnostics. Switching modes
only changes local visibility; drafts, authoritative revisions, meter
ownership, source selection, keyboard shortcuts and output assignments
remain intact. The original detailed faders, numeric gain editing and
other advanced tools remain accessible; no UI mode silently commits
audio. A mute button changes a **local draft**, not the actual bus
until the operator selects **APPLY**.

## Signal flow and evidence

Source strips show stable source identity, confirmed inclusion/routing, EQ and L/R sample peaks.
Bus strips show confirmed Program/Aux role assignments, gain, mute, compressor/sample-peak limiter
and current reduction/clipping evidence. The header explains confirmed bus -> output mappings and
marks an absent bus as MISSING BUS. FOLLOW VIDEO / BREAKAWAY keep the existing explicit governed
commands and confirmed source label.

Zero measured peak displays minus infinity; absent, nonfinite or negative peak evidence displays
an unavailable marker and never enters the meter bar. Stale/offline values remain retained data,
with the live meter disabled. Clipping, invalid/missing observations and silence are distinct.
The meters use Runtime evidence through the existing management delivery. There is no new timer,
render loop, peak hold, display decay, audio analysis or DSP processing in WPF.
RMS, LUFS, true peak and physical device qualification remain unavailable.

## Strip drafts and precision

Gain, mute, source inclusion mode, source-to-bus routing, EQ and bus dynamics remain strip-local
drafts until Apply. Routing cells label both source and destination bus and separately describe
draft ON/OFF and confirmed ON/OFF. Tab/Shift+Tab and Space operate the same custom buttons.
The confirmed routing summary does not follow unapplied toggles. Each source Apply commits its
complete source draft, including bus membership, in one existing revision-checked configuration.
Bus Apply commits only that bus. Other strips keep their drafts.

The existing Inspector numeric primitive provides culture-aware finite/range validation,
Up/Down, Shift fine step, Ctrl coarse step, Alt-drag, Ctrl+Home field reset and Escape cancellation.
An invalid visible field disables its strip Apply. Fixed-size vertical faders retain linear
0..4 range with 0.01/0.1 steps. The companion dB editor uses 0.1 dB steps and the equivalent
0..4 linear contract. Gains below the -96 dB editor floor retain their exact linear value until an explicit dB edit; the -96 dB endpoint selects zero gain. Confirmed labels retain
the actual gain and measured silence is independent of this editor convention.
Unity changes gain to 1 locally; Discard restores the strip from confirmed state.

Pending disables the submitted strip. Reject retains the requested draft and confirmed values
so the operator can inspect, correct or discard it. Host revision conflicts and lost connections
reuse existing rejection/resynchronization behavior. No local undo can reverse a Program action.

## Compatible Inspector transfer

Copy Gain and Paste Gain exchange the existing version-1 InspectorGainAttributes payload and
production revision token with the Inspector's existing session clipboard. Only finite 0..4
gain is transferable across source/bus strips or to a compatible Inspector context.
EQ, routing, mute, resource identities and commands are excluded. Incompatible/stale payloads
leave the destination unchanged; paste is a local draft operation requiring Apply.

## Adaptive layout and docking

Compact is the default bounded bus/source operation surface; Advanced reveals
the original detailed processing and routing banks. Neither mode changes
drafts, selection, commands or Runtime state. Strip widths, meter geometry,
horizontal compact faders and vertical advanced faders use DIPs instead of
scaling with the whole window. The banks and outer workspace scroll to preserve access at small viewports.

DETACH MIXER reparents the same AudioMixerControl instance through OperatorDockController and
OperatorPanelWindow. The existing return/dock/window-close, keyboard routing and monitor
placement recovery remain in use. The AUDIO panel is additive in dock layout version 2.
Version-1 preferences migrate without enabling docking, moving existing panels or changing
floating placements: missing AUDIO is appended to RIGHT. Unknown future layouts recover through
the existing bounded default policy. Older executables may reset this optional UI layout.

Keyboard focus anywhere inside the Mixer suppresses ordinary global production/transport
shortcuts; numeric Enter/Escape and routing Space remain local. Explicit allowed application
shortcuts, including synchronization and docking, retain their existing policies.

## Qualification

Existing AudioMixer interaction/mutation/surface suites are extended with draft route/mute/mode,
gain transfer, invalid numeric gating and finite/NaN/infinite/stale meter cases.
AudioMixerRealHostTests drives a real named-pipe Client, ControlHost and RuntimeHost from the
productive Mixer view model, with a known stereo payload, gain, mute, route removal, unity and
a competing writer revision conflict. The test advances real Runtime service boundaries explicitly
and verifies emitted stereo sample bytes. It first checks that normal readiness blocks the
unqualified manual Runtime, then uses the existing readiness test seam for audio functional
requests. This is not end-to-end readiness qualification. Autonomous 1080p50 scheduler/timing qualification remains
in the existing independent suites with unchanged bounds. Existing audio engine and host suites still qualify EQ,
dynamics, follow-video/breakaway, test signals, clipping and sample payload behavior.

The real-WPF docking qualification exercises AUDIO alongside existing panels, verifies the
same Mixer/Inspector references, numeric input gating, shortcut suppression, all eight sources,
fixed vertical Advanced and horizontal Compact fader geometry, and both mode captures. Mixer PNGs and source-correlated JSON are
retained in the existing Operator visual artifact. Snapshot fixtures are software visual
evidence; physical multi-monitor, touch and audio-device performance require separate hardware
qualification. The five existing Required gates remain mandatory.
