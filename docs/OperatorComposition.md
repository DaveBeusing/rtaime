<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Operator composition and feature ownership

The integrated [scope baseline](OperatorExperienceScope.md) authorizes the approved
Operator extensions. This composition change separates existing presentation and
projection responsibilities; it does not implement a new workspace, decoder,
transport, production capability or look and feel.

## Lifetime and connections

MainWindow constructs one set of existing feature owners and one
[typed connection map](../src/Hosts/rtaime.Operator/OperatorComposition.cs).
The map and Library context borrow references. They create no subscriptions,
monitoring readers, timers, leases or additional snapshots and do not dispose
borrowed services. MainWindow's existing asynchronous close owns shutdown.
OperatorDockController reparents the same view instances; it does not reconstruct
their models.

| Feature connection | Productive presentation | Existing owner / extension point |
| --- | --- | --- |
| Library | [OperatorMediaLibraryView.xaml](../src/Hosts/rtaime.Operator/OperatorMediaLibraryView.xaml) and its code-behind | OperatorComposition.Library supplies MediaPool, MediaDeck, Timeline, workspace states, NDI discovery and the existing collapse command |
| Inspector | [OperatorInspectorControl.xaml](../src/Hosts/rtaime.Operator/OperatorInspectorControl.xaml) and its code-behind | OperatorComposition.Inspector / InspectorView; existing dependency properties, numeric validation and draft transaction handlers are reused |
| Shared selection | Both Library and Inspector, plus timeline/compositing selection | [MediaSelectionContext](../src/Hosts/rtaime.Operator/MediaSelectionContext.cs) delegates to the single MediaPoolInspectorViewModel owner; it stores no selection copy |
| Library projection | Grid/list and search/Smart Bins | [MediaLibraryProjection](../src/Hosts/rtaime.Operator/MediaLibraryProjection.cs) owns the existing bounded, ordered descriptor projection and filtered collection; MediaPool coordinates refresh/filter/selection |
| Inspector drafts | Existing contextual editors | [InspectorDraftHistory.cs](../src/Hosts/rtaime.Operator/InspectorDraftHistory.cs), InspectorAttributeEditing and InspectorAttributeSets retain local revision-qualified drafts and existing Apply commands |
| Viewer | PreviewViewer, ProgramViewer, OperatorMultiviewControl | OperatorComposition.Viewer is the existing OperatorMonitoringViewModel; MediaDeck retains the existing Preview transport |
| Live | LiveSceneCueControl, LiveControlsControl, ShowControlCueStackControl | OperatorComposition.Live is the existing OperatorViewModel; MainWindow retains Rundown and ShowControl owners |
| Timeline | MediaTimelineControl | OperatorComposition.Timeline is MediaDeck.Timeline; the existing MainWindow timeline selection/drop bridge remains |
| Outputs | OutputRoutingHealthControl beside one OutputCanvasEditorControl with OutputCanvasStageSurface | OperatorComposition.Outputs remains the single OutputRoutingHealthViewModel owner, which owns one draft/evidence editor over the existing OperatorControlClient; no independent authority or polling loop |
| Health | HealthCenterControl | OperatorComposition.Health is the existing HealthCenterViewModel |
| AI | Current governed AI controls in MainWindow / Inspector | OperatorComposition.Ai is the same existing OperatorViewModel and commands; an independent AI workspace is approved future implementation, not a placeholder menu |

All paths in the presentation column are under src/Hosts/rtaime.Operator.
No second asset database, command registry, polling loop or production authority
is introduced. Operator -> Client -> ControlHost remains the mutation path.

## Names, binding and focus boundaries

MainWindow retains root shell names, panel identities and layout. Library names
(MediaSearchBox, MediaAssetGrid, MediaAssetList, Smart Bin and batch controls)
belong to the Library view's own namescope. Consumers resolve them from that
view; they must not assume Window.FindName crosses UserControl boundaries.

Library bindings explicitly find OperatorMediaLibraryView.Context, not the
nearest Window. MainWindow supplies this context once after InitializeComponent.
It survives floating, re-docking and temporary bounded qualification hosts.
Item templates retain their item DataContext. The context menu action bridge
resolves the original item's PlacementTarget; no disconnected-menu ancestor
binding or second command instance is required.

The Inspector already has its own namescope and explicit dependency properties.
OperatorComposition supplies the same model, ControlState, MediaDeck, QuickControls
and collapse command directly. They do not depend on inherited DataContext or a
Window ancestor during the detached interval between panel hosts. Its loaded/unloaded subscription follows the same Inspector
model during moves and is removed at teardown. MainWindow and OperatorPanelWindow
continue using the existing shared keyboard router. Ctrl+F selects MEDIA, reveals
or floats the existing TOOLS panel as necessary, then calls the Library's own
FocusSearch. Text-entry suppression, Escape/Return handling and draft scope remain
owned by the existing controls/router.

Grid/list visibility restores the same selected keys. SelectionChanged skips
an already equal selection, preventing a second visit/projection when WPF
reapplies the primary binding or a panel is moved. Hover cancellation, typed
drag payloads, batch limits and all existing mutation guards remain in their
feature owner.

## LIVE action hierarchy and operator safety

The LIVE workspace keeps the existing left **Preview** (green) and right
**Program** (red) viewer identities, shared monitoring resources and lower
Rundown/Timeline surface. No new render target, Control/Runtime command,
monitor reader, polling cadence, panel layout schema or execution authority
is introduced. The 1920×1080 reference and responsive 1600×900 and
960×540/960×500 presentations remain governed by the existing
`OperatorShellViewModel` geometry and `OperatorDockController`.

### Primary actions

The `LiveActionStrip` remains a named `RtaimeScrollViewer` for compatibility
with current Operator startup and accessibility qualification, but **horizontal
and vertical scrolling are disabled**. Its measured, single-row grid keeps
the essential controls and role information within the available center
workspace instead of placing TAKE commands at the start of a very long row.

Its hierarchy from left to right is:

1. **SET PVW**, **CUT**, **AUTO** (the original `SetPreviewCommand`,
   `CutCommand` and `DissolveCommand`). All three retain their
   existing `CanExecute` enforcement and disabled-state details.
2. **NEXT / PVW** shows the confirmed `PreviewSourceName` target,
   separate from local library/selected-source intent; immediately beside
   it `LiveTakeEvidence` shows explicit **UNVERIFIED**, **PENDING**,
   **REJECTED**, **UNCONFIRMED TAKE**, **BLOCKED** or eligible/confirmed
   evidence as produced by the existing live projection. The full
   `PreviewTakeDetail` remains available as the focused control tooltip.
   The next observed Rundown cue remains in the concise second text line
   of the same action row and is **not** represented as Program.
3. **PVW** and **PGM** switch the local compact viewer presentation via
   `SelectCompactBusCommand`, not the authoritative production route;
   **VIEW** opens the local secondary menu. A compact direct **RESTORE**
   retains independent layout recovery when the menu or a panel is closed.

The existing top bar owns the continuously shown confirmed Program identity,
global readiness, network send indication and active recording indication.
Its Program source text now also carries the `LiveProgramIdentity`
qualification name, avoiding a second permanently displayed Program name
inside LIVE. Preview and Program viewer receipt evidence stays within
their existing monitors; the action strip's eligibility text cannot
reclassify retained pixels as current monitoring or successful physical
output. Transition duration editing remains in existing Live Controls;
there is no extra duration editor in the LIVE row.

### VIEW menu

The **VIEW** control opens a themed `RtaimeContextMenu` backed directly
by the **same MainWindow/Shell command instances**. From top to bottom:

- **Show / hide Media Library:** `Shell.ToggleLeftPanelCommand`
- **Show / hide Inspector:** `Shell.ToggleRightPanelCommand`
- **Restore saved workspace layout:** `Shell.ResetLayoutCommand`
- **Restore dual Preview and Program viewers:** `Shell.RestoreViewersCommand`
- **Show checks and activity:** existing `OpenShowChecksCommand`
- **Open Outputs details:** `Shell.SelectWorkspaceCommand("OUTPUTS")`
- **OBSERVED · DETAILS:** read-only local selection, distinct Preview and
  Program monitoring states, network send state, recording state and
  Program audio L/R levels. These details are available by opening VIEW,
  not repeated as permanent text in the main action row.

The menu binds to its button's captured MainWindow instance via
`PlacementTarget.Tag` and uses project-owned menu and separator controls.
Opening or closing the menu does not call production commands, does not
change selected Preview/Program identities, and does not register another
command router or selection store. Command enablement and pane/docking
restore semantics come directly from existing owners.

The **VIEW** button is focusable and opens through normal Enter/Space
activation. While the LIVE actions have focus, the **Menu** key or
**Shift+F10** also opens VIEW; repeated keys and text editors are ignored.
The existing `LiveConsoleInput` suppression of repeated Enter/Space and
central Operator shortcut registry remain authoritative. The navigation
rail's **Show checks** and the top-level readiness action provide
independent incident access even if the LIVE menu is closed. The compact
bus buttons and RESTORE action remain keyboard reachable without
horizontal scrolling at 960×540.

### Validation boundaries

`LiveActionHierarchyTests` checks the command hierarchy, custom menu
bindings, persistent TAKE evidence and optional-status placement, source
of authority, keyboard entry and a Windows WPF presentation journey at
1600×900 and 960×540. The nearby
`LiveConsoleWindowQualification`,
`LiveConsoleUncertainTakeTests`, `OperatorShellWorkspaceTests` and
`OperatorCombinedVisualQualification` retain their existing safety,
focus, compact, selection, dual-view and shutdown responsibilities.

Software WPF measurements do not prove physical mixed-monitor DPI,
GPU scanout, completed recording-file finalization, physical transmission
or externally presented Program. Qualification requires exact compiled
source, pinned SDK, observed CI/Windows execution, focus and viewport
evidence and separate physical test reports; missing or blocked results
remain **UNVERIFIED** rather than being called PASS.

## Registration and compatibility

The current OperatorCommandCatalog already groups commands and workspace
contributions. OperatorKeyboardCommandRegistry remains the sole shortcut router;
OperatorWorkspaceNames and OperatorDockLayout retain their canonical identities.
No new registration mechanism or reflection-based plugin discovery is needed
for this extraction. Later feature changes integrate at the typed connections
and their owning view/catalog section.

No public IPC/protocol/schema, serialized layout, production state, Source
identity, version prefix or shortcut ID changes. Existing finite catalogue,
batch, draft and resource bounds are preserved.

## Qualification

Extend the existing OperatorXamlStartupTests real BAML/STA journey, not a second
application harness. MediaLibraryBrowsingTests covers the actual Library view,
shared projection/selection, custom text controls, context menus, 4096-item
virtualization and unchanged Preview/Program identities. OperatorDockingWindowTests
uses the existing three cycles for Library, Inspector and monitor floating and checks retained model, selection,
commands and monitoring-owner identities. The real close checks floating windows,
Inspector subscription cleanup and drawing-surface subscription balance.

Pane/viewer assertions run in all forty matrix cases, avoiding a duplicate warmup.
All newly realized native surfaces are retained for final teardown checks.
The existing combined qualification covers 100/125/150/200% injected layout DPI,
compact geometry, owned windows, focus, Cinema and restore. Existing Client/host
integration tests retain snapshot/reconnect and monitoring isolation coverage.
The disconnected WPF fixture cannot qualify connected production latency,
physical mixed-monitor DPI, device removal or actual live GPU leases. These
remain UNVERIFIED until the appropriate connected/reference execution.

Build and all five Required Gates must be evaluated at the final complete source
SHA; pending is not PASS. Exact execution evidence and any failures belong in the
PR validation record. The earlier scope-baseline Quality allocation/recovery
failures remain historical findings until assessed on the current source; they
are not repaired by moving XAML.
