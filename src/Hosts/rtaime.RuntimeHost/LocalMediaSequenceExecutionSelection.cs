// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// Reads source-validated native leases for one position on the existing
/// Runtime Program frame clock. It never publishes them or commits a Take.
/// Callers must release every acquired lease and recheck generation before
/// handing pixels or PCM to a real authorized sink.
/// </summary>
public sealed partial class LocalMediaDeckRuntimeService
{
    internal bool TrySelectBoundSequenceBoundary(
        SequencePreparationReference reference,
        ulong programSlot,
        Span<SequenceExecutionClip> selectedClips,
        Span<SequenceClipFrameRequest> clipRequests,
        Span<SequenceMediaFrameRequest> ownerRequests,
        Span<SequenceMediaFrameLease?> ownerFrames,
        Span<int> clipOwnerIndices,
        out SequenceRuntimePosition position,
        out int clipCount,
        out int ownerCount,
        out Failure? failure)
    {
        position = default;
        clipCount = 0;
        ownerCount = 0;
        reference.Validate();
        lock (_sequenceGate)
        {
            var prepared = _sequenceOwners;
            var snapshot = prepared?.ExecutionSnapshot;
            var map = _sequenceShuttleMap;
            if (snapshot is null || map is null || prepared is null ||
                reference.SequenceId != prepared.SequenceId ||
                reference.AuthoringRevision != prepared.AuthoringRevision ||
                reference.Generation != prepared.InitialGeneration ||
                reference.PreparedExecutionId != prepared.PreparedExecution.ToString() ||
                reference.Version != prepared.ProtocolVersion ||
                map.SequenceId != snapshot.SequenceId ||
                map.AuthoringRevision != snapshot.AuthoringRevision ||
                !map.TryMap(programSlot, out position) ||
                position.Generation != prepared.Generation ||
                position.LoopPass != prepared.LoopPass)
            {
                failure = new("runtime.sequence.boundary_stale",
                    "Bounded execution requires current prepared native owners and an active shuttle generation.");
                return false;
            }

            // Source positions come exclusively from Control-compiled, already
            // verified slices. The caller cannot provide arbitrary clip offsets.
            var slices = snapshot.Slices;
            var left = 0;
            var right = slices.Length - 1;
            SequenceExecutionSlice? selected = null;
            while (left <= right)
            {
                var middle = left + (right - left) / 2;
                var candidate = slices[middle];
                if (position.Tick < candidate.StartTick) right = middle - 1;
                else if (position.Tick >= candidate.EndTick) left = middle + 1;
                else { selected = candidate; break; }
            }
            if (selected is null)
            {
                failure = new("runtime.sequence.boundary_missing",
                    "The prepared execution has no compiled slice at this Program position.");
                return false;
            }

            if (selected.Clips.Length > selectedClips.Length ||
                selected.Clips.Length > clipRequests.Length ||
                selected.Clips.Length > clipOwnerIndices.Length ||
                ownerRequests.Length < Math.Min(selected.Clips.Length, _ledger.Capacity) ||
                ownerFrames.Length < Math.Min(selected.Clips.Length, _ledger.Capacity))
            {
                failure = new("runtime.sequence.boundary_capacity",
                    "The output reader has insufficient preallocated clip or owner buffers.");
                return false;
            }

            foreach (var clip in selected.Clips)
            {
                // Graphics will be resolved through the existing graphics owner,
                // never through a fabricated native decoder frame.
                if (clip.Kind == SequenceMediaKind.Graphics) continue;
                var sourceTick = checked((long)((Int128)clip.SourceInTicks +
                    position.Tick - clip.TimelineStartTicks));
                selectedClips[clipCount] = clip;
                clipRequests[clipCount] = new SequenceClipFrameRequest(
                    clip.ClipId, new MediaAssetId(Identity.Parse(clip.AssetId.ToString())),
                    clip.FingerprintSha256, clip.Kind, clip.SourceInTicks,
                    clip.TimelineStartTicks, sourceTick);
                clipCount++;
            }

            if (clipCount == 0)
            {
                failure = null;
                return true; // An authored black/silent gap uses no native owners.
            }

            if (!TryTakeSequenceClipFrameSet(snapshot.SequenceId,
                snapshot.AuthoringRevision, position,
                clipRequests[..clipCount], ownerRequests, ownerFrames,
                clipOwnerIndices[..clipCount], out ownerCount, out failure))
            {
                clipCount = 0;
                return false;
            }

            // Recheck exact ephemeral source identity, not just the asset
            // fingerprint or the matching source/time-offset tuple.
            for (var index = 0; index < clipCount; index++)
            {
                var frame = ownerFrames[clipOwnerIndices[index]];
                var expected = new MediaSourceId(Identity.Parse(selectedClips[index].NativeSourceId!));
                if (frame is not null && frame.SourceId == expected &&
                    IsCurrentSequenceFrame(frame))
                    continue;

                for (var owner = 0; owner < ownerCount; owner++)
                {
                    ownerFrames[owner]?.Dispose();
                    ownerFrames[owner] = null;
                }
                ownerCount = 0;
                clipCount = 0;
                failure = new("runtime.sequence.boundary_fence",
                    "The current decoder leases no longer match the bound native execution source identity.");
                return false;
            }
            failure = null;
            return true;
        }
    }
}
