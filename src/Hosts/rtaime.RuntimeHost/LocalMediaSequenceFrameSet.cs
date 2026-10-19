// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// One unique prepared native owner and source position contributing to the same
/// sequence boundary. Video/audio tracks at the same source offset share an owner.
/// </summary>
internal readonly record struct SequenceMediaFrameRequest(MediaSourceId SourceId, long SourcePositionTicks);

public sealed partial class LocalMediaDeckRuntimeService
{
    /// <summary>
    /// Acquires a bounded, generation-consistent group of real native frame leases.
    /// On any failure all acquired leases are released and no partial frame set is
    /// published. The caller owns every returned lease and must recheck
    /// IsCurrentSequenceFrame immediately before committing to an output sink.
    ///
    /// This is an internal media admission primitive, not an output route,
    /// a second clock, or permission to alter Preview/Program.
    /// </summary>
    internal bool TryTakeSequenceFrameSet(
        Guid sequenceId, ulong authoringRevision, SequenceRuntimePosition position,
        ReadOnlySpan<SequenceMediaFrameRequest> requests,
        Span<SequenceMediaFrameLease?> destination, out Failure? failure)
    {
        if (requests.Length is < 1 or > 4 || requests.Length > _ledger.Capacity ||
            destination.Length < requests.Length)
        {
            failure = new("runtime.sequence.frame_set_bounds",
                "Frame-set requests must fit the existing finite native decoder owner capacity.");
            return false;
        }

        for (var index = 0; index < requests.Length; index++)
        {
            if (requests[index].SourceId.Value.IsEmpty || requests[index].SourcePositionTicks < 0 ||
                destination[index] is not null)
            {
                failure = new("runtime.sequence.frame_set_invalid",
                    "Frame-set requests require valid source identities, positions and empty destination slots.");
                return false;
            }
            for (var earlier = 0; earlier < index; earlier++)
            {
                if (requests[earlier].SourceId == requests[index].SourceId)
                {
                    failure = new("runtime.sequence.frame_set_duplicate",
                        "Each prepared native owner may be acquired at most once per sequence boundary.");
                    return false;
                }
            }
        }

        lock (_sequenceGate)
        {
            var prepared = _sequenceOwners;
            if (prepared is null || prepared.SequenceId != sequenceId ||
                prepared.AuthoringRevision != authoringRevision ||
                prepared.Generation != position.Generation ||
                prepared.LoopPass != position.LoopPass || position.Tick < 0)
            {
                failure = new("runtime.sequence.stale",
                    "The requested sequence revision, position or native generation is no longer prepared.");
                return false;
            }

            // Reject an invalid member before consuming any queued decoded frame.
            // Control-provided source offsets are never supplied by an output sink.
            foreach (var request in requests)
            {
                var matches = 0;
                foreach (var owner in prepared.Owners)
                {
                    if (owner.Request.SourceId != request.SourceId) continue;
                    matches++;
                    if ((Int128)request.SourcePositionTicks - position.Tick !=
                        (Int128)owner.Request.SourceInTicks - owner.Request.TimelineStartTicks)
                    {
                        failure = new("runtime.sequence.source_mismatch",
                            "Frame-set source position does not match the prepared source/timeline offset.");
                        return false;
                    }
                }
                if (matches != 1)
                {
                    failure = new("runtime.sequence.source_mismatch",
                        "Frame-set source is not uniquely bound to the prepared native owners.");
                    return false;
                }
            }

            var acquired = 0;
            var complete = false;
            try
            {
                for (var index = 0; index < requests.Length; index++)
                {
                    var request = requests[index];
                    if (!TryTakeSequenceMedia(sequenceId, authoringRevision, request.SourceId,
                        position, request.SourcePositionTicks, out var frame, out failure))
                        return false;

                    destination[index] = frame!;
                    acquired++;
                }
                for (var index = 0; index < acquired; index++)
                {
                    if (IsCurrentSequenceFrame(destination[index]!)) continue;
                    failure = new("runtime.sequence.frame_set_stale",
                        "A native frame was invalidated before the frame set could be published.");
                    return false;
                }
                failure = null;
                complete = true;
                return true;
            }
            finally
            {
                if (!complete)
                {
                    for (var index = 0; index < acquired; index++)
                    {
                        destination[index]?.Dispose();
                        destination[index] = null;
                    }
                }
            }
        }
    }
}
