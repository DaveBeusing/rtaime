<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
-->

# Showcase UX Hardening

## Purpose

This package removes visible prototype friction from the V1 funding showcase without adding product capability or moving production authority into the WPF Operator.

The scope is presentation and lifecycle quality only: layout consistency, keyboard behavior, tooltips, focus, empty/error states, DPI/reference-surface behavior, second-monitor presentation, startup focus, shutdown cleanup and controlled unexpected-error presentation.

## Interaction consistency

The Operator remains fully command-bound to the existing ViewModel and Client SDK seams. The primary workflow actions now provide concise tooltips that explain the authoritative action rather than implementation detail.

Keyboard navigation is cyclical within the main Operator window. On first presentation, focus lands on **Synchronize**, giving a deterministic keyboard starting point before production mutations are available.

Existing production shortcuts remain unchanged:

- `F5` synchronizes authoritative state.
- `Ctrl+P` routes the selected source to confirmed Preview.
- `Space` CUTs confirmed Preview to Program.
- `Ctrl+Space` dissolves confirmed Preview to Program.

Keyboard commands continue to use the same ViewModel commands and do not bypass readiness or authoritative validation.

## Empty and error presentation

A source bank with no authoritative sources now shows an explicit empty state instead of an unexplained blank region. Existing connection, command, commit, transition, recording, AI, output and health states remain the source of truth for loading or degraded presentation.

The persistent footer now separates the normal last operator event from an active error. The error badge is visible only while `LastError` contains a current failure.

Unexpected WPF dispatcher exceptions are presented in a controlled fatal dialog. Before shutdown, the Operator attempts to write a diagnostic report below the current user Local Application Data directory in `rtaime/logs`. Failure to create the report does not mask the original error.

## Startup and shutdown

Startup does not invent a connected state or automatically mutate production. The window opens with deterministic keyboard focus on Synchronize and retains the existing explicit synchronization workflow.

Main-window closure is intercepted once so the Operator finishes asynchronous ownership cleanup before the final close:

1. stop and close local Program Output;
2. detach Media Deck observation;
3. dispose monitoring;
4. dispose the Media Deck;
5. dispose Operator ViewModel polling resources;
6. allow the final window close.

This prevents the application lifetime from racing asynchronous cleanup.

## DPI and second-monitor behavior

The existing PerMonitorV2 and 1920×1080 qualification remain authoritative. The hardening package does not introduce fixed pixel positioning into the main Operator workspace.

The Program Output controller continues to use the qualified display-topology handling: selected-display removal falls back to the primary display in windowed mode and reports fallback state. The clean feed remains independent from Operator controls.

## Evidence

The Operator UI policy gate verifies cyclic keyboard navigation, deterministic initial focus, shared tooltip presentation, explicit Source Bin empty state, separate last-event/error presentation, awaited graceful shutdown and controlled dispatcher-exception reporting.

All existing Client-only authority, DPI, monitoring, media, graphics, audio, recording, health and AI constraints remain in force.

Manual showcase qualification should confirm the complete workflow at 1920×1080 and at 125%/150% scaling, including a secondary-display connect/disconnect cycle.

## Out of scope

This package adds no new production feature, routing mode, media format, graphics renderer, recording format, AI model, streaming protocol, rundown automation or authority path.
