<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Live Multiview & Scene/Cue Control

## Purpose

The LIVE workspace is the fast production-operation surface. It composes existing Operator, monitoring, Media Deck, graphics, recording and Clean Program projections without creating another routing, transition, media or output authority.

The workspace is organized as three operator zones:

- left: source/scene selection and Media cues;
- center: adaptive multiview and Quick Controls;
- right: explicit routing/take, layer, recording/output and alert controls.

Selection is deliberately non-destructive. A selected source does not become Preview or Program until the existing routing commands are invoked.

## Multiview

The multiview reuses the existing independent RuntimeHost monitoring plane and OperatorSourceTileViewModel source projections. It does not open another monitoring transport, decoder or renderer.

Two dedicated monitor tiles retain Preview and Program observation. The source bank below them adapts its density to the available source count:

- up to 4 displayed sources: 2 columns;
- 5 through 9 displayed sources: 3 columns;
- 10 through 16 displayed sources: 4 columns;
- more than 16 sources: the first 16 are shown and the header reports that the view is bounded.

Each source tile exposes existing data only:

- source name;
- source format;
- source health/state;
- confirmed PGM/PVW tally;
- source thumbnail from the existing monitoring cache;
- stereo audio peaks where the existing Runtime audio snapshot exposes them;
- clipping indication.

Failed or unavailable sources remain visible. LOST, ERROR, FAILED and OFFLINE states are surfaced directly on the tile instead of removing the source from the layout.

Double-clicking Preview, Program or a source tile opens a larger presentation of the already available image. The large-view action has no routing command and cannot change Preview or Program.

The control refreshes its bounded source projection only while visible. Per-source image and meter changes continue to originate from the existing monitoring/audio cadences; no WPF animation or second polling loop is introduced.

## Source, Scene and Cue Selection

The left LIVE region separates selection from production action.

The source list reuses OperatorViewModel.Sources and SelectedSource. Selecting a row changes only Operator selection. SET PVW invokes the existing SetPreviewCommand; CUT TAKE invokes the existing CutCommand.

Media cues reuse MediaDeckViewModel.Cues and SelectedCue. Selecting a cue changes only the cue selection. JUMP SELECTED CUE invokes the existing JumpCueCommand through the current marker/timeline path.

The current V1 product does not expose a governed multi-scene activation contract. The LIVE workspace therefore labels dedicated scene activation as unavailable rather than synthesizing a scene command. Existing graphics/layer visibility remains the current governed layer operation.

## Live Controls

The right LIVE region keeps production mutations explicit:

- selected source versus confirmed Preview versus confirmed Program;
- Set Preview;
- CUT;
- AUTO/DISSOLVE;
- transition duration;
- current graphics/layer status and Show/Hide through the existing graphics command;
- Program recording Start/Stop;
- Clean Program monitoring Start/Stop.

External streaming/on-air transmission is shown as UNVERIFIED because no authoritative external-transmission contract is currently exposed.

The legacy central Production Controls, Media Deck, Source Bin, Audio and Recording panels are hidden in LIVE to avoid duplicate control surfaces. The lower edit timeline is also hidden in LIVE so the multiview retains the available show-operation height; cue access remains in the dedicated left LIVE region. These surfaces remain available in the workspaces where they are otherwise used.

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

- 2/3/4-column adaptive multiview behavior and the 16-source bound;
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
