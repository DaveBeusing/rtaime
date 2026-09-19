<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Live Multiview & Scene/Cue Control

## Purpose

The LIVE workspace is the fast production-operation surface. It composes existing Operator, monitoring, Media Deck, graphics, recording and Clean Program projections without creating another routing, transition, media or output authority.

The workspace follows the 1920×1080 live reference composition below the 60-pixel top bar. The body uses a fixed **360 / 6 / flexible / 6 / 420** split: Scenes & Cues on the left, the live center surface in the middle and Live Controls on the right. LIVE hides the edit timeline so the full body height remains available for show operation.

The center surface is split vertically into the multiview, a 6-pixel gap and a compact 270-pixel Output Routing block.

Selection is deliberately non-destructive. A selected source does not become Preview or Program until the existing routing commands are invoked.

## Multiview

The multiview reuses the existing independent RuntimeHost monitoring plane and OperatorSourceTileViewModel source projections. It does not open another monitoring transport, decoder or renderer.

The reference mode is **default 3×3** with 6-pixel tile gaps. The toolbar can explicitly switch to 2×2, 3×3 or 4×4 presentation and can maximize/restore the live center surface through the existing Shell presentation command. Capacity is bounded to 16 displayed sources. Program, Preview and failed/unavailable sources are preserved preferentially when the selected grid capacity is smaller than the complete source set.

Each source tile uses an aspect-safe 16:9 presentation where an image is available. Its 26-pixel top overlay contains:

- stable input/display number;
- source name;
- observed frame-rate label;
- health dot;
- red PGM tally when the source is authoritative Program.

The lower overlay contains source timecode when it is available, cyan PVW state and slim left/right audio meters backed by the existing Runtime audio observations. Failed sources remain visible with explicit LOST, ERROR, FAILED or OFFLINE evidence and an INPUT UNAVAILABLE overlay.

PGM/PVW state comes only from the existing authoritative routing projection. Clicking a tile changes selection only. Double-clicking Preview, Program or a source tile opens a larger presentation of the already available image and cannot route, take or mutate production state.

The control refreshes its bounded source projection only while visible. Per-source image and meter changes continue to originate from the existing monitoring/audio cadences; no WPF animation or second polling loop is introduced.

## Source, Scene and Cue Selection

The left 360-pixel LIVE region is the compact Scenes & Cues surface. It uses the own rtaime search box and approximately 74-pixel source rows with a 92×52 thumbnail, source index, name, duration/remaining information, NEXT/PVW state, LIVE/PGM state, cyan selected-row treatment and the own overflow affordance.

The list reuses OperatorViewModel.Sources and SelectedSource. Selecting a row changes only Operator selection. SET NEXT invokes the existing SetPreviewCommand; TAKE invokes the existing CutCommand. The selected row does not implicitly become Preview or Program.

Media cues reuse MediaDeckViewModel.Cues and SelectedCue. Selecting a cue changes only the cue selection. JUMP SELECTED CUE invokes the existing JumpCueCommand through the current marker/timeline path.

The current V1 product does not expose a governed multi-scene activation contract. Dedicated scene activation therefore remains explicitly unavailable rather than being synthesized in the Operator.

## Live Controls

The right 420-pixel LIVE region is divided into four compact sections:

- **Transitions** — selected source versus confirmed NEXT/Preview versus confirmed LIVE/Program, explicit SET NEXT, TAKE and AUTO/DISSOLVE plus the existing transition duration.
- **Layer Stack (PGM)** — compact approximately 38-pixel layer rows and the existing governed graphics Show/Hide path.
- **Stream & Record** — Program recording and Clean Program monitoring through the existing command paths. External streaming/on-air transmission remains UNVERIFIED.
- **Alerts & Notifications** — existing lifecycle, health, Operator error and recording-failure evidence using severity-dot presentation and concise two-line messages.

TAKE uses the cyan primary action treatment because it is an operator action, not a Program-state indicator. Red remains reserved for critical/live-state presentation such as confirmed PGM/LIVE or stop/critical conditions.

No independent routing, transition, layer, recording, stream or alert authority is introduced.

## Alerts

LIVE alerts are a compact projection of existing evidence:

- engine lifecycle;
- Engine, Runtime and Media health;
- affected component;
- lifecycle detail;
- current Operator/Control error;
- recording failure.

No new alert authority or independent health poller is introduced.

## Keyboard and Safety

Existing centralized shortcuts remain authoritative:

- Ctrl+P: set selected source to Preview;
- Ctrl+Enter: CUT confirmed Preview to Program;
- Enter: AUTO confirmed Preview to Program;
- R: start/stop Program recording when the matching command is available.

Selection itself never executes those commands. Production commands remain subject to their existing CanExecute safety gates.

## Performance Boundary

The LIVE implementation does not add:

- another monitoring transport;
- another media decoder;
- another source polling loop;
- WPF meter animation;
- local routing state;
- local transition state.

Source thumbnails continue to be written by OperatorMonitoringViewModel. Audio observations continue on the existing bounded Runtime management cadence. Collapsed LIVE presentation does not rebuild the bounded displayed-source collection until it becomes visible again.

## Verification

build/quality/Test-OperatorUiPolicy.ps1 verifies:

- the 360 / 6 / flexible / 6 / 420 body split and compact 270-pixel Output Routing block;
- default 3×3 multiview plus explicit 2×2 / 3×3 / 4×4 presentation and the 16-source bound;
- PGM/PVW, health, format and source-audio presentation;
- failed-source visibility;
- selection-only multiview behavior;
- double-click large view without routing commands;
- explicit source selection versus Preview/Take actions;
- cue selection versus cue execution;
- reuse of CUT/AUTO, graphics, recording and Clean Program commands;
- external transmission remains explicitly unverified;
- LIVE region isolation from duplicate panels;
- reuse of existing monitoring and audio update paths.

Required Windows gates remain authoritative for WPF compilation, architecture, integration and regression qualification.
