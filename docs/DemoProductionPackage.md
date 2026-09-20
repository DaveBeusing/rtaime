<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Demo Production Package

## Purpose

Demo Production Package adds one reproducible Product Showcase package to the WPF Operator. The package prepares the existing V1 workflow through already-qualified Client, ControlHost, RuntimeHost, Media, Audio, Graphics and AI seams.

`Open Demo Production` is not a new production-authority subsystem and is not the software-release package format. It does not replace ControlHost authority, durable state, host lifecycle, update orchestration or the existing Production Specification.

## Bundled package

The package manifest is `src/Hosts/rtaime.Operator/DemoAssets/demo-production.package.json` with schema marker `rtaime.demo.production-package/1`.

The package declares Input A as Program source, Input B as Product Clip source, a bundled 1920x1080p50 MP4 with H.264 video and AAC stereo 48 kHz audio, deterministic IN/OUT and two cue points, Auto Play on Program, Hold Last Frame, a 12-frame DISSOLVE, unity/unmuted clip audio, a pre-rendered rtaime-logo/lower-third PNG, and the Visible AI Showcase Integration Person Segmentation Highlight.

The MP4 and PNG are text-safe base64 bundle resources. The manifest carries SHA-256 digests of the decoded bytes. On activation the Operator verifies those bytes before materializing them below the current user's Local Application Data at `rtaime/demo/v1`. Matching files are reused; corrupt or mismatched bundle bytes fail closed.

## One-click flow

The Operator toolbar exposes `Open Demo Production`. Activation synchronizes authoritative state, resolves Input A/B, restores Program to Input A when needed, materializes verified assets, opens the Product Clip on Input B, recreates IN/OUT and cue points, returns transport to IN, configures Auto Play/Hold Last Frame, confirms Input B audio, loads the pre-rendered lower third, enables Visible AI Showcase Integration AI, sets the Operator DISSOLVE duration to 12 frames, selects Input B as Preview, then resynchronizes and validates the final state.

The ready state is Program = Input A, Preview = Product Clip/Input B, media cued at IN, Auto Play armed, clip audio ready for AFV, lower third loaded, AI enabled and a 12-frame DISSOLVE configured. The package does not automatically TAKE Preview to Program.

## Lower-third boundary

Graphics & Overlay Operator Workflow intentionally did not introduce a general text/CG renderer. Demo Production Package does not reverse that decision. The bundled lower third is a pre-rendered RGBA/PNG asset containing the rtaime logo and lower-third design and uses the existing RuntimeHost graphics overlay path.

The current V1 compositor gives the explicit Operator graphics overlay precedence over the Visible AI Showcase Integration dynamic AI highlight. The package therefore loads the lower third but starts it hidden while AI is enabled. It is immediately ready for SHOW/HIDE, but Demo Production Package does not claim simultaneous lower-third and AI-highlight visibility.

## Reproducibility and failure behavior

A clean build/publish output contains the manifest and both base64 assets because the Operator project copies `DemoAssets/**` to output. Activation may be repeated in one host lifecycle; existing Product Clip markers are cleared and recreated so cue points do not accumulate.

Activation reports FAILED instead of READY when a required source, asset/hash, media, audio, graphics, AI enable, routing command or final confirmation fails. AI execution remains failure-isolated under Visible AI Showcase Integration.

## Acceptance evidence

- manifest/unit validation of all required package fields and decoded asset hashes;
- PNG and MP4 signature validation;
- real AIHost + RuntimeHost + ControlHost integration using the bundled MP4;
- MP4/H.264/AAC/1080p50/stereo-48-kHz probe evidence;
- deterministic IN/OUT, two cue points, Auto Play and Hold Last Frame;
- Program Input A / Preview Input B;
- unity/unmuted Input B audio;
- graphics loaded but initially hidden;
- AI showcase enabled;
- repeated preparation produces the same TAKE-ready state;
- Operator UI policy verifies the one-click action remains on Client SDK / Media Deck seams.

## Out of scope

- generalized customer Production Package format;
- arbitrary package import/export;
- package persistence/migrations;
- automatically starting or installing service hosts;
- a new CG/text renderer;
- playlists, rundown automation or macros;
- autonomous TAKE decisions.
