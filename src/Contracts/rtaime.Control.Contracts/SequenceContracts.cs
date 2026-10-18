// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Control.Contracts;

public static class SequenceContract
{
    public const string Version = "1.0";
    public const long TicksPerSecond = 240000;
    public const long MaximumTime = 24 * 60 * 60 * TicksPerSecond;
    public const int MaximumSequences = 16;
    public const int MaximumTracks = 64;
    public const int MaximumClips = 2048;
    public const int MaximumTransitions = 1024;
    public const int MaximumEdits = 128;
    public const int MaximumJsonBytes = 512 * 1024;
}
public readonly record struct SequenceRate(int Numerator, int Denominator);
public enum SequenceMediaKind { Video = 1, Audio = 2, Graphics = 3 }
public enum SequenceTransitionKind { Cut = 1, Dissolve = 2, Crossfade = 3 }
public sealed record SequenceMediaReference(Guid AssetId, SequenceMediaKind Kind, string FingerprintSha256);
public sealed record SequenceAsset(Guid AssetId, SequenceMediaKind Kind, long DurationTicks, string FingerprintSha256);
public sealed record SequenceTrack(Guid TrackId, string Name, SequenceMediaKind Kind, int LayerOrder,
    bool Enabled = true, bool Muted = false, string? AudioBusId = null);
public sealed record SequenceClip(Guid ClipId, Guid TrackId, SequenceMediaReference Media,
    long SourceIn, long SourceOut, long TimelineStart, long Duration, bool Enabled = true, bool Muted = false, Guid? LinkGroupId = null);
public sealed record SequenceTransition(Guid TransitionId, Guid FromClipId, Guid ToClipId,
    SequenceTransitionKind Kind, long Start, long Duration);
public sealed record SequenceDefinition(string Version, Guid SequenceId, string Name, SequenceRate FrameRate,
    SequenceTrack[] Tracks, SequenceClip[] Clips, SequenceTransition[] Transitions);
public sealed record SequenceLibrary(string Version, ulong Revision, SequenceDefinition[] Sequences);
public sealed record SequenceIssue(string Code, string Path, string Message);
public sealed record SequenceValidation(Guid SequenceId, ulong AuthoringRevision, SequenceIssue[] Issues)
{
    public bool IsValid => Issues.Length == 0;
}
public sealed record SequenceSnapshot(ulong AuthoringRevision, SequenceDefinition Sequence,
    ulong? ActiveExecutionRevision, string PlaybackState, SequenceValidation Validation);
public sealed record SequenceSummary(Guid SequenceId, string Name, SequenceRate FrameRate, int Tracks, int Clips);
public sealed record SequenceList(ulong AuthoringRevision, SequenceSummary[] Sequences, string CapabilityVersion, string PlaybackState);
public enum SequenceEditKind
{
    InsertClip = 1, MoveClip = 2, DeleteClip = 3, SplitClip = 4, TrimClip = 5,
    RippleDelete = 6, CloseGap = 7, Link = 8, Unlink = 9,
    AddTrack = 10, UpdateTrack = 11, DeleteTrack = 12, AddTransition = 13, DeleteTransition = 14
}
public sealed record SequenceEdit(SequenceEditKind Kind, Guid TargetId,
    SequenceClip? Clip = null, SequenceTrack? Track = null, SequenceTransition? Transition = null,
    long? Position = null, long? SourceIn = null, long? SourceOut = null,
    long? Duration = null, Guid? TrackId = null, Guid[]? Ids = null, Guid? LinkGroupId = null);
public sealed record SequenceMutationRequest(string Version, Guid TransactionId, ulong ExpectedRevision,
    Guid SequenceId, SequenceDefinition? Definition = null, SequenceEdit[]? Edits = null);
public sealed record SequenceMutationResult(bool Saved, ulong AuthoringRevision, Guid SequenceId,
    SequenceDefinition? Sequence, SequenceIssue[] Issues, bool Replayed = false);
