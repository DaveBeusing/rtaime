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

**Import Media** opens a bounded multi-file picker. The current Operator workflow selects MP4 files and sends only their local paths to ControlHost. A single request accepts at most 128 selected files.

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

The strict fingerprint requirement prevents a different or modified file from silently inheriting references intended for the original asset.

## Remove from Library

**Remove from Library** deletes only the catalogue record. It never deletes, modifies or moves the underlying media file.

## Operator projection

The Media Library projects authoritative production Sources, persistent catalogue Clips, Audio inputs, the currently loaded Graphics asset and the current Composition / AI feature.

A currently loaded non-catalogued clip, such as an existing demo path, remains visible as a compatibility fallback. Persistent catalogue clips use `AssetId` as their reference identity.

The presentation projection remains bounded to 4096 entries. Grid and List retain recycling/virtualization, deterministic search/filter ordering and local extended multi-selection. Search matches projected name, detail, format, state, stable reference and known local path.

## Preview and existing workflows

For a persistent Clip, **Open in Preview** requires an online catalogue entry and selected production source slot, opens the catalogue source through the existing Media Deck command path while supplying the stable `AssetId`, verifies the loaded identity, then uses the existing Preview command.

The catalogue therefore does not create a second playback path or routing authority. Existing Timeline and Cue behavior remains on established command paths. Cue actions apply only when the selected catalogue `AssetId` is the currently loaded Media Deck asset.

## Context actions

Catalogue Clip cards and list rows expose **Open in Preview**, **Add to Timeline**, **Add Cue at Playhead**, **Reveal in Explorer**, **Relink Media…**, **Remove from Library** and **Properties**.

The current Scene model has no governed media-assignment mutation path. Scene assignment remains out of scope until that authority path exists.

## Loading, empty and error states

The existing Media Library loading/error surfaces now include catalogue operations as well as Media Deck activity. Explicit failures are preserved for missing/unreadable files, unsupported media, duplicate/source conflicts, relink conflicts, relink content mismatch, persistence and IPC failures.

A catalogue failure does not make the Operator authoritative and does not alter already committed production execution.

## Metadata transport

Operator/Client/ControlHost transport carries catalogue metadata only. Catalogue snapshots are paged in bounded groups of at most 256 assets while retaining the existing 1 MiB Control IPC frame limit.

The Client restarts a paged read if catalogue revision changes between pages, preventing a mixed-revision snapshot. Mutation responses carry bounded result metadata; the Client then reads the resulting catalogue through the same paged snapshot path. Raw media bytes are never sent through management/control IPC.

## Thumbnails

The persistent catalogue does not introduce a second decoder or thumbnail-extraction stack. When the currently loaded catalogue asset already has an Operator source thumbnail, that existing presentation image can be reused.

Persistent asynchronous thumbnail extraction/cache generation is not currently claimed and can be added later only through the established media capability without entering production-critical paths.

## Performance and authority boundaries

Search, filtering, selection and scrolling remain in-process Operator projection work and perform no blocking media reads. File hashing and Runtime probing occur only during explicit catalogue operations and are outside production-critical media/render paths.

There is no recursive automatic indexing, bulk-media management IPC, second media decoder, second production scheduler, UI-owned catalogue truth or implicit source-file deletion.

## Verification

Regression coverage verifies stable `AssetId` across store close/reopen, duplicate import identity, unsupported-media rejection, missing-file projection, relink preserving identity, relink conflict rejection, remove-from-library without source-file deletion, optimistic persistence conflict behavior, bounded import size and cancellation without partial catalogue records.

The Operator UI policy gate verifies persistent catalogue projection, bounded/virtualized presentation, multi-file import, Relink and Remove workflow wiring, and the existing production interaction paths.
