<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Operator Workspaces, Multiview and Keyboard UX

## Purpose

The Operator provides five task-oriented workspaces over one shared production state. A workspace changes presentation only: panel visibility, panel dimensions, viewer emphasis, timeline height and the current UI context. It does not own routing, media transport, recording, graphics, AI, health or output authority.

The canonical workspaces are:

- LIVE
- EDIT
- MEDIA
- GRAPHICS
- SYSTEM

Switching workspaces never creates a second production snapshot or forks product state.

## LIVE

LIVE is the primary production workspace. It emphasizes the reusable multiview surface, persistent production controls, Runtime-derived audio observations and Quick Controls.

The initial multiview consumes only existing Operator monitoring projections:

- confirmed Preview monitoring image and state;
- confirmed Program monitoring image and state;
- already available source thumbnails and source state.

Program is visually dominant. The multiview does not subscribe to another monitoring transport, decode media or render a second Program path.

## EDIT

EDIT emphasizes the layered timeline and cue workflow:

- larger persisted timeline region;
- Media Pool;
- Inspector;
- Preview and Program viewers;
- persistent bottom transport.

All seeking, IN/OUT changes and cue operations continue through the existing Media Timeline and marker command paths.

## MEDIA

MEDIA emphasizes preparation:

- expanded Media Pool;
- Preview-focused viewer presentation;
- Media Deck;
- metadata and Context Inspector;
- existing import/open workflow;
- existing IN/OUT and playback-policy preparation.

It does not introduce another ingest or media-management subsystem.

## GRAPHICS

GRAPHICS emphasizes composition using the existing graphics and AI surfaces:

- Preview and Program;
- graphics resources in the Media Pool;
- graphics/overlay controls;
- selection-driven Inspector;
- current AI composition control where supported.

No second graphics renderer is introduced.

## SYSTEM

SYSTEM consolidates operational evidence without exposing a raw developer console. The workspace reuses existing lifecycle, health, monitoring and output projections for:

- Engine;
- Control;
- Runtime;
- AI;
- Media;
- GPU;
- Recording;
- Outputs;
- Diagnostics.

PASS, FAIL and UNVERIFIED evidence semantics remain unchanged.

## Quick Controls

Quick Controls are a bounded pinboard for Inspector-capable editable properties. A pin stores only a stable Inspector property identifier in local UI preferences. It never stores an authoritative value.

The initial supported set includes transition duration, clip playback policy, selected audio gain/mute, graphics visibility/position/scale and AI enable state. A maximum of eight values may be pinned.

Every mutation reuses an existing command path such as Apply Playback Policy, Apply Audio Gain, Toggle Audio Mute, Apply Graphics, Toggle Graphics or the existing AI enable/disable commands. Desired values remain desired until the normal authoritative observation confirms them.

Pins are stored below the current user's local application data in rtaime/operator-quick-controls.json. Unknown or incompatible data is ignored safely.

## Keyboard-first operation

Window-level shortcuts are defined centrally by OperatorKeyboardCommandRegistry. The registry rejects duplicate key/modifier pairs before routing preview key-down events and generates the Help reference from the same definitions. This path supports the unmodified production keys used by the Operator without relying on WPF KeyGesture validation.

Default bindings are:

| Shortcut | Operation |
| --- | --- |
| Space | Media Play / Pause |
| K | Media Pause |
| S | Media Stop |
| I | Set IN |
| O | Set OUT |
| M | Add named Media cue |
| Up | Previous cue |
| Down | Next cue |
| Enter | AUTO Preview to Program |
| Ctrl+Enter | CUT Preview to Program |
| R | Start/stop Program recording when the matching command is available |
| Ctrl+P | Set selected source to Preview |
| F5 | Synchronize authoritative state |
| F11 | Operator fullscreen/windowed |
| Esc | Exit Operator fullscreen |
| Ctrl+1 | Maximize Preview |
| Ctrl+2 | Maximize Program |
| Ctrl+0 | Restore dual viewers |

Timeline-local Page Up/Page Down navigation remains inside the timeline control. J and L are intentionally not bound in V1 because deterministic reverse/forward shuttle semantics are not currently exposed by the Media Deck. The UI does not invent those semantics.

Production shortcuts are suppressed while the operator is typing in a text-entry control. Non-destructive application/view commands such as F5 and fullscreen remain available. Repeated key-down events are consumed without repeatedly executing production commands.

## Shortcut discoverability

Button labels and tooltips show the primary production shortcuts. The Help surface renders the central registry, so displayed window-level shortcuts cannot drift from the applied definitions.

The registry exposes conflict detection as the extension point for future customization. A full keyboard editor is not part of V1.

## Clean Program monitoring

Clean Program presents only the existing Runtime-derived Program monitoring image in a dedicated display/window. The current Program Output controller shares the same OperatorMonitoringViewModel.ProgramImage object used by the in-application Program monitor.

Clean Program:

- contains no production controls over the image;
- can be started/stopped and restored safely;
- can target supported Windows displays;
- can be toggled between fullscreen and windowed monitoring;
- does not route, switch or otherwise change physical Program output.

It is monitoring presentation, not output authority.

## Per-workspace layout persistence

Operator layout storage uses schema version 2. Each canonical workspace persists only presentation fields:

- left and right panel width;
- timeline/lower region height;
- panel collapsed state;
- center-maximized state;
- viewer presentation mode.

Fullscreen preference and selected workspace remain top-level Operator UI preferences.

SAVE LAYOUT persists the active workspace presentation. LAYOUT RESET restores only that workspace's canonical defaults. Missing, corrupt, non-finite, out-of-range or incompatible persisted data recovers to safe canonical layouts.

The layout file remains rtaime/operator-layout.json below the current user's local application data.

## Authority and performance boundaries

The implementation preserves the existing product boundaries:

- UI state is not production authority.
- Multiview and Clean Program use the existing monitoring projection.
- No second frame transport or media renderer is introduced.
- Quick Controls store property identities, not production values.
- Workspace switching performs no Client/Control/Runtime mutation.
- Existing command availability remains the source of shortcut enablement.
- Monitoring and management refresh cadences are unchanged.

## Operator workflow

A typical live workflow is:

1. Prepare media and IN/OUT in MEDIA.
2. Refine cues and timing in EDIT.
3. Prepare overlay state in GRAPHICS.
4. Pin frequently adjusted values from the Inspector.
5. Switch to LIVE for multiview, production controls and Quick Controls.
6. Open Clean Program when a dedicated monitoring display is required.
7. Use SYSTEM when lifecycle, health, recording or output evidence needs attention.

The same underlying production state remains active across every workspace.
