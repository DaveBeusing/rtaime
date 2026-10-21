<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Integrated Operator experience qualification

## Scope and source baseline

This register records **combined** Operator acceptance after the
2026-10-17 [product decision](OperatorExperienceScope.md), without reclassifying
individual feature merges as an approved software release.

- Integration source read: `master` `c250f27b091079a19a8d045646d6b46e1930de51`.
- All eleven input domains are on that master. Relevant merged changes include
  shell/layout #297; media monitoring #298; Live/cue #296; Inspector #295;
  mixer #292; Health #294; governed AI #299; library/analysis #300/#304;
  sequence model, native admission, editor and shuttle #291/#302/#306/#307/#308/#309/#310;
  outputs/mapping #287/#301/#303; and compositing #293.
- No unmerged competing feature PR or remote feature branch was found at the
  integration pre-check. This baseline is **not** an automatically approved
  release candidate.
- Product build target 0.5.40, SDK 10.0.401, .NET 10, existing WPF and x64.
  Layout schema 6; saved Show/Sequence and Output Canvas formats remain
  compatible with their existing explicit versions. Sequence preparation
  `rtaime.sequence-preparation.v1`, native shuttle
  `rtaime.sequence-shuttle.v1` and output canvas `1.0` are distinct
  capabilities. They do not grant additional production authority.

The initial exact-master [Required Gates 39328851264](https://github.com/DaveBeusing/rtaime/actions/runs/39328851264)
showed Security PASS, CI/Quality/Provider Smoke FAIL and Packaged E2E
SKIPPED. All three failed managed gates reported a shared C# parsing
error in `SequenceFrameCompositionIntegrationTests` line 137.
The shared source test was corrected on integration PR #311. Results on
this older master **cannot** be carried forward as PASS.

## Connected functional journey evidence

All entries below distinguish an implemented route from a complete
cross-feature, actual-output qualification. A new test is **PENDING**
until the exact-head Windows runner reports a pass.

| Journey | Real producer and existing checks | Combined qualification boundary |
| --- | --- | --- |
| Cold start, native local import, independent frame analysis and scrutiny | `ExternalControlIntegrationTests.Operator_experience_import_analyze_multitrack_save_prepare_shuttle_preserves_Program_authority` (new real-host journey); `IndependentMediaAnalysisIntegrationTests`; `MediaLibraryBrowsingTests` | Real local pixel payload and media-source identity are covered; native delivery and analysis do not select Preview or Take. **PENDING exact-head** |
| Media prepare, real A/B Preview, explicit CUT/AUTO | `PreparedMediaHandoffIntegrationTests`, `MediaAutoplayProductionIpcIntegrationTests`, `LiveConsoleWindowQualification` and existing production authorization regressions | Existing A/B Preview/Program path is separate from the new sequence reader. No fabricated sequence Take; **PENDING combined proof** |
| Four-track sequence editing, waveform, save, native sequence prepare and shuttle | `SequenceTimelineEditorTests`, `SequenceTimelineWaveformTests`, `SequencePreparationTransportIntegrationTests`, new connected native journey, `SequenceFrameCompositionIntegrationTests` | Actual saved revisions, two native owner offsets, existing decoded RGBA/PCM composition primitive. No committed **sequence Preview/Program sink** or authorized sequence Take; complete journey **UNVERIFIED** |
| Cue and Rundown GO/FOLLOW/HOLD, stale/lost authority | `LiveConsoleWindowQualification`, `ShowReadinessOperatorTests`, current Show Control/Rundown behavioral and production-IPC tests | Existing genuine commands/observations; simultaneous multi-feature fault/recovery path not newly inferred; **PENDING combined proof** |
| Canvas map, Validate, Save, Apply, actual Runtime sink evidence | `OutputCanvasRealHostTests`, `OutputCanvasWorkspaceQualification`, `OutputCanvasViewportTests`, production OutputRouting tests | Authored/saved revision differs from active Runtime render revision. Hardware output scan-out **UNVERIFIED** |
| Audio mixer and governed AI activation/fallback during Program | `AIShowcaseIntegrationTests`, `AudioMixerWorkspaceQualification`, Runtime audio production/fault tests | Runtime-based AI input/output and audio identity; physical real-time mixed-load, monitoring/prog scan-out and GPU headroom **UNVERIFIED** |

The added real-host journey deliberately correlates an imported MP4
catalogue ID/SHA-256, actual independent RGBA observation, one saved
four-track video/audio revision, two distinct native source-offset owners,
exact preparation identity, Runtime shuttle generation, and an unchanged
committed Program snapshot. It asserts the actual contractual
`OutputState=UNAVAILABLE`, not a fictional sequence Preview picture.
It also checks that an explicit later saved revision cannot continue to
use an older staged native owner. That test is not a substitute for
source-bound Take or simultaneous physical output samples.

`SequenceFrameCompositionIntegrationTests` composes decoded GPU pixels
and PCM using the existing processing providers, but the current code
does not route that composed group through an authorized sequence
Preview/Program output on a committed cadence. A library-level GPU
readback alone does **not** qualify the complete Program sink.

## One productive Window and ownership

`OperatorExperienceIntegrationQualification.VerifyWindowAsync`
executes inside the independent **Visual** real-window qualification process
of `OperatorXamlStartupTests.Real_MainWindow_and_viewers_load_render_all_workspaces_and_close_on_STA`.
That process retains one Application/Dispatcher lifetime and shared Owner identities
for its complete visual/sequence/output/health journey. Other composition, interaction,
docking and GPU qualifications execute in their own WPF testhost processes to avoid
the previous single-fixture cumulative deadline failure. It asserts:

- The one retained Operator ViewModel, media selection, monitoring, Inspector,
  output evidence, Timeline and Health owners are shared by the composed
  feature views, not replaced on workspace switching.
- Compiled Sequence Editor, legacy Deck, Output Canvas and governed AI
  surfaces bind to the expected one authoritative local presentation owner.
- Editing remains disabled when that deliberately disconnected test
  environment has no saved sequence or ControlHost.
- The actual timeline mode controls switch Sequence/legacy views without
  creating a second transport; OUTPUTS/AI/HEALTH remain reachable, Program
  and Preview identities do not change, and CUT remains unauthorized.

This is an executable **real WPF composition** check, but uses a
disconnected/safe fixture. It does not pretend to have exercised live
ControlHost state, an actual screenshot review or a Program Take.
The unchanged combined STA lifetime, teardown, rendering subscription
and native GPU target checks remain active without a longer deadline.

## Visual, DPI and accessibility acceptance matrix

| Evidence class | Automated software route | Release/physical status |
| --- | --- | --- |
| LIVE/MEDIA/EDIT/SCENES/COMPOSITING/OUTPUTS/AI/HEALTH/SETTINGS presence | Shared productive WPF startup and workflow owner assertions; 50 geometric shell observations plus connected domain-specific tests | **PENDING exact-head Windows checks** |
| 1920×1080 to 960×540 logical viewport | `OperatorCombinedVisualQualification` and `OperatorShellStateQualification` | Real WPF logical layout; manual typography/contrast/content review **UNVERIFIED** |
| Injected 100/125/150/200% DPI | Existing geometry/visual target tests and actual-scale metadata | Injection is not physical cross-monitor DPI movement |
| Native Preview/Program monitoring pixels, Fit/Fill/100% | `GpuDrawingSurfaceTests`, `MediaViewportVisualQualityQualificationTests` and real native readback | Monitor delivery may be downsampled; original source fidelity, physical scan-out and mixed-DPI are **UNVERIFIED** |
| Tabbed/floating, Focus/Cinema, compact, IME/keyboard/Automation | `OperatorDockingWindowTests`, QoL/shortcut/Inspector tests and new same-owner WPF assertions | Automated focus and roles only; real screen reader, touch, pen and physical IME/monitor hotplug **UNVERIFIED** |
| Empty/Pending/Reject/Stale/Recovery | Real Client/ControlHost, Inspector, library, notifications and failure suites | End-to-end cross-workspace manual review of these states **UNVERIFIED** |
| Native ten-minute/mixed 50/59.94, GPU/SSD latency, decoder quarantine, lease drain | Existing bounded reference media and failure qualification | No new measurements are invented; retained physical reference-platform NO-GO is not cleared |

CI and Quality may retain
`rtaime-operator-visual-<job>-<run>-<attempt>` with
`combined-ui.json`, per-group `interaction-ui-<group>.json` records,
fail-closed aggregate `interaction-ui.json`, and five shell/twenty
owned-window PNG captures. Every actual image requires a human review.
D3DImage video pixels may be absent in WPF capture. Retain exact source,
runner, Windows build, adapter/driver and actual/injected DPI before a
manual/physical approval. This document claims **no human visual sign-off**.

## Viewport geometry and lifecycle regression scope

The Operator workspace implementation retains the existing monitoring and production controls. The focused UI regression additions qualify source-space behavior without claiming physical monitor equivalence:

| Test or qualification | Required observation | Evidence boundary |
| --- | --- | --- |
| `SequenceTimelineEditorTests` | True measured ruler width, repeat-viewport row identity, first 256 correctly ordered visible clips and bounded snapping allocations | Managed model and WPF collection behavior; no native GPU output |
| `OperatorMediaWorkspaceLifecycleTests` | Cancelled monitoring ROI is restored on view replacement; loaded Output Canvas detaches its draft-editor event subscription on unload | Windows STA/WPF lifecycle; actual native graphic devices not qualified |
| `MediaViewportVisualQualityQualificationTests` and `MonitoringVisualPerformanceQualificationTests` | 100/125/150/175/200% geometry, Fit/Fill/100% behavior and unmodified GPU versus truthful CPU fallback basis | Existing software evidence; real multi-monitor topology and screen-reader testing remain separate |
| Existing real XAML and docking qualifications | 1920×1080 through compact, focus/cinema and floating workspace layouts without incorrect crop or overwritten authoring state | Exact runner/source screenshots require human review; `D3DImage` may not appear in bitmap captures |

Passing a newly added test cannot be assumed from its presence. The exact PR-head Windows Release build, targeted tests, complete solution and five required gates must be read from their actual runs; inherited failures and hardware limits remain explicit. The view-only changes do not alter `rtaime.sequence-output.v1`, Program authority, source formats, monitoring transport, allocation pools, persisted layouts or capability versions.

## Validation and release guard

Canonical checks use [Build and Test](BuildAndTest.md) and the existing
[Required Gates](Governance/RepositoryGovernance.md):

```powershell
./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release
dotnet test tests/rtaime.Tests.Integration/rtaime.Tests.Integration.csproj --configuration Release --no-build --filter "FullyQualifiedName~Operator_experience_import_analyze_multitrack_save_prepare_shuttle_preserves_Program_authority|FullyQualifiedName~SequenceFrameCompositionIntegrationTests"
dotnet test tests/rtaime.Tests.Operator/rtaime.Tests.Operator.csproj --configuration Release --no-build --filter "FullyQualifiedName~OperatorXamlStartupTests"
dotnet test rtaime.slnx --configuration Release --no-build -m:1
```

Build and test statements must cite one exact PR-head run. The mandatory
gate names are **CI, Quality, Security, Provider Smoke, Packaged E2E**.
A queued/running/skipped/unavailable check is not PASS. The independent
reference platform, production signing trust, formal accessibility
and live GitHub ruleset enforcement remain separate review domains.
Never publish or promote a release solely from a successful visual
artifact, software composition test or merge event.

## Open integration obligations

1. Wire genuine revision- and source-bound **sequence Preview rendering and
   Program Take** through Control transaction commit and Runtime output;
   verify two distinct videos and two audio sample windows at the actual
   committed sink. Do not relabel native position `PLAYING` as visual output.
2. Run connected full UI journeys, dual video/audio output, Cue/Rundown
   interlock and AI fallback with synchronized media/source/execution IDs.
3. Review the actual combined PNGs, long content/keyboard/screen reader,
   layout clipping and native file dialog focus on reference displays at
   100/125/150/200% DPI.
4. Reproduce/resolve exact-source CI defects and physically measured
   GPU/SSD/latency/handle/fault evidence. Do not adjust budgets just to pass.
5. Complete the five required checks at the **final** source SHA and
   follow the established change/release gates before any promotion.
