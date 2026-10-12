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

## Real XAML tree startup qualification

`OperatorXamlStartupTests` runs the productive `App.InitializeComponent()` resource dictionaries, both compiled Preview/Program views and real `MainWindow` on a dedicated STA thread with a running WPF Dispatcher. It records first-chance `XamlParseException` and `ArgumentException` with the active composition target. BAML's outer `MainWindow.xaml:line 1` is not a reliable source location; independent compiled child-view construction identifies the nested failing view.

The window is shown, waits for `ContentRendered`, switches through all eight workspaces and verifies that named shell panes remain loaded and visible panes allocated by the shell have positive geometry. The fixture requests a 1600 × 900 minimum windowed viewport; diagnostics record the actual runner viewport and compact-layout state. Intentional zero-width shell allocations remain governed by the existing responsive layout contract. Both monitor trees must remain present. The fixture uses the existing view-model composition and productive controls; it only redirects UI layout/session persistence to temporary stores. Closing is cancelled synchronously before any asynchronous layout save; the fixture awaits the real window teardown, including its clean-session persistence marker, before shutting down the application and Dispatcher. Dispatcher shutdown uses Send priority after application teardown, matching application exit instead of continuing graphics callbacks after the window lifetime ends. The resource fixture overrides App's queued startup callback so it owns exactly one window; the separate process test exercises the unmodified application startup.

The same STA application lifetime also activates the compiled ProductionMacroControl with a real ProductionMacroViewModel and a snapshot-only test transport. It verifies confirmed CUT and DISSOLVE text updates through PropertyChanged, active OneWay Run bindings, read-only source state, neighboring display-property metadata and an empty WPF binding-error trace. No production command is executed to qualify a status display.

A separate process test launches the built Windows Operator executable against unique unavailable endpoints, requires its `operator.window-shown` log event, rejects crash/XAML error evidence and requests normal window close. The process must log exit and return zero. No headless flag or substituted visual tree qualifies this launch.

A source guard scans all productive XAML (including dictionaries and inactive templates) for non-whitespace text directly in panel children. It accepts real whitespace and intended text inside `TextBlock`. WPF runtime construction remains the authority for markup-extension, resource-type and compiled-tree errors. The suite also compiles the productive monitor, comparison and scope shader constants with their existing VS/PS 4.0 and CS 5.0 entry points before window startup, so a syntax error in a surface loaded by WPF cannot remain hidden behind a successful managed build. This is shader compilation evidence, not a rendering-quality or hardware qualification.

Required Gates run this focused suite in CI before template/AI integration subsets and in Quality alongside existing startup, visual/DPI and media viewport checks:

    dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter "FullyQualifiedName~OperatorXamlStartupTests" --logger "console;verbosity=detailed"

This proves real Windows composition, layout presence and application/window lifetime. It does not certify production media, hardware availability, pixel output or physical mixed-monitor DPI.

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


## Media viewport visual quality qualification

`MediaViewportVisualQualityQualificationTests` extends the existing Operator visual/DPI qualification without introducing a second screenshot framework. The managed qualification separates deterministic presentation geometry and physical-pixel mapping from renderer-sensitive raster evidence.

The automated matrix covers 720p, 1080p, 2160p/4K and synthetic 8K sources; 16:9, 4:3, portrait, square and 2048 × 858 non-standard aspect content; smaller, native and oversized viewport relationships; Fit, Fill, 100% physical-pixel inspection and representative zoom; and 100%, 125%, 150%, 175% and 200% Windows DPI scales.

The suite qualifies these invariants:

- Fit remains aspect-correct and entirely contained by the physical viewport.
- Fill remains aspect-correct, covers the physical viewport and uses centered intentional cropping.
- 100% maps one source pixel to one physical target pixel independently of WPF DPI scale.
- a one-physical-pixel target error and an accidental second scaling stage are independently detectable.
- the productive monitor template contains one media `Image` surface, uses high-quality bitmap scaling and device-pixel snapping, and contains no nested `Viewbox`.
- rapid resize/DPI changes do not repeatedly adopt transient render targets; after the settle interval the final target is adopted once.
- a stable 60 fps-equivalent sequence of 3,600 presentation requests retains one render-target revision, providing a deterministic resource-churn regression bound.

These checks intentionally avoid pixel-identical GPU golden images in normal CI. Driver-sensitive raster evidence belongs to the existing reference-platform qualification strategy; normal Required Gates prove the renderer-independent geometry, scaling-path declaration and resource-lifetime contracts.

Failure messages and matrix case names expose the source class, viewport, DPI and presentation mode at the failing assertion. The underlying geometry and render-target types retain the exact source/target dimensions, scale, quality and scaling-path semantics needed for diagnosis.

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


## Shared GPU target resize and recovery qualification

The focused Required Gates filter includes `GpuDrawingSurfaceTests` in both CI and Quality:

    dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter "FullyQualifiedName~OperatorXamlStartupTests|FullyQualifiedName~ProductionMacroOperatorTests|FullyQualifiedName~GpuDrawingSurfaceTests" --logger "console;verbosity=detailed"

The productive STA fixture reuses its single Application/Dispatcher lifetime and assigns the productive MainWindow as the application's primary window. Secondary GPU windows are exercised after that window has rendered all workspaces; a final CompositionTarget render pulse proves the primary window continues rendering after their teardown. It first creates a real Hardware D3D11 device and requires E_INVALIDARG (0x80070057) from a shared BGRA8 descriptor with width uint.MaxValue. Its log includes the actual Vortice.Wpf assembly informational version. This proves a native invalid-descriptor boundary; it does not reconstruct the missing production-session descriptor.

Each of GpuMonitorPresentationSurface, GpuMediaCompareSurface and GpuScopeAnalysisSurface is shown in an actual Windows window. Qualification requires zero-size deferral with no allocation, one allocation for a burst of resize requests, unchanged-target reuse during redraw, collapse/restore, window hide/show, minimize/restore, front-buffer transition injection, injected device-removal HRESULT followed by successful new-device recovery, unload/reload and window-close cleanup. Every attempted shared target must satisfy the descriptor contract, graphics diagnostics other than the single injected device-removal failure fail the check, and created/released target counts must return to equality. The Windows runner's Microsoft Basic Render Driver executes the real D3D11/D3D9Ex resource APIs; it is software adapter evidence, not qualification of a physical GPU. A native clear/copy/staging-map verifies red BGRA8 pixel output from the shared target; it does not certify the source-media shader or monitor scan-out.

A scale-injection surface uses the same native target owner at 100/125/150/200%, verifies ceiling conversion for fractional bounds and defers a target above the conservative device limit. Allocation failure is injected before native creation, after descriptor validation. Qualification requires full failure/HRESULT diagnostics, no retries from resize/redraw, exactly three transition retries and no allocation storm after exhaustion. Injected front-buffer loss, scale changes and allocation failure are identified explicitly in logs; these are not physical display removal, actual per-monitor DPI messages or hardware device removal.

Policy unit cases cover fractional/one-pixel/maximal targets; zero, negative, NaN, infinity, multiplication overflow/underflow and oversize bounds; and every incompatible descriptor field (mips, arrays, format, samples/quality, usage, binding, CPU access and sharing). Existing viewport tests remain responsible for Fit/Fill/100% source geometry and single-stage physical scaling.

| Evidence | Automated boundary | Physical status |
| --- | --- | --- |
| Descriptor validation and DPI arithmetic | Deterministic unit matrix | Covered by software checks |
| Productive controls, window resize/minimize/unload/close | Actual Windows WPF and Hardware D3D11/D3D9Ex interop | Runner adapter only |
| BGRA8 target contents | Actual GPU pixel readback | Source-media crispness/scan-out UNVERIFIED |
| Front-buffer loss/recovery, changed scale | Deterministic event/scale injection with real native targets | Display reconnect/mixed-monitor DPI UNVERIFIED |
| Allocation failure and three-retry budget | Injected E_INVALIDARG with real device lifetime | Physical device removal/reset UNVERIFIED |
| Original reported crash descriptor | Absent from original crash evidence | Original trigger UNVERIFIED |

Reference-desktop procedure:

1. Run the focused filter after the canonical Release developer build and retain its detailed log. Launch the normal Operator with existing host/session diagnostics enabled.
2. Show deterministic native-resolution pixel-grid, thin-line, checkerboard and aspect markers through the existing monitoring path. Compare Fit, Fill and 100% inspection in Preview, Program and Clean Program; verify no stretching, duplicate DPI scaling, unintended crop or softened 100% lines.
3. Rapidly resize and dock/undock panes, collapse/show scopes and comparison, minimize/restore repeatedly, and close/reopen Clean Program while the main window remains open. Require bounded create/release counts and stable fallback state.
4. Move between physical 100/125/150/200% monitors, including non-integer viewport edges. Record actual adapter, DPI and descriptor; compare CPU/WPF and GPU aspect/100% behavior.
5. Lock/unlock the desktop, disconnect/reconnect the display and exercise approved driver/device-reset qualification. Require explicit unavailable state, bounded transition recovery and no unhandled Dispatcher failure.
6. Collect the Operator HostLog GPU events and crash evidence with session correlation. For an E_INVALIDARG, use the exact pre-allocation descriptor, requested bounds, adapter and HRESULT to classify the native rejection. A caught exception, a passing build or injected recovery alone does not establish the original crash cause.

Physical matrix results must be recorded with Windows build, adapter/driver, monitor DPI, media format, presentation mode and observed events. Unavailable physical cases remain UNVERIFIED.
