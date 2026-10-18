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
| Outputs | OutputRoutingHealthControl | OperatorComposition.Outputs is the existing OutputRoutingHealthViewModel |
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
Those are preserved. Its loaded/unloaded subscription follows the same Inspector
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
moves the actual Library and Inspector and checks retained model, selection,
commands and monitoring-owner identities. The real close checks floating windows,
Inspector subscription cleanup and drawing-surface subscription balance.

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
