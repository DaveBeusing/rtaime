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

### Directory ownership and restart reclamation

Before opening a segment writer, the replay worker acquires an exclusive `.replay-owner.lock` in its rolling `segments` directory. A second capture owner cannot reclaim or write that directory. The lease remains held after store disposal while an existing clip pin still owns a segment; the last pin releases it. Storage preparation runs on the replay worker and cannot block the Program producer.

Storage observations are published as one immutable snapshot after each mutation,
including failed cleanup. Reading segments, byte accounting and failure evidence
does not take the filesystem mutation lock. Replay captures that coherent state
before taking its producer queue lock, so a management snapshot cannot hold the
Program path while waiting for disk cleanup. Starting capture remains explicitly
`Starting` until its initial storage reconciliation completes.

Rolling history is session-local because its sequence/time ranges belong to the previous Program session. Startup reclaims only recognized `replay-<GUID>.mp4`, `replay-<GUID>.partial.mp4` and `replay-<GUID>.mp4.lock` files in the owned directory. It does not recurse into `clips`, remove saved assets, or delete unrelated filenames. Abandoned writer artifacts are reconciled after abort and before another writer opens.

Retained-byte evidence includes files awaiting deletion. Expiration removes their playable index entries, but neither their physical byte charge nor successful-eviction accounting is released until deletion succeeds. A failed cleanup records storage failure evidence and blocks a new segment writer until a later retry succeeds. Restart, trimming and pin release retry eligible cleanup. Segment descriptors must match actual finalized file sizes. Quota admission includes the newly finalized segment before older history is reclaimed; a rejected candidate that cannot be deleted remains charged as well.

The finalized-history quota excludes the one currently open encoder segment, whose duration is bounded by the configured segment duration. A finalization or abort cannot permit repeated abandoned segments to accumulate: unresolved cleanup prevents the next writer from opening. Legacy files that already exceed quota stay visibly charged while storage is unavailable. The lock file contains no payload and is retained as the stable ownership rendezvous.

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

## Replay queue and shutdown evidence

ReplayCaptureEngine exposes a fixed-size QueueStatistics snapshot: current depth, maximum observed depth and configured capacity. Capacity remains bounded to 1..64; the default is unchanged. Statistics do not retain frame history or allocate during admission.

Every Stop call waits for the existing capture worker. Cancelling one wait does not cancel or abandon the shared drain. Repeated capture or Runtime replay disposal joins one finalization and resource release; no caller reports success while the first caller still owns a writer. Partial writer configuration/open failure invokes best-effort Abort before reporting the encoder failure and returning the sample lease.

Mixed Runtime qualification retains Program readbacks while replay and recording writers are stalled, replaces source content, exercises monitoring churn and verifies retained pixels stay intact. Simulated replay artifacts only exercise ownership/finalization; they are not MP4 codec qualification.

