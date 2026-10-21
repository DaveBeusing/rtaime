// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Control.Contracts;

/// <summary>
/// Opt-in sequence Preview and Program output capability. This version
/// does not change published sequence authoring, Show or production schemas.
/// </summary>
public static class SequenceOutputContract
{
    public const string Version = "rtaime.sequence-output.v1";
    public const int MaximumDetailLength = 512;
}

/// <summary>
/// Control authorizes a previously prepared sequence at its current Preview
/// route; Take is a separate optimistic, non-replayable production mutation.
/// </summary>
public sealed record SequenceOutputCommand(
    string Version, SequencePreparationReference Preparation,
    string PreviewSourceId, ulong ExpectedProductionRevision,
    Guid TransactionId)
{
    public void Validate()
    {
        if (Version != SequenceOutputContract.Version || Preparation is null ||
            !Guid.TryParse(PreviewSourceId, out var source) || source == Guid.Empty ||
            TransactionId == Guid.Empty)
            throw new InvalidDataException("Sequence output command requires a valid source, preparation and unique transaction.");
        Preparation.Validate();
    }
}

public sealed record SequenceOutputObservation(
    string Version, SequencePreparationReference Preparation,
    string PreviewSourceId, string OutputState,
    ulong PublishedSourceFrames, ulong FailedSourceFrames,
    ulong? LastSourceSlot, ulong? LastProgramSlot,
    ulong ProductionRevision, string? FailureCode, string Detail)
{
    public void Validate()
    {
        if (Version != SequenceOutputContract.Version || Preparation is null ||
            !Guid.TryParse(PreviewSourceId, out var source) || source == Guid.Empty ||
            OutputState is not ("UNAVAILABLE" or "PREVIEW_READY" or "BUFFERING" or
                "TAKE_PENDING" or "TAKE_COMMITTED" or "PROGRAM_CONFIRMED" or
                "STALE" or "FAILED") ||
            Detail is null || Detail.Length > SequenceOutputContract.MaximumDetailLength ||
            FailureCode is { Length: > 128 } ||
            LastProgramSlot is not null &&
                (LastSourceSlot is null || LastProgramSlot > LastSourceSlot))
            throw new InvalidDataException("Sequence output observation has invalid or unconfirmed state.");
        Preparation.Validate();
    }
}
