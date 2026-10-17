// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Media.Contracts;

// Metadata only. A resident candidate is not a no-gap or physical qualification.
public sealed record MediaDeckPreparation(
    string TicketId, string RuntimeEpoch, string SourceId, string AssetId,
    string PreparedExecutionId, ulong AuthorityRevision, long DecodeGeneration,
    long PositionFrame, long EndFrame, string FingerprintSha256,
    DateTimeOffset ExpiresAtUtc);
