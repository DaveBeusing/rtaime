// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime;

namespace rtaime.RuntimeHost;

/// <summary>
/// Validates the compiled Control execution snapshot against the actual
/// native owners offered to the existing Runtime ledger. This does not
/// authorize publication or advance a Program or Preview clock.
/// </summary>
internal static class SequenceNativeExecutionSnapshot
{
    internal static void Validate(SequenceExecutionSnapshot snapshot,
        SequenceRuntimePositionMap map, IReadOnlyList<SequenceMediaOwnerRequest> owners)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(owners);
        snapshot.Validate();
        if (snapshot.SequenceId != map.SequenceId ||
            snapshot.AuthoringRevision != map.AuthoringRevision ||
            snapshot.DurationTicks != map.DurationTicks ||
            (Int128)snapshot.FrameRate.Numerator * map.ProgramRate.Denominator !=
            (Int128)map.ProgramRate.Numerator * snapshot.FrameRate.Denominator)
            throw new InvalidDataException("Compiled sequence identity, revision, duration or Program cadence changed.");

        if (owners.Count is < 1 or > SequencePreparationContract.MaximumScalableNativeOwners ||
            owners.Select(owner => owner.SourceId).Distinct().Count() != owners.Count)
            throw new InvalidDataException("Compiled sequence requires a finite unique native owner set.");

        var used = new HashSet<MediaSourceId>();
        foreach (var slice in snapshot.Slices)
        foreach (var clip in slice.Clips)
        {
            if (clip.Kind == SequenceMediaKind.Graphics) continue;
            var sourceId = new MediaSourceId(Identity.Parse(clip.NativeSourceId!));
            var source = owners.SingleOrDefault(owner => owner.SourceId == sourceId);
            if (source is null ||
                source.AssetId.ToString() != clip.AssetId.ToString() ||
                !string.Equals(source.FingerprintSha256, clip.FingerprintSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                (Int128)source.SourceInTicks - source.TimelineStartTicks !=
                (Int128)clip.SourceInTicks - clip.TimelineStartTicks ||
                clip.SourceInTicks + (Int128)slice.EndTick - clip.TimelineStartTicks >
                    SequenceContract.MaximumTime)
                throw new InvalidDataException(
                    "Compiled sequence clip is not bound to its exact admitted native source and time offset.");
            used.Add(sourceId);
        }
        if (used.Count != owners.Count)
            throw new InvalidDataException("Compiled sequence contains an unreferenced native source reservation.");
    }
}
