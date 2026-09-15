using rtaime.Core;

namespace rtaime.Control.Contracts;

/// <summary>
/// Authoritative request to transition Program to a declared production source over a deterministic frame duration.
/// The actual activation boundary belongs to Runtime and is not derived from UI or wall-clock time.
/// </summary>
public sealed record DissolveProgramCommand
{
    public DissolveProgramCommand(
        ControlCommandMetadata metadata,
        ProductionSourceId sourceId,
        uint durationFrames)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        if (durationFrames < 2)
            throw new ArgumentOutOfRangeException(nameof(durationFrames), "DISSOLVE requires a duration of at least two production frames.");

        SourceId = sourceId;
        DurationFrames = durationFrames;
    }

    public ControlCommandMetadata Metadata { get; }
    public ProductionSourceId SourceId { get; }
    public uint DurationFrames { get; }
}
