// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Control.Contracts;

/// <summary>
/// A separate, opt-in native resource preparation capability. The authoring
/// contract and durable show-project format are not changed. Preparation
/// neither commits a production execution nor enables Preview/Program playback.
/// </summary>
public static class SequencePreparationContract
{
    // Historical readers retain the original finite four-owner identity.
    public const string Version = "rtaime.sequence-preparation.v1";
    public const int MaximumNativeOwners = 4;
    // Explicit opt-in contract. This is a protocol safety ceiling, not provider capacity.
    public const string ScalableVersion = "rtaime.sequence-preparation.v2";
    public const int MaximumScalableNativeOwners = 64;
    public const int MaximumDetailLength = 512;

    public static bool IsSupportedVersion(string? version) =>
        version is Version or ScalableVersion;

    public static int MaximumOwnersForVersion(string version) => version switch
    {
        Version => MaximumNativeOwners,
        ScalableVersion => MaximumScalableNativeOwners,
        _ => throw new NotSupportedException("Unsupported sequence preparation protocol version.")
    };
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
        if (!SequencePreparationContract.IsSupportedVersion(Version) ||
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
        if (!SequencePreparationContract.IsSupportedVersion(Version) || SequenceId == Guid.Empty ||
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
        if (!SequencePreparationContract.IsSupportedVersion(Version) || SequenceId == Guid.Empty ||
            AuthoringRevision == 0 || Generation == 0 ||
            !Guid.TryParse(RuntimeHostInstanceId, out var runtime) || runtime == Guid.Empty ||
            DecoderOwners < 0 || DecoderOwners > SequencePreparationContract.MaximumOwnersForVersion(Version) ||
            State is not ("PREPARED" or "CANCELED" or "REJECTED" or "STALE") ||
            Detail is null || Detail.Length > SequencePreparationContract.MaximumDetailLength ||
            ErrorCode is { Length: > 128 } ||
            State == "PREPARED" && (DecoderOwners == 0 ||
                !Guid.TryParse(PreparedExecutionId, out var prepared) || prepared == Guid.Empty) ||
            State != "PREPARED" && DecoderOwners != 0)
            throw new InvalidDataException("Invalid bounded sequence preparation observation.");
    }
}

/// <summary>Current Control-observed epoch and saved revision; no resource readiness promise.</summary>
public sealed record SequencePreparationContext(
    string Version, string RuntimeHostInstanceId, ulong AuthoringRevision)
{
    public void Validate()
    {
        if (!SequencePreparationContract.IsSupportedVersion(Version) ||
            !Guid.TryParse(RuntimeHostInstanceId, out var runtime) || runtime == Guid.Empty)
            throw new InvalidDataException("Sequence preparation context is stale or unavailable.");
    }
}
