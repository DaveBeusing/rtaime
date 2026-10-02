<!-- Copyright (c) Dave Beusing <david.beusing@gmail.com>. -->

# Replay and Clip Production

## Purpose

Replay and Clip Production retains a bounded encoded history of committed Program video and final mixed Program audio inside RuntimeHost. An operator can mark a retained range, materialize it as an ordinary MP4 media asset, adopt it into the Media Library, open it through Media Deck and route it through the normal Preview/CUT/DISSOLVE production path.

Replay is not a second Program renderer, production authority or playback engine.

## Authority and ownership

ControlHost remains Production Authority. RuntimeHost owns replay capture because the required post-composite Program pixels, final mixed Program audio and frame timing already exist there.

The path is:

```text
committed Program A/V
  -> retained Program pixel lease + final mixed audio
  -> bounded ReplayCaptureEngine queue
  -> Windows Media Foundation H.264/AAC segment writer
  -> RollingReplaySegmentStore
  -> selected/pinned range
  -> ReplayClipMaterializer
  -> finalized MP4
  -> ControlHost Media Library adoption
  -> Media Deck
  -> Preview / CUT / DISSOLVE
```

Raw media does not cross management IPC. Replay never mutates Program directly and never creates a replay-only decoder.

## Bounded rolling capture

`ReplayCaptureEngine` owns a bounded asynchronous queue. The queue capacity is explicit and limited; saturation drops replay samples instead of blocking Program. A dropped or rejected sample records discontinuity evidence.

Finalized encoded segments are retained by `RollingReplaySegmentStore`. Retention is bounded by both elapsed Program history and storage bytes. Full-resolution raw RGBA history is prohibited.

Segments currently pinned by clip materialization are not evicted. If retention pressure cannot be resolved because eligible history is pinned, admission fails explicitly rather than deleting data that an active materialization owns.

## Timing and continuity

Each segment carries first/last Program sequence, Program-derived start/end time, encoded byte count, audio presence and discontinuity-before evidence.

Replay uses Program frame timing as its media timeline. It does not use a wall-clock scheduler to synthesize continuity.

A range crossing a discontinuity is rejected. A range that has already expired from the rolling store is rejected deterministically.

## MARK IN / MARK OUT

MARK IN selects a retained Program time and supports a bounded lookback. MARK OUT resolves to the latest retained Program history. The resulting range must still be continuously retained before materialization begins.

The Operator presents confirmed Runtime state only.

## Clip materialization and integrity

Materialization pins all encoded segments intersecting the selected range, decodes only the selected normal-speed interval and writes a new H.264/AAC MP4 through the existing Media Foundation recording writer.

The materialized clip:
- uses a deterministic safe file name;
- uses create-new collision semantics;
- remains partial until successful writer finalization;
- is probed through the existing local-media capability before success is returned;
- publishes SHA-256 integrity evidence;
- is cleaned up on failed materialization.

A clip is not considered ready for production until ControlHost successfully imports it and confirms an online Media Library asset with a stable `MediaAssetId`.

## Playback reuse

Replay playback is ordinary Media Deck playback. The Operator opens the adopted Media Library asset through `MediaDeckViewModel` and uses the existing Preview selection. CUT/DISSOLVE remain the established governed production commands.

There is no direct replay-to-Program path.

## Failure isolation

Replay capture, encode, storage, materialization or Media Library adoption failure must not change committed Program execution.

Backpressure is explicit through dropped-sample and discontinuity statistics. Encoder/storage faults move replay to degraded/failed evidence while Program continues. Active recording and replay retain independent bounded asynchronous queues while sharing the same explicit Program-frame lease model.

## Operator surface

The Replay surface exposes:
- capture state;
- retained duration and configured retention limit;
- storage usage;
- finalized/evicted segment evidence;
- discontinuity/backpressure evidence;
- MARK IN / MARK OUT and selected duration;
- CREATE CLIP;
- OPEN IN MEDIA DECK;
- SEND TO PREVIEW;
- finalization/import failure evidence.

All interactive controls use the custom Operator control set.

## Qualification boundary

The first replay implementation is normal-speed only. Variable speed, slow motion, reverse playback, interpolation, multi-angle replay, synchronized replay channels and highlight-reel editing are outside this capability.

Automated qualification covers bounded retention/storage behavior, pinned-history protection, expired/discontinuous range rejection, Operator authority boundaries, build policy and the existing full Required Gates. Windows reference-media regression continues to qualify the shared H.264/AAC decode/encode path.

Long-duration physical-storage qualification and hardware encoder behavior remain separate reference-platform evidence.
