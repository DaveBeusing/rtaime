<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
All rights reserved.
-->

<p align='right'>
	<img src='../src/Hosts/rtaime.Operator/Assets/Brand/RtaimeLogoHorizontal.svg' alt='rtaime — Real Time AI Media Engine' width='180' />
</p>

# Media Library & Asset Browser

## Purpose

The Operator Media Library is a persistent, production-usable catalogue for explicitly imported local media assets. Catalogue identity survives application and ControlHost restart, while the Operator remains a presentation and interaction surface only.

Independent Media Library browsing and time-indexed analysis belong to the approved [product scope](ProductScope.md). Current implementation evidence does not retroactively broaden historical software tests or hardware qualification.

Runtime remains authoritative for decoder capability and media execution. ControlHost owns catalogue mutation and persistence orchestration. The Client carries bounded catalogue metadata. The Operator owns only local selection, filtering and interaction state.

## Stable asset identity

Every catalogue entry has an immutable `MediaAssetId`.

The identity is independent of current Operator selection, production source slot, filename, source path and Media Deck runtime lifetime. A relink changes the source location while preserving the same `MediaAssetId`. Later Scene, Show Control and Timeline persistence can therefore reference catalogue assets by stable identity rather than by path or transient UI objects.

The catalogue stores stable `AssetId`, local-file origin, canonical source path, display name, container/codec/format metadata, duration, source file length, SHA-256 content fingerprint, import/update timestamps and availability state.

## Ownership and persistence

The catalogue reuses `SqliteManagementStore`; no second persistence stack or DAM/MAM database is introduced.

ControlHost stores one versioned, checksummed management document under area `media.asset.catalog`, key `catalog`, format `rtaime.media-asset-catalog.v1`.

Writes use the existing optimistic document-version mechanism. A failed or stale write cannot partially replace the persisted catalogue document. The catalogue is outside production-critical media execution; Runtime decode, rendering, audio and output paths do not synchronously depend on SQLite catalogue access.

## Import

**Import Media** opens a bounded multi-file picker. The current Operator workflow selects MP4 files and sends only their local paths to ControlHost. The Operator prepares up to 128 selected files for review before confirmation. Each confirmed item uses one existing import request; ControlHost's general single-request limit remains 128 files.

For each candidate ControlHost canonicalizes the path, verifies readability, computes a SHA-256 fingerprint, applies deterministic duplicate/conflict rules, asks RuntimeHost to probe through the existing local-media decoder path, creates a new immutable `MediaAssetId` only for a new accepted asset, and persists the resulting catalogue atomically after the bounded batch is evaluated.

Unsupported, corrupt, unreadable or missing candidates are returned as explicit failures and are not inserted as partial catalogue records. No recursive drive indexing, directory crawler, remote transfer, proxy generation or transcoding is performed.

## Duplicate policy

- Same canonical source path and same fingerprint returns the existing asset as `Duplicate`.
- The same fingerprint at a different path also returns the existing asset as `Duplicate`.
- An existing source path whose content fingerprint changed is rejected as a source conflict rather than silently changing asset identity.

Independent catalogue records for byte-identical content are therefore not created implicitly.

## Availability

Catalogue assets remain visible when their source file becomes unavailable. Availability is `ONLINE` when the local file can be opened for reading, `MISSING` when the file or containing directory no longer exists, and `OFFLINE` for other controlled access/I/O failures.

Availability is evaluated only at controlled catalogue read/refresh points. No background hot polling loop is introduced. A missing/offline asset remains searchable and inspectable and can be recovered through **Relink Media…**.

## Relink

Relink preserves `MediaAssetId`. The replacement must be readable, not already assigned as another asset source, not collide with another catalogue fingerprint, have the same SHA-256 fingerprint as the original asset, and still pass the existing Runtime media probe.

The strict fingerprint requirement prevents a different or modified file from silently inheriting references intended for the original asset. After the Runtime probe, relink rechecks the source fingerprint to reject a file modified while the operation was in flight. The original catalogue identity and source location remain unchanged on rejection.

## Remove from Library

**Remove from Library** deletes only the catalogue record. It never deletes, modifies or moves the underlying media file.

## Operator projection

The Media Library projects authoritative production Sources, persistent catalogue Clips, Audio inputs, the currently loaded Graphics asset and the current Composition / AI feature.

A currently loaded non-catalogued clip, such as an existing demo path, remains visible as a compatibility fallback. Persistent catalogue clips use `AssetId` as their reference identity.

The presentation projection remains bounded to 4096 entries. Grid and List reuse the same bounded virtualizing panel and recycling generator, deterministic search/filter ordering and local extended multi-selection. List uses one full-width column with the shared 48-DIP row token; direct index geometry avoids estimated-height jumps for distant rows. Both views retain the same target-container and resource-bound qualification. `Ctrl+F` focuses Media Library search. Search matches projected name, detail, format, state, stable reference and known local path.

## Preview and existing workflows

For a persistent Clip, **Open in Preview** requires an online catalogue entry and selected production source slot, opens the catalogue source through the existing Media Deck command path while supplying the stable `AssetId`, verifies the loaded identity, then uses the existing Preview command.

The catalogue therefore does not create a second playback path or routing authority. Existing Timeline and Cue behavior remains on established command paths. Cue actions apply only when the selected catalogue `AssetId` is the currently loaded Media Deck asset.

## Guided media-first Library actions

The Library keeps the search, virtualized grid/list and persistent catalogue
unchanged. A compact, always-visible selection strip now presents the
catalogue name, typed duration/format and current readiness with four explicit
next actions: **Inspect**, **Prepare Options**, **Open Preview** and **Relink**.
**Import Media** is prominent; the existing Add menu still contains generator
and refresh actions. Advanced **More Tools** reveals Smart Bin editing,
metadata filters, bounded batch operations and representative preflight
options without making them prerequisites for ordinary playback.

**Inspect** opens local metadata/capability details for the selected asset;
hover frames remain independently cancelable and never seek a production
deck. **Prepare Options** exposes the existing optional representative
sample checks, not a new resident preload owner. A recent
`CONDITIONALLY_READY` result is not `READY` or a retained deck. For one
eligible, online catalogue Clip, **Open Preview** reuses the existing Media
Deck open and authoritative Set Preview request. It retains the selected
source and asset fingerprint/revision across the asynchronous open; a
changed selection, catalogue identity, source slot or Control connection
prevents the subsequent route request. A Clip cannot be opened by this
guided action into the active Program source; first choose an off-Program
A/B slot. The displayed
`PREVIEW REQUESTED` message is pending authority, not a claim that
Preview pixels or Program output were already delivered. The resident
Deck and confirmed route remain separate evidence.

Missing, offline or stale items get a specific recovery message and a
guarded **Relink** path, preserving stable catalogue identity and
fingerprint validation in ControlHost. Multi-selection cannot open
Preview or mutate a single asset; the existing bounded batch workflows
remain accessible via **More Tools**. No single selection, hover,
preflight, import or local navigation implicitly calls Take, CUT or
DISSOLVE. The existing drag/drop route is still an independent legacy
interaction surface and is not silently rewritten by the selection
strip; later cross-workspace consolidation must retain the same
post-await stale-source fences and explicit-authority rules.

## Context actions

**Clear selection** in the Add menu clears the entire local selection. **Refresh availability** in the Add menu uses the existing catalogue-wide availability refresh. Single-target Preview, Timeline, Cue, Relink and Remove context actions stay disabled during multi-selection. Use **Batch · Remove selected…** for explicit multi-record removal with preflight and confirmation. Reveal and Properties remain read-only single-target inspection actions. No source file is deleted.

Catalogue Clip cards and list rows expose **Open in Preview**, **Add to Timeline**, **Add Cue at Playhead**, **Reveal in Explorer**, **Relink Media…**, **Remove from Library** and **Properties**.

The current Scene model has no governed media-assignment mutation path. Scene assignment remains out of scope until that authority path exists.

## Loading, empty and error states

The existing Media Library loading/error surfaces now include catalogue operations as well as Media Deck activity. Explicit failures are preserved for missing/unreadable files, unsupported media, duplicate/source conflicts, relink conflicts, relink content mismatch, persistence and IPC failures.

A catalogue failure does not make the Operator authoritative and does not alter already committed production execution.

## Metadata transport

Operator/Client/ControlHost transport carries catalogue metadata only. Catalogue snapshots are paged in bounded groups of at most 256 assets while retaining the existing 1 MiB Control IPC frame limit.

The Client restarts a paged read if catalogue revision changes between pages, preventing a mixed-revision snapshot. Mutation responses carry bounded result metadata; the Client then reads the resulting catalogue through the same paged snapshot path. Raw media bytes are never sent through management/control IPC.

## Smart Bins

The custom controls below the existing search/type filters save up to 16 named filter combinations. Use **Filters** to reveal the duration/readiness row without permanently reducing the browser viewport. Choose a known-duration range (under one minute, one to ten minutes, ten minutes or longer, or unknown) and presentation readiness, then **Save**. The expanded Filters area also provides a **Smart Bin name** field (128 characters). A blank name keeps the generated-name fallback; a nonblank name must be unique ignoring case. **Rename** changes only the selected saved definition's name and preserves its query. To save changed filters under another definition, enter a new name first. Selecting a saved entry restores search, category, type/availability, duration and readiness together. Changing a filter detaches the saved selection without overwriting its definition. **Del** removes only the saved filter.

Duration comes directly from the immutable catalogue descriptor, never from formatted text or a file probe. Non-catalogue items without typed duration remain unknown. Readiness is the existing presentation hint and does not qualify Program or startup production readiness. Tags are not supported by the catalogue and are not offered.

Preferences live separately in `%LOCALAPPDATA%/rtaime/operator-media-bins.json`, version 1. Reads are asynchronous and limited to 64 KiB, names to 128 characters and search to 256. Valid version-1 definitions retain the same schema and query semantics. Invalid search expressions, control characters in names, duplicate names and excess definitions are ignored individually with visible feedback; malformed, oversized, incomplete or future-version documents are rejected with visible feedback. Loading never rewrites the file. Unknown typed metadata still matches neither positive nor negated codec/resolution/FPS terms. Saves are serialized and atomically replace only this presentation file; failures are displayed in the Library footer and retained in the saved-bin tooltip. Shutdown waits for the pending preference save. Catalogue persistence, recovery checkpoints and production state are unaffected.

## Independent library frame scrubbing

For online catalogue Clips with a stable asset ID and known positive duration, the
Library issues read-only, time-indexed **Frame** analysis through
`MediaPoolInspectorViewModel` → `IMediaAssetCatalogClient` → ControlHost catalogue
authority → RuntimeHost's bounded analysis worker. Raw RGBA8 pixels return only through
the authenticated local analysis observation pipe, not through Program monitoring or
management IPC. Remote gRPC metadata-only clients cannot display the extracted frame
until a separately qualified pixel channel exists.

Move over the **thumbnail area** of a Grid tile or a List row to map horizontal pointer
position to the catalogued duration, clamped before the end of the asset. Focus an item
to preview its current/middle position, then use **Ctrl+Left** or **Ctrl+Right** in either
browser to move in 5% increments without changing ListBox navigation. A 180-ms latest-only
dwell/debounce cancels superseded requests. The current tile/List image is replaced by
a frozen, bounded 320×180 frame only while it is the active scrub target. The original
monitoring image is never overwritten. The timecode overlay shows requested position;
the footer separately reports **requested time** and **actual decoded PTS**, or Loading,
Unavailable, Offline, Unsupported and Stale. A decoded timestamp is not guessed from
the pointer fraction.

**Hover / scrub previews** in Filters is a session-local switch. Disable, leave,
switch filters/view, change asset selection or catalogue revision, begin a drag,
receive fresh measured Runtime pressure, disconnect or close the Operator to cancel
the active request and clear the temporary bitmap. Per-consumer generations, asset ID,
Runtime epoch and observation fields are checked before display. A returned job ID is
retained even when the pointer has left, so the canceled job can be explicitly
cancelled at the Runtime boundary. The client has a 15-second optional presentation
deadline and a bounded cleanup request; a transport cancellation response does not
prove native decoder release. Runtime keeps its shared resource reservation until
actual cleanup.

Non-catalogue sources can still show a separately labelled **MONITOR THUMBNAIL**,
which is **not** time-indexed. Offline, missing, unknown-duration and unsupported
assets never receive a fabricated frame. No media decoder, filesystem crawler,
fingerprinter, media clock, second cache, or additional monitoring poller runs in
the Operator. Hover does not call Media Deck open/seek, change Preview/Program routes,
or perform a Take. Analysis remains optional and lower priority than production;
see [IndependentMediaAnalysis](IndependentMediaAnalysis.md) for resource admission,
real decoder coverage, and remote-transport limitations.

Dragging reports the frozen selection size and the primary item effect. Dropping
the primary item into the existing Timeline creates a local authoring draft;
dropping onto Preview follows the established explicit deck route. A multi-item
selection is not silently bulk-added, and no drop implicitly takes Program.
Selection-specific context menus require one eligible asset for mutating actions,
Reveal and Properties; explicit bounded batch controls remain available.

**Workflow help:** Import Media → independent Analysis (visual inspection only) →
Prepare/Retry (representative conditional evidence) → Open in Preview (resident deck
preparation) → explicit Take/CUT or AUTO. File Availability, time-bound conditional
verification and resident PREPARED are separate: each preparation evidence row
shows the current catalogue availability, sample state, observation age, resident-deck
state and the actual reason. Analysis never upgrades preparation readiness.

## Filmstrip preview preparation

A reusable `MediaAssetFilmstripService` provides catalogue-fenced filmstrip
data. **Inspect → REVIEW FRAMES** explicitly starts one read-only operation
for the selected online catalogue Clip and publishes up to **five** bounded,
time-indexed source images inside a horizontally scrollable inspection strip.
It aligns samples with the source's rational nominal frame rate and labels
*actual decoded PTS* where confirmed; failures and unavailable pixels retain
their own visible state. **CLEAR REVIEW** cancels and removes samples.
Library hover remains an independent, single-frame scrubbing consumer.

The selected Inspector also offers source-relative **IN % / OUT %** inspection
sliders (default 0–100). These are local, non-destructive observation windows:
they do not change saved sequence trims, Media Deck IN/OUT, Preview or Program.
An invalid/zero-width window cannot request analysis; changing markers cancels
superseded jobs and clears old output. Filmstrip frame positions and timestamps
refer to the selected window. **REVIEW AUDIO** reuses the existing
`SequenceTimelineWaveformService` and `SequenceWaveformGlyph` to display at
most 120 seconds and 128 actual decoded PCM buckets from the selected range.
The temporary source reference is observation-only, never written to Sequence
authoring. Missing/discontinuous samples remain gaps. Audio review verifies
the authoritative fingerprint before and after the analysis operation and is
canceled on selection, relink, offline or Control disconnect. One optional
review runs at a time; frame and PCM admission never fan out for the full tile
collection.

Selection changes, multi-selection, catalogue revision/fingerprint/length/
timestamp changes, offline/relink and Control disconnection cancel the
review and clear old images. Passive refresh and the new local `SORT` control
rebind selected projection records by stable asset key without creating a
second recent-item visit. Sorting offers category/name, name, short/long
known duration (unknown last), and ready-first; it does not rewrite catalogue
order, Smart Bin preferences or Timeline asset IDs. The default sort remains
category/name for saved workspace compatibility.

The optional client has one replaceable operation, 150-ms debounce,
coalesced identical in-flight requests, 45-second overall time budget and
frozen local image-cache limits of **16 strips / 8 MiB / ten seconds**.
Before reuse it confirms the current authoritative catalogue fingerprint,
asset identity, online state and source rate. A changed/offline asset yields
STALE with no returned old pixels. The Runtime decoder owns all actual frame
extraction, file identity checks and disk caching; no Program playback path
is used. See [IndependentMediaAnalysis](IndependentMediaAnalysis.md).

## Performance and authority boundaries

Search, filtering, selection and scrolling remain in-process Operator projection work and perform no blocking media reads. File hashing and Runtime probing occur only during explicit catalogue operations and are outside production-critical media/render paths.

There is no recursive automatic indexing and no second media index. Batch presentation coordinates existing single-item Client calls; it introduces no bulk-media IPC, decoder, scheduler, catalogue authority or source-file deletion.

## Verification

Regression coverage verifies stable `AssetId` across store close/reopen, duplicate import identity, unsupported-media rejection, missing-file projection, relink preserving identity, relink conflict rejection, remove-from-library without source-file deletion, optimistic persistence conflict behavior, bounded import size and cancellation without partial catalogue records.

The Operator UI policy gate verifies persistent catalogue projection, bounded/virtualized presentation, multi-file import, Relink and Remove workflow wiring, and the existing production interaction paths.

## Smart Bin and hover qualification

`MediaLibraryBrowsingTests` checks duration boundaries and unknown metadata, independent readiness/availability, bounded preference round-trip and corrupt/future/oversized files, cancellation without replacing valid preferences, 1000 rapid hover requests, existing-image/offline capability handling, clamped scrub timestamps, true decoded PTS and RGBA/BGRA transfer, latest-generation cancellation, stale identity rejection and a 30,000-record catalogue projected to at most 4096 items. Repeated search and saved-filter restoration assert no production transport calls, catalogue mutations or routing changes. Additional cases cover named save/rename/restart, valid v1 preservation, unsupported query feedback without rewriting, enable/disable cancellation storms, fresh/stale/future pressure and a pressure change during dwell with unchanged Preview/Program and no mutation requests. Hidden selections are pruned.

Sorting, stable-instance rebinding after catalogue refresh, bounded
source-relative IN/OUT math and accessible filmstrip/PCM actions are covered by
focused Operator regressions. Existing `SequenceTimelineWaveformTests` and
`MediaAssetFilmstripServiceTests` verify the reusable analysis consumers.
These checks do not qualify physical presentation or hardware audio output.
Library review markers are not authored per-asset IN/OUT edits: the
established Media Deck and sequence editor retain transport/editing authority.

The real `OperatorXamlStartupTests` window verifies custom control keyboard focus/accessibility names and scrolls both 4096-item Grid/List views while bounding realized containers. Existing compact-window and source-bound functional/visual checks remain required. The independent Runtime/Client host-path tests are owned by `IndependentMediaAnalysisIntegrationTests`; Operator regression coverage does not replace them. Physical keyboard/screen-reader usability, mixed-DPI interaction, VFR/audio-only availability and hardware Program render/IPC latency under real analysis load remain **UNVERIFIED** until reference-platform execution. Source-level tests or hosted geometry do not substitute for those measurements.

## Local recent references and favorites

The Commands menu's Recent/Favorites surface reuses stable Media Library keys for sources, catalogue clips and audio streams. Enable local history explicitly; multi-selection records its primary selected item. Favorite media toggles that current selection. Opening a reference clears local browser filters and selects the current item for inspection, with no Preview load or Program mutation. Renames retain the reference; removed items can be removed locally. Missing-path evidence comes from the current authoritative catalogue and its explicit Refresh/Relink actions. Graphics names and other non-stable presentation keys are not stored as favorites.

See [Operator commands and shortcuts](OperatorCommandsAndShortcuts.md#recent-items-favorites-and-inline-help) for privacy, bounds, help search and unavailable-item recovery.

## Smart search expressions

Search combines up to 16 terms with AND inside the existing 256-character limit. Quote phrases and prefix a term with `-` to exclude it. The WPF search binding waits 180 ms after typing; saved-bin restoration and programmatic filtering remain immediate. The 4096-item projection is ordered once on descriptor refresh, not sorted again on every query. Queries inspect only that existing bounded projection.

Examples: `name:"Evening show" codec:h264 resolution:1920x1080 fps:50`, `type:clip availability:missing`, `ready:true -name:rehearsal`. Decimal FPS uses an invariant dot; exact rational forms such as `fps:60000/1001` are accepted. Codec matches the confirmed video or audio codec. Resolution and FPS use typed catalogue values, never formatted labels. Items without typed metadata match neither positive nor negated codec/resolution/FPS terms. Plain terms still search projected name/detail/format/state/reference/path; Windows drive paths remain plain text.

`type` accepts source/clip/audio/graphics/composition, `availability` online/offline/missing, and `ready` true/false. Readiness remains a presentation hint. Invalid fields, malformed values and unmatched quotes show a reason and return no matches. Tags and last-use timestamps are absent from the authoritative descriptor: `tag:`, `recent:` and `used:` report that limitation. Opt-in local recent references do not become catalogue usage metadata. Smart Bins retain expressions in the existing preference schema.

## Reviewed batch operations

**Import Media** now prepares the chosen paths. **Batch · Remove selected…** freezes up to 128 selected entries; only persistent catalogue clips are eligible, including missing/offline records. Other selected kinds are explicitly skipped. Review each entry, then choose **Confirm**. Removal deletes catalogue records only; source files remain intact. A changed catalogue revision at the fresh preflight read blocks the entire batch before the first mutation and requires a new preparation.

Requests run sequentially through `IMediaAssetCatalogClient`, with concurrency one and at most 128 retained outcomes. ControlHost still validates readability, probe support, fingerprints, duplicates and persistence for imports. No file scan, hashing or decoder work runs in Operator. Each response supplies the confirmed catalogue snapshot; no optimistic record is inserted or removed locally. Each item is visibly READY, SKIPPED, PENDING, DONE, FAILED, CANCELLED or UNCONFIRMED, with progress and its reason.

**Stop** finishes the active authoritative request and admits no further requests. Confirmed earlier items remain committed; a batch is not an all-or-nothing transaction or live undo. **Retry failed** prepares only rejected or unsent items and requires confirmation again. A transport timeout/cancellation/error marks that active item UNCONFIRMED, stops admission and excludes it from automatic retry: refresh the catalogue and prepare deliberately after inspecting the result. Disconnected, stale or pending production state disables new preparation/admission; no routing, Preview load or Program action is included. Close removes only the local review/results.

Search and selection can change while a batch is reviewed or running; frozen names and stable asset IDs in the results make the actual targets explicit. Relink keeps its existing single-target fingerprint-checked workflow. There is no inferred bulk relink mapping.

## Smart search and batch qualification

`MediaSmartSearchBatchTests` covers combined positive/negative/quoted terms, typed unknowns, malformed/unsupported fields, rational FPS, bounded large-list predicate work, mixed eligibility, partial rejection, cancellation with an active confirmation, retry excluding successes/uncertain results, stale preflight and oversized/disconnected admission. Existing real-window qualification checks debounced custom search, batch accessibility and bounded result virtualization in addition to the large Grid/List checks. Physical DPI/IME/screen-reader and hardware Program-latency evidence remains UNVERIFIED.

## Explicit asset preparation

File availability is independent of playback readiness. The Add menu and each
asset context menu expose **Prepare selected assets…**. Select 1–16 online
catalogue clips, choose required audio (default off), 1–3 seek points and 1–4
frames per point, then choose **Prepare / Retry**. Requests run sequentially
through ControlHost; Operator never opens files or decoders. Close loaded file
decks deliberately first. Preparation never evicts, seeks or changes a live A/B
deck, Preview, Program or confirmed transport state.

| Status | Meaning |
| --- | --- |
| UNVERIFIED | No current source-bound verification, cancelled, expired or invalidated observation. |
| PROBED | Container/track/profile metadata admitted by the existing probe; installed decoder and playback capacity are still unproven. |
| DECODER_AVAILABLE | This job negotiated the existing installed decoder; representative reads are still running. |
| CONDITIONALLY_READY | Selected representative seeks/frames and required audio passed against the hash-locked file; the optional decoder has been released. |
| PREPARED | An existing resident deck is loaded and ready, playing or paused, with no observed deck error. This is not a preflight cache entry. |
| UNAVAILABLE | This bounded attempt failed; inspect its reason before retrying. This does not prove permanent format unavailability. |

Online catalogue entries no longer receive READY merely for existing on disk.
The Ready filter refers to resident deck readiness; conditional observations
remain separate. The new **Asset playback** Show Preflight check is UNVERIFIED
without matching current observations, FAIL on observed failure, and WARNING for
representative success. It never promotes availability or a sample into a full
show-ready PASS.

**Cancel** admits no next asset and explicitly cancels the current Runtime job.
If native open has not returned, the reservation remains occupied until its
owner cleans up. A later deck-open intent cancels optional work and returns an
actionable retry result; it does not wait in Program admission. A failed batch
invalidates its earlier partial positive observations. No catalogue mutations,
save/restore, auto-route or background retries are performed. Unknown transport
cancellation outcomes are displayed, and no uncertain job is silently re-admitted.

Observations are session-only, at most 16 in Operator and one in Runtime. They
expire after ten seconds and are never reused to skip verification. Catalogue
refresh/relink, disconnect/stale control state, file length/timestamp change,
Runtime epoch and loaded OS native-module identity changes invalidate positive
evidence. Every new Prepare hashes again. The UI timer only expires metadata; it
does not scan files or trigger probes. Open in Preview remains the separate
existing route/preroll workflow for actually retaining a prepared deck.
