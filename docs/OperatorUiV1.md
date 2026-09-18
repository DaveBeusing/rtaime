<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Operator UI V1

## Scope

The V1 Operator is the WPF reference client for human interaction with the authoritative ControlHost. It is a presentation and interaction surface only. It does not own production authority, Runtime execution, media processing, persistence truth, AI admission, recording truth or provider state.

The Operator depends only on `rtaime.Client`. All production mutations continue to cross the versioned remote-control path and are accepted or rejected by authoritative ControlHost state.

AP-29 adds a separate non-authoritative monitoring transport through the same Client SDK assembly. Visual observation remains isolated from the management/control transport.

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

Keyboard commands invoke the same ViewModel commands as the visible buttons. They do not bypass readiness or authoritative validation.

## Recovery and stale-state behavior

A `RemoteHostSessionChangedException` marks the current presentation snapshot stale and initiates a full authoritative resynchronization. While stale or resynchronizing, mutation commands remain unavailable. After a successful resynchronization, the Operator reports the restored revision and requires the human operator to repeat the interrupted production command.

Transport loss and timeout conditions on the control path also mark the presentation stale. Existing source and status information may remain visible for operator context, but it is explicitly non-commandable until synchronization succeeds again.

Monitoring state is independent. A lost or stale monitoring stream affects visual observation only and does not invalidate an otherwise current authoritative ControlHost snapshot.

## Visual monitoring boundary

AP-29 provides live Preview and Program monitoring through a dedicated RuntimeHost monitoring pipe. Production bulk media is not sent through ControlHost or RuntimeHost management IPC.

Preview visual monitoring follows the authoritative Preview source identity received through the control snapshot. Program visual monitoring is emitted from the actual Runtime post-composite Program output, including V1 transition and visual-layer results.

The qualified V1 monitor stream is deliberately sampled and downscaled. It is bounded and loss-tolerant: monitoring frames may be dropped under pressure before Program execution is ever delayed.

See `docs/OperatorMonitoringPlane.md` for transport, backpressure and failure-isolation details.

## Styling and layout

The Operator uses `Themes/OperatorTheme.xaml` for reusable dark-surface, typography, button, source-bank and semantic state resources. Preview and Program use distinct semantic accents. The window is resizable and uses minimum dimensions rather than the original fixed bootstrap layout.

## AP-46 design system and reference-layout qualification

AP-46 formalizes the Operator presentation layer as a reusable production-console design system. `Themes/OperatorTokens.xaml` owns typography, spacing, geometry and semantic color tokens. `Themes/OperatorTheme.xaml` consumes those tokens and provides panels, toolbars, buttons, armed toggles, source tiles, status badges, Preview/Program tallies, meters, text inputs, timecode typography and a timeline seeker style.

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

At 125% and 150%, the minimum workspace remains within the available logical bounds and vertical scrolling preserves access to lower panels. AP-46 does not claim pixel-identical rendering across GPU drivers, Windows text-rendering settings or monitor profiles; screenshot-based visual review remains a manual showcase check.

### AP-46 acceptance evidence

- primary Operator views use the shared dark production theme instead of bootstrap/default styling;
- Preview and Program monitors use distinct tally and panel semantics;
- buttons, toggles, text inputs, source tiles, meters and the timeline use reusable styles;
- keyboard focus is visible;
- the Operator declares per-monitor DPI awareness;
- the Quality gate checks 1920 x 1080 reference-layout constraints and 125%/150% scaling invariants;
- Control/Runtime/Media authority boundaries are unchanged.

## AP-47 Preview / Program production workspace

AP-47 formalizes the switcher workflow as **Selected Source → confirmed Preview → confirmed Program**. The local source-bank selection is only an operator intent for `Set Preview`; it is never treated as Program authority.

The workspace exposes:

- distinct live Preview and Program monitors with their existing tally semantics;
- the locally selected source next to the confirmed Preview / next-take source;
- explicit `SET SELECTED → PREVIEW`, `CUT PREVIEW → PROGRAM` and `AUTO PREVIEW → PROGRAM` actions;
- configurable DISSOLVE duration in frames;
- Runtime, commit and transition state next to Program;
- command, commit, rejection/failure and last-event state in the operator-status panel.

### Authority and commit semantics

`Set Preview` uses `OperatorControlClient.SelectPreviewAsync`. CUT and AUTO/DISSOLVE use `CutPreviewAsync` and `DissolvePreviewAsync`, which derive the take target from the client's last synchronized authoritative Preview routing. The Operator does not send `SelectedSource.Id` directly to Program.

The Program name/id are updated only by applying a synchronized authoritative snapshot after an accepted mutation. Rejected or failed mutations do not call `Apply` with a locally invented Program state. During a take the UI reports `COMMIT PENDING`; after synchronization it reports the confirmed revision. Runtime/session loss marks the presentation unconfirmed/stale and blocks further mutation until recovery.

Rapid repeated UI actions remain serialized by the existing `AsyncRelayCommand` execution guard plus `OperatorViewModel.IsBusy`; a second action cannot run concurrently while a take is in flight.

### AP-47 integration evidence

`ProductionIpcIntegrationTests.Operator_commands_cross_ControlHost_and_RuntimeHost_process_boundaries` proves Source → Preview → CUT Preview → Program and Preview → DISSOLVE Preview → Program over the real ControlHost/RuntimeHost IPC path.

`ProductionIpcIntegrationTests.Serialized_operator_preview_takes_remain_authoritative_under_rapid_actions` performs repeated serialized Preview/take cycles without delay and verifies that every confirmed Program source matches the authoritative Preview target, revisions advance, Runtime remains committed and no pending execution is left behind.

`ProductionIpcRecoveryTests.Runtime_loss_rejects_preview_take_without_changing_confirmed_program` proves that a take rejected after Runtime loss leaves the previously confirmed Program source and revision unchanged.

Existing RuntimeHost restart and Operator reconnect/resynchronization tests remain the recovery evidence for the workspace; AP-47 does not introduce a second recovery mechanism.

## AP-48 Source Bin & Live Source Tiles

AP-48 upgrades the source bank into a production source bin while preserving the same authoritative routing model. The Operator does not create source truth; it projects observations already owned by RuntimeHost, ControlHost, the Media Deck and the independent monitoring plane.

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

Selecting a source tile updates `SelectedSource`. `Set Preview` / `Ctrl+P` remains the explicit operator action that sends that selected source through the authoritative `SelectPreviewAsync` path. CUT and AUTO continue to take only the confirmed Preview source as established by AP-47.

### AP-48 acceptance evidence

- RuntimeHost snapshot exposes actual V1 format and per-source signal state;
- ControlHost source snapshot distinguishes LIVE from the currently loaded MEDIA slot;
- Client source descriptors carry format, health, Media Deck state, remaining time and file identity;
- source tiles display monitoring thumbnails plus PGM/PVW state;
- integration tests verify live health changes and Media Deck READY/PLAYING/remaining observations across real IPC;
- Operator UI policy verifies the source-bin bindings and monitoring/media projection boundaries.

## AP-49 Graphics & Overlay Operator Workflow

AP-49 adds a bounded P0 graphics workflow to the Operator without introducing a second renderer or any WPF-owned production state. The implementation is classified as **REALTIME_CRITICAL / HOST_INTEGRATION** because it extends the RuntimeHost composite path and the private management IPC seams. No public Control contract or persisted schema version changes are required.

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

AP-49 does not add durable graphics rundown persistence. The current asset and placement are RuntimeHost process state and are observable through ControlHost snapshots. A RuntimeHost restart therefore clears the loaded asset and the Operator resynchronizes to the empty graphics state. Durable graphics/rundown persistence is outside the AP-49 P0 scope.

### P1 lower third decision

The optional P1 lower-third workflow is deliberately not implemented in AP-49. The current V1 production path has no qualified text/CG renderer. Rendering lower-third text in WPF and presenting it as production truth would violate the RuntimeHost rendering boundary. A future lower-third implementation should first introduce a governed production CG/text-rendering capability rather than simulating one in the Operator.

### AP-49 acceptance evidence

- bounded graphics-asset validation covers dimensions and exact RGBA payload length;
- Runtime integration tests cover alpha, show/hide, position, scale and persistence across DISSOLVE frames;
- recording integration evidence verifies that the recorded video payload exactly equals the post-graphics Program pixels;
- real process-boundary integration verifies Operator/Client → ControlHost → RuntimeHost load, placement, show and clear state;
- graphics state changes do not advance authoritative Preview/Program routing revision;
- Operator UI policy verifies the PNG decode path, graphics controls and Client-SDK-only authority boundary.

## AP-50 Audio Operator Workflow

AP-50 adds a production-facing audio panel while preserving the existing authority boundary. The Operator does not own audio routing, meter generation or Program truth.

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

### AP-50 acceptance evidence

- unit tests cover stereo metering, gain and clipping;
- Runtime integration covers actual external/clip-style Float32 Program payload, gain, mute, silence, clipping and underrun;
- process-boundary integration covers Operator audio commands without changing production revision;
- AFV integration verifies that the Runtime audio source follows confirmed Program after source changes;
- Operator UI policy verifies stereo/master meters, AFV source, health, clip-audio state, gain/mute commands and the absence of WPF meter synthesis;
- Operator remains dependent only on `rtaime.Client`.

## AP-51 Program Output / Clean Feed

AP-51 adds a separate local Program Output window for presentation on a second Windows display without introducing another production renderer or authority path.

The Operator can:

- select any currently attached Windows display;
- start and stop the clean-feed window explicitly;
- switch the running output to another display;
- toggle fullscreen and windowed presentation;
- see a dedicated output health state and placement detail;
- continue operating the main Operator workspace independently.

The clean-feed window contains only the Program image on a black surface. It exposes no source selection, transition, graphics, audio or transport controls.

### Program truth and aspect ratio

ProgramOutputWindow binds directly to the existing OperatorMonitoringViewModel.ProgramImage. The same frozen bitmap object that feeds the Operator Program monitor therefore feeds the clean-feed presentation surface. AP-51 does not create another decode path, WPF media player, compositor or routing state.

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

The current monitoring plane is monitor-grade: 320×180 and sampled every fourth production frame. This is sufficient for the AP-51 local showcase surface and preserves hot-path isolation, but it is not a claim of full-resolution/full-frame-rate broadcast output. SDI, NDI, SRT and network streaming remain outside AP-51.

AP-51 evidence consists of:

- the existing Runtime monitoring integration proving Program monitoring originates from the actual Program frame;
- Operator UI policy checks proving the clean feed binds that same ProgramImage;
- structural checks for selectable displays, start/stop, fullscreen/windowed placement and display-change fallback;
- the existing Architecture gate proving the Operator still references only rtaime.Client.

## AP-52 Media Autoplay & End Behavior

The local Media Deck now exposes production playback policy next to the existing transport and timeline:

- Auto Play on Program enable/disable;
- Hold Last Frame, Stop, Loop and Return to IN end behavior;
- ON PROGRAM / OFF PROGRAM state;
- effective IN/OUT range;
- T-minus countdown derived from effective remaining frames;
- explicit APPLY POLICY action through the Client SDK.

The Operator remains a presentation/control surface. Program-edge detection and end behavior execute in RuntimeHost against committed Program state. The UI continuously refreshes loaded-deck observations so a Runtime-triggered autoplay transition from READY/PAUSED to PLAYING is visible without synthesizing local state.

The source tile and deck countdown use the same effective remaining range. AP-52 deliberately does not implement auto-pause when the clip leaves Program, playlist auto-advance, rundown automation or macros.

### AP-52 acceptance evidence

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
