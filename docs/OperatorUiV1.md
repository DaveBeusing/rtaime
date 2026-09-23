<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>
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
- a governed Scene catalog with presentation-only selection and explicit TAKE SCENE activation;
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
| `Ctrl+F` | Focus Media Library search |
| `Ctrl+P` | Set selected source to Preview |
| `Space` | Preview media Play/Pause when the loaded source is confirmed Preview |
| `K` | Preview media Pause |
| `S` | Preview media Stop |
| `I` / `O` | Set Preview media IN / OUT |
| `M` | Add Preview media cue |
| `Delete` | Delete selected Preview cue when available |
| `Up` / `Down` | Previous / next Preview cue |
| `Enter` | AUTO/DISSOLVE confirmed Preview source to Program |
| `Ctrl+Enter` | CUT confirmed Preview source to Program |
| `R` | Start/stop Program recording when available |
| `F11` | Toggle Operator fullscreen/windowed |
| `Esc` | Exit Operator fullscreen |
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

### Token ownership and feature presentation

The visual contract has three layers. Reference geometry tokens describe the 1920×1080 qualification baseline and shared shell proportions; they are reference values, not a fixed render size. Semantic tokens and common styles own cross-workspace typography, spacing, radii, media/thumbnail canvases, diagnostic surfaces, timeline surfaces, monitor overlays, scrims and guide presentation. Productive feature XAML consumes those resources instead of redefining the global palette or shared typography locally.

Feature-specific presentation remains local only when the shape or metric expresses a real workspace-specific requirement and is not repeated as a cross-workspace convention. Literal visible colors and numeric font sizes are not permitted in productive feature XAML; reusable presentation values are promoted to `OperatorTokens.xaml` or shared theme resources. This keeps visual consolidation independent from Runtime, Control, Media and monitoring authority.

Production semantics are deliberate rather than decorative:

- Preview uses the green Preview semantic only.
- Program/on-air uses the red Program semantic only.
- Armed/selected intent uses amber or the neutral selection accent; it is not presented as Program truth.
- Healthy, Warning and Error each have independent semantic resources.
- keyboard focus is rendered with a high-contrast focus border on primary controls and selectable tiles.

The design system changes presentation only. It does not add production authority, infer successful commits, synthesize monitoring state or bypass `rtaime.Client`.

### Reference resolution and DPI

The qualified reference surface is **1920 x 1080**. The Operator opens at 1600 x 900 device-independent units, retains a 960 x 500 minimum workspace and uses vertical scrolling when the available logical height is reduced. Below the compact-workspace threshold the shell reduces presentation-only panel allocation without overwriting persisted workspace sizes.

Reference geometry is converted to render geometry from the current view container. The viewport scale retains a 1.0 reference at 1920×1080, resolves to approximately 0.83 at 1600×900, 0.80 at 1536×864 and 0.67 at 1280×720, and uses a bounded 0.60 presentation floor for the 960×540 compact qualification surface. Media Deck preview height, LIVE lower output height, Compositing preview height, Health sidebar/cards, Source Bin thumbnail width, Quick Control card width and Timeline track-header width follow that render scale or a bounded compact minimum. Persisted 400 / 340 / 320 workspace dimensions remain reference coordinates; this render-only completion does not change layout schema version 6.

WPF device-independent layout, `UseLayoutRounding`, device-pixel snapping and an explicit `PerMonitorV2` manifest are used together. The policy gate qualifies the layout invariants for:

- reference qualification: 1920 x 1080 logical surface.
- standard window: 1600 x 900 logical workspace.
- 125% qualification: 1536 x 864 logical workspace.
- 150% qualification: 1280 x 720 logical workspace.
- 200% qualification: 960 x 540 logical workspace.

At 125%, 150% and 200%, the minimum workspace remains within the available logical bounds. Vertical scrolling plus compact panel allocation preserves access to the central production surface without changing authoritative state or persisted workspace dimensions. Operator UI Design System does not claim pixel-identical rendering across GPU drivers, Windows text-rendering settings or monitor profiles; screenshot-based visual review remains a manual showcase check.

Automated responsive qualification is implemented by the Windows Operator test suite rather than by reproducing the same viewport arithmetic in the static PowerShell policy. The matrix exercises every workspace, Compact collapse, viewer/fullscreen restore, persistence stability and the bounded Startup surface. See [Operator Visual Qualification](OperatorVisualQualification.md) for the supported matrix, regression contract and rendering limits.

### Operator UI Design System acceptance evidence

- primary Operator views use the shared dark production theme instead of bootstrap/default styling;
- Preview and Program monitors use distinct tally and panel semantics;
- buttons, toggles, text inputs, source tiles, meters and the timeline use reusable styles;
- media canvases, overlays, guides, thumbnails, diagnostic surfaces and repeated typography consume semantic shared resources rather than feature-local palette values;
- keyboard focus is visible;
- the Operator declares per-monitor DPI awareness;
- the Quality gate checks reference geometry plus 1920×1080, 1600×900, 1536×864, 1280×720 and 960×540 responsive layout invariants;
- Control/Runtime/Media authority boundaries are unchanged.

## Preview / Program production workspace

Preview / Program Production Workspace formalizes the switcher workflow as **Selected Source → confirmed Preview → confirmed Program**. The local source selection is operator intent for `Set Preview`; it is never treated as Program authority.

At the 1920×1080 reference viewport, EDIT uses a 1070×700 reference composition. The rendered production-workspace height follows the shared viewport scale at smaller logical sizes, while the upper and lower rows retain the 390:304 reference proportion around the 6-pixel separator. At reference size, the upper monitor row resolves to 390 pixels and is split into equal Preview and Program viewers with a 6-pixel gap. Both viewers reuse the existing `MonitorView` presentation contract, a shared 40-pixel header and a 42-pixel custom transport strip. FIT remains aspect-safe through `Stretch.Uniform`; 50% and 100% remain local presentation modes.

At reference size, the lower row uses the following 320:462:276 proportions:

- **Scene Stack — 320 px**: a compact source-routing projection used by the EDIT workspace. The LIVE workspace additionally exposes the governed Scene catalog. Scene row selection remains local presentation state; TAKE SCENE is the only Scene activation action. Confirmed active-Scene presentation comes only from synchronized `ActiveSceneId` evidence.
- **Output Routing — 462 px**: four presentation roles using the existing output and monitoring projections: Program, Preview, Aux and Clean Feed. Program and Aux project governed Control/Runtime output-role state with provider evidence, Preview reflects confirmed Control routing for monitoring/switching, and Clean Feed reflects the existing local Program monitor. Missing or stale backend evidence remains explicitly `UNVERIFIED` rather than being inferred by the UI.
- **System Status — 276 px**: compact existing Engine, Control, Runtime, Media and GPU health rows plus thin `RtaimeMetricBar` presentation for Disk, Network and Temperature. Those three metrics remain `UNAVAILABLE` while the authoritative health contract does not publish them; the Operator performs no local probing.

Preview retains the existing media transport, cue and IN/OUT command paths. Program intentionally acquires no Preview transport authority. The visible monitor controls use rtaime icon/toggle controls only.

The compact EDIT Scene Stack continues to reuse the existing `SetPreviewCommand`, `CutCommand` and `DissolveCommand`. CUT and AUTO still operate only on confirmed Preview through the established Client/ControlHost path. LIVE Scene activation uses the separate `ActivateSceneCommand`, so Scene selection cannot implicitly invoke any production mutation.

Program `ON AIR` is visible only when the existing Program monitor state is observed as `LIVE`. It describes the confirmed Program bus and does not claim that an external transmission path is on air.

Viewer maximize/restore and monitor fullscreen remain presentation-only. `Ctrl+1`, `Ctrl+2` and `Ctrl+0` continue to change the local viewer allocation without changing routing, monitoring subscriptions, Program Output, recording, Runtime state or media decoding.

### Authority and commit semantics

`Set Preview` uses `OperatorControlClient.SelectPreviewAsync`. CUT and AUTO/DISSOLVE use `CutPreviewAsync` and `DissolvePreviewAsync`, deriving the take target from the client's last synchronized authoritative Preview routing. The Operator does not send `SelectedSource.Id` directly to Program.

The Program name/id are updated only by applying a synchronized authoritative snapshot after an accepted mutation. Rejected or failed mutations do not call `Apply` with a locally invented Program state. During a take the UI reports the existing commit state; after synchronization it reports the confirmed revision. Runtime/session loss marks the presentation unconfirmed/stale and blocks further mutation until recovery.

Rapid repeated UI actions remain serialized by the existing `AsyncRelayCommand` execution guard plus `OperatorViewModel.IsBusy`; a second action cannot run concurrently while a take is in flight.

The mockup layout adds no pipeline restart, alternate monitor transport, scene authority, output authority or telemetry poller. The same monitoring images, routing commands, source projections and health observations are reused in denser presentation.

### Preview / Program Production Workspace integration evidence

`ProductionIpcIntegrationTests.Operator_commands_cross_ControlHost_and_RuntimeHost_process_boundaries` proves Source → Preview → CUT Preview → Program and Preview → DISSOLVE Preview → Program over the real ControlHost/RuntimeHost IPC path.

`ProductionIpcIntegrationTests.Serialized_operator_preview_takes_remain_authoritative_under_rapid_actions` performs repeated serialized Preview/take cycles without delay and verifies that every confirmed Program source matches the authoritative Preview target, revisions advance, Runtime remains committed and no pending execution is left behind.

`ProductionIpcRecoveryTests.Runtime_loss_rejects_preview_take_without_changing_confirmed_program` proves that a take rejected after Runtime loss leaves the previously confirmed Program source and revision unchanged.

Existing RuntimeHost restart and Operator reconnect/resynchronization tests remain the recovery evidence for the workspace; the mockup presentation introduces no second recovery mechanism.

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

RuntimeHost materializes bitmap and Production CG content into independent transparent full-frame RGBA sources when their prepared content or placement changes. The frame hot path materializes only currently visible sources and submits them as one bounded ordered layer request. Existing GPU alpha compositing remains the single render path for both managed-reference and CUDA backends.

The render order is:

1. resolve CUT/DISSOLVE;
2. materialize visible governed layers;
3. sort by confirmed layer order and stable identity;
4. composite each layer over the transitioned background;
5. read the final Program pixels;
6. publish Program monitoring;
7. stage those same Program pixels for recording.

The current layer budget is eight active provider layers. Stable Runtime evidence exposes layer identity, kind, order, visibility, opacity, transform and content identity. The Runtime performance snapshot additionally reports the last measured composition duration and active composition layer count; these observations are evidence, not hardware qualification.

This means Program monitoring and recording observe the same post-graphics image rather than independent approximations.

### Recovery and persistence boundary

Graphics & Overlay Operator Workflow still does not add durable graphics rundown persistence across a ControlHost restart.

For RuntimeHost replacement/restart, ControlHost now retains the confirmed in-process recovery state for bitmap content/placement, Production CG definition, layer visibility/opacity and layer order. After authority reconciliation it restores available graphics content and then reapplies the retained order against the new Runtime layer set. Operator resynchronizes from the resulting Runtime-confirmed snapshot; it does not infer restoration locally.

### Production CG lower third

The Graphics workspace provides a governed Production CG lower-third editor. Text, primary typeface, explicit fallback typeface and font size are submitted through **Operator → rtaime.Client → ControlHost → RuntimeHost**. RuntimeHost rasterizes the bounded text surface into its dedicated CG source and feeds it into the same ordered Program compositor used by bitmap overlays. Bitmap and CG can therefore coexist concurrently rather than replacing one another.

Font resolution is fail-closed: RuntimeHost uses the requested installed family, then only the explicitly configured fallback family. Missing primary and fallback fonts reject the command rather than silently changing Production appearance.

The Operator never uses WPF-rendered text as Production pixels. The confirmed Runtime snapshot exposes the resolved typeface, cache state and render duration. Bitmap overlays remain valid and are not migrated automatically.

See [ProductionCgTextRendering.md](ProductionCgTextRendering.md) for the complete contract, cache bounds and recovery semantics.

### Graphics & Overlay Operator Workflow acceptance evidence

- bounded graphics-asset validation covers dimensions and exact RGBA payload length;
- Runtime integration tests cover bitmap alpha, show/hide, position, scale and persistence across DISSOLVE frames;
- managed-reference tests prove deterministic ordered multi-layer alpha composition, bounded layer count, invalid duplicate rejection and intermediate-surface cleanup;
- Production CG tests cover Runtime rasterization, alpha composition, explicit font fallback, missing-font failure and bounded surface reuse;
- bitmap + Production CG integration proves concurrent composition in one Runtime-owned ordered stack;
- recording integration evidence verifies that the recorded video payload exactly equals the post-graphics Program pixels;
- real process-boundary integration verifies Operator/Client → ControlHost → RuntimeHost graphics state and bounded layer mutations;
- RuntimeHost restart recovery restores retained bitmap + CG content, layer state and ordering;
- graphics state changes do not advance authoritative Preview/Program routing revision;
- Operator UI policy verifies custom-control layer operations and the Client-SDK-only authority boundary while prohibiting local WPF Production rendering.

## Audio Operator Workflow

Audio Operator Workflow adds a production-facing audio panel while preserving the existing authority boundary. The Operator does not own audio routing, meter generation or Program truth.

The panel exposes:

- the confirmed AFV source that follows Program;
- Runtime-owned stereo L/R Program meters and a master peak meter;
- per-input Runtime health and AFV/PGM indication;
- per-input linear gain from 0.0x to 4.0x;
- confirmed mute/unmute;
- generated audio diagnostic state with the Runtime-confirmed mode and active identification channel;
- cycle control for Silence, Tone, Stereo ID, Channel ID and Pulse without a UI audio timer;
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
- process-boundary integration covers Operator audio commands and generated test-signal control without changing production revision;
- generated-audio tests cover phase continuity, safe level bounds, channel identification, pulse timing and hot-path allocations;
- AFV integration verifies that the Runtime audio source follows confirmed Program after source changes;
- Operator UI policy verifies stereo/master meters, AFV source, health, clip-audio state, gain/mute/test-signal commands, active-channel projection and the absence of WPF meter synthesis;
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

## Live Multiview & Scene/Cue Control

The LIVE workspace now uses a dedicated three-zone production layout. The left region is source/cue selection, the center is the adaptive multiview plus Quick Controls, and the right region contains explicit take, transition, layer, recording/output and alert controls.

The multiview retains the existing Preview and Program monitor images and adds an adaptive bounded source bank. Source tiles show existing name, format, health, PGM/PVW tally, monitoring thumbnail and Runtime-derived stereo audio peaks. Failed sources remain present. Source selection is non-destructive; routing still requires Set Preview and Program changes still require CUT/AUTO.

Source grid density adapts through 2, 3 and 4 columns with a maximum of 16 displayed source tiles. Double-click opens a larger view of the already-available image only. No command path is invoked by the large-view interaction.

The LIVE source/cue surface separates source and cue selection from activation. Media cue execution reuses the existing Media Deck Jump Cue command. Scene selection remains presentation-only, while TAKE SCENE invokes the governed `ActivateSceneCommand` through the existing Client/ControlHost/Runtime authority path. Confirmed active-Scene and failure evidence is projected back to the Operator; the UI does not create local Scene authority.

The right Live Controls surface reuses existing transition, graphics visibility, recording and Clean Program monitoring commands. Compact alert presentation is derived from the existing lifecycle, health, Operator error and recording error projections. External stream/on-air transmission remains explicitly UNVERIFIED.

Detailed behavior and authority boundaries are documented in docs/LiveMultiviewAndSceneControl.md.

## Layered Timeline & Cue Workspace

The persistent lower workspace is a frame-accurate 1476×320 production timeline at the 1920×1080 reference surface. Its exact vertical composition is 44 pixels of toolbar, 30 pixels of ruler/marker lane and six compact reference lanes: V3 Graphics, V2 Video, V1 Video, A1 Music, A2 SFX and A3 VO. Video/graphics lanes are 42 pixels high; audio lanes are 40 pixels high. The track-header column is 238 pixels at reference size and scales down to a bounded 170-pixel compact presentation width; the remaining width is the frame canvas.

The visible lane names are presentation roles only. The loaded Media Deck clip remains the authoritative V1 Video item. Existing Graphics resources project only to V3 Graphics, and existing Audio resources project only to A1 Music. V2 Video, A2 SFX and A3 VO remain empty reference lanes until matching governed product semantics exist. The underlying timeline categories and shared Inspector selection model remain unchanged.

The ruler and clip geometry continue to use the existing frame-derived visible range and frame-to-pixel conversion. The playhead is presented as a 2-pixel cyan line with a cyan head. Confirmed IN/OUT trim handles, bounded snapping, 1×–32× zoom, horizontal scrolling, Fit and pointer seek retain the existing command paths. Named cues are rendered above the tracks in the ruler marker lane and still use the existing Media Deck marker commands for add, jump, rename and delete.

The timeline toolbar uses rtaime controls only. It exposes current timeline context, SELECT mode, explicit LINK N/A, SNAP, observed FPS, Preview Play/Pause and Stop, previous/next cue, zoom, Fit, Operator fullscreen, timecode and named cue creation. The current contracts expose no sequence-switch command, Blade/Cut edit command, link command or frame-rate mutation, so the UI does not invent them.

The previous separate Bottom Transport visual is collapsed because its active transport/fullscreen actions now live inside the timeline toolbar. This allows the six reference tracks to consume the complete 320-pixel lower workspace without overlap.

Rendering remains viewport-bounded through VisibleItems and VisibleCues, so zoom/scroll do not allocate a second timeline or present off-screen objects. Selection, Shift-add/Ctrl-toggle multi-selection, cue focus, trim preview, marker commit/cancel and shared Inspector projection remain on their existing paths.

The current backend exposes no governed clip-move edit command, timeline undo/redo history, section/show-marker domain, transition projection or reusable audio-waveform projection. The Operator therefore renders no fake waveform, transition or edit history merely to imitate the mockup.

The detailed operator and authority semantics are documented in docs/LayeredTimeline.md.

### Layered timeline acceptance evidence

- the shell default lower-region height remains exactly 320 pixels;
- UI policy verifies the 44/30 reference density, responsive track-header derivation and all six reference lane labels;
- Client unit tests continue to cover frame/time conversion, zoomed visible ranges, pointer mapping and snapping;
- marker-controller unit tests continue to cover absolute IN/OUT command generation and previous/next cue navigation;
- UI policy verifies cyan playhead/cue markers, custom controls, zoom/scroll, trim preview/commit/cancel paths, multi-selection-to-Inspector integration and viewport-bounded projection refresh;
- stable track and cue projections remain hash-gated so routine playback observations do not allocate a new composition model per frame.

## Pixel-parity hardening

The Operator uses the 1920×1080 at 100 percent reference as the binding pixel-layout baseline: 60 px top bar, 92 px navigation, 400 px Media Library, 1070 px center, 340 px Inspector, 700 px upper workspace, 320 px timeline and 6 px region gaps. Dual Preview/Program remains the approximately equal 532 / 6 / 532 split, the Inspector extends beside the lower region, and the timeline terminates before the Inspector.

Feature XAML below `src/Hosts/rtaime.Operator/` is statically audited as XML by `build/quality/Test-OperatorUiPolicy.ps1`. Direct visible WPF Button, ToggleButton, CheckBox, RadioButton, TextBox, ComboBox, Tab/List/Tree/DataGrid, Slider/ProgressBar, scrolling, splitter, menu and toolbar elements fail CI. Theme/control implementation dictionaries remain the only permitted location for WPF base primitives required to implement the own rtaime controls.

The same structured audit rejects numeric feature `Border` radii above the 4 px panel contract and rejects hardcoded uses of the shared global palette where an Operator color token exists. Local purpose-specific colors such as the compositing graph grid and timeline ruler/background remain local because they are not global semantic palette values.

Media Deck, Quick Controls, clean Program controls, Graphics, Audio, Recording and AI actions now use the own `RtaimeButton` family. Audio meter surfaces use `RtaimeMetricBar` rather than visible stock `ProgressBar` chrome. Timeline and Compositing semantic colors resolve through the shared color tokens instead of duplicating global hex values.

DPI/layout qualification keeps the same structure at 2560×1440 at 100 percent, 1920×1080 at 125 percent and 3840×2160 at 150/200 percent. The center can grow at larger viewports; reference shell dimensions, control chrome, borders and glyph resources do not fall back to native WPF visuals.

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

The default Windows RuntimeHost output is now `.mp4` with H.264/AVC video and AAC-LC stereo 48 kHz audio. RuntimeHost remains authoritative for the normalized target name and final path, so the Operator does not infer container or codec state. The deterministic `.rtaime-recording` artifact remains a separate injected test/evidence backend and is not presented as a delivery format. MOV/MXF are not supported recording outputs.


## Runtime Performance Status Bar

The Production Shell now reserves a permanent compact bottom status bar for CPU, GPU, RAM, VRAM, Runtime frame time, measured Output FPS and cumulative dropped-frame evidence. These values reuse the same Runtime/Control performance projection as the OUTPUTS and HEALTH workspaces; the bar owns no telemetry source, hardware probe or polling loop.

Measured Output FPS comes from the existing Program scheduler-boundary observations and uses constant-space smoothing. The title bar remains focused on application lifecycle/readiness, timecode and Program state instead of duplicating performance metrics. See [Runtime Performance Status Bar](RuntimePerformanceStatusBar.md).

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

CPU, system-memory, GPU and VRAM measurements are nullable evidence. CPU and system-memory values are qualified on Windows and sampled by RuntimeHost. GPU utilization and VRAM usage are populated from NVML when a qualified NVIDIA driver is available. Missing measurements remain `UNVERIFIED`; known capacities are never converted into invented usage values.

### Operator update cadence

Runtime Health & Performance HUD and the permanent Runtime Performance Status Bar add no second UI telemetry loop. Health/performance observations reuse the existing bounded 200 ms management snapshot refresh that already drives audio/recording observations. The effective presentation cadence is therefore at most 5 Hz and does not participate in Runtime scheduling or Program rendering. Hardware values remain independently bounded by the Runtime hardware sampling cache.

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

The Operator is hosted by a persistent production workspace rather than a conventional form-style page. The shell defines a global Top Bar, persistent left Workspace Navigation, left Tools/Media region, center Workspace, right Inspector, lower Timeline, and bottom Transport/Status.

The left and right regions are independently resizable and collapsible. The lower Timeline height is resizable, and **MAXIMIZE VIEW** temporarily dedicates the available workspace to the center region without changing production state. Splitter positions, panel visibility, fullscreen preference, normalized window placement and the selected workspace are local Operator preferences only.

Layout preferences are stored below the current user's local application data in `rtaime/operator-layout.json`. Persisted dimensions are normalized into bounded ranges before they are applied. Missing, corrupt, non-finite or out-of-range values fall back to safe defaults or are clamped. No routing, Runtime state, recording state, health authority or other production truth is persisted in this file.

### Fullscreen operation

`F11` toggles between the normal resizable window and borderless Production Fullscreen. `Esc` exits fullscreen without closing the Operator. Fullscreen removes standard Windows chrome and uses the current display's maximized work area while preserving all keyboard production controls.

Windowed mode remains the default for a fresh profile, so development and debugging do not require fullscreen. A user's explicit fullscreen preference is restored on subsequent launches and can always be exited with `F11` or `Esc`.

### Persistent production controls

The compact Top Bar keeps engine lifecycle, connection state, CPU utilization, GPU utilization, system-memory usage, Program-boundary processing time, timecode, Program source and synchronization visible. Hardware values are projections of Runtime-published measurements; unavailable measurements remain explicitly UNVERIFIED. External LIVE/ON AIR state remains UNVERIFIED while no authoritative external-transmission feed exists. The Bottom Transport/Status region keeps media play/pause, stop, current timecode, countdown, on-Program state, the last operator event and active errors visible even while the center workspace scrolls.

The existing Preview/Program, source-bin, transition, graphics, audio, recording, AI, monitoring and system-status workflows remain in the center workspace. The existing Media Deck remains available there, while the timeline itself is hosted once in the persistent lower region to avoid duplicate seeker surfaces.

### Authority boundary

The production shell is presentation-only. `OperatorShellViewModel` owns only layout dimensions, collapsed state, center-maximize state, fullscreen preference and workspace selection. It has no Client, Control, Runtime, Media, AI or Recording dependency and never synthesizes or persists authoritative production state.

`build/quality/Test-OperatorUiPolicy.ps1` guards the canonical region names, fullscreen entry/exit path, splitter availability, persisted layout fields, bounded normalization, single shell-hosted timeline and the presentation-only dependency boundary.


## Media Pool & Context Inspector

The persistent left shell region is the Operator **Media Pool**. It projects only resource types already represented by current product data: Sources, the loaded local Clip, Audio inputs, the loaded Graphics asset and the current Composition/AI effect. Search and filtering operate only on loaded projection metadata and never mutate source, Runtime or routing state.

The Media Pool now acts as the Operator Media Library & Asset Browser. It supports virtualized Grid and List presentation over a deterministic UI projection bounded to 4096 entries. Grid cards expose existing thumbnails, duration/type information and explicit textual ONLINE/OFFLINE state; the List uses recycling virtualization. Offline or unavailable resources remain visible when authoritative observations still identify them rather than being silently removed.

Search remains local to the current projection, type/availability filters are exposed as compact filter chips, and Ctrl+F focuses the Media Library search field through the central keyboard registry. Grid and List support Extended multi-select while retaining one primary selection for the existing context Inspector.

Local media import continues to use the existing Media Deck open path. Preview, Timeline and Cue actions reuse their existing command paths; Reveal in Explorer is limited to an already-known local Clip path. No second ingest, metadata extraction, thumbnail extraction, directory crawl, media index or persistent DAM/MAM subsystem is introduced. The current Scene model has no governed asset-assignment action, so the browser does not invent one.

### Selection and drag/drop

Media Pool selection is bounded Operator UI context. Selecting a Source or loaded Clip projects the matching existing OperatorViewModel.SelectedSource; selecting Audio projects the existing SelectedAudioInput. Ctrl/Shift multi-select is maintained separately as local presentation state, with SelectedItem remaining the primary Inspector context. The selection itself is never production authority.

A Source or the currently loaded Clip may be dragged to Preview when the corresponding existing source is available. The drop invokes the existing SetPreviewCommand, so the mutation continues through the Client SDK and authoritative ControlHost path. Invalid drops are rejected with DragDropEffects.None.

The currently loaded Clip may be dropped on the persistent Timeline surface only as a request to select and refresh that existing Media Deck/Timeline context. It does not create a second timeline, playlist or ingest path.

### Context Inspector

The persistent right shell region is selection-driven. Every projected Inspector property has a stable identifier intended for future Quick Controls pinning. Property presentation explicitly distinguishes:

- `METADATA` — verified read-only resource metadata;
- `DESIRED` — local editable configuration that has not yet been confirmed;
- `COMMITTED` — state observed back from the existing authoritative path.

Clip playback policy edits reuse MediaDeckViewModel.ApplyPlaybackPolicyCommand. Audio gain/mute reuse the existing audio commands. Graphics position/scale reuse ApplyGraphicsCommand. AI feature/provider/confidence/fallback are displayed from confirmed observations, and AI enable/disable reuses the existing Client control commands.

The Inspector is hosted by the reusable `OperatorInspectorControl` and keeps the same interaction model across Media, Edit, Scenes and Compositing shell contexts. At the 1920×1080 reference surface it is a continuous **340 px** panel from y=60 to the bottom, spanning beside the timeline without an external card margin. A single 1 px left divider separates it from the center workspace.

The top row is a 40 px custom tab strip with **Inspector**, **Processing** and **Metadata**. The active tab uses the shared 2 px cyan underline. Each context begins with a compact selected-item header using the existing thumbnail projection at approximately 72×42 px, selection identity, technical/path context and a non-authoritative overflow affordance.

The Inspector tab adds a compact 34 px property-category strip with **Video**, **Audio**, **Transform** and **FX**. Basic Properties, Effects and Transport & Controls are flat sections rather than nested cards. Read-only property rows use a 96 px label column and a 30–34 px row rhythm; editable values reuse only the existing rtaime TextBox, ComboBox, CheckBox, Slider and button controls. Property-group expansion state remains retained per selection context. Numeric edits combine direct text entry with bounded sliders where the existing product model exposes a numeric range, and invalid text conversion is surfaced inline rather than silently ignored.

Extended Media Library selection is represented explicitly. When multiple assets are selected, shared values are shown directly and divergent values are shown as `MIXED`; mutation controls remain scoped to a single active context so the Operator does not accidentally turn a primary-selection command into an unsupported bulk edit.

Reset actions restore the established local desired defaults for playback, audio gain and graphics transform values. Resetting remains a presentation-side edit only; the existing APPLY/command path is still required before authoritative state can change.

The current graphics capability exposes Position X, Position Y and Scale. Rotation, Anchor and Crop are therefore shown as unavailable capability fields rather than being implemented as parallel UI-only domain state. Likewise, the current processing capability exposes the existing governed AI effect enable/disable path but no effect-stack reordering; the Inspector states that limitation explicitly.

No local edit is presented as committed before the corresponding existing command path has been applied and authoritative state is observed again. Empty selection, no-assets, filtered-empty, loading, error and offline states use concise production-facing messages.

See `docs/MediaLibraryAssetBrowser.md` for the Media Library scalability, selection, drag/drop, context-action and authority model.


## Workspaces, Multiview & Keyboard-First UX

The Operator exposes eight canonical task workspaces — MEDIA, EDIT, LIVE, SCENES, COMPOSITING, OUTPUTS, HEALTH and SETTINGS — over the same authoritative product state. Workspace switching changes presentation only and persists independent layout geometry per workspace. COMPOSITING preserves the global Media Library, Inspector and Timeline shell while the 1070×700 center uses an approximately 64 / 6 / 36 graph-to-preview split. The graph projects current sources, Preview/Program routing, the existing graphics transform, GPU composition, Program output and recording from already observed state. Preview reuses the established monitor chrome and transport paths. System & Performance reuses existing lifecycle and bounded health metrics. CPU and system-memory measurements come from the RuntimeHost Windows telemetry sampler; GPU/VRAM measurements use Runtime-published NVML evidence when available. The Operator performs no local hardware probing and preserves unavailable measurements as UNVERIFIED. Node status refreshes update stable projection objects without moving the layout, node selection feeds the shared Inspector, and arbitrary Runtime rewiring remains unavailable because no authoritative rewiring contract exists. OUTPUTS, HEALTH and SETTINGS continue to reuse the existing operational projection. HEALTH adds the central event-driven subsystem-health presentation while global production readiness remains owned by RuntimeReadinessService.

LIVE uses the dedicated 360 / 6 / flexible / 6 / 420 show-operation composition: Scenes & Cues on the left, a default 3×3 multiview with compact Output Routing in the center, and 420px Live Controls on the right. The multiview consumes the existing Preview/Program monitoring images and already available source thumbnails and does not open another monitoring transport. Its toolbar supports explicit 2×2, 3×3 and 4×4 presentation plus center fullscreen/maximize. PGM/PVW tallies, failed-input visibility, audio meters and timecode remain projections of existing authoritative or observed state. Scene/source selection is non-destructive; Set Preview/Take remain explicit commands.

Window-level keyboard bindings are centralized in OperatorKeyboardCommandRegistry with deterministic conflict detection and text-entry safety. Space controls Preview media play/pause when the loaded Media Deck source is the confirmed Preview source; K/S, I/O, M and Up/Down use the same Preview-source guard. Enter performs AUTO, Ctrl+Enter performs CUT, R controls recording where the matching command is available, and F11 retains Operator fullscreen. Unsupported J/L shuttle semantics remain deliberately unbound.

OUTPUTS, HEALTH and SETTINGS reuse the existing lifecycle, health, monitoring, recording and output projections rather than creating a second diagnostics truth. HEALTH is the dedicated detailed diagnostics surface; SETTINGS remains configuration-oriented. Clean Program remains a presentation of the existing Runtime-derived Program monitoring image and is explicitly not the physical Program output path.

Layout persistence is schema-versioned and keeps only UI presentation fields. SAVE LAYOUT stores the current workspace layout, while LAYOUT RESET restores that workspace's canonical defaults. Corrupt or incompatible layout data falls back safely.

See docs/OperatorWorkspaces.md for the complete workspace, Quick Controls, shortcut, multiview and Clean Program operating model.\n\nSee `docs/CompositingNodeGraph.md` for the Compositing graph projection, interaction model and authority boundary.


## Preview / Program production monitors

Preview and Program retain separate `PreviewViewer` and `ProgramViewer` role controls over the reusable `MonitorView` state contract. Their structural presentation is centralized in `RtaimeMonitorPresentation`: header geometry, monitoring frame surface, format/zoom chrome, Safe Area/Center Mark/Grid overlays, timecode, unavailable-frame presentation and shared overlay toggles are defined once. Role-specific header/status/footer content is supplied through explicit presentation slots, so common visual changes do not require parallel Preview/Program XAML edits. Both monitors consume the existing independent monitoring bitmap projection; neither creates a decoder, playback session or frame transport.

Each monitor presents the confirmed source identity, the authoritative Runtime video format, monitoring-surface status and the current presentation zoom. The Runtime health contract currently exposes resolution, frame rate and pixel format but no verified color-space value, so the monitor header shows color space explicitly as `N/A` rather than synthesizing metadata.

Fit, 50 percent and 100 percent modes affect only WPF presentation of the already received monitoring bitmap. Safe Area, Center Mark and Grid are independent overlay layers above the image surface and likewise do not mutate Runtime or media state.

Preview reuses the existing Media Deck and Timeline commands for play/pause, stop, IN/OUT and cue navigation. Those controls are enabled only when the loaded Media Deck source identity matches the confirmed Preview source. The centralized keyboard registry applies the same source guard, preventing Preview transport shortcuts from accidentally controlling media that is no longer on Preview. J/L remain unbound because deterministic shuttle semantics are not exposed by the existing Media Deck contract.

Program contains no Preview transport bindings. Its `ON AIR` presentation is derived from the existing observed Program monitor state being `LIVE`, while the separate local Clean Program Output state remains visible beside it. This does not claim that an external transmission path is live; the top-bar external LIVE / ON AIR state remains explicitly unverified.

Monitor fullscreen is transient Shell presentation state. It maximizes the selected monitor, collapses surrounding presentation regions and uses the existing Operator fullscreen window mode. Exiting fullscreen restores the prior viewer mode and center-layout state. No second playback or monitoring instance is created.

Timecode is displayed only when the loaded Media Deck source identity matches the source currently shown by that monitor. Other sources show an explicit unavailable timecode rather than borrowing unrelated transport state.
