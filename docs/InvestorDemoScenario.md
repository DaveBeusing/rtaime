<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Investor Demo Scenario & Acceptance

## Purpose

This document defines the single V1 funding-showcase scenario and the evidence required to call that scenario demo-ready.

The demo is intentionally a production-shaped architectural proof. It uses the real ControlHost, RuntimeHost, AIHost, Client SDK, Operator, Media Deck, graphics, audio, recording, monitoring and health paths already present in the product. It does not introduce a second demo-only authority path.

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

### 1. Start and readiness

Double-click `Start-rtaime-Showcase.cmd`.

The Operator opens after the managed service topology is ready. Use **Synchronize** to make the authoritative connection/revision state visible before presenting production actions.

Expected evidence:

- Control/Runtime are connected and commandable.
- Runtime Health is visible.
- no terminal interaction occurred;
- no service was manually started.

### 2. Open Demo Production

Select **Open Demo Production**.

Expected prepared state:

- Input A is confirmed Program.
- Product Clip / Input B is confirmed Preview.
- the bundled SSD-style local media clip is loaded through the Media Deck path.
- deterministic IN/OUT and Product Intro / Product End Cue points are present.
- Auto Play on Program is enabled.
- end behavior is Hold Last Frame.
- Product Clip Audio is unity/unmuted.
- the pre-rendered logo/lower-third Graphics asset is loaded and ready.
- Person Segmentation Highlight AI is enabled through the governed path.

The action prepares Preview but does not automatically TAKE it to Program.

### 3. Media Deck, Timeline and Cues

Scrub the Timeline and demonstrate the confirmed playhead.

Select the Product Intro and Product End Cue points and return to the intended take position.

Expected evidence:

- Timeline seek is deterministic.
- IN/OUT remains visible.
- Cue selection moves through the existing Media Deck path.
- remaining/countdown presentation remains consistent with the effective range.

### 4. Preview, TAKE and Program

Confirm Product Clip on Preview and perform a CUT or TAKE to Program.

Expected evidence:

- the Program source changes only after the authoritative command is accepted;
- the clip starts automatically when it reaches Program;
- Program and Preview tallies remain distinct;
- Program monitoring follows Runtime output rather than local selection.

### 5. DISSOLVE

Route the alternate source to Preview and use **AUTO PREVIEW → PROGRAM** with the prepared DISSOLVE duration.

Expected evidence:

- DISSOLVE uses the confirmed Preview source;
- transition state is visible;
- Program commits only authoritative Runtime output.

### 6. Graphics

Show and hide the prepared logo/lower-third asset.

Expected evidence:

- Graphics are composited in RuntimeHost;
- Program monitoring reflects the post-composite frame;
- graphics controls do not own render authority.

When the explicit graphics layer is visible, the AI highlight may be reported as suppressed according to the existing layer precedence.

### 7. Audio / AFV

Show the AFV source, stereo/master meters and confirmed mute/gain state.

Expected evidence:

- AFV follows confirmed Program;
- Audio meters are Runtime observations;
- audio controls cross the normal Client/Control/Runtime path.

### 8. AI showcase

Use **AI ON** and **AI OFF** to demonstrate Person Segmentation Highlight and controlled fallback.

Expected evidence:

- provider/status/inference time/person-region/confidence information is visible;
- inference executes through AIHost;
- disabling or failing AI does not interrupt Program continuity.

### 9. Recording

Start Program Recording, run a short production action, then stop Recording.

Expected evidence:

- REC state and elapsed time are visible;
- the finalized recording path is shown;
- recorded content is the Runtime-owned Program video/audio/graphics result.

The V1 reference artifact remains the deterministic `.rtaime-recording` format. The demo does not claim MP4/MOV/MXF delivery.

### 10. Program Output on display 2

Select the second display, start Program Output and switch it to fullscreen.

Expected evidence:

- display 2 contains only the clean Program surface;
- Operator controls remain on the primary display;
- aspect ratio is preserved;
- removing the selected display produces the defined windowed fallback state.

The current Program Output uses the bounded monitoring plane and is showcase/monitor grade, not a claim of SDI/NDI/SRT broadcast output.

### 11. Runtime Health

Show the Runtime Health / Performance HUD.

Expected evidence:

- Engine, Control, Runtime, Media and Provider evidence is visible;
- frame time, dropped frames and uptime are visible;
- unavailable GPU utilization or VRAM telemetry remains UNVERIFIED instead of false PASS.

### 12. Stop

Stop Recording/Program Output if still active and close the Operator.

When the showcase launcher owns the service lifecycle, closing the Operator triggers graceful managed host shutdown. No service-stop command is required from the presenter.

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

A funding-demo run is accepted only when the automated Required Gates are green and these visual checks have been completed on the actual presentation workstation.

## Explicit non-goals

The showcase does not claim or add:

- SDI, NDI, SRT or network streaming;
- replay, playlist auto-advance or macros;
- complex routing, advanced audio mixing, multiview or a generalized title/CG engine;
- multiple AI models or autonomous production decisions;
- Multi-GPU scheduling;
- remote control or user-management workflows.

Those capabilities remain outside the V1 funding-showcase acceptance boundary.
