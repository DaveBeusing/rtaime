<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

<p align="right">
	<img src="../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg" alt="rtaime — Real Time AI Media Engine" width="180" />
</p>

# Operator Experience Design Contract

## Decision and document authority

Design direction accepted on **2026-10-17**: a professional, media-first Live Operator with dark graphite surfaces, restrained cyan interaction accents, clearly separated Preview/Program, contextual tools and persistent operational evidence. A generic dashboard of large infrastructure cards is not the primary production interface.

The [accepted scope decision](OperatorExperienceScope.md), effective **2026-10-17**,
authorizes the full design implementation beyond the former V1 functional ceiling.
Independent analysis/scrubbing, real multitrack sequences/editing, Preview shuttle,
2D canvas/mapping, contextual Inspector extensions and an independent AI workspace
are approved **TO IMPLEMENT**. This contract defines executable acceptance targets;
documentation approval is not feature completion or visual/hardware/release acceptance. The historical design-foundation source was `7bd13506272dec071bccda23beb767da44562fac`; the [current reviewed baseline](OperatorExperienceScope.md#status-vocabulary-and-delivery-ownership) is recorded separately. Implementation must recheck the current master, relevant branches and pull requests before changing anything.

Precedence is explicit:

1. Approved product scope, architecture, contracts and [repository governance](Governance/RepositoryGovernance.md).
2. The owning capability contracts and their current implementation; [Operator UI V1](OperatorUiV1.md), [Operator Workspaces](OperatorWorkspaces.md) and the subsystem documents describe that implementation.
3. This document for the accepted experience direction, presentation requirements and interpretation of concepts.
4. Illustrative mockups, which are not capability, telemetry or pixel-parity evidence.

A conflict must be resolved explicitly, not by silently replacing a capability contract. The [experience roadmap](OperatorExperienceRoadmap.md) separates existing foundations, remaining qualification and proposed refinements. This documentation change does not modify product SemVer, layout schema 6, production/checkpoint schemas, Runtime budgets or release readiness.

## Interpreting the approved concept

Use the Live Operator concept as the visual direction: media browser, large monitors, deliberate production actions, cue context and compact output/health evidence. Retain the canonical lower-case `rtaime` name, existing logo assets and slogan. The alternative dashboard/streaming collages are not the primary shell specification.

Illustrated show names, dates, images, meters, device labels, 4K/HDR/10-bit badges, additional outputs, generative scenes and delivery services are sample content, not approved capabilities. They must not appear as live evidence without the appropriate contract and qualification. No screenshot asset is introduced by this document; it does not claim a retained pixel-exact reference image.

Preserve existing operator muscle memory and qualified geometry. The normal dual-view arrangement remains **Preview left, Program right**, even if an illustration reverses them. Do not rename persisted workspace identities or replace the navigation with a new implementation solely to resemble a picture.

## Workflow architecture

Four experience groups organize the existing eight canonical destinations. They are documentation groups, not new workspace identifiers or a migration:

| Experience group | Existing destinations | Primary operator question |
| --- | --- | --- |
| Live Operator | LIVE; SCENES for preparation and governed cue/scene operation | What is live, what is next, and which explicit action will execute? |
| Media & Timeline | MEDIA, EDIT, COMPOSITING | Which media am I preparing, and what will the proposed change affect? |
| Outputs | OUTPUTS | Where is Program/Aux going, and what is actually confirmed? |
| System & Health | HEALTH, SETTINGS | What is degraded, why, and which supported recovery action is safe? |

The earlier functional deferrals are superseded by the
[2026-10-17 scope record](OperatorExperienceScope.md#supersession-and-acceptance-checklist):
2D canvas/mapping and an independent AI workspace are approved implementation
targets. Existing output-role routing and AI controls remain the implemented
baseline until their real replacements/extensions are wired. A complete 3D stage,
new hardware drivers, formats or commercial SDKs are not implied delivery obligations.
The eight existing workspace identities remain compatible; a new AI destination
is additive and requires deliberate layout migration/tests.

Retain the same production state, ViewModels, Client commands and monitoring resources across groups. Existing docking, layout templates, Focus/Cinema and detached views are foundations to extend, not systems to rebuild.

## Live Operator composition

The visual hierarchy is Program identity and evidence, confirmed Preview, the next explicit production action, then cue/media context, followed by diagnostics.

- Keep show/project identity, selected workspace, connection/synchronization state and the existing safety state in stable shell positions.
- Give the center to the actual media. Do not let empty tracks, large metric cards, decorative padding or duplicated toolbars displace the viewers.
- Keep media/source/cue selection in the preparation region. Selection, hover and inspection never route or TAKE.
- Group CUT and AUTO with their confirmed Preview target and transition duration. Keep cue ARM/GO and TAKE SCENE distinct: they are different commands with different eligibility and consequences.
- Use the contextual Inspector for detailed editing. Quick Controls contain the existing bounded property pins, not a second parameter store.
- Keep compact Program/output/audio evidence in the production surface. Move detailed scopes, device configuration and performance histories to their existing destinations; unavailable measurements are not painted as activity.

The confirmed Program name and bus state must remain visible when optional panes collapse or a different workspace is selected. Clean Program and Cinema remain monitoring views, not physical output controllers.

### Required LIVE target and measurable acceptance

This is the **TO IMPLEMENT** composition; current LIVE uses scene/cue selection,
multiview and Live Controls. Existing screenshots do not certify the new target.

| Region | Required target | Acceptance observation |
| --- | --- | --- |
| Left | Media browser/source preparation with usable names, thumbnails, search and readiness | Selection/hover/analysis never changes routing or transport on a production deck |
| Center | Green Preview on the left, red Program on the right; equal media-canvas allocation | Same available width/height within one physical-pixel rounding tolerance; preserve each source aspect inside its canvas and label PVW/PGM without color dependence |
| Right | Selection-driven contextual Inspector, explicit draft/Apply/Discard and committed evidence | Stable selection identity; pending/rejected/stale state and units remain visible; no second property store |
| Bottom | Cue/Rundown context and deliberate ARM/GO/CUT/AUTO actions with confirmed targets | One visible Preview transport per context; Program has no Preview transport; distinct rundown/cue operations are not duplicate playback bars |
| Shell | Project/Program identity, connection state and compact permanent status | No dashboard-card primary view; detailed health/scopes stay in their task destinations |

Derive all regions from the available container, including titlebar/navigation/
status space. At **960×540 logical** size, keep Program identity, current Preview
target, deliberate live actions, critical incident access and restore/navigation
logically reachable by pointer and keyboard. Move detail into tabs/collapsible
panes/local scroll regions; do not shrink the entire text/control tree.
A collapsed Inspector or Library must have a reachable reopen/focus path.

Retain English UI, the existing logo/brand, cyan interaction and the role colors.
The reference geometry may change when its owner supplies deliberate default/
layout migration (where persisted meaning changes), source-bound geometry and
interaction tests, and the necessary focused policy update. Current geometry/
schema assertions remain active until that implementation; they are not a
permanent prohibition on the accepted target.

## Tokens, typography and geometry

[`OperatorTokens.xaml`](../src/Hosts/rtaime.Operator/Themes/OperatorTokens.xaml) remains the single source for shared numerical/color resources. [`OperatorTheme.xaml`](../src/Hosts/rtaime.Operator/Themes/OperatorTheme.xaml) and the existing custom-control family consume them. Do not introduce a competing palette or copy token values into feature XAML.

The reviewed token baseline includes:

| Role | Existing token/value | Design use |
| --- | --- | --- |
| Background / surface | `OperatorColorBackground` `#071017`; `OperatorColorSurface` `#101B24` | Quiet dark working surfaces |
| Raised surface | `OperatorColorRaisedSurface` `#16242F` | Local hierarchy without heavy shadows |
| Primary / secondary text | `#EEF3F6` / `#9AA8B4` | Readable labels and supporting metadata |
| Interaction / focus | `OperatorColorAccent` / `OperatorColorFocus` `#16CDD3` | Selection, navigation and focus, not Program truth |
| Preview | `OperatorColorPreview` `#29D38B` | Confirmed Preview role plus explicit PVW label |
| Program | `OperatorColorProgram` `#FF4D52` | Confirmed Program role plus explicit PGM label |
| Armed / warning | `#F6B84A`, separate semantic keys | Intent or warning with distinct text/icons |
| Text families | Segoe UI Variable Text / Segoe UI; Cascadia Mono / Consolas | Interface text; stable-width timecode/numeric readouts |
| Spacing / radii | Existing 4/6/8/12/16/24 spacing; panel 4, control 3 | Consistent compact rhythm |
| Standard / compact controls | 30 / 26 DIP | Preserve legibility and usable hit areas |

Shared colors can have different semantic keys; therefore green health is not Preview identity and red error is not Program identity. Always pair role/state color with a label or icon. Critical labels must not depend on the existing 8/9-DIP fine-print tokens. Audit real labels at their actual size; do not shrink all text with a whole-window scale transform.

The 1920×1080 reference, 60-DIP top bar, 92-DIP navigation, 400/340/320 persisted panel defaults, 6-DIP gaps and EDIT proportions remain the current qualification baseline. They describe nested reference allocations, not an instruction to add all widths into an oversized fixed window. Any changed default geometry needs a focused layout change and migration/qualification decision rather than an implicit documentation-driven reset.

Use the established dark titlebar and custom rtaime presentation for controls, menus, fields, dialogs, splitters and scroll surfaces. Standard WPF mechanics may underlie a custom template; visible stock styling is not the design target. Preserve platform file dialogs where the existing workflow deliberately uses them, including their focus and accessibility behavior.

## Responsive behavior and viewport clarity

Size each video canvas from its actual container and current monitor DPI. Preserve aspect ratio and clip only to that canvas. Optional panes collapse before media identity, core transport, critical incidents or restore controls are sacrificed. Scroll actual lists/properties locally; do not put a viewer inside an oversized feature root and use scrolling to hide the sizing defect.

Retain qualification at 1920×1080, 1600×900, 1536×864, 1280×720 and 960×540 logical workspaces, with 100/125/150/200% DPI coverage. Mixed physical monitor moves, taskbar/work-area constraints and display removal need separate retained hardware evidence. Persist reference layout coordinates, not temporary scaled sizes. Compact mode and Focus/Cinema must restore the prior arrangement without changing production or overwriting ordinary preferences.

### Honest image-detail contract

Fit is the composition-safe default. Fill is an explicit inspection choice that may crop; it never silently changes the output composition. Pixel inspection, free zoom, pan, ROI and channel views reuse the existing monitor presentation path and bounded geometry.

Distinguish three quantities whenever the contract supplies them: original media/Program format, delivered monitoring-frame dimensions, and physical display-target dimensions. A 100% view means one **delivered image pixel** per physical display pixel unless native-resolution delivery is independently established. It cannot recreate details already removed by sampled/downscaled monitoring. Do not label an upscaled monitoring image as native source detail or fabricate bit depth, color space, HDR or frame rate.

Preserve the existing GPU presentation path, explicit WPF fallback, high-quality scaling, integer physical target calculation and 80-ms resize settling. Avoid an outer Viewbox, chained resampling or per-frame target allocation. Inspect final resize settlement, fractional DPI edges, aspect preservation and pointer/ROI mapping. GPU/channel/ROI statistics are available only when the actual qualified path supplies them.

Optional monitoring work loses quality/rate or drops frames before it can delay Program. Independent analysis and position scrubbing are already approved by the scope
record. Implement their bounded Runtime admission, ownership, source/generation
and cancellation contracts before enabling them. A preview-only analysis session
must never seek/reuse a live production deck or create a second admission owner.
Until that real path exists, current hover remains thumbnail-only; unavailable
original detail stays explicit.

## Interaction and production safety

The shared state sequence is **local selection/draft → explicit request → pending response → synchronized authority → execution/provider evidence where supplied**. Those stages are not interchangeable. An accepted response alone is not proof of physical output delivery.

Keep existing `CanExecute`, busy, stale, role and Runtime-readiness guards. Text editing consumes editing keys before production shortcuts; key-repeat must not issue repeated live mutations. Use the existing command/shortcut registry and [Commands and Shortcut Profiles](OperatorCommandsAndShortcuts.md), including the palette's explicit Execute treatment for production actions. Do not maintain another shortcut table or invent a generic TAKE key.

Normal CUT/AUTO retain their established deliberate direct controls; adding a modal confirmation to every take would obstruct timed operation. Destructive lifecycle/configuration actions use the existing appropriate review/confirmation path. A future live-lock policy must be governed, not a decorative switch that bypasses or disagrees with backend authorization.

Local draft undo/redo, attribute transfer, timeline authoring undo and workspace restoration remain separate from live operations. Undo must never retake a previous source, replay a recording action or silently issue another production mutation. Preserve the owning editor's undo context.

Drag-and-drop communicates destination and consequence before release. Unsupported targets reject clearly. A media drop may select or prepare only through the existing command contract; dropping onto an image is never an implicit Program take. Dirty Inspector drafts need explicit Apply/Discard handling and must not be silently lost on selection changes.

## Media, cues and readiness language

The media browser must show usable names/thumbnails and meaningful availability/readiness evidence without requiring every asset to allocate a decoder. Preserve existing virtualized grid/list modes, typed search, named Smart Bins, selection-aware context menus and bounded batch operations.

The integrated preparation capability distinguishes:

- **Availability:** the asset can be found or is missing/unavailable.
- **Conditional preparation observation:** bounded representative samples passed for the recorded file/context/Runtime epoch/profile, within their validity window; this is not a whole-file or physical-device guarantee.
- **Resident readiness:** the actual deck is loaded/prerolled and the current authoritative path permits the operation.

Present the backend's status, reason, freshness and allowed retry/cancel action. Never turn catalogue presence, an expired observation or an illustration's READY label into resident readiness. Show checks are advisory evidence, not an authorization to launch. See [Media Library](MediaLibraryAssetBrowser.md) and [Show Checks and Activity](ShowPreflightAndActivity.md).

Retain distinct confirmed and draft timeline cursors, IN/OUT, cue identity and context-specific snapping. Only render waveform, transition or editing information that an owning contract actually provides. Real multitrack sequences/editing and Preview shuttle are approved TO IMPLEMENT through their own versioned models and Runtime execution, not decorative extra lanes. Show-control cues use the existing ARM/GO/CANCEL and recovery semantics; asset cue markers are not automatically show-control actions.

## Loading, failure and recovery

Every asynchronous surface needs recognizable empty, loading, available, unavailable, stale, failed and recovery presentation as applicable. An empty project offers the next safe preparation action. A failed operation shows the affected object, an understandable reason and a supported next step, with technical detail available separately.

Startup reuses the existing branded AppHost/Operator lifecycle path. Show actual stage names and readiness; do not substitute a fabricated percentage or an optimistic all-green completion. Distinguish engine unavailable from optional subsystem degraded.

Control loss disables affected mutations until authoritative resynchronization. Monitoring loss is independently labelled and does not by itself imply Control loss. Retained last frames must be visibly stale. Reconnection does not automatically repeat interrupted live commands. Recovery links and support export reuse existing services.

Persistent incidents remain visible after acknowledgement. Reminder pauses do not resolve faults or hide fatal conditions. Program bus status, Clean Program monitoring and external output-send evidence remain separate labels. A local ON AIR badge is not proof of external transmission.

## Motion, accessibility and performance

State, tally, focus and command eligibility update immediately. Shared presentation chrome may use the cancellable 150-ms opacity entrance in OperatorControlPresentation, constrained to 120–180 ms with ease-in/ease-out. Productive selection-menu chrome replaces the independent Popup fade with this path. Closing hides the popup immediately and removes its old clock. Windows client-area animation, High Contrast and the local Reduce motion preference can suppress the effect; suppression changes no command or final state. Error/loading/tally controls remain immediate and unanimated. No layout or Program-cadence animation is permitted. The positive Operator UI policy protects cancellation, duration, system/local preferences and cleanup. See [shared control contract](OperatorControlDesignSystem.md) for resources, activation and qualification limits.

Use WCAG contrast and target-size guidance as engineering references for this native desktop client, not as an untested conformance claim. Design for at least 4.5:1 normal-text contrast and 3:1 essential non-text/focus contrast. Audit all applicable states and backgrounds. Aim for at least 24×24-DIP effective pointer targets in dense desktop controls and larger primary actions; CSS-pixel guidance is not automatically native-DPI certification. Keyboard focus, automation names/roles/states, text entry, IME and screen-reader behavior must be verified in the actual custom controls.

Reusing custom drawing does not remove accessibility obligations. Retain bounded virtualization, thumbnail work, telemetry history, cancellation and deterministic disposal. Workspace switches and unrelated dock changes must not recreate monitoring sessions or retained floating subtrees. Do not add a second poller, render loop or production-state cache for visual polish.

## Acceptance and evidence

Implementation and visual acceptance are separate. Require the existing Windows/Operator qualification plus a human review of source-bound captures showing connected, disconnected, pending, rejected, unavailable, stale, compact, maximized and detached states. Record the exact commit, Windows/DPI/topology, relevant device/driver and media profile. Generated test fixtures or a successful screenshot export are not human visual acceptance or physical-output qualification.

The [scope integration map](OperatorExperienceScope.md#binding-namespace-and-numeric-allocation-map)
defines backend namespace/tag ownership; the roadmap defines delivery dependencies.
Use EXISTING, TO IMPLEMENT, IMPLEMENTED and QUALIFIED as defined in that record.
Missing human/physical acceptance stays UNVERIFIED without blocking every
independent model/implementation strand. Existing functional, performance,
accessibility, security and release blockers are not waived by this approval.

## Market references and adopted principles

The following primary sources were reviewed on **2026-10-17**. They support the described workflow observations, not a usability benchmark or a claim that rtaime matches those products. Source pages may change; recheck them when revising an observation. No third-party artwork, code, control skin or product claim is copied into rtaime.

| Primary source | Observed pattern | rtaime decision and limit |
| --- | --- | --- |
| [Blackmagic Design: Resolve Edit](https://www.blackmagicdesign.com/uk/products/davinciresolve/edit/) and [Cut](https://www.blackmagicdesign.com/products/davinciresolve/cut) | Contextual Inspector, precise time-based work, markers and keyboard-oriented operation | Keep precise media preparation and contextual editing; do not import an entire NLE scope |
| [Adobe: Source and Program monitors](https://helpx.adobe.com/premiere/desktop/get-started/source-and-program-monitor-adjustments/about-source-monitor-and-program-monitor.html) and [saved/resettable workspaces](https://helpx.adobe.com/premiere/desktop/get-started/tour-the-workspace/change-create-reset-workspaces.html) | Separate source/sequence inspection and persistent panel layouts | Preserve role clarity and recoverable layouts; an editor's Program monitor is not equivalent to live broadcast authority |
| [PIXERA: User Interface Overview](https://help.pixera.one/user-interface-/user-interface-overview) | Task modes, selection pane, central workspace, selection-driven Inspector, detachable panes | Reuse docking and selection-driven tools; implement approved 2D canvas/mapping through real governed contracts |
| [Disguise: Designer User Interface](https://help.disguise.one/designer/ui/designer-user-interface) | Distinct Stage/Feed views and timeline transport | Separate content intent from output routing/evidence; no new 3D stage or feed engine |
| [Resolume: Clips](https://resolume.com/support/en/clips) and [Screens](https://resolume.com/support/screens) | Direct clip access, previewing and explicit output-device assignment | Fast browsing and visible output targets, but retain explicit preparation/take safety rather than adopting single-click live triggering |
| [Ross Video: Vertex](https://www.rossvideo.com/products/experiential/vertex/) and [Vertex ControlView](https://ioversal.com/ManualContent/controlview.html) | Unified AV/show-control context and purpose-built operator surfaces | Prefer concise operation over infrastructure dashboards; do not imply equivalent protocols, redundancy, codecs or automation scope |
| [W3C: Non-text Contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast), [Contrast Minimum](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html) and [Target Size Minimum](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum) | Contrast and effective-target guidance | Use explicit engineering targets and test native behavior; no blanket WCAG certification |

Vendor feature descriptions are observational references. The accepted design decisions, safety boundaries and prioritization in this document are rtaime product decisions.
