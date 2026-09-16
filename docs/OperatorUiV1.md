<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Operator UI V1

## Scope

The V1 Operator is the WPF reference client for human interaction with the authoritative ControlHost. It is a presentation and interaction surface only. It does not own production authority, Runtime execution, media processing, persistence truth, AI admission, recording truth or provider state.

The Operator depends only on `rtaime.Client`. All production mutations continue to cross the versioned remote-control path and are accepted or rejected by authoritative ControlHost state.

## Control surface

The professional V1 control surface provides:

- explicit connection, revision and synchronization state;
- distinct Preview and Program presentation with source identity;
- a source bank with explicit selection;
- Set Preview, CUT and AUTO/DISSOLVE controls;
- configurable DISSOLVE duration in frames;
- Runtime, timing and input state;
- AI, recording, audio peak and visual-layer state;
- command in-flight, rejected, failed and resynchronized presentation states;
- a persistent last-event and error/rejection footer.

Controls are disabled whenever the presentation snapshot is stale, synchronization is in flight, Runtime is not `READY`, or no source is selected.

## Keyboard operation

| Shortcut | Action |
| --- | --- |
| `F5` | Synchronize authoritative state |
| `Ctrl+P` | Set selected source to Preview |
| `Space` | CUT selected source to Program |
| `Ctrl+Space` | AUTO/DISSOLVE selected source to Program |

Keyboard commands invoke the same ViewModel commands as the visible buttons. They do not bypass readiness or authoritative validation.

## Recovery and stale-state behavior

A `RemoteHostSessionChangedException` marks the current presentation snapshot stale and initiates a full authoritative resynchronization. While stale or resynchronizing, mutation commands remain unavailable. After a successful resynchronization, the Operator reports the restored revision and requires the human operator to repeat the interrupted production command.

Transport loss and timeout conditions also mark the presentation stale. Existing source and status information may remain visible for operator context, but it is explicitly non-commandable until synchronization succeeds again.

## Visual monitoring boundary

AP-28 does not transport or render production media. The Preview and Program areas intentionally display source identity and management metadata only, together with the explicit message `Monitoring unavailable until AP-29`.

No production bulk media is sent through the management Named Pipe. A non-authoritative, bounded and loss-tolerant monitoring plane is reserved for AP-29.

## Styling and layout

The Operator uses `Themes/OperatorTheme.xaml` for reusable dark-surface, typography, button, source-bank and semantic state resources. Preview and Program use distinct semantic accents. The window is resizable and uses minimum dimensions rather than the original fixed bootstrap layout.

## Verification

`build/quality/Test-OperatorUiPolicy.ps1` verifies the primary AP-28 architectural and UX guardrails, including:

- reusable theme loading;
- Preview/Program semantic resources;
- source-bank binding;
- keyboard command declarations;
- explicit AP-29 monitoring boundary;
- absence of fake video controls;
- stale/busy/connection presentation state;
- retained Client-only project dependency.

The existing Required Gates remain authoritative for build, architecture, contracts, unit, integration, security, provider smoke and packaged end-to-end qualification.
