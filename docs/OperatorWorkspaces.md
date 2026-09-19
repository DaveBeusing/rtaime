<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Operator Workspaces, Multiview and Keyboard UX

## Purpose

The Operator provides seven task-oriented workspaces over one shared production state. A workspace changes presentation only: panel visibility, panel dimensions, viewer emphasis, timeline height and the current UI context. It does not own routing, media transport, recording, graphics, AI, health or output authority.

The canonical workspaces are:

- MEDIA
- EDIT
- LIVE
- SCENES
- COMPOSITING
- OUTPUTS
- SETTINGS

Switching workspaces never creates a second production snapshot or forks product state.

## LIVE

LIVE is the primary fast production workspace. It is arranged as a three-zone operator surface: source/cue selection on the left, adaptive multiview plus Quick Controls in the center, and explicit Live Controls on the right.

The multiview consumes only existing Operator monitoring and Runtime audio projections:

- confirmed Preview monitoring image and state;
- confirmed Program monitoring image and state;
- already available source thumbnails, source state and PGM/PVW tally;
- existing source audio peak observations.

The source bank adapts from 2 to 3 to 4 columns and is bounded to 16 displayed source tiles. Failed sources remain visible with their failure state. Double-clicking a source or monitor tile opens a larger presentation of the same image and does not route or take the source.

Selection is deliberately separate from production mutation. Selecting a source changes only SelectedSource; Set Preview, CUT and AUTO remain explicit existing commands. Media cue selection is likewise separate from Jump Selected Cue. Dedicated scene activation remains unavailable because the current V1 contracts expose no governed scene command; the LIVE workspace does not synthesize one.

The right Live Controls surface reuses existing transition, graphics/layer, recording and Clean Program commands and projects compact existing health/error evidence. External stream/on-air state remains UNVERIFIED until an authoritative contract exists.

The multiview does not subscribe to another monitoring transport, decode media or render a second Program path.

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

## SCENES

SCENES is a shell destination for scene/layer operation. In the current V1 implementation it reuses the existing graphics projection and Inspector surfaces; it does not create a second scene authority or renderer. LIVE also exposes the current governed layer state and source/cue selection, but dedicated multi-scene activation remains unavailable until a scene command contract exists.

## COMPOSITING

COMPOSITING emphasizes composition using the existing graphics and AI surfaces:

- Preview and Program;
- graphics resources in the Media Pool;
- graphics/overlay controls;
- selection-driven Inspector;
- current AI composition control where supported.

No second graphics renderer or processing graph is introduced by the shell.

## OUTPUTS

OUTPUTS is the production-facing routing, output-health and performance workspace. It projects the authoritative Runtime Program source and the existing clean Program monitoring presentation without creating a second output or routing authority.

Preview-to-Program routing reuses the existing CUT command and becomes SAFE READ-ONLY whenever the shared mutation gate is unavailable. Output detail exposes only confirmed format, target, recording and health evidence. Missing color-space, streaming, CPU, system-memory, disk, network and measured Output-FPS telemetry remains explicitly UNAVAILABLE.

Available Runtime frame-time, dropped-frame, GPU and VRAM evidence is presented with bounded presentation-only mini histories. The workspace introduces no independent thresholds and no additional telemetry polling loop. See [Output Routing, Health and Performance](OutputRoutingHealth.md).

## SETTINGS

SETTINGS retains the generic lifecycle, system-status and monitoring diagnostics using the existing system projection. It does not own Runtime configuration or create a parallel settings state. PASS, FAIL and UNVERIFIED evidence semantics remain unchanged.

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
| Space | Preview media Play / Pause when the loaded Media Deck source is confirmed Preview |
| K | Preview media Pause |
| S | Preview media Stop |
| I | Set Preview media IN |
| O | Set Preview media OUT |
| M | Add named Preview media cue |
| Delete | Delete the selected Preview media cue when that command is available |
| Up | Previous Preview media cue |
| Down | Next Preview media cue |
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

Operator layout storage uses schema version 3. Each canonical workspace persists only presentation fields:

- left and right panel width;
- timeline/lower region height;
- panel collapsed state;
- center-maximized state;
- viewer presentation mode.

Fullscreen preference, selected workspace and normalized window placement remain top-level Operator UI preferences. Legacy version-2 GRAPHICS/SYSTEM layouts migrate to the corresponding COMPOSITING/OUTPUTS/SETTINGS presentation defaults without carrying production state.

SAVE LAYOUT persists the active workspace presentation. LAYOUT RESET restores only that workspace's canonical defaults. Missing, corrupt, non-finite, out-of-range or incompatible persisted data recovers to safe canonical layouts.

The layout file remains rtaime/operator-layout.json below the current user's local application data. Persistence writes first use a temporary sibling file and then replace the target path so an interrupted write cannot leave partially serialized layout JSON as the preferred state.

Canonical workspace defaults are:

| Workspace | Left | Right | Lower | Collapsed by default | Viewer |
| --- | ---: | ---: | ---: | --- | --- |
| LIVE | 220 | 300 | 340 | none | DUAL |
| EDIT | 280 | 350 | 560 | none | DUAL |
| MEDIA | 460 | 360 | 360 | none | PREVIEW |
| SCENES | 260 | 390 | 360 | none | DUAL |
| COMPOSITING | 300 | 390 | 360 | none | DUAL |
| OUTPUTS | 220 | 420 | 340 | left | PROGRAM |
| SETTINGS | 220 | 460 | 340 | left and right | PROGRAM |

At constrained logical widths the shell enters a presentation-only compact viewport mode. It limits the visible left region, temporarily removes the right region from the grid allocation and reduces the lower region allocation without modifying the persisted per-workspace dimensions. Returning to a larger viewport restores the stored presentation values.

## Docking, focus and visual polish

The three production-shell splitters share the same focus-visible theme and remain keyboard-focusable. Their tooltips advertise arrow-key resizing. Standard ComboBox, CheckBox and TabItem controls inherit the shared focus visual, while the window retains cyclic tab navigation so core controls remain reachable without a mouse.

Loading, error and empty presentation reuse shared Operator styles instead of workspace-specific colors. Production Operator XAML intentionally avoids decorative Storyboard/animation transitions so focus, tally and command-state changes remain immediate.

The reference window can shrink to 900 x 500 device-independent units. Combined with PerMonitorV2 awareness, vertical scrolling and the compact workspace presentation this keeps the shell within a 1920 x 1080 display at 100%, 125%, 150% and 200% Windows scaling. Per-monitor movement remains presentation-only and does not restart Runtime or media processing.

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
3. Prepare scene/layer state in SCENES or COMPOSITING.
4. Pin frequently adjusted values from the Inspector.
5. Switch to LIVE for multiview, production controls and Quick Controls.
6. Open Clean Program when a dedicated monitoring display is required.
7. Use OUTPUTS for recording/output evidence and SETTINGS for shell/status diagnostics.

The same underlying production state remains active across every workspace.


## Production monitor operation

EDIT, MEDIA and the other standard-viewer workspaces reuse the same Preview and Program monitor components. Viewer changes therefore preserve production state across workspace switches instead of instantiating another playback path.

Preview is the only monitor that exposes Media Deck transport controls. The control row and the corresponding Space/K/S/I/O/M/Up/Down shortcuts are active only when the loaded Media Deck source is the confirmed Preview source. Program intentionally exposes no Preview transport commands.

Both monitors support Fit, 50 percent and 100 percent presentation plus Safe Area, Center Mark and Grid overlays. These are local presentation controls over the independent monitoring bitmap. The header uses the authoritative Runtime video-format projection for resolution/frame-rate/pixel-format evidence. Color space remains `N/A` until a governed contract exposes it.

The Program monitor shows `ON AIR` while the existing Program monitor state reports `LIVE`. The local Clean Program Output state remains a separate indicator, and external transmission is not inferred from either state.

The monitor-specific FULL action uses transient Shell state: it selects the requested viewer, maximizes the center region and enters the existing fullscreen window presentation. Escape or the fullscreen toggle restores the previous viewer mode and center-layout state without restarting media decoding, monitoring or Runtime execution.
