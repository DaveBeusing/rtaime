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

This capability is a deliberate post-V1 product expansion. It does not retroactively change the documented V1 software-closure or hardware-qualification claims.

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

The presentation projection remains bounded to 4096 entries. Grid and List retain recycling/virtualization, deterministic search/filter ordering and local extended multi-selection. `Ctrl+F` focuses Media Library search. Search matches projected name, detail, format, state, stable reference and known local path.

## Preview and existing workflows

For a persistent Clip, **Open in Preview** requires an online catalogue entry and selected production source slot, opens the catalogue source through the existing Media Deck command path while supplying the stable `AssetId`, verifies the loaded identity, then uses the existing Preview command.

The catalogue therefore does not create a second playback path or routing authority. Existing Timeline and Cue behavior remains on established command paths. Cue actions apply only when the selected catalogue `AssetId` is the currently loaded Media Deck asset.

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

The custom controls below the existing search/type filters save up to 16 named filter combinations. Use **Filters** to reveal the duration/readiness row without permanently reducing the browser viewport. Choose a known-duration range (under one minute, one to ten minutes, ten minutes or longer, or unknown) and presentation readiness, then **Save**. Selecting a saved entry restores search, category, type/availability, duration and readiness together. Changing a filter detaches the saved selection without overwriting its definition. **Del** removes only the saved filter.

Duration comes directly from the immutable catalogue descriptor, never from formatted text or a file probe. Non-catalogue items without typed duration remain unknown. Readiness is the existing presentation hint and does not qualify Program or startup production readiness. Tags are not supported by the catalogue and are not offered.

Preferences live separately in `%LOCALAPPDATA%/rtaime/operator-media-bins.json`, version 1. Reads are asynchronous and limited to 64 KiB, names to 128 characters and search to 256. Invalid, oversized or future-version documents are ignored. Saves are serialized and atomically replace only this presentation file; failures are displayed in the Library footer and retained in the saved-bin tooltip. Shutdown waits for the pending preference save. Catalogue persistence, recovery checkpoints and production state are unaffected.

## Hover thumbnails and capability limits

Hovering or keyboard-focusing an asset admits a latest-only preview after 180 ms. It shares the existing Operator monitoring image, if present. At most the active image is retained; images are not copied and no asset thumbnail cache, media read, hashing, probe, decoding or control/monitoring request is started.

Leaving an item/browser, hiding a browser, changing a filter/catalogue context, starting a drag or closing the window cancels pending admission and clears the image. Generation checks prevent cancelled work from publishing a stale thumbnail.

The non-interactive footer overlay preserves the browser viewport and distinguishes **MONITOR THUMBNAIL**, **OFFLINE** and **UNSUPPORTED**. Independent position scrubbing and asynchronous asset thumbnail extraction are **unsupported** by the current backend and are explicitly capability gated. Hover never opens or seeks the Media Deck and never changes Preview or Program. Existing monitoring thumbnails can show the current source image; they are not claimed as time-indexed proxies.

There is no expensive thumbnail worker to pool in this implementation. A future extraction capability must provide a bounded, cancelable worker pool outside Program processing before the gate can be enabled.

## Performance and authority boundaries

Search, filtering, selection and scrolling remain in-process Operator projection work and perform no blocking media reads. File hashing and Runtime probing occur only during explicit catalogue operations and are outside production-critical media/render paths.

There is no recursive automatic indexing and no second media index. Batch presentation coordinates existing single-item Client calls; it introduces no bulk-media IPC, decoder, scheduler, catalogue authority or source-file deletion.

## Verification

Regression coverage verifies stable `AssetId` across store close/reopen, duplicate import identity, unsupported-media rejection, missing-file projection, relink preserving identity, relink conflict rejection, remove-from-library without source-file deletion, optimistic persistence conflict behavior, bounded import size and cancellation without partial catalogue records.

The Operator UI policy gate verifies persistent catalogue projection, bounded/virtualized presentation, multi-file import, Relink and Remove workflow wiring, and the existing production interaction paths.

## Smart Bin and hover qualification

`MediaLibraryBrowsingTests` checks duration boundaries and unknown metadata, independent readiness/availability, bounded preference round-trip and corrupt/future/oversized files, cancellation without replacing valid preferences, 1000 rapid hover requests, existing-image/offline capability handling and a 30,000-record catalogue projected to at most 4096 items. Repeated search and saved-filter restoration assert no production transport calls, catalogue mutations or routing changes. Hidden selections are pruned.

The real `OperatorXamlStartupTests` window verifies custom control keyboard focus/accessibility names and scrolls both 4096-item Grid/List views while bounding realized containers. Existing compact-window and source-bound functional/visual checks remain required. Physical keyboard/screen-reader usability, mixed-DPI interaction and hardware Program render/IPC latency under hover/search load remain **UNVERIFIED** until reference-platform execution. Hosted tests do not substitute for those measurements.

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
