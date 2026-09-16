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

Transport loss and timeout conditions on the control path also mark the presentation stale. Existing source and status information may remain visible for operator context, but it is explicitly non-commandable until synchronization succeeds again.

Monitoring state is independent. A lost or stale monitoring stream affects visual observation only and does not invalidate an otherwise current authoritative ControlHost snapshot.

## Visual monitoring boundary

AP-29 provides live Preview and Program monitoring through a dedicated RuntimeHost monitoring pipe. Production bulk media is not sent through ControlHost or RuntimeHost management IPC.

Preview visual monitoring follows the authoritative Preview source identity received through the control snapshot. Program visual monitoring is emitted from the actual Runtime post-composite Program output, including V1 transition and visual-layer results.

The qualified V1 monitor stream is deliberately sampled and downscaled. It is bounded and loss-tolerant: monitoring frames may be dropped under pressure before Program execution is ever delayed.

See `docs/OperatorMonitoringPlane.md` for transport, backpressure and failure-isolation details.

## Styling and layout

The Operator uses `Themes/OperatorTheme.xaml` for reusable dark-surface, typography, button, source-bank and semantic state resources. Preview and Program use distinct semantic accents. The window is resizable and uses minimum dimensions rather than the original fixed bootstrap layout.

## Verification

`build/quality/Test-OperatorUiPolicy.ps1` verifies the primary UI architectural and UX guardrails, including:

- reusable theme loading;
- Preview/Program semantic resources;
- source-bank binding;
- keyboard command declarations;
- live Preview/Program image bindings;
- stale/busy/connection presentation state;
- retained Client-only Operator project dependency.

`build/quality/Test-OperatorMonitoringPolicy.ps1` verifies the monitoring-plane separation, bounded/loss-tolerant behavior and prohibition on management-IPC pixel transport.

The existing Required Gates remain authoritative for build, architecture, contracts, unit, integration, security, provider smoke and packaged end-to-end qualification.
