<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Operator UI V1

## Scope

The V1 Operator is the WPF reference client for human interaction with the authoritative ControlHost. It is a presentation and interaction surface only. It does not own production authority, Runtime execution, media processing, persistence truth, AI admission, recording truth or provider state.

The Operator depends only on `rtaime.Client`. All production mutations continue to cross the versioned remote-control path and are accepted or rejected by authoritative ControlHost state.

Operator Monitoring Plane adds a separate non-authoritative monitoring transport through the same Client SDK assembly. Visual observation remains isolated from the management/control transport.

## Control surface

The professional V1 control surface provides:

- explicit connection, revision and synchronization state;
- distinct Preview and Program presentation with source identity;
- live non-authoritative Preview and Program monitoring surfaces;
- a source bank with explicit selection;
- Set Preview plus Preview-to-Program CUT and AUTO/DISSOLVE controls;
- configurable DISSOLVE duration in frames;
- Runtime, timing and input state;
- AI, recording, audio peak and visual-layer state;
- command in-flight, rejected, failed and resynchronized presentation states;
- a persistent last-event and error/rejection footer.

Production mutations are disabled whenever the presentation snapshot is stale, synchronization is in flight, or Runtime is not `READY`. `Set Preview` additionally requires a locally selected source. CUT/AUTO do not take the local selection directly; they take the confirmed authoritative Preview source.

## Keyboard operation

| Shortcut | Action |
| --- | --- |
| `F5` | Synchronize authoritative state |
| `Ctrl+P` | Set selected source to Preview |
| `Space` | CUT confirmed Preview source to Program |
| `Ctrl+Space` | AUTO/DISSOLVE confirmed Preview source to Program |
| `Ctrl+1` | Maximize Preview viewer |
| `Ctrl+2` | Maximize Program viewer |
| `Ctrl+0` | Restore dual Preview/Program view |

Keyboard commands invoke the same ViewModel commands as the visible buttons. They do not bypass readiness or authoritative validation.

## Recovery and stale-state behavior

A `RemoteHostSessionChangedException` marks the current presentation snapshot stale and initiates a full authoritative resynchronization. While stale or resynchronizing, mutation commands remain unavailable. After a successful resynchronization, the Operator reports the restored revision and requires the human operator to repeat the interrupted production command.

Transport loss and timeout conditions on the control path also mark the presentation stale. Existing source and status information may remain visible for operator context, but it is explicitly non-commandable until synchronization succeeds again.

Monitoring state is independent. A lost or stale monitoring stream affects visual observation only and does not invalidate an otherwise current authoritative ControlHost snapshot.

## Visual monitoring boundary

Operator Monitoring Plane provides live Preview and Program monitoring through a dedicated RuntimeHost monitoring pipe. Production bulk media is not sent through ControlHost or RuntimeHost management IPC.

Preview visual monitoring follows the authoritative Preview source identity received through the control snapshot. Program visual monitoring is emitted from the actual Runtime post-composite Program output, including V1 transition and visual-layer results.

The qualified V1 monitor stream is deliberately sampled and downscaled. It is bounded and loss-tolerant: monitoring frames may be dropped under pressure before Program execution is ever delayed.

See `docs/OperatorMonitoringPlane.md` for transport, backpressure and failure-isolation details.

## Styling and layout

The Operator uses `Themes/OperatorTheme.xaml` for reusable dark-surface, typography, button, source-bank and semantic state resources. Preview and Program use distinct semantic accents. The window is resizable and uses minimum dimensions rather than the original fixed bootstrap layout.

## design system and reference-layout qualification

Operator UI Design System formalizes the Operator presentation layer as a reusable production-console design system. `Themes/OperatorTokens.xaml` owns typography, spacing, geometry and semantic color tokens. `Themes/OperatorTheme.xaml` consumes those tokens and provides panels, toolbars, buttons, armed toggles, source tiles, status badges, Preview/Program tallies, meters, text inputs, timecode typography and a timeline seeker style.

Production semantics are deliberate rather than decorative:

- Preview uses the green Preview semantic only.
- Program/on-air uses the red Program semantic only.
- Armed/selected intent uses amber or the neutral selection accent; it is not presented as Program truth.
- Healthy, Warning and Error each have independent semantic resources.
- keyboard focus is rendered with a high-contrast focus border on primary controls and selectable tiles.

The design system changes presentation only. It does not add production authority, infer successful commits, synthesize monitoring state or bypass `rtaime.Client`.

### Reference resolution and DPI

The qualified reference surface is **1920 x 1080**. The Operator opens at 1600 x 900 device-independent units, retains a 1100 x 640 minimum workspace and uses vertical scrolling when the available logical height is reduced.

WPF device-independent layout, `UseLayoutRounding`, device-pixel snapping and an explicit `PerMonitorV2` manifest are used together. The policy gate qualifies the layout invariants for:

- 100% scaling: 1920 x 1080 logical reference surface.
- 125% scaling: 1536 x 864 logical workspace.
- 150% scaling: 1280 x 720 logical workspace.

At 125% and 150%, the minimum workspace remains within the available logical bounds and vertical scrolling preserves access to lower panels. Operator UI Design System does not claim pixel-identical rendering across GPU drivers, Windows text-rendering settings or monitor profiles; screenshot-based visual review remains a manual showcase check.

### Operator UI Design System acceptance evidence

- primary Operator views use the shared dark production theme instead of bootstrap/default styling;
- Preview and Program monitors use distinct tally and panel semantics;
- buttons, toggles, text inputs, source tiles, meters and the timeline use reusable styles;
- keyboard focus is visible;
- the Operator declares per-monitor DPI awareness;
- the Quality gate checks 1920 x 1080 reference-layout constraints and 125%/150% scaling invariants;
- Control/Runtime/Media authority boundaries are unchanged.

## Preview / Program production workspace

Preview / Program Production Workspace formalizes the switcher workflow as **Selected Source → confirmed Preview → confirmed Program**. The local source-bank selection is only an operator intent for `Set Preview`; it is never treated as Program authority.

The workspace exposes:

- dedicated reusable Preview and Program viewers fed only by the existing non-authoritative monitoring plane;
- a default dual-view allocation in which Program receives more visual area than Preview and is explicitly labeled as authoritative output;
- the locally selected source next to the confirmed Preview / next-take source;
- explicit `SET SELECTED → PREVIEW`, `CUT PREVIEW → PROGRAM` and `AUTO PREVIEW → PROGRAM` actions directly below the viewers;
- the current V1 transition set, `CUT` and `DISSOLVE`, plus configurable DISSOLVE duration in frames;
- local presentation-only Preview/Program maximize and dual-view restore controls;
- permanently visible Program stereo meters with dBFS readout and explicit clipping text;
- concise Program overlays for PROGRAM, recording and local clean-feed output state;
- explicit `NO SIGNAL`, `DISCONNECTED`, `RECOVERING`, `SOURCE OFFLINE` and `OUTPUT DISABLED` presentation states;
- Runtime, commit and transition state next to Program;
- direct access to existing Program Output and Program Recording commands;
- a visible HOLD/FTB extension surface that remains non-interactive because those deterministic backend commands are not part of V1;
- command, commit, rejection/failure and last-event state in the operator-status panel.

### Authority and commit semantics

`Set Preview` uses `OperatorControlClient.SelectPreviewAsync`. CUT and AUTO/DISSOLVE use `CutPreviewAsync` and `DissolvePreviewAsync`, which derive the take target from the client's last synchronized authoritative Preview routing. The Operator does not send `SelectedSource.Id` directly to Program.

The Program name/id are updated only by applying a synchronized authoritative snapshot after an accepted mutation. Rejected or failed mutations do not call `Apply` with a locally invented Program state. During a take the UI reports `COMMIT PENDING`; after synchronization it reports the confirmed revision. Runtime/session loss marks the presentation unconfirmed/stale and blocks further mutation until recovery.

Rapid repeated UI actions remain serialized by the existing `AsyncRelayCommand` execution guard plus `OperatorViewModel.IsBusy`; a second action cannot run concurrently while a take is in flight.

Viewer maximize/restore changes only local WPF layout widths. It does not alter routing, monitoring subscriptions, Program Output, recording or Runtime state. The Program viewer derives degraded/unavailable presentation from the confirmed Runtime/source observations; AI-only degradation is therefore displayed in the AI status surface while a valid Runtime fallback can continue to remain visible as Program.

Program stereo metering reuses the existing bounded 200 ms management snapshot cadence. The viewer converts the confirmed linear peak values to dBFS for display only and does not allocate or animate per media frame.

### Preview / Program Production Workspace integration evidence

`ProductionIpcIntegrationTests.Operator_commands_cross_ControlHost_and_RuntimeHost_process_boundaries` proves Source → Preview → CUT Preview → Program and Preview → DISSOLVE Preview → Program over the real ControlHost/RuntimeHost IPC path.

`ProductionIpcIntegrationTests.Serialized_operator_preview_takes_remain_authoritative_under_rapid_actions` performs repeated serialized Preview/take cycles without delay and verifies that every confirmed Program source matches the authoritative Preview target, revisions advance, Runtime remains committed and no pending execution is left behind.

`ProductionIpcRecoveryTests.Runtime_loss_rejects_preview_take_without_changing_confirmed_program` proves that a take rejected after Runtime loss leaves the previously confirmed Program source and revision unchanged.

Existing RuntimeHost restart and Operator reconnect/resynchronization tests remain the recovery evidence for the workspace; Preview / Program Production Workspace does not introduce a second recovery mechanism.

## Source Bin & Live Source Tiles

Source Bin & Live Source Tiles upgrades the source bank into a production source bin while preserving the same authoritative routing model. The Operator does not create source truth; it projects observations already owned by RuntimeHost, ControlHost, the Media Deck and the independent monitoring plane.

Each source tile exposes:

- source name and stable source identity;
- LIVE or MEDIA source type;
- production video format;
- per-source signal/health state;
- live monitoring thumbnail when a sampled source frame is available;
- explicit PVW and PGM tallies derived from authoritative routing;
- Media Deck state and remaining time for the slot currently hosting local media;
- media file name when a local clip is loaded.

### Data ownership

RuntimeHost remains the owner of live source signal state and active V1 video format. Its existing private host snapshot now carries per-source input observations and the active format to ControlHost. ControlHost combines those observations with its production specification and, when applicable, the existing Media Deck snapshot before returning the Operator snapshot.

The source snapshot extension is observational only. It does not change Control contracts, production authority, prepared execution, commit semantics or Runtime scheduling.

Media Deck state is also pushed into the tile projection from `MediaDeckViewModel` so PLAYING/PAUSED/READY and remaining time update while the clip is running without waiting for an unrelated production mutation. Closing the deck clears the MEDIA overlay immediately.

Live thumbnails come exclusively from `OperatorMonitoringViewModel` source frames. Monitoring loss may make thumbnails stale, but it does not block or alter Program execution.

### Selection and Preview

Selecting a source tile updates `SelectedSource`. `Set Preview` / `Ctrl+P` remains the explicit operator action that sends that selected source through the authoritative `SelectPreviewAsync` path. CUT and AUTO continue to take only the confirmed Preview source as established by Preview / Program Production Workspace.

### Source Bin & Live Source Tiles acceptance evidence

- RuntimeHost snapshot exposes actual V1 format and per-source signal state;
- ControlHost source snapshot distinguishes LIVE from the currently loaded MEDIA slot;
- Client source descriptors carry format, health, Media Deck state, remaining time and file identity;
- source tiles display monitoring thumbnails plus PGM/PVW state;
- integration tests verify live health changes and Media Deck READY/PLAYING/remaining observations across real IPC;
- Operator UI policy verifies the source-bin bindings and monitoring/media projection boundaries.

## Graphics & Overlay Operator Workflow

Graphics & Overlay Operator Workflow adds a bounded P0 graphics workflow to the Operator without introducing a second renderer or any WPF-owned production state. The implementation is classified as **REALTIME_CRITICAL / HOST_INTEGRATION** because it extends the RuntimeHost composite path and the private management IPC seams. No public Control contract or persisted schema version changes are required.

The Operator can:

- load a PNG asset through the Windows reference client;
- decode it to straight RGBA while preserving the PNG alpha channel;
- load the bounded RGBA asset into RuntimeHost;
- position it using normalized X/Y coordinates;
- scale it from 0.05x to 4.0x;
- explicitly show/hide the overlay;
- clear the loaded asset.

The V1 asset boundary is intentionally narrow: PNG input only, maximum 384 x 384 pixels, with exact RGBA payload validation. Management IPC frames remain retained at 1 MiB, which accommodates the largest supported 384 x 384 RGBA asset after JSON/base64 framing without turning management IPC into a bulk-media transport.

### Production rendering and authority

Graphics commands follow **Operator → rtaime.Client → ControlHost → RuntimeHost**. ControlHost serializes the commands through its existing mutation gate, requires an authoritative production state plus a connected RuntimeHost, and reports success only after RuntimeHost accepts the requested graphics state. The Operator then refreshes the confirmed snapshot; it never marks an overlay on-air locally.

The graphics mutation seam is intentionally separate from production routing revisions. Loading or positioning a logo does not invent a new Preview/Program routing revision, while RuntimeHost remains the execution owner of the confirmed graphics state.

RuntimeHost materializes the selected asset into a transparent full-frame RGBA layer when the asset or placement changes. The frame hot path only materializes the already-prepared GPU layer. Existing GPU alpha compositing therefore remains the single render path for both managed-reference and CUDA backends.

The render order remains:

1. resolve CUT/DISSOLVE;
2. composite the active graphics layer over the transitioned background;
3. read the final Program pixels;
4. publish Program monitoring;
5. stage those same Program pixels for recording.

This means Program monitoring and recording observe the same post-graphics image rather than independent approximations.

### Recovery and persistence boundary

Graphics & Overlay Operator Workflow does not add durable graphics rundown persistence. The current asset and placement are RuntimeHost process state and are observable through ControlHost snapshots. A RuntimeHost restart therefore clears the loaded asset and the Operator resynchronizes to the empty graphics state. Durable graphics/rundown persistence is outside the Graphics & Overlay Operator Workflow P0 scope.

### P1 lower third decision

The optional P1 lower-third workflow is deliberately not implemented in Graphics & Overlay Operator Workflow. The current V1 production path has no qualified text/CG renderer. Rendering lower-third text in WPF and presenting it as production truth would violate the RuntimeHost rendering boundary. A future lower-third implementation should first introduce a governed production CG/text-rendering capability rather than simulating one in the Operator.

### Graphics & Overlay Operator Workflow acceptance evidence

- bounded graphics-asset validation covers dimensions and exact RGBA payload length;
- Runtime integration tests cover alpha, show/hide, position, scale and persistence across DISSOLVE frames;
- recording integration evidence verifies that the recorded video payload exactly equals the post-graphics Program pixels;
- real process-boundary integration verifies Operator/Client → ControlHost → RuntimeHost load, placement, show and clear state;
- graphics state changes do not advance authoritative Preview/Program routing revision;
- Operator UI policy verifies the PNG decode path, graphics controls and Client-SDK-only authority boundary.

## Audio Operator Workflow

Audio Operator Workflow adds a production-facing audio panel while preserving the existing authority boundary. The Operator does not own audio routing, meter generation or Program truth.

The panel exposes:

- the confirmed AFV source that follows Program;
- Runtime-owned stereo L/R Program meters and a master peak meter;
- per-input Runtime health and AFV/PGM indication;
- per-input linear gain from 0.0x to 4.0x;
- confirmed mute/unmute;
- clipping, silence, underrun and error state;
- local clip audio codec/channel/sample-rate/transport state.

Audio control follows **Operator → rtaime.Client → ControlHost → RuntimeHost**. ControlHost accepts audio commands only for authoritative production sources and serializes them through the management mutation gate. RuntimeHost remains the execution owner and the Operator refreshes the confirmed snapshot after each command.

Audio state changes do not advance the authoritative Preview/Program production revision. AFV follows the confirmed Program source only after Runtime execution has switched to that source.

### Live meters and hot-path isolation

The Runtime media paths measure actual Float32 stereo samples. Physical Media I/O uses captured audio samples; local media uses the decoded Media Deck Float32 payload; the virtual reference provider supplies deterministic stereo observations when no external media source is active.

The WPF client refreshes audio observations through the management snapshot at a bounded 200 ms cadence. There is no locally generated meter animation and no raw audio payload crosses Operator/ControlHost management IPC.

RuntimeHost keeps external sample buffering bounded and consumes one exact Program audio window per boundary. The same post-gain/mute Program payload is used by recording and physical Program output.

### Audio Operator Workflow acceptance evidence

- unit tests cover stereo metering, gain and clipping;
- Runtime integration covers actual external/clip-style Float32 Program payload, gain, mute, silence, clipping and underrun;
- process-boundary integration covers Operator audio commands without changing production revision;
- AFV integration verifies that the Runtime audio source follows confirmed Program after source changes;
- Operator UI policy verifies stereo/master meters, AFV source, health, clip-audio state, gain/mute commands and the absence of WPF meter synthesis;
- Operator remains dependent only on `rtaime.Client`.

## Program Output / Clean Feed

Program Output / Clean Feed adds a separate local Program Output window for presentation on a second Windows display without introducing another production renderer or authority path.

The Operator can:

- select any currently attached Windows display;
- start and stop the clean-feed window explicitly;
- switch the running output to another display;
- toggle fullscreen and windowed presentation;
- see a dedicated output health state and placement detail;
- continue operating the main Operator workspace independently.

The clean-feed window contains only the Program image on a black surface. It exposes no source selection, transition, graphics, audio or transport controls.

### Program truth and aspect ratio

ProgramOutputWindow binds directly to the existing OperatorMonitoringViewModel.ProgramImage. The same frozen bitmap object that feeds the Operator Program monitor therefore feeds the clean-feed presentation surface. Program Output / Clean Feed does not create another decode path, WPF media player, compositor or routing state.

RuntimeHost remains the source of Program pixels. The monitoring tap is derived from the post-transition/post-graphics Program readback already used by the independent monitoring plane. Stretch=Uniform preserves the Program aspect ratio; letter/pillar boxing is black.

### Display lifecycle

The controller enumerates Windows displays and targets the selected physical display using native window placement so PerMonitorV2 DPI does not reinterpret physical monitor bounds as WPF DIPs.

If the selected display disappears while output is running:

1. the controller re-enumerates display topology;
2. the primary display is selected;
3. fullscreen is disabled;
4. the clean feed remains available in a centered windowed fallback;
5. the Operator reports FALLBACK instead of silently claiming a healthy output.

A user-selected display or fullscreen change clears the fallback state. Closing the clean-feed window is equivalent to a controlled stop.

### Performance and evidence boundary

The clean feed deliberately reuses the existing loss-tolerant monitoring plane, so it does not add another RuntimeHost subscriber, another frame conversion, or a management-IPC bulk-media path. The Operator UI and clean-feed window share the already-created frozen ProgramImage.

The current monitoring plane is monitor-grade: 320×180 and sampled every fourth production frame. This is sufficient for the Program Output / Clean Feed local showcase surface and preserves hot-path isolation, but it is not a claim of full-resolution/full-frame-rate broadcast output. SDI, NDI, SRT and network streaming remain outside Program Output / Clean Feed.

Program Output / Clean Feed evidence consists of:

- the existing Runtime monitoring integration proving Program monitoring originates from the actual Program frame;
- Operator UI policy checks proving the clean feed binds that same ProgramImage;
- structural checks for selectable displays, start/stop, fullscreen/windowed placement and display-change fallback;
- the existing Architecture gate proving the Operator still references only rtaime.Client.

## Media Autoplay & End Behavior

The local Media Deck now exposes production playback policy next to the existing transport and timeline:

- Auto Play on Program enable/disable;
- Hold Last Frame, Stop, Loop and Return to IN end behavior;
- ON PROGRAM / OFF PROGRAM state;
- effective IN/OUT range;
- T-minus countdown derived from effective remaining frames;
- explicit APPLY POLICY action through the Client SDK.

The Operator remains a presentation/control surface. Program-edge detection and end behavior execute in RuntimeHost against committed Program state. The UI continuously refreshes loaded-deck observations so a Runtime-triggered autoplay transition from READY/PAUSED to PLAYING is visible without synthesizing local state.

The source tile and deck countdown use the same effective remaining range. Media Autoplay & End Behavior deliberately does not implement auto-pause when the clip leaves Program, playlist auto-advance, rundown automation or macros.

### Media Autoplay & End Behavior acceptance evidence

- contract tests validate playback-policy and effective-range invariants;
- Runtime integration covers all four end modes, IN/OUT and retake after end;
- process-boundary integration covers cued CUT and DISSOLVE autoplay;
- client tests prove confirmed policy round-trip;
- Operator UI policy checks the autoplay control, end-mode selector, Program state, effective range and countdown.

## Verification

`build/quality/Test-OperatorUiPolicy.ps1` verifies the primary UI architectural and UX guardrails, including:

- reusable token and theme loading;
- typography, spacing and semantic production-state resources;
- visible keyboard-focus resources;
- 1920 x 1080 reference layout and 125%/150% DPI invariants;
- Preview/Program semantic resources;
- source-bin binding with live/media metadata, health, thumbnails, PGM/PVW and remaining time;
- keyboard command declarations;
- live Preview/Program image bindings;
- stale/busy/connection presentation state;
- selected-source vs confirmed-Preview separation;
- Preview-derived CUT/AUTO take semantics;
- visible commit and transition status;
- PNG/RGBA graphics load, placement, scale and confirmed show/hide controls;
- AFV source, stereo/master audio meters, gain, mute, clipping/health and clip-audio state;
- media autoplay/end-behavior controls with effective-range countdown;
- retained Client-only Operator project dependency.

`build/quality/Test-OperatorMonitoringPolicy.ps1` verifies the monitoring-plane separation, bounded/loss-tolerant behavior and prohibition on management-IPC pixel transport.

The existing Required Gates remain authoritative for build, architecture, contracts, unit, integration, security, provider smoke and packaged end-to-end qualification.


## Program recording workflow

The right-side production workspace now includes a dedicated **PROGRAM RECORDING** panel.

It exposes:

- confirmed REC lifecycle state;
- live elapsed time;
- editable destination directory;
- editable file name;
- explicit START REC and STOP REC controls;
- written/dropped/writer-failure statistics;
- finalized file path;
- Runtime-reported failure detail.

The panel is a Client-SDK projection only. `OperatorViewModel` calls `OperatorControlClient.StartRecordingAsync` and `StopRecordingAsync`; it never creates a recorder, recording writer, encoder or file stream. The existing bounded 200 ms management refresh also projects recording observations so elapsed time and asynchronous writer failures become visible without inventing local state.

Recording commands are serialized through ControlHost and delegated to RuntimeHost. They do not alter Preview/Program routing or advance Production revision. The recorded media is the same post-transition/post-graphics Program video and post-AFV Program audio already owned by RuntimeHost.

The current V1 output is the deterministic `.rtaime-recording` reference artifact. It is externally verifiable with `ReferenceRecordingPayloadReader`; it is not presented as an MP4/MOV/MXF broadcast deliverable.


## Runtime Health & Performance HUD

Runtime Health & Performance HUD replaces the former coarse SYSTEM summary with a compact evidence-based Runtime health/performance HUD. The Operator still owns no health truth; it renders the health projection returned through `rtaime.Client`.

The HUD exposes:

- Engine Health;
- Control Health;
- Runtime Health;
- Media Health;
- Provider Health;
- GPU Provider Health;
- current Runtime video format;
- last measured Program-frame processing time and frame budget;
- cumulative dropped-frame evidence;
- Runtime uptime;
- GPU utilization when a qualified measurement source exists;
- VRAM usage/capacity when a qualified measurement source exists;
- the observation timestamp.

### PASS / FAIL / UNVERIFIED semantics

Health evidence uses exactly three presentation states:

- `PASS` means the required live evidence is present and healthy.
- `FAIL` means a required subsystem has explicit failed/degraded execution evidence or is unavailable where availability is required.
- `UNVERIFIED` means the system may be operating, but available evidence is insufficient to claim healthy PASS.

The theme maps PASS to the healthy semantic, FAIL to the error semantic and UNVERIFIED to the warning semantic. UNVERIFIED is therefore never presented as a false green state.

The V1 managed reference GPU provider is intentionally `Degraded` because it is functional but not hardware-qualified. Consequently the provider/GPU projection and aggregate Engine Health remain UNVERIFIED rather than PASS on that development path. This is deliberate evidence semantics, not a UI failure.

### Runtime metrics and hot-path boundary

RuntimeHost reuses its existing scheduler timing observation to publish the last Program-boundary processing duration and frame budget. A small `RuntimeFrameDropCounter` keeps only the prior scheduler timestamp plus a cumulative counter; it allocates no history and performs no file/network I/O.

Dropped-frame evidence combines:

1. scheduler frame periods missed between consecutive committed Program boundaries; and
2. cumulative native Program-output backpressure/rejections when the native Media I/O path is active.

Uptime is measured by a monotonic RuntimeHost stopwatch.

GPU utilization and used VRAM are nullable evidence. The current active backend exposes no qualified utilization or used-VRAM telemetry source, so these fields remain `UNVERIFIED`. A known total-memory capacity may be shown separately, but it is not converted into an invented usage value.

### Operator update cadence

Runtime Health & Performance HUD adds no second UI telemetry loop. Health/performance observations reuse the existing bounded 200 ms management snapshot refresh that already drives audio/recording observations. The effective presentation cadence is therefore at most 5 Hz and does not participate in Runtime scheduling or Program rendering.

RuntimeHost support diagnostics consume the same performance snapshot, preserving one observation source instead of creating a parallel metrics truth.

### Runtime Health & Performance HUD acceptance evidence

- deterministic projection tests cover healthy PASS evidence and degraded-provider UNVERIFIED behavior;
- process-boundary integration covers Runtime disconnect becoming visible as FAIL;
- dropped-frame counter tests cover missed scheduler cadence plus output backpressure/rejection;
- Operator UI policy verifies all required HUD fields and PASS/FAIL/UNVERIFIED visual semantics;
- policy verifies one bounded 200 ms management polling loop and no local WPF/NVML/performance-counter GPU synthesis;
- observability policy verifies support diagnostics reuse the same Runtime performance snapshot and that the drop counter remains allocation/history/I/O free.


## Visible AI Showcase

Visible AI Showcase Integration adds one deliberately narrow, visually understandable AI feature: **Person Segmentation Highlight**.

The repository does not currently expose a person-detection capability or object bounding boxes. The implemented showcase therefore uses the existing governed `ai.person-segmentation` capability instead of inventing a detection result. The managed reference provider returns a deterministic normalized person region; RuntimeHost renders that region through its existing dynamic RGBA layer.

The Operator panel exposes:

- explicit AI ON / AI OFF controls;
- feature name;
- AIHost provider name;
- current state;
- measured inference round-trip time;
- Person Regions count;
- result confidence;
- source-sequence to Program-application sequence;
- failure/suppression detail.

### Execution and authority boundary

The control path is **Operator → rtaime.Client → ControlHost → RuntimeHost**. RuntimeHost owns the bounded showcase policy but not inference execution. At most every 200 ms it submits the current committed Program `FrameDescriptor` to AIHost through AIHost's existing versioned named-pipe protocol.

AIHost remains the governed inference boundary. No inference runtime, provider package, model execution or segmentation logic exists in WPF. Only descriptors and inference metadata cross management IPC; Program pixel payloads are not transported to ControlHost or the Operator.

RuntimeHost validates the returned source frame/sequence, person semantic, normalized region and confidence threshold before updating its existing dynamic visual layer. The effect becomes visible only on a subsequent Program boundary, and the UI exposes both source and application sequence.

### Failure and fallback

Inference is asynchronous and never awaited by the Program media loop. Only one request may be in flight and submissions are capped at 5 Hz.

Provider unavailability, transport loss, malformed/stale results, low confidence and timeout all fail closed for the AI effect. RuntimeHost restores the previous visual-layer mode and Program continues without the AI highlight. AI failure therefore cannot terminate or replace committed Program execution.

If the explicit Operator graphics overlay is active, the AI result is reported as `SUPPRESSED` rather than falsely claiming that the highlight is visible, because the current V1 compositor gives that explicit graphics layer precedence.

The managed reference provider is architecture/demo evidence, not a claim of production model quality or qualified GPU inference.

### Visible AI Showcase Integration acceptance evidence

- real AIHost → RuntimeHost process integration enables the segmentation highlight;
- Operator/Client/ControlHost enable and disable cross the normal management authority boundary;
- provider-unavailable and timeout integration tests prove clean Program continuity;
- inference time, provider, confidence, Person Regions and source/application synchronization are visible;
- Operator UI policy verifies ON/OFF controls and prohibits local inference ownership;
- no project-reference topology or public AI/Runtime contract version changes are required.

## Demo Production Package

The Operator toolbar now exposes `Open Demo Production` as a single explicit showcase-bootstrap action. It does not create a new authority path: production routing still crosses `OperatorControlClient` into ControlHost, Media Deck operations remain on the existing Client/Runtime path, graphics use the Graphics & Overlay Operator Workflow overlay seam, audio uses the Audio Operator Workflow input-state seam, and AI enable uses the Visible AI Showcase Integration showcase seam.

The bundled package prepares Program = Input A and Preview = Product Clip/Input B, deterministic IN/OUT plus two cue points, Auto Play on Program with Hold Last Frame, unity/unmuted clip audio, a 12-frame DISSOLVE, a pre-rendered lower-third/logo asset and the Person Segmentation Highlight.

The lower-third asset is loaded but initially hidden because the current V1 explicit graphics overlay takes precedence over the dynamic AI highlight. This keeps the package honest about what is simultaneously visible while leaving the lower third immediately ready for the existing SHOW/HIDE control.

Package state is presented as IDLE / LOADING / READY / FAILED in the toolbar. READY is emitted only after the Client/Runtime snapshots confirm the package's Program, Preview, Media Deck, graphics and AI enable state. Details and integrity/failure errors remain visible via the package status tooltip.

See `docs/DemoProductionPackage.md` for package contents, integrity verification, repeatability and acceptance evidence.

## Showcase UX hardening

The funding-demo Operator now has an explicit presentation and lifecycle hardening layer with no new production authority or feature scope. It standardizes primary-action tooltips, cyclic keyboard navigation, initial focus, empty-state presentation, status/error separation, graceful asynchronous shutdown and controlled unexpected-UI-error reporting.

The existing 1920×1080, 125% and 150% DPI qualification remains unchanged, as does the local Program Output fallback behavior for second-display removal.

See `docs/ShowcaseUxHardening.md` for scope, lifecycle behavior, qualification evidence and the manual showcase checklist.


## Startup, recovery and System Status UX

The Operator now presents a single coherent lifecycle vocabulary: `STARTING`, `HEALTHY`, `DEGRADED`, `RECOVERING`, `FAILED`, `STOPPING` and `STOPPED`. The visible state is a projection of existing lifecycle, authoritative synchronization and health evidence; it is not a second monitoring or supervision subsystem.

### Startup

A full-window startup surface remains above the production workspace until the first authoritative Control snapshot establishes qualified Operator readiness. It shows real Control, Runtime, AI and Operator states and starts ordinary Client SDK synchronization automatically. No timer is used to simulate progress.

Startup completion is latched. Later service loss does not bring the startup surface back over the production UI.

### Persistent engine status and safety

The header always includes an `ENGINE <state>` badge with text as well as semantic styling. System Status exposes lifecycle, Program Safety, affected component, recovery action, Control/Runtime/Media/provider evidence, AI state, recording state and the existing bounded performance observations.

`Program Safety = BLOCKED` participates in the shared mutation gate. Stale/recovering Control state therefore preserves observation where possible but cannot issue Preview, TAKE, graphics, audio, recording or AI mutations.

### Automatic resynchronization

The existing 200 ms management refresh remains the only periodic Operator management loop. It now continues attempting a full snapshot while disconnected or stale. The first successful full snapshot after a loss restores the authoritative session and clears recovery state; no local state is promoted to authority.

### AI degradation

An enabled AI effect that becomes unavailable, times out or fails is represented as AI-specific degradation. If Control and Runtime remain valid, the UI does not mark Program failed and continues to describe the governed fallback as independent of core production execution.

### Evidence semantics

System Status retains `PASS / FAIL / UNVERIFIED` semantics for subsystem evidence. Lifecycle presentation never turns missing GPU/provider metrics into healthy evidence. A terminal `FAILED` lifecycle requires explicit recovery-exhaustion evidence rather than an Operator-local retry guess.

## Fullscreen Production Shell & Docking

The Operator is hosted by a persistent production workspace rather than a conventional form-style page. The shell defines six presentation regions: Top Bar, left Tools/Media, center Workspace, right Inspector, lower Timeline, and bottom Transport/Status.

The left and right regions are independently resizable and collapsible. The lower Timeline height is resizable, and **MAXIMIZE VIEW** temporarily dedicates the available workspace to the center region without changing production state. Splitter positions, panel visibility, fullscreen preference and the selected workspace placeholder are local Operator preferences only.

Layout preferences are stored below the current user's local application data in `rtaime/operator-layout.json`. Persisted dimensions are normalized into bounded ranges before they are applied. Missing, corrupt, non-finite or out-of-range values fall back to safe defaults or are clamped. No routing, Runtime state, recording state, health authority or other production truth is persisted in this file.

### Fullscreen operation

`F11` toggles between the normal resizable window and borderless Production Fullscreen. `Esc` exits fullscreen without closing the Operator. Fullscreen removes standard Windows chrome and uses the current display's maximized work area while preserving all keyboard production controls.

Windowed mode remains the default for a fresh profile, so development and debugging do not require fullscreen. A user's explicit fullscreen preference is restored on subsequent launches and can always be exited with `F11` or `Esc`.

### Persistent production controls

The compact Top Bar keeps engine lifecycle, connection state, timecode, active format, recording state, synchronization and layout controls visible. The Bottom Transport/Status region keeps media play/pause, stop, current timecode, countdown, on-Program state, the last operator event and active errors visible even while the center workspace scrolls.

The existing Preview/Program, source-bin, transition, graphics, audio, recording, AI, monitoring and system-status workflows remain in the center workspace. The existing Media Deck remains available there, while the timeline itself is hosted once in the persistent lower region to avoid duplicate seeker surfaces.

### Authority boundary

The production shell is presentation-only. `OperatorShellViewModel` owns only layout dimensions, collapsed state, center-maximize state, fullscreen preference and workspace selection. It has no Client, Control, Runtime, Media, AI or Recording dependency and never synthesizes or persists authoritative production state.

`build/quality/Test-OperatorUiPolicy.ps1` guards the canonical region names, fullscreen entry/exit path, splitter availability, persisted layout fields, bounded normalization, single shell-hosted timeline and the presentation-only dependency boundary.
