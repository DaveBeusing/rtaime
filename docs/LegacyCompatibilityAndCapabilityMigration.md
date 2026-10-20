<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Legacy Compatibility and Capability Migration

**Decision effective:** 2026-10-20 · [canonical product scope](ProductScope.md)  
**Original inventory baseline:** `master` `e61ebf3313cc170999e4ff1c245407049d8ca477`  
**Current integrated master baseline:** `cbbee8721a94ade6df771e0c2b9a92785d3a0256` (synchronized into this branch; refresh at each subsequent integration).  
**Repository source of truth:** `DaveBeusing/rtaime`; refresh the full SHA and PR inventory before every write.  
**State:** PARTIALLY IMPLEMENTED / QUALIFICATION PENDING. The targeted internal Program execution rename and initial product-facing documentation cleanup are committed; public/serialized compatibility changes and provider-dependent scaling are not completed. No hardware support or green CI is inferred.

## Repository and quality preflight (2026-10-20)

- Default/integration branch at original preflight: `master` `e61ebf3313cc170999e4ff1c245407049d8ca477`, with 1,227 tracked blobs in that recursive tree snapshot. The current branch is synchronized to the later master identified above.
- At the original preflight, active non-master remote branches: none; open pull requests: none. The active integration branch is now `docs/product-scope-capability-migration` (do not treat the old preflight as a current PR inventory).
- Recent feature PRs #292–#311 in the inspected batch are closed and merged. In particular, sequence preview shuttle (#310), sequence composition (#309), canvas editor (#303), mapping renderer (#301) and combined Operator qualification (#311) are integrated. No parallel shared-runtime PR was present at inspection time.
- At the original baseline `Directory.Build.props` contained `VersionPrefix=0.5.40`; current master and the synchronized branch use `0.5.41`. The project does not bump PATCH per commit. SDK `10.0.401` is pinned; `net10.0-windows` Operator remains a Windows WPF host.
- On `master` Required Gates run [39335581204](https://github.com/DaveBeusing/rtaime/actions/runs/39335581204), `Quality`, `Security`, `Provider Smoke` and `Packaged E2E` passed, **CI failed**, and the separate release-readiness assessment failed. CI built successfully but the real WPF `OperatorXamlStartupTests.Real_MainWindow_and_viewers_load_render_all_workspaces_and_close_on_STA` timed out on the Windows runner. Release-readiness was correctly `FAIL / NO-GO`. This is pre-existing, NOT a passing or newly fixed result.

## Reproduce the inventory

Search **tracked source**, not generated `bin/`, `obj/` or artifact contents. The following commands are entry points, not safe global replacements:

~~~powershell
git rev-parse master
git branch -r
git log -n 20 --oneline master
git grep -n -I -i -E "V1|v1-only|v1 scope|proof product|reference.only" -- src tests docs schemas build .github README.md
git grep -n -I -E "Maximum[A-Za-z]+|Max[A-Za-z]+|development-four|1080p|Aux output|decoder" -- src tests docs schemas build
git grep -n -I -E "rtaime[.][a-z0-9.-]*[.]v1|/v1|LegacyV1" -- src tests schemas
~~~

For each hit, assign owner, symbol or literal, effect, exposure, risk, resolution, test and PR; use the classification vocabulary below. A source-token match alone never establishes semantic removability. Search every user-visible resource and generated-help source for displayed product-phase language before declaring completion.

### Migration categories

| Class | Meaning and exit condition |
| --- | --- |
| `REMOVE_LANGUAGE` | Product/UI/CLI/help/docs text asserts historical feature/product ceiling; change wording without implying added support |
| `RENAME_INTERNAL` | Active in-process type, file or internal-only symbol; coherent rename of all callsites, reflection and tests in an exclusive integration window |
| `COMPATIBILITY_ONLY` | Published wire, persisted, endpoint, API, ABI, checkpoint, test fixture or versioned migration identifier; retain and round-trip; do not count as product branding |
| `CAPABILITY_GATE` | Historical feature ceiling or unsupported profile; replace with provider capability, format matching, resource planning and explicit failure |
| `VALIDATED_RESOURCE_BOUND` | Measured, safety, ownership, queue/heap/protocol or deterministic real-time bound; keep finite, describe rationale and scale only with evidence |
| `CONTRACT_MIGRATION` | Semantic public API/persistence change; separate versioned adapter, negotiation, migration tests and approval |
| `HARDWARE_UNVERIFIED` | Product feature is in scope, but target GPU/driver/display/codec/output validation is absent; retain unsupported/unverified runtime state |

## Source-bound inventory and ownership

Each path is a **confirmed entry point**, not a claim that all matches in all 1,227 files have already been rewritten. Compound rows represent one migration decision and explicitly named related files.

| Path / symbol or value | Owner | Category | Exposure / risk | Required resolution |
| --- | --- | --- | --- | --- |
| `src/Hosts/rtaime.RuntimeHost/V1RuntimeHostService.cs` — `V1RuntimeHostService` | RuntimeHost | `RENAME_INTERNAL` | Used by host composition, IPC, media, recording and extensive tests | Exclusive coherent rename to `RuntimeProgramExecutionService` (or agreed non-versioned equivalent), including partial output-mapping file, factories, refs and path-sensitive quality scripts; keep the one renderer/cadence |
| Same path — `V1ProgramBoundaryResult` | RuntimeHost | `RENAME_INTERNAL` | Readback lease ownership and recording/monitoring consumers | Move to `ProgramBoundaryResult` without changing public/runtime bytes, `Dispose`/retain lifetime, or host boundary timing |
| Same path — `V1RuntimeHostSnapshot`, `V1VisualLayerMode`, `V1CompositingLayerSnapshot` and other runtime model names | RuntimeHost | `RENAME_INTERNAL` / `CONTRACT_MIGRATION` if external | Snapshot/callback consumers and possible serialization | Enumerate actual declarations and external shape first; rename only purely internal semantics, version public DTOs deliberately |
| `src/Hosts/rtaime.RuntimeHost/ProductionCgTextRenderer.cs` — `V1CgLayer`, `V1CgAnchor`, `V1CgColor`, related styles | RuntimeHost/Graphics | `RENAME_INTERNAL` or `CONTRACT_MIGRATION` | Runtime IPC numeric layer and downstream fixtures | Check wire integer/enum mapping and older consumers; maintain values, rename in single graphic-contract integration |
| `src/Providers/rtaime.Provider.Gpu/GpuProcessing.cs` — `V1Formats` | GPU provider | `RENAME_INTERNAL` / `CAPABILITY_GATE` | Current accepted RGBA8 reference formats | Name as currently supported/qualified formats; evolve per provider and exact format, not by changing a label |
| `src/AI/rtaime.AI/GovernedInferenceRuntime.cs` — `InferenceRuntimeLimits.ReferenceV1` | Inference | `RENAME_INTERNAL` / `VALIDATED_RESOURCE_BOUND` | Defaults consumed by AIHost service/process | Rename default profile without relaxing concurrent requests, bytes or rate limits |
| `src/Control/rtaime.Control/OutputRoleControlDomain.cs` — V1 output-role rejection text | Control | `REMOVE_LANGUAGE` / `CAPABILITY_GATE` | User-facing validation text for Aux cardinality, selector, format and timing | Eliminate phase language; preserve actual unsupported checks until capability-backed multi-output transaction is implemented |
| `src/Hosts/rtaime.Operator/GraphicsOverlayAssetLoader.cs` — V1 PNG-only message; `DemoProductionPackageController.cs` — V1 graphics bounds | Operator | `REMOVE_LANGUAGE` / `CAPABILITY_GATE` | Local error text and actual asset/schema validation | State real accepted formats/budgets; leave unsupported formats rejected pending provider/asset pipeline |
| `src/Media/rtaime.Media/LocalMediaFileSource.cs` and `src/Providers/rtaime.Provider.VirtualMedia/VirtualMediaReferenceProvider.cs` — V1 reference/platform format messaging | Media/Providers | `REMOVE_LANGUAGE` / `HARDWARE_UNVERIFIED` | Platform/format rejection text | Remove phase rationale, not actual Windows/codec/format dependency or safety checks |
| `src/Hosts/rtaime.RuntimeHost/RuntimeHostProcess.cs` — `rtaime.v1.runtime.default`, `src/Hosts/rtaime.AIHost/AIHostProcess.cs` — `rtaime.v1.ai.default`, Operator/Client `rtaime.v1.control.default` | Hosts/IPC | `COMPATIBILITY_ONLY` / possible `CONTRACT_MIGRATION` | Existing named-pipe identities may be in scripts or saved configurations | Keep live endpoint spelling until negotiated alias/rollout and old-peer test; do not silently rename default transport |
| `src/Client/Grpc*Transport.cs` and `src/Hosts/rtaime.ControlHost/ExternalControlServer.cs` — `Rtaime.ExternalControl.V1` | Client/Control | `COMPATIBILITY_ONLY` | Protobuf-generated namespace and gRPC package | Preserve source and service numbers for older clients; isolate versioned adapter if upgraded |
| `schemas/external-control/v1/external_control.proto` and `schemas/**/v1/*` | Contract owners | `COMPATIBILITY_ONLY` | Protobuf tags, JSON schema IDs, release manifests, state formats | Retain versioned directories and old decoders; additive/migrated new versions separate |
| `src/Hosts/rtaime.ControlHost/ShowProjectPersistenceStore.cs`, `SequencePersistence.cs`, `OutputCanvasPersistence.cs` and related catalogue/recovery stores | Persistence/Control | `COMPATIBILITY_ONLY` | `rtaime.show-project.v1`, `rtaime.sequence-authoring.v1`, `rtaime.output-canvas.v1`, checkpoints and stable identifiers | Retain read/write compatibility and migration provenance; round-trip original fixture bytes/identities |
| `src/Contracts/rtaime.Control.Contracts/SequencePreparationContracts.cs` — `rtaime.sequence-preparation.v1` and owner ceiling | Control/Runtime Contracts | `COMPATIBILITY_ONLY` + `CAPABILITY_GATE` | Wire identity versus maximum concurrent native owners are separate semantics | Keep version identifier; scale owner budget through negotiated profile and matching Runtime reservation evidence |
| `src/Contracts/rtaime.Media.Contracts/MediaAnalysisContracts.cs` — `rtaime.media-analysis.v1`, 320×180/256/8 ceilings | Media Contracts | `COMPATIBILITY_ONLY` + `VALIDATED_RESOURCE_BOUND` | Bounded observation IPC and optional analysis, not Program fidelity | Preserve old frame protocol; higher-quality analysis requires negotiated explicit bounded payload/owner and separate tests |
| `docs/OperatorUiV1.md`, `docs/BootstrapArchitecture.md`, `docs/V1ContractFoundation.md`, `docs/V1EndToEndProof.md`, `docs/V1FunctionalGapClosure.md` | Docs/Architecture | `REMOVE_LANGUAGE` where current; otherwise historical provenance | V1 name mistaken for current product ceiling | Current entry points defer to `ProductScope.md`; preserve clearly marked historical implementation/contract records |
| `README.md`, `docs/BuildAndTest.md`, `docs/OperatorExperienceScope.md`, `docs/OperatorExperienceRoadmap.md` | Docs/Operator | `REMOVE_LANGUAGE` | Product narrative/current workflows | Replace current phase branding and link canonical scope; preserve reference to actual legacy formats |
| `docs/HighResolutionMediaDecision.md` | Media/Architecture | `CAPABILITY_GATE` / `HARDWARE_UNVERIFIED` | Proposed per-resolution approval gate conflicts with new full-product scope if read as permanent feature prohibition | Supersede product-stage veto; retain physical/format/commit qualification and separately approved costly or breaking dependencies |

### Capacity/format admission inventory

| Source / current behavior | Type and migration owner | Target acceptance / rejection |
| --- | --- | --- |
| `LocalMediaResourceLedger` / `RuntimeHostProcessOptions`: 1–2 conservative owners; explicit `development-four` up to 4 | `CAPABILITY_GATE` + `VALIDATED_RESOURCE_BOUND` · RuntimeHost | Profile-resolved number of native decoder/pool owners; atomic group reservation, measured per-owner budget, cleanup quarantine, native pressure and no Program eviction. No arbitrary unlimited owner count |
| `LocalMediaDeckRuntimeService`: configured/bounded source registry (documented up to 8) | `CAPABILITY_GATE` · RuntimeHost | Dynamic authoritative Source IDs and bounded registry keyed to validated owner/provider capacity; legacy A/B remains readable |
| `SequencePreparationContract.MaximumNativeOwners=4` | `COMPATIBILITY_ONLY` version / `CAPABILITY_GATE` capacity · Control/Runtime | Negotiate new capacity separately from the old serialized version; old contracts remain bounded and round-trippable |
| `OutputCanvasContract`: 16 canvases, 128 surfaces, 32 bindings, 256 ops, dimension 16384, logical pixels 67,108,864 and JSON 262,144 bytes | `VALIDATED_RESOURCE_BOUND` until provenance established · Control | Differentiate authoring/document transport limits from mapped physical resource limits and profile support; avoid blanket raising old wire limits |
| `V1RuntimeHostService.OutputMapping.cs`: exactly 1 mapped Program target, at most 8 mapped surfaces, 16 MiB mapped frame | `CAPABILITY_GATE` plus memory budget · Runtime/GPU/Output | Per-target advertised device capabilities, planned layout, finite framebuffer/VRAM reservations and atomic activation/rollback. Reject impossible layouts without losing committed output |
| `RuntimeHostProcessOptions`: only 1080p50 or 1080p59.94 RGBA8 | `CAPABILITY_GATE` / `HARDWARE_UNVERIFIED` · Runtime/Providers | Exact provider/format/cadence match, frame/queue budgets, readback/recording/output tests and physical qualification for any new supported tuple |
| `GpuProcessing.cs` and native Media I/O / NDI / recording providers: finite reference tuples | `HARDWARE_UNVERIFIED` / `VALIDATED_RESOURCE_BOUND` · individual providers | Capability descriptors and evidence by backend, device, codec, pixel/color and timing. Do not promise CUDA/SDI/HDR/UHD merely because a product phase was removed |
| `OutputRoleControlDomain.cs`: one Aux, `auto` provider, inherited format/timing | `CAPABILITY_GATE` · Control | Dynamic role/binding plan with resource negotiation, revisioned preparation and safe update. Keep existing role IDs and qualified behavior until the new owner is wired |

## Integration plan and compatibility obligations

**Parallel, isolated:** this document, [ProductScope](ProductScope.md), current vs. historic ADR conflict register and targeted compatibility/architecture tests. Domain-owned media, sequencing, mapping, monitoring and GPU/IO capacity work may be developed separately from current master.

**Exclusive serial integration:** one named owner migrates `V1RuntimeHostService`, `V1ProgramBoundaryResult` and other truly internal identifiers across source, tests, path-sensitive `build/quality` scripts, diagnostics and docs. No competing runtime class/clock, no wholesale textual replacement, and no automatic rename of `rtaime.v1.*` endpoint strings, Protobuf namespaces, enum numbers, persisted format tags, Source IDs or archived evidence.

**After capability domain merges:** align shared README, Help, Operator status and contract negotiation; integrate one provider-driven admission model, then run final CI on the exact new head. Do not merge mutually stale branches or mark the migration done because a docs-only PR passed.

## Tests and release qualification matrix

- **Compatibility:** Old saved show/sequence/layout, JSON and `LegacyShowProjectV1.json` / `LegacyAuthorityV1.json` fixtures, Protobuf tags, gRPC, IPC endpoints, Source/Program/Runtime identities, revisions and round trips.
- **Resource:** group all-or-none admission, cancellation, retained pool/quarantine, unsupported format/provider, preflight/prepare/commit/rollback, stale authority, overbudget, multiple resolutions, tracks and outputs without disturbing Program.
- **Host:** one scheduler and readback owner, native end-to-end Preview/Program pixels and audio, deterministic frame cadence and monitoring isolation.
- **Visual and hardware:** correctly sized/clear monitoring frame plus mixed DPI; physical color/HDR/SDI/NDI/device latency, long soak, SSD/GPU/RAM/VRAM/GC measurements with exact source/runner evidence.
- **Windows and CI:** `./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release` then focused architecture/contracts/integration/Operator/failure suites, `dotnet test rtaime.slnx --configuration Release --no-build -m:1` and the five Required Gates. Run after the last commit and inspect the exact head; always report actual failures.

## Implementation status / resumption

This register distinguishes work committed on the active branch from older inventory and runner evidence. Actual merge readiness still requires the final-head Windows gates and compatibility checks.

| Item | Current verified implementation status |
| --- | --- |
| Full-product strategy authority | APPROVED (2026-10-20) |
| Initial GitHub branch/PR collision inspection | COMPLETED for original baseline and refreshed through current master; recheck before further shared edits |
| Source/compatibility/cap inventory | INITIAL CLASSIFICATION with path/owner/risk, historic contract boundaries and capacity-gate owners; exhaustive per-symbol and user-facing-resource audit still pending |
| Canonical product scope | DOCUMENTED in `docs/ProductScope.md` |
| Internal runtime/CG/AI renames | `RuntimeProgramExecutionService` and `ProgramBoundaryResult` renamed with callsites and quality paths; private GPU format-array field neutralized. Runtime snapshot/CG/inference names require compatibility exposure review before migration |
| Wire/persistence/endpoint migration | NOT AUTHORIZED; legacy protocol, Show, Source, named-pipe and Protobuf identities retained. New architecture tests guard sampled stable identities; exhaustive round-trip coverage remains an acceptance requirement |
| Dynamic scaling beyond currently validated resource profiles | NOT IMPLEMENTED |
| Physical high-resolution, CUDA, SDI/HDR, sequence actual sink | UNVERIFIED / as qualified in owning source |
| Baseline CI | Original master run 39335581204 failed due to Windows WPF Operator startup timeout; no successful current-branch CI result claimed |
| Baseline release-readiness | Original master NO-GO; current-branch release gates and physical support remain UNVERIFIED until exact-head evidence |

**Resume:** refresh `master` SHA, open/closed PRs and any new source overlaps. Continue versioned compatibility contract classification and provider/profile admission work with domain owners; do not broaden real resource limits without reservation and failure evidence. Inspect the new `ProductScopeCompatibilityTests` with existing Architecture/Contract/Integration/Operator suites on the exact branch head, repair actual failures, and record five Required Gates before merge. The targeted Program internal rename, initial architecture guard and shared scope documentation are committed; remaining public DTO/CG/inference migrations and hardware support are **not complete**. No unexecuted test or hardware qualification is represented as passed.
