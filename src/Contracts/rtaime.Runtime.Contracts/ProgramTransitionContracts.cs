using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Runtime.Contracts;

public enum RuntimeProgramTransitionKind
{
    Cut = 1,
    Dissolve = 2
}

/// <summary>
/// Prepared transition intent delivered with an authoritative Runtime replacement.
/// Runtime anchors the intent to its next production boundary after a successful commit.
/// </summary>
public sealed record RuntimeProgramTransitionIntent
{
    public RuntimeProgramTransitionIntent(
        CompatibilityVersion version,
        RuntimeProgramTransitionKind kind,
        MediaSourceId fromSourceId,
        MediaSourceId toSourceId,
        uint durationFrames)
    {
        RuntimeContractVersion.EnsureSupported(version);
        if (!Enum.IsDefined(typeof(RuntimeProgramTransitionKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (fromSourceId == toSourceId)
            throw new ArgumentException("Program transition source and destination must be distinct.", nameof(toSourceId));
        if (kind == RuntimeProgramTransitionKind.Cut && durationFrames != 1)
            throw new ArgumentOutOfRangeException(nameof(durationFrames), "CUT must activate on exactly one production boundary.");
        if (kind == RuntimeProgramTransitionKind.Dissolve && durationFrames < 2)
            throw new ArgumentOutOfRangeException(nameof(durationFrames), "DISSOLVE requires at least two production frames.");

        Version = version;
        Kind = kind;
        FromSourceId = fromSourceId;
        ToSourceId = toSourceId;
        DurationFrames = durationFrames;
    }

    public CompatibilityVersion Version { get; }
    public RuntimeProgramTransitionKind Kind { get; }
    public MediaSourceId FromSourceId { get; }
    public MediaSourceId ToSourceId { get; }
    public uint DurationFrames { get; }

    public static RuntimeProgramTransitionIntent Cut(MediaSourceId from, MediaSourceId to) =>
        new(RuntimeContractVersion.Current, RuntimeProgramTransitionKind.Cut, from, to, 1);

    public static RuntimeProgramTransitionIntent Dissolve(MediaSourceId from, MediaSourceId to, uint durationFrames) =>
        new(RuntimeContractVersion.Current, RuntimeProgramTransitionKind.Dissolve, from, to, durationFrames);
}
