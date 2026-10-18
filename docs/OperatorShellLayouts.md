<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Operator shell layouts and compatibility

LIVE embeds the existing Library, shared role viewers, contextual Inspector and
Rundown/Timeline. It hides EDIT's diagnostic cards. Preview is left and Program
right with equal dual-view allocation; the available central media allocation
is derived from the viewport and saved side-panel widths. The shell does not
scale the complete control tree. Client geometry excludes native caption/borders; the existing center scroll viewport supplies the settled viewer height, including any visible scrollbar allocation. These temporary measurements are never persisted.

The LIVE action strip keeps the confirmed Preview/Program identities and existing
SET PVW / CUT / AUTO commands separate from selection and media playback. Preview
owns its transport; the Timeline embedding hides its duplicate Play/Stop pair in
LIVE through PreviewPlaybackVisibility. Other Timeline embeddings retain their
established playback controls. Show/project identity, synchronization, global
readiness, Program identity and incident navigation remain in the top shell.

## Destinations and feature connections

OperatorWorkspaceRegistry supplies navigation groups, canonical layout defaults
and IDs to the existing command catalogue. MEDIA, EDIT, LIVE, SCENES, COMPOSITING,
OUTPUTS, HEALTH and SETTINGS remain stable. GRAPHICS and SYSTEM retain their
existing aliases. MULTIVIEW embeds the existing source/scene/live-control surfaces;
AI embeds the existing test, approval and activation controls. Neither destination
introduces a monitoring reader, decoder, model, production command registry or
Runtime authority. Mapping is a navigation group name; an unavailable editor is
not registered as a productive tab.

Future contributions require a productive view at the existing composition
connection, a unique stable registry ID, canonical defaults and qualification.
There is no eight-destination array/viewport ceiling. Layout storage is bounded
to 32 supported/inactive destination entries and the existing 16 named templates.

## Compact presentation and restore

At compact logical viewports, a stored DUAL preference renders one bus at a time.
Both identities and immediate PVW/PGM switching remain in the LIVE strip.
Library and Inspector buttons reveal one temporary pane at a time and can hide it
again. Compact Scenes, Outputs and Settings expose VIEW/DETAILS buttons for the existing auxiliary tools instead of hiding them behind a zero-width column. The compact LIVE lower region keeps 200 DIP and a 280-DIP Rundown column so deliberate cue actions remain reachable. Returning to a larger viewport restores the reference widths and dual
presentation. Temporary pane allocations and compact bus choice are not serialized.
The existing maximize, fullscreen, Focus/Cinema, dock/float/return and reset paths
remain presentation-only. The existing keyboard router leaves focused-button Enter/Space to that explicit button; the shell strip reuses LiveConsoleInput for repeat/double-click handling.

Viewer and property details scroll locally; fixed control targets do not shrink
with the window. Existing taskbar-workarea, removed-display and negative-coordinate
recovery remains in OperatorMonitorGeometry/OperatorPanelRestore. Mixed physical
DPI and monitor removal still require retained hardware qualification.

## Schema 7 migration

| Input | Read behavior | Save behavior |
| --- | --- | --- |
| v2-v6 | Preserve valid geometry, collapse/viewer state, named templates and window/floating placement; fill missing defaults and clamp invalid dimensions | Explicit user actions write normalized v7 through the existing serialized temporary-sibling/atomic replacement path |
| v7 | Restore registered destinations; retain valid inactive IDs within the finite budget | Same bounded v7 representation |
| Future layout or nested docking version | Use safe presentation defaults and surface the unsupported-file reason | Original file retained; writes blocked |
| Oversized file | Do not read more than the 1 MiB admission bound; surface the reason | Original file retained; writes blocked |
| Malformed supported preferences | Safe defaults; loading writes nothing | An explicit successful save may replace the malformed file |
| Interrupted temporary write | Load the committed destination, never the incomplete sibling | Subsequent atomic save uses the established serialized owner |

Registered entries are considered before inactive IDs so unrecognized entries
cannot evict a known saved layout. Unknown workspace navigation falls back to LIVE;
it does not activate an unsupported feature. Portable template import/export keeps
format version 1 and records layout schema 7. Unsupported future layout/template
versions are rejected explicitly. Older executables cannot consume schema 7.

## Qualification boundaries

OperatorDockLayoutTests (extended in OperatorShellPersistenceTests.cs) covers supported-version, future-file, malformed
version, oversized-file and interrupted-temporary-write cases. The existing
OperatorVisualQualificationTests covers the existing logical size matrix. WorkspacePresentationTemplateTests (extended in OperatorShellWorkspaceTests.cs) covers registry aliases, five LIVE size targets, compact bus/pane state and bounded layout transactions. These cases run in the existing focused Dock/Template filter before the broad suites.

OperatorCombinedVisualQualification checks actual compiled LIVE viewers, equal
dual allocation, viewport fit, one visible playback/TAKE context and 100 real
workspace/float/dock/restore cycles using the same monitoring, Inspector, selection
and panel instances, with no monitor-panel unloads during Inspector moves. The stress phase starts in a stable 1600x900 docked layout and restores every original workspace layout. Only changed dock groups are rebuilt; unrelated groups and floating windows retain their presentation. Compact, ratio and Focus/Cinema topology changes retain the existing full restore path. Existing source-correlated PNG/JSON capture retains logical
size and injected layout DPI. The additional 80-case matrix loads the real MainWindow with controlled Client responses in no-data, connected, busy and stale states across all destinations at 1920x1080 and 960x540. It verifies retained routing, explicit button keyboard ownership and teardown. These are qualification targets; actual execution,
source SHA, failures and five Required Gate results are recorded in the PR.

Software layout-DPI injection is not physical mixed-monitor or touch certification.
Controlled snapshots are presentation evidence; production commands and actual
media output remain qualified by the independent Client/host/Runtime suites.

The persistent lower Rundown uses the existing control in console presentation: selection, PREPARE, RUNDOWN GO, HOLD, confirmed state/timing, prepared identity, live eligibility and recovery remain on the same owner. Full authoring and previous/next navigation remain available in Multiview → RUNDOWN. The compact lower region retains its existing 200-DIP budget; authoring rows do not impose their 140-DIP list minimum on the embedded console. No cue execution or production timing changes.

The existing shortcut router leaves unmodified list navigation and deletion to the focused selector. Enter/Space in Library selection cannot invoke global AUTO or Preview playback; explicit cue-list and focused-button handling retains the shared Live Console input rules. The controlled MainWindow journey checks this boundary without issuing production mutations.
