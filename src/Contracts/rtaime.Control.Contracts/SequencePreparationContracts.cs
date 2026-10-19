// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Control.Contracts;

/// <summary>
/// A separate, opt-in native resource preparation capability. The authoring
/// contract and durable show-project format are not changed. Preparation
/// neither commits a production execution nor enables Preview/Program playback.
/// </summary>
public static class SequencePreparationContract
{
    public const string Version = "rtaime.sequence-preparation.v1";
    public const int MaximumNativeOwners = 4;
    public const int MaximumDetailLength = 512;
}

/// <summary>
/// The caller supplies saved identity/revision and the RuntimeHost epoch observed
/// in its last authoritative snapshot. No local paths or decoder handles cross
/// the client API.
/// </summary>
public sealed record SequencePreparationRequest(
    string Version, Guid SequenceId, ulong ExpectedAuthoringRevision, string ExpectedRuntimeHostInstanceId)
{
    public void Validate()
    {
        if (Version != SequencePreparationContract.Version ||
            SequenceId == Guid.Empty || ExpectedAuthoringRevision == 0 ||
            !Guid.TryParse(ExpectedRuntimeHostInstanceId, out var epoch) || epoch == Guid.Empty)
            throw new ArgumentException("Sequence preparation requires a saved revision and current RuntimeHost identity.");
    }
}

/// <summary>
/// Exact prepared owner fence. A reference from another ControlHost lifetime,
/// authoring revision or RuntimeHost is not transferable.
/// </summary>
public sealed record SequencePreparationReference(
    string Version, Guid SequenceId, ulong AuthoringRevision,
    string RuntimeHostInstanceId, string PreparedExecutionId, ulong Generation)
{
    public void Validate()
    {
        if (Version != SequencePreparationContract.Version || SequenceId == Guid.Empty ||
            AuthoringRevision == 0 || Generation == 0 ||
            !Guid.TryParse(RuntimeHostInstanceId, out var runtime) || runtime == Guid.Empty ||
            !Guid.TryParse(PreparedExecutionId, out var prepared) || prepared == Guid.Empty)
            throw new ArgumentException("Sequence preparation reference is invalid or unsupported.");
    }
}

/// <summary>
/// Runtime-reported resource state, not playback state. PREPARED is never
/// confirmation of Preview or Program delivery.
/// </summary>
public sealed record SequencePreparationStatus(
    string Version, Guid SequenceId, ulong AuthoringRevision,
    string RuntimeHostInstanceId, string PreparedExecutionId, ulong Generation,
    int DecoderOwners, string State, string? ErrorCode, string Detail)
{
    public void Validate()
    {
        if (Version != SequencePreparationContract.Version || SequenceId == Guid.Empty ||
            AuthoringRevision == 0 || Generation == 0 ||
            !Guid.TryParse(RuntimeHostInstanceId, out var runtime) || runtime == Guid.Empty ||
            DecoderOwners is < 0 or > SequencePreparationContract.MaximumNativeOwners ||
            State is not ("PREPARED" or "CANCELED" or "REJECTED" or "STALE") ||
            Detail is null || Detail.Length > SequencePreparationContract.MaximumDetailLength ||
            ErrorCode is { Length: > 128 } ||
            State == "PREPARED" && (DecoderOwners == 0 ||
                !Guid.TryParse(PreparedExecutionId, out var prepared) || prepared == Guid.Empty) ||
            State != "PREPARED" && DecoderOwners != 0)
            throw new InvalidDataException("Invalid bounded sequence preparation observation.");
    }
}
