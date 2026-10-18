<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Operator experience scope and integration baseline

## Accepted product decision — 2026-10-17

The Product Owner authorizes implementation of the complete Operator experience
design without the former V1 functional ceiling. Independent media analysis and
position scrubbing, real multitrack sequences and editing, Preview shuttle,
2D canvas/mapping, contextual Inspector editing and an independent AI workspace
are **TO IMPLEMENT: approved**, not requests for another V1 scope decision.

This decision supersedes the functional deferrals in the
[design contract](OperatorExperienceDesign.md) and
[roadmap](OperatorExperienceRoadmap.md) introduced by
[the accepted design foundation](https://github.com/DaveBeusing/rtaime/pull/281).
The earlier contract described the then-implemented client; treating its missing
features as permanent prohibitions would contradict the accepted design.
That historical baseline remains discoverable in the linked revision history.
The design contract owns visual/interaction acceptance; the roadmap owns delivery
dependencies; this record owns scope, supersession and shared namespace allocation.

Implementation approval is **not** hardware, codec/format, security, commercial
dependency or release certification. It does not authorize paid services, new
commercial SDKs, external media transmission or new hardware drivers. A complete
replacement NLE and 3D stage authoring are not delivery obligations of this
decision; they are not subject to a permanent V1 prohibition either.
[UHD/high-bit-depth approval](HighResolutionMediaDecision.md) and
[professional decoder dependency decisions](ProfessionalDecoderDecision.md)
remain separate. Accepted Media I/O adapter/ABI boundaries remain unchanged.

This baseline delivers documentation and integration rules, not feature code,
new endpoints or a UI rebuild. After integration on master, independent delivery
areas can start from that current master. Missing human/physical acceptance
does not block every implementation area; a consumer needing a missing backend
contract must implement and test that real contract before claiming completion.

## Compatibility and authority that remain binding

- Operator → Client → ControlHost remains the production mutation path.
  Control owns durable state and prepare/commit; Runtime owns execution,
  decoder/resource admission and the one Program media/audio cadence.
  AI and monitoring remain non-authoritative.
- Preserve Source identities, expected revisions, PreparedExecution and generation
  fences. Selection, draft, request, Accepted, committed state and measured
  output remain distinct. Undo does not replay confirmed live operations.
- Keep existing V1 protocol names, serialized values, old operation IDs and
  checkpoint/layout compatibility. New semantic payloads need deliberate
  capability/version negotiation, migration and old-peer rejection tests.
  A new workspace needs additive identity and layout migration, not renaming
  the eight existing destinations or overwriting saved presentation.
- Resources remain finite. Decoder, analysis, frame, queue, memory and history
  limits may evolve with explicit admission, cancellation, ownership and
  measurements; removing a functional ceiling does not mean unlimited resources.
  Track count is not concurrent decoder capacity. Existing A/B limits remain
  actual behavior until their owning admission implementation changes.
- Reuse the asset catalogue, command registry, monitoring path and resource owner.
  No second database, reconciliation loop, Program renderer/clock, global
  command registry or monitoring poller. Bulk media stays outside management IPC.
- Preserve RBAC, stale/busy/readiness gates, bounded failure recovery and the
  five [Required Gates](Governance/RepositoryGovernance.md). Failed/unknown
  hardware, latency, security and release evidence stays FAIL/UNVERIFIED.

## Status vocabulary and delivery ownership

| Status | Meaning |
| --- | --- |
| EXISTING | Located in the current source/inventory; no blanket acceptance implied |
| TO IMPLEMENT | Approved behavior with incomplete real product wiring |
| IMPLEMENTED | Real contracts, owner, consumers and error/recovery paths are wired; cite source and tests |
| QUALIFIED | The explicitly named test/visual/hardware case passed at an exact source; never infer another category |

Reviewed master: `b5b7520184cdea8160a07495d792d5e13530069b`.
The current build target is 0.5.40 in Directory.Build.props; pinned SDK is
10.0.401. Versioning rules remain in [Product versioning](Versioning.md);
this baseline changes no version, protocol, schema or production source file.

| Requirement / status | Reuse and implementation owner | Real acceptance boundary |
| --- | --- | --- |
| Resource admission — EXISTING owners / TO IMPLEMENT extensions | Existing Runtime deck registry, provider capability/reservation and bounded pools | Evolve finite limits with explicit byte/session/queue budgets, ownership, cancellation and measured admission; no new global admission owner or inferred four-deck/hardware support |
| Independent analysis/scrub — TO IMPLEMENT | MediaLibraryHoverPreview currently reuses a thumbnail; catalogue identity and Runtime admission/provider lifecycle are EXISTING | Runtime-owned bounded preview-only jobs; generation/source fencing, cancel/close, metadata/audio/sample results; no seek/open of the production deck from hover; Client/host tests before UI completion |
| Multitrack sequence/editing — TO IMPLEMENT | MediaTimelineController/ViewModel, markers, local drafts, Show Control and Rundown are EXISTING | Control-owned versioned durable sequence/track/clip model, revision checks and migration; Runtime prepared execution/sink on existing cadence; authoring/transport/failure/recovery tests; current reference lanes are not tracks with this behavior |
| Preview shuttle — TO IMPLEMENT | Existing confirmed-Preview guard and shared command/keyboard registry | Typed rate/direction, seek/cancel/end semantics and source identity through Client/Control/Runtime; no Program seek from J/L; existing J steps one Preview frame back and L plays at normal rate, both excluded on Program; reverse/multirate shuttle is still missing |
| 2D canvas/mapping — TO IMPLEMENT | Existing compositing, transforms and output-role routing | Versioned surfaces/assignments/geometry, Control validation/persistence, Runtime output sink/admission and rejection evidence; no new driver, 3D stage or format inferred |
| Contextual Inspector editing — EXISTING foundation / TO IMPLEMENT extensions | Shared Inspector, draft undo, typed attribute transfer and existing mutation commands | Selection/units/mixed values; explicit Apply/Discard; compatible multi-object edits require authoritative transaction/revision semantics; no fake live undo |
| Independent AI workspace — TO IMPLEMENT | Existing capability browser, approval, inference/fallback and monitoring | Separate workflow destination over the same governed AI services; additive layout identity/migration; descriptor-only tests, explicit activation approval and executed/fallback evidence |
| LIVE composition — TO IMPLEMENT target | Existing shell, role viewers, Library, Inspector, timeline/cues, status and docking | [Concrete target and compact acceptance](OperatorExperienceDesign.md#live-operator-composition); no claim that current LIVE already matches |
| Authority/compatibility — IMPLEMENTED existing paths | ControlHost, RuntimeHost, Client, durable recovery and current contracts | Existing connected integration and packaged recovery suites remain required |
| Geometry — QUALIFIED software subset | Existing Windows STA/viewport harness | The historical cases below; human, physical DPI and new target acceptance remain UNVERIFIED |

One owner at a time changes shared MainWindow composition, dispatchers,
ExternalControlOperationCatalog, Proto, show envelopes, workflow, design/version
files and README. Feature owners keep domain files local, then integrate small
shared patches serially after reviewing current master and competing PR diffs.
Do not stack branches on unmerged prerequisites or duplicate a backend with a UI fake.

## Binding namespace and numeric allocation map

The current register is
[ExternalControlRequest.oneof operation](../schemas/external-control/v1/external_control.proto).
At the reviewed source its 62 assigned operation tags end at **97**
(`get_production_macro`). No `reserved` declaration occupies the following
ranges. Existing field tags 1–97, including gaps, are not reused by this decision.

Allocate the next contiguous free ranges from 98 for independent backend work:

| Domain namespace / owner | Future capability/message families | Allocated future ExternalControlRequest operation tags |
| --- | --- | --- |
| `media.analysis` — media analysis owner | `control.media.analysis.*`; `runtime.media.analysis.*` | **98–113** (16 tags) |
| `sequence` — sequence model/execution owner | `control.sequence.*`; `runtime.sequence.*` | **114–145** (32 tags) |
| `output.canvas` — canvas/output model owner | `control.output.canvas.*`; `runtime.output.canvas.*` | **146–161** (16 tags) |

These are documentary allocations for future operations, not implemented Proto
fields, advertised capabilities, performance limits or new wire enum values.
Each owner assigns concrete messages within its range in its real backend change,
records consumed tags and version semantics here, and updates the existing
Proto/Client/dispatcher/RBAC catalogue in one exclusive integration patch.
Recheck the current register before consuming a tag; collisions stop integration.
Consumed wire tags are never renumbered/recycled; exhaustion requires an explicit
serial extension of this map, not grabbing another owner's range.

The existing Named Pipe WireEnvelope uses string `MessageType`, not a numeric
operation registry. Reuse those versioned string families; do not invent a second
numeric IPC catalogue. Numeric operation enums inside individual payloads and
Proto field numbers inside other messages are separate scopes and retain values.
The existing ExternalControlOperationCatalog stays the capability/RBAC catalogue.
No new generic plugin/command framework is introduced. Future external operations
require explicit role classification, authentication/limits and real host mapping;
a read-like analysis request can still allocate resources and needs admission.

## Retained Windows baseline and evidence limits

Source-bound historical software baseline:
[Required Gates 39087602959, attempt 1](https://github.com/DaveBeusing/rtaime/actions/runs/39087602959),
source `e8eaceec9e839045ef2970bab2f9d28934fcdda7`, SDK 10.0.401.
The current master adds its merge and a VersionPrefix-only change; no Operator
implementation change is inferred from that version update.
Final-head checks/capture metadata for this documentation change are recorded
in its PR, not inherited from this historical run.

| Available evidence | Proven conditions / remaining limits |
| --- | --- |
| CI visual artifact 12121503448; Quality visual artifact 12121537350 | Producer logs confirm 40 real-WPF workspace/viewport cases and five preference-transition rows. Existing harness emits five LIVE and twenty owned-window PNGs, combined-ui.json and interaction-ui.json; archives are retained, not newly visually reviewed here |
| Logical geometry/DPI | 1920×1080, 1600×900, 1536×864, 1280×720, 960×540; injected 100/125/150/200% layout. Actual Windows DPI is recorded separately in JSON and is not established by these injected values |
| Media/source provenance | Disconnected, non-authoritative MainWindow fixture; no authored production media loaded. Seven native presentation surfaces discovered, zero observed draws in the reported measurement. No live image quality, connected TAKE or physical output claim |
| Interaction/resource observations | 24 warmed samples: dispatcher P95 0.2476 ms, max 2.2354 ms; 24 layout events; target growth 0; handles +0; private bytes +77,824; managed bytes +476,992. Gross software regression observations, not a latency service level or hardware acceptance |
| Human/physical acceptance | Typography/contrast/content review, connected live journeys, physical mixed-DPI moves, native file-dialog focus, touch/pen, display removal and loaded media quality remain UNVERIFIED |

Artifact metadata and producer logs were read through the repository connection;
binary ZIP/PNG contents were not independently inspected. The
[existing qualification procedure](OperatorVisualQualification.md#combined-workspace-regression-evidence)
owns capture/review, and existing MainWindow STA tests own interaction setup.
No parallel screenshot or test infrastructure is needed.

Historical release/hardware findings remain open: the same source's
[reference run 39087602905](https://github.com/DaveBeusing/rtaime/actions/runs/39087602905)
recorded dual-deck 59.94 cleanup FAIL and 1,065 missed slots; 50-fps cleanup
passed with 1,801 missed slots and timing FAIL. Physical qualification was 0/12,
UNVERIFIED / NO-GO. These do not prohibit independent model/design implementation,
and documentation does not mark them repaired.

## Supersession and acceptance checklist

| Earlier statement / source | Effective interpretation from 2026-10-17 |
| --- | --- |
| Design foundation: separate AI workspace is a future scope decision | Independent AI workspace is approved, TO IMPLEMENT |
| Design/roadmap: inspection, scrub/shuttle and editing need another scope decision | Feature approval is settled; real bounded contracts and end-to-end evidence remain delivery requirements |
| Workspace/design: Outputs & Mapping does not authorize mapping | 2D canvas/mapping is approved; 3D stage, new driver and physical output certification are separate |
| Eight destinations / schema-6 geometry in current workspace/UI docs | Current implementation inventory, not a permanent workspace or geometry ceiling; migrate/test deliberate additions |
| Layered Timeline explicit non-goals, Media Library unsupported scrub and Inspector batch deferrals | Historical/current implementation limits, superseded as functional prohibitions for approved sequence/editing, analysis/scrub and contextual editing; real contracts remain required |
| Capacity proposals requiring another V1 functional inclusion decision | Finite resource-admission evolution is authorized; exact capacities need real contracts and measured evidence, with no automatic four-deck/SSD-support claim |
| Bootstrap architecture, Media I/O accepted ADR and decoder decisions | Authority, dependencies, compatibility, native ABI, security and hardware/codec evidence remain binding; no blanket repeal |

Review all active scope statements against this record; preserve old V1 names and
historical source citations. Require logical containment/reachability, English UI,
brand/role labels, actual container allocation and no duplicate transports.
Use existing UI/governance policy and Windows tests before the complete solution
and exact-head five Required Gates. Keep current safety/geometry policies active
until their owning implementation changes include focused migration/tests and
necessary policy amendments. Missing human/physical evidence stays UNVERIFIED
without making every independent implementation wait.
