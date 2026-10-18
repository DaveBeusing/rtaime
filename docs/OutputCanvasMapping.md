<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Output canvas configuration and mapping

Control owns the versioned, durable authored 2D model in the canonical show project.
Operator consumers use Client → ControlHost. This backend provides configuration,
validation and saving; it does not activate a mapping renderer. Existing governed
Program/Aux execution continues unchanged. Preview and Clean Feed are monitoring,
never enumerated physical output targets.

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
must clip to canvas edges and leave uncovered output transparent black.
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
ActiveRenderRevision and ActivationState. ActiveRenderRevision is null and
ActivationState is RENDERER_REQUIRED until a separately implemented renderer has
an explicit, qualified Prepare/Commit activation path. Saving never calls Runtime
Prepare/Commit, changes Program or requires a Take.

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

Tags 149–161 remain allocated to this domain and unconsumed. Existing tags and
wire/API versions are preserved. Request context, authentication, rate/in-flight
quotas, frame size, deadline and host replacement/resynchronization remain those
of the existing transports. Canvas JSON additionally rejects duplicate/unknown
properties and excessive depth/bytes. Older peers reject unknown operations through
their existing structured unknown-operation paths. There is no activation endpoint
and no generic Aux command for changing Program.

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
The PR records actual exact-source Required Gates evidence. A cancelled-save case
does not establish actual OS termination during the SQLite commit; physical
crash/power-loss, hardware output and renderer pixel qualification remain separate
unverified evidence boundaries. Existing hardware/release NO-GO findings remain.

The next functional consumer is the mapping renderer/Prepare-Commit activation,
then the editor using these real Client methods. Consumers must preserve stable IDs,
crop/coordinate semantics, expected revision, configured versus active distinction,
source generation fencing and actual Runtime resource ownership.
