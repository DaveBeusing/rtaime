<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Operator Experience Implementation Roadmap

## Purpose and status

Turn the [Operator Experience Design Contract](OperatorExperienceDesign.md), accepted on 2026-10-17, into dependency-ordered improvements of the existing Operator. This is a planning and traceability document, not an implementation request, release approval or a claim that the concept already matches the running application.

Reviewed integration source: `master` at `7bd13506272dec071bccda23beb767da44562fac`, including integrated media asset preparation. Existing repository capability documentation and relevant changes were inspected. No new build, Windows visual acceptance, hardware measurement or interaction benchmark was executed to establish this documentation baseline.

Before subsequent implementation, inspect current master, relevant remote branches and all applicable open/closed pull requests; continue usable work instead of duplicating it. Start a new technical branch only from current master when no reusable work exists. Preserve [repository governance](Governance/RepositoryGovernance.md), [versioning](Versioning.md), the existing five Required Gates and subsystem authority. Documentation changes do not allocate a new product version or change layout/production schemas.

## Capability and document ownership

“Existing” below means present in the reviewed repository path/documentation. It does not mean complete visual acceptance, physical support or production readiness. Revalidate the relevant path when implementation begins.

| Area | Existing foundation and owner | Remaining design obligation |
| --- | --- | --- |
| Production shell | [Operator UI V1](OperatorUiV1.md); [Workspaces](OperatorWorkspaces.md); shared Operator tokens/theme | Consolidate media-first hierarchy without replacing eight workspace identities |
| Preview / Program | Existing role controls, Client commands and [Monitoring Plane](OperatorMonitoringPlane.md) | Keep role, selected intent, pending authority and execution evidence unambiguous in every layout |
| Image inspection | DPI-derived target, GPU/WPF fallback, zoom/pan/ROI and channel presentation documented in Operator UI V1 | Show delivered monitoring detail honestly; qualify scaling and fallback instead of promising lost source detail |
| Workspace recovery | Schema-6 layouts, docking, retained detached panels, named templates, Focus/Cinema | Preserve restoration, focus, resource identity and physical work-area recovery |
| Media Library | [Media Library](MediaLibraryAssetBrowser.md): virtualized grid/list, typed queries, named Smart Bins and bounded batches | Improve discoverability and state presentation; no duplicate asset store or decoder |
| Asset preparation | Integrated representative-sample preparation, retry/cancel and Show checks | Keep availability, conditional observation and resident readiness separate; stale evidence cannot become READY |
| Timeline / cues | [Layered Timeline](LayeredTimeline.md); [Show Control](ShowControlCueSequencing.md) | Preserve draft/confirmed cursors and distinguish asset markers from executable show cues |
| Inspector | [Context Inspector](ContextInspector.md); existing draft editing/attribute transfer | Make selection context, units, pending values and Apply/Discard explicit without replaying live edits through undo |
| Commands | [Commands and Shortcut Profiles](OperatorCommandsAndShortcuts.md) | Reuse palette, registry and focus guards; do not implement a second shortcut editor |
| Outputs / audio | [Output Routing](OutputRoutingHealth.md); [Runtime Status Bar](RuntimePerformanceStatusBar.md) | Separate Program bus, Clean Program monitoring, Program/Aux provider evidence and recording evidence |
| Health / incidents | [Health Center](HealthCenter.md); [Show Checks and Activity](ShowPreflightAndActivity.md) | Keep recovery actionable and incidents persistent; acknowledgement is not resolution |
| Startup | [Application Startup](ApplicationStartup.md) | Preserve branded real-stage startup and safe recovery, without a second bootstrap or fake percentage |
| Qualification | [Operator Visual Qualification](OperatorVisualQualification.md); [Build and Test](BuildAndTest.md) | Extend existing source-bound evidence; distinguish software tests, human visual acceptance and physical qualification |

## Preserve existing behavior before refinement

The following are already foundations, not new projects: commands/shortcut profiles, local draft undo, typed attribute transfer, portable workspace templates, docking, Focus/Cinema, named Smart Bins, read-only show checks and bounded asset preparation. An older overview saying a complete shortcut editor or all undo is absent must be read together with the later owning capability document. This does not imply timeline-wide live undo or unrestricted editing support.

Independent position-based hover scrubbing remains unavailable. Existing hover/focus reuses monitoring thumbnails and must not open or seek a production deck. New full-resolution inspection, editable waveforms, reverse shuttle, multi-object live transactions, unrestricted processing graphs, 3D stage mapping and new device/protocol support require their own verified contracts and scope decisions.

Current reference geometry and schema 6 remain unchanged. Four workflow groups are an information architecture, not four replacement serialized workspaces. Cyan does not replace green Preview or red Program. Proposed cosmetic motion does not override the current no-decorative-Storyboard policy.

## Delivery sequence

### Establish a connected visual and interaction baseline

Capture the actual current Operator using the existing qualification harness and approved reference environment. Cover LIVE, MEDIA, EDIT, COMPOSITING, OUTPUTS, HEALTH, SETTINGS and SCENES, plus startup/recovery. Retain exact source, environment, DPI, source media and availability evidence. Mark unavailable hardware clearly.

Inspect the journeys Prepare → Preview → CUT/AUTO, scene/cue ARM → GO, output failure → diagnosis, and workspace detach → restore. Count unnecessary steps and observe where names, focus, pending state or media detail are obscured. Do not infer usability from screenshots alone or promote an unreviewed capture to visual acceptance.

Acceptance: a retained baseline with explicit findings, reproducible journeys and identified owning views/tests. Existing lifecycle, timing/resource and physical-support findings remain open unless separately resolved. Documentation approval does not repair them.

### Consolidate shell hierarchy and semantic presentation

Depends on the retained baseline. Refine shared tokens and existing shell/view composition only where the findings require it. Preserve canonical navigation identities, common command instances, monitor roles and reference/persisted geometry. Use stable Program/Preview identity, compact health and clear active-context labels. Remove duplicate visual controls only after verifying every existing workflow remains reachable.

Likely implementation surface: `src/Hosts/rtaime.Operator/MainWindow.xaml`, its code-behind, `Themes/OperatorTokens.xaml`, `Themes/OperatorTheme.xaml` and the existing workspace views. Locate the actual affected feature files before editing; do not create a replacement shell.

Acceptance: no visible stock-control inconsistency in the changed surfaces; selected/pending/live/stale states are distinguishable without color alone; core actions and restore paths are reachable at compact sizes; no new polling, authority or monitoring path.

### Qualify container-first media inspection

Depends on shared shell geometry. Extend the existing monitor presentation rather than adding a renderer. Confirm aspect-safe Fit, explicit Fill cropping, physical-pixel inspection, resize settling and truthful source/delivered/target metadata. Preserve channel/ROI availability rules and the explicit GPU/WPF fallback. Do not promise original-resolution detail from a sampled stream.

Acceptance: pointer and ROI coordinates remain stable across fractional DPI, zoom and pan; resize settles without per-frame target churn; retained/detached monitor identities and final resource counts remain bounded; monitoring pressure cannot delay Program. Full-resolution inspection backend work is excluded until separately approved.

### Consolidate preparation and live-operation journeys

Depends on hierarchy and capability-state clarity. Improve existing Media Library, Inspector, Timeline, Scene/cue and Quick Control presentation. Surface the actual eligibility reason and target before mutation. Keep draft undo scoped to its editor, batch operations bounded, context menus selection-aware and production shortcuts suppressed during text entry.

Present asset preparation as representative conditional evidence, not a playback guarantee. Keep explicit Prepare/Retry/Cancel and actual resident-deck readiness distinct. Do not add an optimistic launch-ready aggregate.

Acceptance: mouse and keyboard paths reach the same commands; selection, hover, drag, workspace switches and restore do not mutate Program; rejected or stale requests remain visibly unconfirmed; no live operation is replayed through undo; disconnected and partial-failure paths have a safe next action.

### Consolidate output, startup and recovery communication

Can follow hierarchy while preparation work proceeds in separate views; coordinate changes to shared badges and the shell status bar. Reuse existing output/audio/recording projections, Health Center, notifications, Show checks and startup services.

Acceptance: the UI separately identifies local Program, monitoring availability, external-send evidence, recording confirmation and authoritative readiness. Unknown/stale telemetry never becomes a green metric. Control loss and monitoring loss have different effects. Critical incidents survive acknowledgement; support export and supported recovery are discoverable. Startup uses real stages rather than simulated progress.

### Complete combined acceptance

Depends on all changed interaction surfaces. Extend the existing Windows Operator regression and visual evidence, not a parallel test framework. Review connected/live, unavailable, failure/recovery, compact and detached modes on the same candidate source. Include loading, dirty drafts, partial batches, keyboard focus and shutdown disposal.

Acceptance is recorded separately for functional tests, visual review, accessibility interaction and reference hardware. A failed or unexecuted category remains FAIL or UNVERIFIED; passing screenshots or hosted geometry do not clear physical output or long-soak requirements.

## Validation contract

Use [Build and Test](BuildAndTest.md) and the current `.github/workflows/required-gates.yml` as executable sources. The baseline commands are:

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
./build/quality/Test-OperatorUiPolicy.ps1
dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

Run the smallest affected tests first, then broader validation required by the change. The existing Operator startup/combined visual qualification, workspace/template, shortcut, media browsing/preparation and pointer/monitoring tests are extension points. Do not weaken timeouts, container/resource limits, mutation assertions or lifecycle drain requirements to make visual work pass.

| Review dimension | Required cases |
| --- | --- |
| Layout | 1920×1080, 1600×900, 1536×864, 1280×720 and 960×540 logical surfaces; no accidental schema writes |
| DPI / topology | 100/125/150/200%; fractional coordinates; real work areas; removed/mixed-DPI displays separately qualified |
| Command safety | Editing focus, repeated keys, palette activation, stale/busy/disconnected state, rejected response, confirmed Preview target |
| State honesty | Missing asset, expired preparation, loading/preroll, stale monitor, unavailable metric, local Program versus external output |
| Restore | Built-in/docked/tabbed/floating layouts; Focus/Cinema/fullscreen; off-screen recovery; cancelled preference I/O |
| Performance | Bounded realized containers/thumbnail work; retained monitor identity; allocation/resource behavior; deterministic cancellation/disposal |
| Accessibility | Contrast at real text sizes; effective hit targets; visible focus; automation names/roles/states; keyboard/IME and screen-reader review |
| Release evidence | Exact-head CI, Quality, Security, Provider Smoke and Packaged E2E; no inheritance of a predecessor's PASS |

For documentation-only changes, review relative links, named paths, source citations, scope/status consistency and the actual diff. Do not state that build/tests ran unless exact-source execution evidence is available. Opening a pull request starts the existing workflows; queued/running checks are not PASS.

## Change and document maintenance

Keep changes small and technically scoped: shared presentation, workspace integration, tests, documentation and any necessary policy adjustment should have clear review boundaries. Reuse an applicable branch/PR and synchronize it with current master; never overwrite unrelated capability work. Product-facing descriptions must use technical names and retain canonical branding.

Update the owning document when implementation changes behavior, then update this roadmap's evidence/status and the design contract only for an explicitly accepted design change. Do not copy entire capability specifications into this roadmap. Altering persisted layout structure requires a deliberate schema/migration decision; changing display allocations alone does not.

The design baseline is complete when it is discoverable from the existing documentation, capability ownership is explicit, deferred features are not presented as implemented and subsequent work can choose one bounded improvement without redoing the architecture or inventing product authority.
