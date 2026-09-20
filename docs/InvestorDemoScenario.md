<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Product Showcase Scenario & Acceptance

## Purpose

This document defines the single V1 Product Showcase scenario and the evidence required to call that scenario demo-ready.

Product identity for the presentation is **rtaime** (spoken **“realtime”**), **Real Time AI Media Engine** — **Production-grade real-time AI media platform.** Canonical naming rules are maintained in [ProductIdentity.md](ProductIdentity.md).

The Product Showcase is intentionally a production-shaped architectural proof. It uses the real ControlHost, RuntimeHost, AIHost, Client SDK, Operator, Media Deck, graphics, audio, recording, monitoring and health paths already present in the product. It does not introduce a second demo-only authority path.

## Entry point

A packaged Windows release contains:

`Start-rtaime-Showcase.cmd`

The presenter starts the showcase by double-clicking that entry point. No terminal commands are required.

The root entry point delegates to `tools\Invoke-InvestorDemo.ps1`, which:

1. verifies or starts the existing managed host lifecycle;
2. lets ControlHost supervise RuntimeHost and AIHost;
3. waits for runtime readiness;
4. launches the packaged Operator with the exact lifecycle endpoints;
5. waits for the Operator to exit;
6. stops the lifecycle only when the launcher started it.

No terminal interaction, No JSON editing and No manually started services or manual service restarts are part of the demo procedure.

## Demo sequence

**Target duration:** approximately five minutes for the complete continuous run.

### 1. Launch

Double-click `Start-rtaime-Showcase.cmd` in the installed release root. No terminal interaction is part of the presentation.

### 2. Confirm authoritative readiness

Use **Synchronize** and confirm that Control/Runtime are connected, the revision is visible and production commands are available. The presenter must not need to start or restart a service manually.

### 3. Open Demo Production

Select **Open Demo Production**. Input A becomes the confirmed Program baseline and Product Clip / Input B becomes confirmed Preview. The action prepares Preview but does not automatically TAKE it to Program.

### 4. Show the Source Bin

Confirm the visible Input A and Input B/Product Clip sources, source type/format, Preview/Program tallies and health state.

### 5. Show the local SSD-style media source

Confirm that the bundled local media clip is loaded through the Media Deck path, including H.264/AAC metadata and the deterministic effective range.

### 6. Scrub the Timeline

Scrub the Timeline and demonstrate that the confirmed playhead follows the existing Media Deck seek path.

### 7. Demonstrate Cue points and IN/OUT

Select the Product Intro and Product End Cue points, show IN/OUT, and return to the intended take position. Remaining/countdown presentation must stay consistent with the effective range.

### 8. Confirm Preview

Show Product Clip as the authoritative Preview / next-take source. Local source selection must remain distinct from confirmed Preview.

### 9. TAKE / CUT to Program

Use **CUT PREVIEW → PROGRAM**. Program must change only after the authoritative command is accepted.

### 10. Demonstrate autoplay and end behavior

Confirm that the clip starts automatically when it reaches Program and that the prepared **Hold Last Frame** behavior is visible at the effective end of the clip.

### 11. Demonstrate DISSOLVE

Route the alternate source to Preview and use **AUTO PREVIEW → PROGRAM** with the prepared DISSOLVE duration. Transition state must remain visible and Program must reflect Runtime-owned output.

### 12. Demonstrate Graphics

Show and hide the prepared logo/lower-third Graphics asset. Program monitoring must reflect the RuntimeHost post-composite frame. When the explicit graphics layer is visible, the AI highlight may be reported as suppressed according to the existing layer precedence.

### 13. Demonstrate Audio / AFV

Show the AFV source, stereo/master meters and confirmed mute/gain state. AFV must follow confirmed Program and the meters must remain Runtime observations.

### 14. Demonstrate AI

Use **AI ON** and **AI OFF** to demonstrate Person Segmentation Highlight. Provider, status, inference time, person-region count and confidence remain visible. Disabling or failing AI must not interrupt Program continuity.

### 15. Start Recording

Start Program Recording and confirm REC state plus elapsed time. Recording remains Runtime-owned and includes Program video, audio and graphics.

### 16. Show Program Output on display 2

Select the second display, start Program Output and switch it to fullscreen. Display 2 contains only the clean Program surface while Operator controls remain on the primary display. Aspect ratio is preserved. A selected-display disconnect must use the defined windowed fallback.

The current Program Output uses the bounded monitoring plane and is showcase/monitor grade, not a claim of SDI/NDI/SRT broadcast output.

### 17. Show Runtime Health / Performance

Show Engine, Control, Runtime, Media and Provider evidence together with frame time, dropped frames and uptime. Unavailable GPU utilization or VRAM telemetry must remain UNVERIFIED rather than false PASS.

### 18. Stop cleanly

Stop Recording and Program Output if active, confirm the finalized recording path, then close the Operator. When the showcase launcher owns the service lifecycle, closing the Operator triggers graceful managed host shutdown with no service-stop command from the presenter.

The V1 recording remains the deterministic `.rtaime-recording` reference artifact; the showcase does not claim MP4/MOV/MXF delivery.

**Continuous-run acceptance:** all 18 steps complete without developer configuration changes, terminal commands, JSON editing, manually started services, manual process restarts or unresolved error states that require presenter explanation.

## Automated acceptance

Required Gates provide two layers of automated evidence.

The standard solution and integration suites already cover the production capabilities used in the scenario, including:

- bundled Demo Production preparation across real process boundaries;
- Media Deck autoplay/end behavior and Preview-to-Program transitions;
- CUT/DISSOLVE authority semantics;
- Graphics composition;
- AFV/Audio observations and control;
- Program Recording;
- governed AI execution and failure continuity;
- Operator UI authority and UX policy;
- managed host lifecycle readiness/restart/stop.

The scenario is traceable to existing executable evidence:

| Demo area | Primary automated evidence |
| --- | --- |
| One-click packaged bootstrap | `build/showcase/Test-PackagedInvestorDemo.ps1` |
| Demo Production preparation / packaged media state | `DemoProductionPackageIntegrationTests` |
| Timeline, cues, autoplay and end behavior | `MediaAutoplayProductionIpcIntegrationTests` plus the Demo Production integration |
| Preview/CUT/DISSOLVE authority | `ProductionIpcIntegrationTests` and `V1EndToEndProofTests` |
| Graphics composition | `GraphicsOverlayIntegrationTests` |
| Audio / AFV | `AudioOperatorWorkflowIntegrationTests` |
| Program Recording | `RecordingOperatorWorkflowIntegrationTests` |
| AI showcase and continuity | `AIShowcaseIntegrationTests` |
| Runtime Health evidence semantics | `RuntimeHealthPerformanceHudIntegrationTests` |
| Program Output display selection/fallback | Operator UI policy plus manual physical-display acceptance |

Packaged E2E additionally installs the generated release bundle and executes the same showcase launcher in bounded one-shot qualification mode:

`install → managed lifecycle → Operator authoritative synchronization → readiness evidence → Operator exit → graceful lifecycle stop`

This proves that the packaged demo does not depend on repository binaries, terminal configuration or manually started services.

## Manual visual acceptance

The following remain intentional human visual checks because CI does not provide a representative investor-demo workstation/display topology:

- complete 1920×1080 Operator layout review;
- 125% and 150% DPI visual review;
- actual display-2 selection/fullscreen Program Output presentation;
- physical display disconnect/fallback;
- subjective legibility of monitoring, meters, Cues and status surfaces.

A Product Showcase run is accepted only when the automated Required Gates are green and these visual checks have been completed on the actual presentation workstation.

## Explicit non-goals

The showcase does not claim or add:

- SDI, NDI, SRT or network streaming;
- replay, playlist auto-advance or macros;
- complex routing, advanced audio mixing, multiview or a generalized title/CG engine;
- multiple AI models or autonomous production decisions;
- Multi-GPU scheduling;
- remote control or user-management workflows.

Those capabilities remain outside the V1 Product Showcase acceptance boundary.
