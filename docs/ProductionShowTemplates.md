<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->
# Portable production setup templates

## Capability map and authority

The durable show project remains the sole authored production document in the existing management database. Saved scenes already preserve routing and typed compositing settings. Show Control already owns bounded authored cue sequences and their execution/recovery state. Rundown and Production Macros retain their existing authored formats and governed execution coordinators. Operator workspace presets remain local presentation settings.

The missing reusable operation was a portable scene/cue setup definition with explicit local dependencies, an observable dry run and an optimistic add-only import. Templates fill that gap by reusing those definitions, the existing ControlHost authoring boundary and the existing project write. There is no template database, scheduler, macro authority or automatic production execution.

This is a constrained authoring improvement within the existing V1 scene, cue, audio-input and output-routing capabilities. It does not add an output provider, production command, automation trigger or physical qualification claim.

## Operator workflow

Open Show Control → TEMPLATES.

1. Set a template name. CURRENT SETUP captures saved scenes and creates a cue for the current committed routing, persisted audio mode/input gain and mute, and non-Program output routes. SAVED CUES captures the selected **saved** cue list with saved scenes. Unsaved cue-editor changes are not exported.
2. EXPORT FILE writes a new JSON file. Existing files are rejected rather than silently replaced. Recording-start actions must be authored locally because their destinations are machine-specific; a cue list containing them cannot be exported.
3. IMPORT FILE validates the format, contract compatibility, checksum and bounded canonical content. The result is visibly DRAFT. Importing or creating a draft sends no production action.
4. Choose a new show name and explicit local dependency targets. Source/output/layer choices use the observed local production; media choices come from the local Media Library. Unavailable targets remain unresolved. Adopt sources and prepare/relink media locally before continuing.
5. PREVIEW CHANGES lists the scenes and cue list to add and reports missing, incompatible, stale or active-state conflicts. Names are scoped as `<new show> / <scene>`. Existing scene and show names are compared without case sensitivity.
6. SAVE NEW SHOW DEFINITIONS rechecks dependencies and versions, then adds new scene, cue-list, cue and action identities in one durable write. It does not select the list, ARM, GO, recall a scene or change Preview/Program. A separate explicit selection and governed ARM/GO workflow is required to execute the imported cues.

Changing a dependency or instance name invalidates the preview. A response arriving after a draft edit cannot re-enable commit. Editing is disabled while an operation is in progress.

## Portable format and bindings

Format `rtaime.show-template.v1` contains a template identity, display name, required Control contract version, canonical scene/cue definitions, declared dependencies and a SHA-256 checksum over the complete document with an empty checksum field. Size is limited to 256 KiB and depth to 32. The canonical cue and scene limits remain in force. Duplicate JSON properties, unknown envelope fields, noncanonical embedded definitions, unsupported formats/contracts and checksum mismatches fail closed.

Source dependencies declare their provider capability kind. Output dependencies declare the role kind. Scene references point to included scenes and are remapped to new identities. Source, output, media, media-cue and layer bindings are explicit; undeclared references and duplicate/missing bindings are rejected.

Media dependencies contain asset identities, not filesystem locations. The target must exist and be online in the local persistent catalog. A media cue must exist in the locally loaded deck and belong to the mapped asset. Preflight does not load or play media.

Compositing transforms and typed processing settings reuse the existing scene codec. Layer content is exported as an opaque SHA-256 identity, then resolved to matching locally prepared graphics during preflight. Bitmap payloads and CG text are not packaged, and layer kinds cannot be rebound to another kind. Scenes requiring inconsistent content for the same layer are rejected at export. Templates contain no provider endpoints, device credentials, recording paths, media paths, workspace layout or execution cursor.

The checksum detects accidental modification; it is not a signature or a trust assertion. Import never authorizes execution.

## Compatibility, conflicts and recovery

Only the explicit v1 format and currently supported Control contract are accepted. There is no historical template format to migrate. Newer or unknown versions are rejected without rewriting them or replacing current project state. Future migrations require a separately reviewed versioned codec and tests. Existing durable project and compositing migrations remain unchanged.

ControlHost preflight uses fresh Runtime input/output observations. Unavailable or stale Runtime evidence blocks dependent imports; a source being present in the logical catalog is not hardware readiness. Adopted external inputs require a fresh signal observation and matching provider capability. Output roles require fresh healthy authoritative-active confirmation.

The import compares both the show-project storage version and Show Control workspace version, under the existing serialization gates. A concurrent macro, rundown, audio, scene or cue write makes the preview stale. The project write atomically stores both appended scenes and the new cue list before publishing their in-memory authored catalogs. Existing scenes, active scene, Program routing, selected cue list and execution state remain intact. Capacity is bounded at 128 total scenes and 32 persisted cue lists. Armed, executing, waiting and recovery-required Show Control states block import.

On a rejection, preserve the local draft, resolve the reason and preview again. After a timeout or lost response, refresh the show and check for the new show name before retrying: a durable commit may have succeeded before feedback was lost. Duplicate names prevent a blind retry from adding another instance. Restart restores committed definitions through normal durable project loading and never replays imported actions.

## Packaging limits

A template is a scene/cue setup recipe, not a full project backup. CURRENT SETUP captures audio input gain/mute and audio-routing mode; advanced bus dynamics, EQ, crossfade/ducking, CG text and bitmap data continue to use their existing authored project settings. No local file paths, provider adoption, output endpoint provisioning or bulk media transfer occurs. Production Macro and Rundown libraries, their cursor/recovery state, and Operator workspace layouts stay in their existing formats and workflows.

The reference local Operator named-pipe transport exposes create, preflight and instantiate operations. Existing external gRPC clients report template operations as unavailable until their separately governed API surface supports them; they must not fall back to unguarded cue saves.

## Validation and evidence

Contract regressions cover version/corruption/unknown-field rejection, private recording-destination rejection, fresh identities and action parameter rebinding. Coordinator regressions exercise dry-run immutability, atomic authored import, restart without replay, duplicate names, stale concurrent edits, absent sources/provider conflicts and cancellation. Operator regressions cover draft-only actions, edits during preflight, conflict gating and rejection recovery. Required Gates run focused template regressions before broader solution tests.

Canonical local build remains `./build/development/Invoke-DeveloperBuild.ps1 -Configuration Release`. Hosted validation uses the pinned SDK and repository Release build path. Physical media hardware, external provider interoperability, long-soak continuity and physical mixed-monitor DPI remain UNVERIFIED.
