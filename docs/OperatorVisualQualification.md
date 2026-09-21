<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Operator Visual Qualification

## Purpose

The Operator visual qualification protects responsive geometry, DPI behavior and startup presentation with deterministic tests that run on the existing Windows Required Gates. It complements the static Operator UI policy with executable layout checks instead of reproducing viewport calculations in PowerShell.

The qualification intentionally does not require pixel-identical golden images. Windows text rendering, GPU drivers, font rasterization and monitor profiles can produce harmless pixel differences even when the layout contract is unchanged.

## Qualification matrix

The following logical viewports are the supported automated qualification surfaces:

| Qualification | Logical viewport | Windows scale context | Expected workspace scale | Compact workspace | Navigation |
| --- | ---: | ---: | ---: | --- | --- |
| Reference | 1920 × 1080 | 100% | 1.00 | No | Full labels |
| Standard window | 1600 × 900 | 100% | 0.8333 | No | Full labels |
| Reference 125 | 1536 × 864 | 125% | 0.80 | No | Full labels |
| Reference 150 | 1280 × 720 | 150% | 0.6667 | No | Compact rail |
| Reference 200 | 960 × 540 | 200% | 0.60 floor | Yes | Compact rail |

The percentage is qualification metadata for the logical viewport expected from the Windows PerMonitorV2 environment. WPF device-independent layout remains the geometry authority inside the Operator.

## Automated layout invariants

tests/rtaime.Tests.Operator/OperatorVisualQualificationTests.cs executes the complete matrix against every Operator workspace:

- LIVE;
- EDIT;
- MEDIA;
- SCENES;
- COMPOSITING;
- OUTPUTS;
- HEALTH;
- SETTINGS.

For each matrix entry the test verifies:

- WorkspaceScale, Compact mode and navigation collapse thresholds;
- finite, non-negative shell columns, rows, splitters and auxiliary geometry;
- positive center workspace capacity after fixed shell chrome;
- primary and auxiliary workspace minimums fitting inside the available center container;
- lower/timeline geometry remaining inside the available body height;
- Preview/Program production workspace height remaining inside the body;
- Compact mode collapsing optional side, splitter and auxiliary presentation geometry;
- workspaces without a left region reserving no left column or splitter;
- feature-level derived sizes remaining positive.

The reference dimensions stored in OperatorLayoutSettings remain persistence coordinates. Updating the viewport to a Compact surface must not rewrite saved panel dimensions.

## Viewer and fullscreen qualification

The visual qualification exercises:

- Preview maximize;
- Program/dual viewer restore;
- center maximize and restore;
- monitor fullscreen entry and exit.

These operations are qualified as presentation-only. Restoring the view must recover the previous workspace allocation without creating or mutating production authority.

## Startup surface qualification

Startup lifecycle semantics remain covered by StartupLifecycleViewModelTests, including failure, degraded recovery and ready handoff.

The visual qualification additionally parses the productive MainWindow.xaml and verifies that:

- StartupOverlay spans the application shell without a fixed width or height;
- all seven lifecycle stages remain represented;
- no synthetic progress bar is introduced;
- the technical-details action remains present;
- layout rounding and device-pixel snapping remain enabled;
- startup animation still honors the Windows client-animation/reduced-motion setting.

The test does not manipulate AppHost, RuntimeHost, ControlHost or production state to construct a visual fixture.

## Structural workspace qualification

The productive MainWindow must continue to bind shell geometry through the centralized OperatorShellViewModel properties, including navigation, left/right regions, lower row, center minimums, auxiliary layout and the Preview/Program production workspace height.

Fixed 1920 × 1080 render dimensions are forbidden in the productive window. The reference surface remains a design and qualification coordinate system, not a hard render size.

## Static policy boundary

build/quality/Test-OperatorUiPolicy.ps1 remains responsible for static architectural and design-system contracts such as:

- custom-control ownership;
- semantic theme resources;
- PerMonitorV2 manifest binding;
- layout rounding and pixel snapping;
- absence of ungoverned WPF default controls;
- central responsive geometry seams.

Numerical viewport calculations are intentionally owned by executable Operator tests instead of being duplicated in the PowerShell policy.

## CI

Required Gates execute the visual qualification as a dedicated Quality step:

    dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter "FullyQualifiedName~OperatorVisualQualificationTests"

The complete solution test run also executes the same tests through the normal test graph.

## Screenshot evidence

Manual screenshots may be captured for showcase or release review, but they are evidence rather than CI baselines. They must use deterministic product/test content and must not become a renderer-sensitive merge gate.

If a future render harness proves stable across the supported Windows runner image, it may add coarse visibility, clipping or overflow assertions. Full-frame pixel identity remains outside the automated acceptance contract.

## Regression contract

Required Gates must fail before merge when a change unintentionally:

- returns the Operator to a fixed 1920 × 1080 render size;
- breaks Compact navigation or Compact workspace collapse;
- creates negative or overflowing workspace geometry;
- reserves a hidden workspace region;
- mutates persisted reference panel dimensions as a side effect of viewport scaling;
- removes the bounded Startup overlay or real lifecycle presentation.

Physical monitor movement and cross-GPU/font pixel identity are not inferred from these managed tests.
