// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// One Control-validated clip's native source request at a precise sequence tick.
/// Clip metadata never grants output authority; it must match the prepared
/// catalogue-bound native owner before a decoded frame can be consumed.
/// </summary>
internal readonly record struct SequenceClipFrameRequest(
    Guid ClipId,
    MediaAssetId AssetId,
    string FingerprintSha256,
    SequenceMediaKind Kind,
    long SourceInTicks,
    long TimelineStartTicks,
    long SourcePositionTicks);

public sealed partial class LocalMediaDeckRuntimeService
{
    internal const int MaximumSequenceClipSelections = 32;

    /// <summary>
    /// Resolves saved Video/Audio clip selections to independently admitted
    /// native owners. Linked video/audio clips at the same asset and source
    /// offset reuse one lease, not another decoder or a second read.
    ///
    /// All caller buffers are bounded and owned outside the Runtime boundary.
    /// Invalid clip bindings are rejected before decoder reads. If a native
    /// frame-set read subsequently fails, acquired leases are released and no
    /// partial result is published; consumed frames are not rewound.
    /// The acquired leases are observations only: the sink rechecks generation
    /// and have separate Control-authorized production execution.
    /// </summary>
    internal bool TryTakeSequenceClipFrameSet(
        Guid sequenceId,
        ulong authoringRevision,
        SequenceRuntimePosition position,
        ReadOnlySpan<SequenceClipFrameRequest> clips,
        Span<SequenceMediaFrameRequest> ownerRequests,
        Span<SequenceMediaFrameLease?> ownerFrames,
        Span<int> clipOwnerIndices,
        out int ownerCount,
        out Failure? failure)
    {
        ownerCount = 0;
        if (clips.Length is < 1 or > MaximumSequenceClipSelections ||
            ownerRequests.Length < Math.Min(clips.Length, _ledger.Capacity) ||
            ownerFrames.Length < Math.Min(clips.Length, _ledger.Capacity) ||
            clipOwnerIndices.Length < clips.Length)
        {
            failure = new("runtime.sequence.clip_selection_bounds",
                "Clip selection or caller-owned buffers exceed the finite Runtime admission limits.");
            return false;
        }

        clipOwnerIndices[..clips.Length].Fill(-1);

        lock (_sequenceGate)
        {
            var prepared = _sequenceOwners;
            if (prepared is null || prepared.SequenceId != sequenceId ||
                prepared.AuthoringRevision != authoringRevision ||
                prepared.Generation != position.Generation ||
                prepared.LoopPass != position.LoopPass ||
                position.Tick is < 0 or > 20_736_000_000L)
            {
                failure = new("runtime.sequence.stale",
                    "The selected clips do not belong to the currently prepared sequence position.");
                return false;
            }

            var count = 0;
            var accepted = false;
            try
            {
            for (var index = 0; index < clips.Length; index++)
            {
                var clip = clips[index];
                if (clip.ClipId == Guid.Empty || clip.AssetId.Value.IsEmpty ||
                    clip.Kind is not (SequenceMediaKind.Video or SequenceMediaKind.Audio) ||
                    clip.FingerprintSha256 is null || clip.FingerprintSha256.Length != 64 ||
                    clip.FingerprintSha256.Any(c => !Uri.IsHexDigit(c)) ||
                    clip.SourceInTicks < 0 || clip.TimelineStartTicks < 0 ||
                    clip.SourcePositionTicks < 0 ||
                    (Int128)clip.SourcePositionTicks - position.Tick !=
                    (Int128)clip.SourceInTicks - clip.TimelineStartTicks)
                {
                    failure = new("runtime.sequence.clip_selection_invalid",
                        "Clip identity, fingerprint, kind or time offset is inconsistent.");
                    return false;
                }
                for (var earlier = 0; earlier < index; earlier++)
                {
                    if (clips[earlier].ClipId == clip.ClipId)
                    {
                        failure = new("runtime.sequence.clip_selection_duplicate",
                            "A clip may be selected only once per sequence boundary.");
                        return false;
                    }
                }

                SequenceMediaOwnerRequest? matched = null;
                foreach (var candidate in prepared.Owners)
                {
                    var owner = candidate.Request;
                    if (owner.AssetId != clip.AssetId ||
                        !string.Equals(owner.FingerprintSha256, clip.FingerprintSha256,
                            StringComparison.OrdinalIgnoreCase) ||
                        (Int128)owner.SourceInTicks - owner.TimelineStartTicks !=
                        (Int128)clip.SourceInTicks - clip.TimelineStartTicks)
                        continue;

                    if (matched is not null)
                    {
                        failure = new("runtime.sequence.clip_selection_ambiguous",
                            "Clip source identity maps to more than one prepared native owner.");
                        return false;
                    }
                    matched = owner;
                }

                if (matched is null)
                {
                    failure = new("runtime.sequence.clip_selection_unbound",
                        "The selected clip has no matching catalogue-bound prepared native owner.");
                    return false;
                }

                var ownerIndex = -1;
                for (var existing = 0; existing < count; existing++)
                {
                    if (ownerRequests[existing].SourceId != matched.SourceId) continue;
                    if (ownerRequests[existing].SourcePositionTicks != clip.SourcePositionTicks)
                    {
                        failure = new("runtime.sequence.clip_selection_position_conflict",
                            "One decoder cannot supply multiple positions in the same boundary.");
                        return false;
                    }
                    ownerIndex = existing;
                    break;
                }
                if (ownerIndex < 0)
                {
                    if (count >= _ledger.Capacity)
                    {
                        failure = new("runtime.sequence.clip_selection_capacity",
                            "The sequence boundary exceeds the admitted native decoder owner capacity.");
                        return false;
                    }
                    ownerIndex = count;
                    ownerRequests[count++] = new(matched.SourceId, clip.SourcePositionTicks);
                }

                clipOwnerIndices[index] = ownerIndex;
            }

            if (!TryTakeSequenceFrameSet(sequenceId, authoringRevision, position,
                ownerRequests[..count], ownerFrames[..count], out failure))
            {
                clipOwnerIndices[..clips.Length].Fill(-1);
                return false;
            }

            ownerCount = count;
            failure = null;
            accepted = true;
            return true;
            }
            finally
            {
                if (!accepted)
                    clipOwnerIndices[..clips.Length].Fill(-1);
            }
        }
    }
}
