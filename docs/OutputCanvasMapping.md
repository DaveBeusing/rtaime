<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Output canvas configuration and mapping
## Operator OUTPUTS workflow and confirmation

The single OUTPUTS workspace provides **ROUTING / SIGNAL** and **CANVAS / MAPPING** modes; switching modes changes presentation only. The canvas editor keeps the same durable-saved configuration, one local draft, undo/redo and existing authority/client transport across these changes. Its initial visibility is contextual; neither showing the editor nor selecting a target changes Program.

Canvas actions are explicitly ordered: **VALIDATE DRAFT** must succeed at the current local edit before **SAVE** becomes available; after a successful Control-host save, **VALIDATE SAVED** must succeed for the authoritative saved revision before **REQUEST RUNTIME APPLY** can be issued. SAVE remains non-executing. A successful Apply reply reporting `PENDING_BOUNDARY` remains unconfirmed until Runtime returns `ACTIVE` with the matching saved render revision; a target may still be UNMAPPED, FAILED or missing. Changed local geometry invalidates validation. Conflicted saved revisions or lost-save replies block retries until reconciliation. An ambiguous Apply reply blocks another Apply/Disable until a fresh Runtime observation resolves whether the existing request reached a terminal state. No optimistic Program update or implicit retry is made.

The target selector lists only Control-authorized targets and exposes role, provider, target identity, profile and declared geometry. That authorization alone does **not** demonstrate device/driver compatibility, professional media-I/O or physical scan-out; absent evidence remains UNVERIFIED. Numerical warp, crop and edge-blend controls are behind **ADVANCED GEOMETRY / BLEND**, while the geometry stage and source/target binding remain accessible. Hiding numerical controls does not modify a draft or add another geometry render loop.



Control owns the versioned, durable authored 2D model in the canonical show project.
Operator consumers use Client → ControlHost. This backend provides configuration,
validation, saving and explicit mapping activation. A currently qualified
managed-reference GPU backend can apply saved surfaces to the existing Program
output sink. Program/Aux role routing remains authoritative; Preview and Clean
Feed are monitoring, never enumerated physical output targets.

## Contract and geometry

OutputCanvasContract.Version is **1.0**, independently of the existing Control,
Runtime and external API **1.0** identities. CanvasId, SurfaceId, OutputId and
ConfigurationId are nonempty GUIDs, unique across each configuration. Renaming and
updating preserve them. Repeated source/bus use is allowed. A surface references
exactly one existing production SourceId or governed program/aux bus. A bus follows
its existing authoritative role; a source reference retains that source identity.

Canvas width/height are logical pixel edge coordinates, origin at upper left,
X right and Y down. Pixel centres are (column + 0.5, row + 0.5). Logical canvas
resolution, normalized source crop and provider device resolution are distinct.
A crop is normalized source edges X/Y/Width/Height wholly inside [0,1], with
positive finite area. It never modifies the source or seeks a deck.

Destination is TL, TR, BR, BL, strictly clockwise and convex in screen coordinates.
Rectangles are represented by their four corners. Inverted, self-intersecting,
concave, zero/near-zero area, nonfinite and excessive coordinates fail validation.
Successive edge cross products must exceed 1e-8 square logical pixels.
Coordinates outside the canvas are allowed within ±16384; the renderer consumer
must clip to canvas edges. Transparent-black mapping is flattened to opaque
black for the existing Program output sink, ensuring blend attenuation affects
actual RGB pixels even if downstream sinks ignore alpha.
Surfaces composite by ascending ZOrder then ascending SurfaceId (ordinal GUID
string); disabled surfaces retain identity/configuration but contribute nothing.

OutputCanvasGeometry.Map implements the unit square → destination homography,
not bilinear corner interpolation. Source pixel centre (x+0.5,y+0.5), given source
size W×H, maps using u=((x+0.5)/W-crop.X)/crop.Width and the analogous v. Only centres
inside the crop participate; sampling/filter choice belongs to the rendering
consumer, which must honour this geometry/version and explicit resource admission.

Expected mappings:

| Model | Source edge/centre | Destination |
| --- | --- | --- |
| Identity 1920×1080, full crop | first pixel centre | (0.5,0.5) |
| Identity 1920×1080, full crop | last pixel centre | (1919.5,1079.5) |
| Crop (0.25,0,0.5,1), destination 1280×720 | normalized source edge (0.5,0.5) | (640,360) |
| Quad (0,0),(200,0),(150,100),(50,100) | unit square (0.5,0.5) | (100,66.66666666666667) |

A complete [version-1 example model](examples/OutputCanvasV1.json) contains two
logical canvases, crop and quad geometry, with no fabricated provider bindings.
Replace its configuration/revision through a real Get/draft transaction when
using it in an existing production.

## Transactions and revision meaning

GetOutputCanvasAsync returns Saved, SavedRevision, AvailableTargets,
ActiveRenderRevision and ActivationState. ActiveRenderRevision comes from the running RuntimeHost's applied output pass;
RuntimeEvidence also reports pending revision, last rendered frame sequence,
source, target and bounded dimensions. On unavailable older Runtime peers the
activation state remains RENDERER_REQUIRED. Saving never calls Runtime Prepare/
Commit, changes Program or requires a Take. ActivateOutputCanvasAsync explicitly
requests activation of the exact SavedRevision; successful acceptance initially
reports PENDING_BOUNDARY, not applied output.

Clients hold drafts. Draft EditRevision must be the next SavedRevision, with the
same ConfigurationId. ValidateOutputCanvasAsync returns that identity/revision and
path-specific geometry, reference, provider/profile and capacity issues.
SaveOutputCanvasAsync accepts a versioned transaction GUID, ConfigurationId,
ExpectedSavedRevision and 1–256 typed edits. Add/Update/Remove Canvas/Surface,
Bind and Unbind enforce exact payload/identity/existence rules and apply atomically.
Removing a surface on a bound canvas requires an earlier explicit Unbind in the
same transaction or a previous transaction. RemoveCanvas also requires explicit
surface removal. There is no implicit rebind, live reroute or source substitution.

SavedRevision advances only on a durable successful write. Stale writes, invalid
models and unsupported provider/profile assignments do not advance it. Last 64
transaction identities and canonical request SHA-256 hashes are persisted together
with the model. A matching retained replay returns current saved state with
Replayed=true and no write; different content fails transaction_conflict. After
receipt eviction the old expected revision still prevents a second write.
History retains the previous four configurations; it is recovery input, not live
undo. Existing database backup/integrity/upgrade tooling covers the same show.

## Provider boundary and caps

AvailableTargets comes from the fresh Runtime output-role snapshot intersected
with current Control-authorized roles/source bindings. ProviderId and TargetId
are the actual admitted sink identities, ProfileId is the existing production
format policy, and dimensions come from that sink. RoleId gives the authorized
role context. It is configuration capability evidence, not physical certification
or current healthy frame evidence. No synthetic display labels, new hardware,
drivers, formats or extra professional ports are advertised.

There may be 16 canvases, 128 surfaces and 32 bindings, independently of the two
current live roles. Each dimension is 1–16384; total logical area is at most
67,108,864 pixels. Model/request JSON is at most 256 KiB, four retained historical
models plus 64 receipts at most 2 MiB. One provider/target can have one binding,
including disabled bindings. These are finite authoring caps, not allocations or
claims that the renderer/hardware can execute them. Runtime activation must
separately admit actual surfaces, bytes, queues and provider capabilities.

## Persistence and compatibility

Legacy shows remain rtaime.show-project.v1 until their first successful canvas
save. Get deterministically reconstructs one identity canvas and one surface per
existing Program/Aux role, with production dimensions, full crop, role bus reference
and existing target/enable state. SHA-256 of the production identity and canonical
role key supplies repeatable IDs; no live state, checkpoint or Take changes.

First canvas save writes the additive OutputCanvasJson subdocument inside
**rtaime.show-project.v2**, containing **rtaime.output-canvas.v1**. This deliberate
show format advance makes older binaries reject startup instead of silently
discarding the new state on write. The new reader accepts v1 without the extension
and v2 with it, preserving scenes, adopted sources, graphics, show control,
rundown and macros. Unknown show/model/history versions and malformed documents
fail closed without replacement. Existing checkpoint payload/schema remains
unchanged. Before downgrade, retain a full database backup and follow existing
upgrade/recovery policy; do not strip the extension from a saved show.

SQLite outer-document optimistic concurrency, checksum, transaction, WAL and FULL
synchronous durability remain the commit boundary. Cancellation before commit
preserves the previous state; response loss after commit requires Get or replay of
the same transaction, never an assumption that cancellation proves rejection.

## Transport and authorization

The existing Named Pipe dispatcher and authenticated TLS gRPC dispatcher expose:

| Capability | External operation tag | Minimum external role |
| --- | --- | --- |
| control.output.canvas.get | 146 | Observer |
| control.output.canvas.validate | 147 | Observer |
| control.output.canvas.save | 148 | Operator |
| control.output.canvas.activate | 149 | Operator |

Tags 150–161 remain allocated to this domain and unconsumed. Existing tags and
wire/API versions are preserved. Request context, authentication, rate/in-flight
quotas, frame size, deadline and host replacement/resynchronization remain those
of the existing transports. Canvas JSON additionally rejects duplicate/unknown
properties and excessive depth/bytes. Older peers reject unknown operations through
their existing structured unknown-operation paths. The activation endpoint is a governed Program mapping mutation, not a generic
Aux command or output-routing bypass.

## Qualification and rendering handoff

OutputCanvasModelTests covers exact identity/perspective mapping, geometry failures,
multiple sizes/surfaces, invalid crops, source/provider/profile failures and finite
dimensions. OutputCanvasContractTests covers strict bounded serialization.
The Canvas_ cases in ExternalControlIntegrationTests and
ShowProjectPersistenceIntegrationTests exercise both real transports, Observer
denial, saved revision conflicts, transaction replay, source-state preservation,
Control restart, legacy reconstruction, cancellation and fail-closed future data.

Run the focused cases after the canonical Windows Release build, then the affected
projects and full solution as described in [Build and test](BuildAndTest.md).
The existing ProcessSupervisionRecoveryTests killed-ControlHost case also holds a
real SQLite writer lock, issues an in-flight canvas save, kills the owned process
and verifies the prior saved revision/receipt after supervised restart. Retrying
the interrupted transaction then commits exactly once without advancing Program.
This tests actual process termination with a blocked save; it does not simulate
power loss inside the SQLite commit or qualify storage hardware durability.
The PR records actual exact-source Required Gates evidence. Physical power-loss,
hardware output and renderer pixel qualification remain separate unverified
evidence boundaries. Existing hardware/release NO-GO findings remain.

The next functional consumer is the canvas/warp editor using the real Client
methods, and independent device/profile qualification for physical output.
Source generations, committed output identity, draft-versus-active state and
provider resource ownership remain mandatory.


## Operator canvas editor

OUTPUTS presents one interactive 2D authoring surface without constructing
another Program compositor. The `OutputCanvasStageSurface` draws only local
surface contours, independent of Runtime media frames. It fits the logical
canvas to available WPF DIPs, then applies local pan/zoom and explicitly
converts pointer coordinates to logical canvas edges. DPI pixel alignment
is a presentation detail; no rounded screen coordinate is persisted as a
media-source or device pixel. Mouse wheel zoom holds the logical pixel under
the pointer. Middle-drag pans; body drag translates, four corner handles
warp, Alt-drag rotates, Ctrl bypasses snapping and Shift constrains moves.
Arrow keys move 1 logical pixel (Shift: 10); Home fits the stage. Numerical
TL/TR/BR/BL, crop and blend values use the same validated authored geometry.

The Editor reads `GetOutputCanvasAsync` and optional Runtime mapping
evidence. It maintains a finite local 48-step draft history; Undo and Redo
never perform a TAKE, edit Program or revert a previous live route.
`ValidateOutputCanvasAsync` checks the proposed next EditRevision before
`SaveOutputCanvasAsync` sends only the edited canvas/surface/binding records.
When a bound surface is removed, the finite transaction explicitly unbinds
the affected output before removal and restores surviving intended bindings.
ControlHost remains the sole persistency and optimistic-concurrency authority.

The UI shows `SavedRevision`, local next revision, requested/pending mapping
revision and separately confirmed `ActiveRenderRevision`. Only
`ActivateOutputCanvasAsync` can request the saved revision; `PENDING_BOUNDARY`
is not represented as active. The existing Operator health stream triggers
bounded follow-up evidence reads while activation awaits its Runtime boundary,
without an independent ticker or global command registry. Target, output
size, source identity, rendered sequence and failure reasons are taken from
Runtime; no local editor-preview pixel is labeled a verified output.

Unconfirmed saves are reconciled by reading the authoritative snapshot. A
matching saved revision/content can close a lost-response transaction;
different stored revision blocks subsequent editing persistence until the
operator explicitly discards/reloads. Pending mapping and stale Runtime
states remain unconfirmed. Saving never activates mapping; disabling mapping
is also an explicit governed request. Hardware discovery, physical warp/
blend calibration and unqualified multi-output/HDR operation remain outside
this UI. Output monitoring and routing, Clean Program, recording, streaming
and explicit test patterns keep their existing independently authorized paths.

## Current output renderer and operational semantics

RuntimeHost compiles validated saved canvas geometry into a finite, immutable
output-side plan. The plan binds the exact saved EditRevision, current
RuntimeExecutionRevision and governed Program provider/target identity. ControlHost
passes the observed RuntimeHost process identity; a replacement during activation
rejects the operation. Preparation never changes Program. Commit queues the
prepared plan and the RuntimeHost swaps it only at the next frame boundary,
where the execution revision and target are checked again. A subsequent execution
commit invalidates an active mapping and returns to the legacy Program output;
activation is not silently replayed across changes of execution authority.

The existing composite remains the sole Program compositor. Each eligible
mapped surface obtains its current committed source or the post-composite Program
frame; a separate output transform uses one inverse projective homography, one
bilinear filtered crop and destination clipping. Visible surfaces are composited
in stable authored z-order. Alpha and overlap use RGBA8 encoded-space source-over.
Optional EdgeBlend widths (left/top/right/bottom) are normalized values in
[0,0.5]; curves are "linear" or "smoothstep". The managed reference backend
flattens the final mapped result over opaque black. No linear-light projector
compensation, calibrated gamma, 10-bit, HDR or physical display output is claimed.

The runtime mapping capability is currently limited to the **managed-reference
GPU backend** and **one governed Program output target**, with at most eight
visible surfaces and a 16 MiB maximum additional mapped framebuffer allocation.
Canvas authoring limits are larger: saving an authored model does not guarantee
that it can be activated by the current provider. Other GPU backends and any
multi-output mapping are rejected explicitly, not emulated or redirected. The
existing Program output is untouched for an identity pass and when mapping is
inactive. On mapping failure, RuntimeHost records the failure and falls back
to the original Program frame; it does not silently claim that warped output
was delivered. Preview capture remains pre-mapping; Program/output monitoring
and Program recording/output pixels are post-mapping where the existing consumers
receive them.

### Bounded mapping preparation and frame ownership

The managed reference raster validates every surface, crop, source byte extent and
inverse homography before clearing or writing the destination. Its per-boundary
z-order sort now uses at most eight stack-local indices; source memory references
use a fixed inline value buffer, and inverse homographies use bounded stack
storage. Crop tap bounds are calculated once per surface, not once per destination
pixel. Canonical GUID identity ordering preserves the same deterministic
z-order tie-break. The backend reuses its source resolver delegate rather than
constructing a new captured delegate per mapped frame. These adjustments
remove temporary mapping-preparation allocations without creating another
Program framebuffer, changing encoded RGBA8 math or removing the existing
separately owned mapped output surface.

A mapped input must also carry the **same Program sequence number** as the
frame being mapped; the provider rejects a retained previous-boundary frame
with `gpu.output_mapping.sequence_stale` before mapping allocation. The full
timestamp/timebase need not match a different input's native clock, so the
check is specifically the committed Program slot. Source identity, provider
ownership, disposal and video format remain independently checked. Partial
render failure releases the newly allocated mapping surface; if that release
fails it remains explicitly tracked for retry at shutdown rather than being
considered free. A returned mapped frame owns its output surface until the
last frame reference releases it. Monitoring and recording can retain the
**same** existing readback lease after the mapped surface itself is retired.

The CUDA backend still does **not** implement `IGpuOutputMappingBackend`.
In addition to the missing native implementation, the provider-level
capability gate deliberately admits only `ManagedReference`: merely adding
the interface to a hardware backend cannot accidentally turn on unqualified
warp/blend. The public GPU provider also rejects any RGBA8 mapped output
above the same 16-MiB additional framebuffer bound enforced by Runtime,
before allocating a second full-frame mapping surface. This is defense in
depth against direct provider clients, not an expanded format envelope.
Hardware compositing/monitoring capabilities cannot advertise warp/blend
or trigger CPU readback-and-reupload as a fallback. An unsupported mapping
request is rejected at the existing runtime preparation boundary; legacy
Program output remains authoritative. The software qualification uses the
real `OutputMappingGpuTests`, `OutputMappingSurfaceOwnershipTests` and
`OutputMappingRuntimeIntegrationTests`, plus a separately labeled
bounded `OutputMappingSoftwarePerformanceTests` P50/P95/P99/GC observation
at 50 and 59.94 fps. This is **not** 1080p CUDA output or physical scan-out
qualification.

These are functional software paths, not real-time/hardware qualifications:
the managed reference raster is CPU-executed and not approved for guaranteed
50/59.94 fps physical scan-out. Pixel parity, Runtime virtual sink, memory
retention and failure paths need source-bound CI evidence. Hardware projector
blending, driver capability, physical scan-out, HDR and sustained frame cadence
remain independently unverified.
