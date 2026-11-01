<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

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

The list now projects the governed Scene catalog through `OperatorViewModel.Scenes` and `SelectedScene`. Selecting a row remains presentation-only and never mutates Preview or Program. **TAKE SCENE** invokes the explicit governed Scene activation command through ControlHost; **CUT PVW** keeps the established confirmed Preview-to-Program CUT workflow available.

NEXT evidence is derived from the Scene's declared Preview source against authoritative Preview routing. Scene rows also expose whether the desired Scene definition is routing-only or carries a declared compositing layer stack. LIVE evidence is shown only when the synchronized authoritative state explicitly reports the Scene as `ActiveSceneId`. Direct routing mutations clear active-Scene evidence, and confirmed direct graphics/layer mutations also clear it whenever they make the governed production state differ from the activated Scene.

Media cues reuse MediaDeckViewModel.Cues and SelectedCue. Selecting a cue changes only the cue selection. JUMP SELECTED CUE invokes the existing JumpCueCommand through the current marker/timeline path.

The Scene contract may bundle Preview/Program routing with the bounded versioned multi-layer compositing state already covered by the same Control → Runtime prepared-execution boundary. TAKE SCENE submits that complete desired state as one mutation. Runtime rejects missing or mismatched required layer resources before commit, so routing and graphics cannot become partially confirmed. Output roles remain outside Scene recall.

## LIVE Preview/Program switching evidence

LIVE retains the original two-up professional production viewers (Preview
left/green, Program right/red). MULTIVIEW remains a separate destination,
with no replacement or second production canvas. Viewer headers reuse the
existing source/route/generation-fenced monitoring owners and report the age
of each matching **transport receipt**, not a claim of delivered hardware
frames. The monitoring watchdog refreshes the text using its existing
cadence; unavailable frames never become fabricated black or zero-latency
evidence.

The compact LIVE row prioritizes SET PVW, CUT and AUTO ahead of horizontally
scrolling auxiliary controls. Local selection, confirmed Preview and
authoritative Program are distinct. The separate right-side transitions
panel presents Preview request pending/rejected/confirmed and Program Take
pending/rejected/uncertain/Control-confirmed outcomes, plus existing dissolve
duration. NEXT rundown item/status are read-only projections; all actual
Rundown GO, Scene TAKE and Show Control cue commands stay at their existing
owners. Failed/unconfirmed Take does not auto-retry or optimistically
advance Program. An ambiguous CUT/AUTO failure retains a local fail-closed
lock on SET PVW, CUT/AUTO, Scene TAKE and Rundown/Show Control live actions,
which only an explicit successful Control synchronization clears. Neither
an ordinary polling refresh nor the old monitor image clears the lock.
Known rejected commands retain their own distinct failure status. The Operator
must reconcile an uncertain result with ControlHost before attempting another
governed command.

## Live Controls

The right-hand **Live Controls** panel is the dedicated **MULTIVIEW** control
surface (`Shell.LiveControlsVisibility`). The primary **LIVE** workspace uses
the separate, always-visible `LiveActionStrip` for SET PVW, CUT and AUTO and
continues to show the contextual Inspector on the right. The same Operator
commands own both deliberate control surfaces; they are not displayed together,
and no new action owner, command registry or persisted navigation mode is added.

### Always-visible operational surface

The panel presents only bounded current-production controls and safety feedback:

| Region | Visible in the default panel | Existing authority |
| --- | --- | --- |
| **Transitions** | Local selected source; confirmed Preview/NEXT and Program/LIVE labels; guarded SET PVW, CUT and AUTO; LIVE Take evidence, Control revision/status and last command error | `OperatorViewModel.SetPreviewCommand`, `CutCommand`, `DissolveCommand` |
| **Stream & Record** | Confirmed recording status, elapsed time, failure and active profile; START REC while idle/eligible or prominent red STOP REC while the observed state is `RECORDING` | `OperatorViewModel.StartRecordingCommand`, `StopRecordingCommand` |
| **Clean monitoring** | Explicit **LOCAL CLEAN PROGRAM** state, separate from network send | `MainWindow.ProgramOutput.RunState` |
| **Network transmission** | Separate Program send and Aux send state, using the existing evidence-aware output-role projection | `MainWindow.OutputHealth.ProgramTransmissionState` and `AuxTransmissionState` |
| **Layer Stack (PGM)** | Confirmed graphics asset/state/visibility and bounded confirmed compositing-layer count | Existing `OperatorViewModel` graphics and layer projections |
| **Alerts & Notifications** | Engine, Runtime and Media health, active problem count, persistent operator problems and their recovery text | Existing lifecycle/health and `MainWindow.Notifications.ActiveProblems` |

While the authoritative recording state is `RECORDING`, **STOP REC** replaces
**START REC** in the main visible recording row. STOP uses the existing recording
command and its unchanged `CanExecute` boundary; a stale or disconnected Control
still disables the request instead of inventing a local stop. During
`FINALIZING`, neither start nor stop appears, while the actual state remains
visible. Recording failure text is not hidden in a secondary section. A stopped
request is not evidence that a recording file was finalized.

The existing guarded SET PVW, CUT and AUTO controls remain directly accessible
in MULTIVIEW, where no `LiveActionStrip` is visible. CUT uses the cyan action
treatment, not the red confirmed-Program tally. The current target, eligibility,
revision, rejected/pending state and any **unconfirmed TAKE** warning stay visible
even when every optional section is closed. Opening a section never routes or
takes a source.

### Optional secondary details

Every section is expanded by an ordinary keyboard-accessible `RtaimeButton`
and starts collapsed. The button reports SHOW/HIDE state; it only toggles local
WPF visibility. There is no additional state store, new navigation destination,
session-persisted preference or separate production command.

| Button | Revealed content | Behavior |
| --- | --- | --- |
| **SHOW TAKE DETAILS** | Existing `TransitionFrames` draft, full Take-target description and the read-only next Rundown item/status | No SET PVW, CUT, AUTO, Rundown GO or Scene TAKE on expand |
| **SHOW LAYER SETTINGS** | Bounded 38-DIP confirmed layer rows, existing SHOW/HIDE CURRENT LAYER command and editing guidance | Actual visibility changes still require `ToggleGraphicsCommand` |
| **SHOW CLEAN MONITORING** | OPEN CLEAN / STOP CLEAN using the existing `ProgramOutput.StartCommand` and `StopCommand` | Local Clean Program is monitoring, not a Program route or physical output |
| **SHOW TRANSMISSION DETAILS** | Existing Program/Aux provider-send detail strings and explicit physical-output disclaimer | A recent send does not prove physical scan-out |

The persistent incident list is deliberately **not** under any optional toggle.
Its existing problem title, impact, operator action and technical-detail expansion
remain available. The always-visible recording and Take fields precede layer
configuration so the main control path stays usable in compact 960×540 and
960×500 logical viewports; the scroll viewer owns overflow instead of shrinking
the media or hiding critical actions in a menu.

Network send, **LOCAL CLEAN PROGRAM** monitoring, an accepted Control Take,
Runtime-executed Program, recording-file finalization and physical display output
are distinct claims. The panel binds only to the existing sources of evidence,
including unavailable/stale provider states; it does not convert absent data into
a LIVE or healthy indicator.

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

Selection itself never executes those commands. Production commands remain subject to their existing CanExecute safety gates. Secondary SHOW/HIDE buttons retain the shared focus and WPF keyboard activation behavior. The existing `LiveConsoleInput` suppression protects production buttons from repeated Enter/Space and double-click activation; text editing still suppresses centralized production shortcuts. Closed secondary sections do not change or retry a command.

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
- explicit Scene selection versus governed Scene activation;
- confirmed active-Scene evidence plus cue selection versus cue execution;
- reuse of CUT/AUTO, graphics, recording and Clean Program commands;
- external transmission is confirmed only by connected provider evidence with a recent successful send; physical-network interoperability remains unverified;
- LIVE region isolation from duplicate panels;
- reuse of existing monitoring and audio update paths.

The `LiveControlsActionMenuTests` suite checks source-level context ownership,
critical recording and active-incident visibility, custom-control policy,
optional section bindings, clean-versus-transmission evidence and a real WPF
MULTIVIEW/LIVE composition using the existing test-only isolated STA process.
The recording states in that view test are simulated presentation values, not
ControlHost/Runtime recording or file-finalization qualification.
`LiveConsoleTests`, `LiveConsoleUncertainTakeTests`,
`LiveConsoleWindowQualification` and Show Control tests continue to own
live mutation, ambiguous Take recovery, focus and alternate-context behavior.
Required Windows gates remain authoritative for WPF compilation, architecture,
integration and regression qualification. Hardware output, physical mixed-DPI
and live device/audio acceptance remain separate, unverified evidence.
## Show Control cue stack

The SCENES & CUES region now keeps Media Cues and governed Show Control as separate operator surfaces in the same production workflow. Show Control selection is presentation-only; selecting a cue never executes it.

The Show Control RUN surface exposes explicit SELECT, ARM, GO and CANCEL operations plus recovery acknowledgement. `F9` is the centralized, conflict-checked GO shortcut. Execution evidence is projected from confirmed Control snapshots and shows the authoritative execution state, current cue/action and failure detail.

The EDIT surface is bounded to the supported action union and preserves stable list/cue/action identities across rename and reorder. Server-side validation remains authoritative. See `docs/ShowControlCueSequencing.md` for the execution, timing and recovery model.

## Confirmed Live console operation

Live separates the local selected source/Scene from confirmed NEXT/PVW, LIVE/PGM and the active Scene. CUT and AUTO act on confirmed Preview; Scene TAKE acts on the explicitly selected Scene. Their target and availability detail stays visible, including while a command is disabled. No modal confirmation is added to ordinary takes.

The Scenes & Cues tabs include the existing Rundown owner. RUNDOWN GO operates its prepared item or governed pending follow target. SHOW GO operates the armed Show Control cue/action, regardless of a different local editor selection. Media marker jump remains separate. Show Control lists can be explicitly reloaded with the refresh button; live execution observations preserve local editor rows and text drafts. ARM requires the selected definition to match the saved definition.

The existing 200 ms Operator synchronization loop projects routing, source/Scene evidence and Show Control execution. Its attached Rundown observation uses the same Client and loop; no extra timer, transport, decoder or follow clock is introduced. Version/generation fences reject deliveries overtaken by a command. Busy cue actions also gate the existing production controls. Synchronization loss disables production actions until confirmed recovery. Recovery acknowledgement and continuation remain ControlHost decisions; reconnect sends no GO.

NEXT badges are visible only for confirmed Preview evidence. PGM layers come from the existing confirmed compositing descriptors. Rundown thumbnails use identity-matched Library/monitoring evidence; missing images and durations stay missing/unknown. ONLINE describes catalogue availability, never resident readiness. NEXT_READY is shown only from prepared execution evidence, with expiry/reprepare detail and host revalidation at GO.

Enter in a cue list does not take or GO. Enter on an explicitly focused button belongs to that button, text editors retain native commit behavior, and open menus suppress production shortcuts. The centralized registry remains the help source; F9 is Show GO, Ctrl+Enter is CUT and Enter outside editing/cue/button contexts retains AUTO. A separate, focus-scoped Rundown Ctrl+Shift+G invokes only its guarded GO and Ctrl+Shift+H invokes only its guarded HOLD; neither works in a text editor, on key repeat or without a valid ControlHost-command CanExecute state. No generic selection-triggered TAKE/GO shortcut is added.

Focused Live console tests cover retained drafts, storage conflict, stale/recovery eligibility and actual armed targets. Real-host Operator tests cover imported reference media, Preview CUT/AUTO, Scene activation/rejection, resident Rundown Prepare/GO, observed follow countdown, HOLD and independent reconnect observation. The existing coordinator/IPC suites retain bounded follow, ambiguous completion, restart and no-replay recovery qualification. The real MainWindow phase checks focused actions and text entry at compact and desktop sizes within the existing STA/monitoring lifetime. Physical output and performance qualification remain separate.
