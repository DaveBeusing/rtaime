<!--
Copyright (c) 2026 Dave Beusing
david.beusing@gmail.com
All rights reserved.
-->

# Media Library & Asset Browser

## Purpose

The Operator Media Library is the fast visual browser for media resources already exposed by the existing rtaime production state. It remains a presentation and interaction surface only: Runtime, Media, routing and playback services remain authoritative.

The browser deliberately introduces no second media index, directory crawler, metadata probe, decoder path or persistent DAM/MAM store.

## Asset projection

The browser projects existing Operator data into `MediaPoolItemViewModel` entries for:

- production Sources;
- the currently loaded local Clip;
- Audio inputs;
- the currently loaded Graphics asset;
- the current Composition / AI feature.

The presentation projection is bounded to 4096 entries. Search and filtering remain deterministic and client-side over that already available projection. This is a UI scalability bound, not an ingest or media-library database.

The current product model does not expose persistent folders or collections. The UI therefore uses existing resource categories and does not invent folder, collection or asset-management semantics.

## Search and filters

Search reacts directly to changes in the search field and matches the projected name, detail, format, state, reference and known local path. Category selection remains separate from type / availability filter chips.

`Ctrl+F` is registered through the centralized `OperatorKeyboardCommandRegistry`. It switches to the MEDIA workspace, focuses the Media Library search field and selects the current query. Text-entry safeguards from the shared shortcut system remain in effect.

## Grid and list presentation

At the 1920×1080 reference viewport, the Media Library occupies the fixed 400-pixel left shell region. The visible inset is 12 pixels from the region edge. Its control stack is 34 pixels for the header, 34 pixels for search/category selection and 32 pixels for the filter row.

The default Grid presents exactly three `RtaimeMediaTile` controls per row. Each tile occupies 120×104 pixels with a 120×68 reference thumbnail surface, 8-pixel horizontal spacing and 10-pixel vertical spacing. The filename is a single 11-pixel ellipsized line. Duration is rendered in a compact monospaced overlay. Offline state overlays the thumbnail without resizing the tile and is always expressed in text as well as color.

The Grid continues to use the existing `VirtualizingWrapPanel`. The panel now includes horizontal and vertical spacing in its realization and scrolling calculations while retaining recycling and bounded visible-row realization. The Media Library uses an overlay `RtaimeScrollBar`, so scrollbar visibility does not reduce the three-column reference viewport. The List continues to use recycling virtualization.

Search uses `RtaimeSearchBox`; filters use the shared rtaime selection controls; asset actions use `RtaimeContextMenu` / `RtaimeMenuItem`. Existing source thumbnails are reused. The Media Library does not add a parallel thumbnail extraction or media-analysis pipeline.

## Selection

Grid and List both use Extended selection:

- click selects one asset;
- Ctrl-click adds or removes individual assets;
- Shift-click selects ranges.

`MediaPoolInspectorViewModel.SelectedItems` keeps the current UI selection while `SelectedItem` remains the primary Inspector context. Multi-select is local Operator state only and never becomes production authority.

Typed drag operations use `MediaAssetDragPayload`, carrying the primary asset plus the current selected set. Existing drop targets continue to validate and act on the primary asset through their established command paths.

## Context actions

Asset cards and list rows expose the same bounded actions:

- **Open in Preview** reuses the existing Preview selection / command path.
- **Add to Timeline** reuses `MediaTimelineViewModel.CanAcceptMediaPoolDrop` and `ProjectMediaPoolDrop`; unsupported track semantics remain rejected.
- **Add Cue at Playhead** reuses the current Media Deck cue command and is valid only for the loaded Clip.
- **Reveal in Explorer** is available only when the Operator already knows the local Clip path; it performs no media mutation.
- **Properties** makes the asset the current selection so the existing context Inspector presents its metadata and controls.

The current Scene model exposes no existing asset-assignment mutation path. The Media Library therefore does not invent a Scene command. Scene integration should be added only when a governed Scene asset action exists.

Hover quick actions expose Preview and Timeline only, keeping the card surface compact. Selection is rendered with the shared cyan border and left accent while ordinary hover remains on the dark raised-hover surface.

## Loading, empty and error states

The browser presents:

- a loading indicator while the existing Media Deck is busy;
- an explicit error surface using the existing Media Deck error;
- no-assets and filtered-empty messages;
- assets that are known but offline instead of silently removing them.

Unknown or unavailable resources remain governed by the state already projected by their owning subsystem.

## Local media path

The Operator retains the path of a successfully opened local Clip only for UI convenience such as **Reveal in Explorer**. The path is cleared when the Media Deck unloads or when the authoritative loaded filename no longer matches the known path.

The local path is not persisted as production state and does not change Media, ControlHost or RuntimeHost contracts.

## Performance and authority boundaries

The browser keeps filtering in process and performs no blocking media I/O during search, selection or scrolling. Visual virtualization is independent from the bounded presentation projection: both constraints work together to keep large asset sets responsive.

Preview, Timeline and Cue actions still use the existing Operator / Client / Media Deck paths. No second playback authority, media index, thumbnail extractor, timeline or scene model is introduced.

## Verification

The Operator UI policy gate verifies:

- Grid and List presentation;
- Extended multi-select;
- typed drag payloads;
- Grid virtualization and List recycling;
- duration, file-type and textual offline presentation;
- loading and error states;
- Preview / Timeline / Cue / Explorer / Properties action wiring;
- centralized `Ctrl+F` search focus;
- the bounded 4096-entry UI projection;
- absence of a second directory/media indexing path.

The repository build and existing Operator gates remain responsible for XAML compilation, architecture constraints and regression coverage.
