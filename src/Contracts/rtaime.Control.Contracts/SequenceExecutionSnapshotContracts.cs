// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;

namespace rtaime.Control.Contracts;

/// <summary>
/// An exact, bounded execution snapshot compiled from one persisted sequence
/// revision and the source identities admitted by Control. This is not a
/// Program command, a decoder reservation or a Preview/Take capability.
/// </summary>
public static class SequenceExecutionSnapshotContract
{
    public const string Version = "rtaime.sequence-execution-snapshot.v1";
    public const int MaximumWindows = 256;
    public const int MaximumSelectedClips = 32;
    public const int MaximumClipReferences = 2048;
    public const int MaximumJsonBytes = 256 * 1024;
}

public sealed record SequenceExecutionSnapshot(
    string Version, Guid SequenceId, ulong AuthoringRevision,
    SequenceRate FrameRate, long DurationTicks, SequenceExecutionSlice[] Slices)
{
    public void Validate()
    {
        if (Version != SequenceExecutionSnapshotContract.Version ||
            SequenceId == Guid.Empty || AuthoringRevision == 0 ||
            FrameRate.Numerator <= 0 || FrameRate.Denominator <= 0 ||
            (Int128)FrameRate.Numerator > (Int128)240 * FrameRate.Denominator ||
            DurationTicks is <= 0 or > SequenceContract.MaximumTime ||
            Slices is null or { Length: < 1 or > SequenceExecutionSnapshotContract.MaximumWindows })
            throw new InvalidDataException("Sequence execution snapshot version, identity, rate or duration is invalid.");

        long nextTick = 0;
        var totalReferences = 0;
        foreach (var slice in Slices)
        {
            if (slice is null || slice.StartTick != nextTick ||
                slice.EndTick <= slice.StartTick || slice.EndTick > DurationTicks ||
                slice.Clips is null or { Length: > SequenceExecutionSnapshotContract.MaximumSelectedClips })
                throw new InvalidDataException("Sequence execution slices must cover one bounded, contiguous time range.");
            totalReferences = checked(totalReferences + slice.Clips.Length);
            if (totalReferences > SequenceExecutionSnapshotContract.MaximumClipReferences)
                throw new InvalidDataException("Sequence execution clip reference budget exceeded.");

            var seen = new HashSet<Guid>();
            var previousLayer = int.MinValue;
            foreach (var clip in slice.Clips)
            {
                if (clip is null || clip.ClipId == Guid.Empty || clip.AssetId == Guid.Empty ||
                    !seen.Add(clip.ClipId) ||
                    clip.FingerprintSha256 is null or { Length: not 64 } ||
                    clip.FingerprintSha256.Any(value => !Uri.IsHexDigit(value)) ||
                    !Enum.IsDefined(clip.Kind) || clip.Kind is not (
                        SequenceMediaKind.Video or SequenceMediaKind.Audio or SequenceMediaKind.Graphics) ||
                    clip.LayerOrder < previousLayer ||
                    clip.SourceInTicks < 0 || clip.TimelineStartTicks < 0 ||
                    clip.TimelineStartTicks > slice.StartTick ||
                    (Int128)clip.SourceInTicks + slice.EndTick - clip.TimelineStartTicks >
                        SequenceContract.MaximumTime ||
                    (clip.Kind == SequenceMediaKind.Graphics) != (clip.NativeSourceId is null) ||
                    (clip.NativeSourceId is not null &&
                        (!Guid.TryParse(clip.NativeSourceId, out var source) || source == Guid.Empty)) ||
                    (clip.Kind == SequenceMediaKind.Audio && string.IsNullOrWhiteSpace(clip.AudioBusId)) ||
                    (clip.Kind != SequenceMediaKind.Audio && clip.AudioBusId is not null) ||
                    clip.AudioBusId is { Length: > 128 } ||
                    (clip.TransitionKind is null &&
                        (clip.TransitionDurationTicks != 0 || clip.TransitionStartTicks != 0 || clip.FadeIn)) ||
                    (clip.TransitionKind is not null &&
                        (clip.TransitionKind is not (SequenceTransitionKind.Dissolve or SequenceTransitionKind.Crossfade) ||
                        clip.TransitionDurationTicks <= 0 || clip.TransitionStartTicks < 0 ||
                        (Int128)clip.TransitionStartTicks + clip.TransitionDurationTicks >
                            DurationTicks)))
                    throw new InvalidDataException("Sequence execution clip or source identity is invalid.");
                previousLayer = clip.LayerOrder;
            }
            nextTick = slice.EndTick;
        }

        if (nextTick != DurationTicks ||
            JsonSerializer.SerializeToUtf8Bytes(this, SequenceJson.Options).Length >
                SequenceExecutionSnapshotContract.MaximumJsonBytes)
            throw new InvalidDataException("Sequence execution snapshot is incomplete or exceeds its transfer budget.");
    }
}

public sealed record SequenceExecutionSlice(
    long StartTick, long EndTick, SequenceExecutionClip[] Clips);

public sealed record SequenceExecutionClip(
    Guid ClipId, Guid AssetId, string FingerprintSha256, SequenceMediaKind Kind,
    string? NativeSourceId, int LayerOrder, string? AudioBusId,
    long SourceInTicks, long TimelineStartTicks,
    SequenceTransitionKind? TransitionKind, long TransitionStartTicks,
    long TransitionDurationTicks, bool FadeIn);
