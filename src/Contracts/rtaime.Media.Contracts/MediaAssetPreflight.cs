// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Media.Contracts;

public enum MediaAssetReadinessState
{
    Unverified = 0, Probed = 1, DecoderAvailable = 2, Prepared = 3,
    ConditionallyReady = 4, Unavailable = 5
}

// These are representative verification budgets, not production performance guarantees.
public sealed record MediaAssetPreflightOptions(
    bool RequireAudio = false, int SeekPoints = 3, int FramesPerPoint = 2,
    long MaximumBytes = 8L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        if (SeekPoints is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(SeekPoints));
        if (FramesPerPoint is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(FramesPerPoint));
        if (MaximumBytes is < 1 or > 32L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaximumBytes));
    }
}

// Metadata only. A result never carries a lease, route command or authority revision.
public sealed record MediaAssetPreflightObservation(
    string JobId, string AssetId, string RuntimeEpoch, string SourceLocation,
    string FingerprintSha256, long LengthBytes, long LastWriteUtcTicks,
    string TargetFormat, string EnvironmentIdentity, MediaAssetReadinessState State,
    bool Running, bool Cancelled, string Detail, int VerifiedFrames, int VerifiedSeeks,
    bool AudioRequired, DateTimeOffset ObservedAtUtc)
{
    public void Validate()
    {
        if (!Guid.TryParse(JobId, out _) || !Guid.TryParse(AssetId, out _) ||
            !Guid.TryParse(RuntimeEpoch, out _) || !Enum.IsDefined(State) ||
            FingerprintSha256.Length != 64 || FingerprintSha256.Any(c => !Uri.IsHexDigit(c)) ||
            SourceLocation.Length is < 1 or > 4096 || TargetFormat is not ("1080p50" or "1080p59.94") ||
            LengthBytes < 0 || LastWriteUtcTicks < 0 || VerifiedFrames is < 0 or > 12 ||
            VerifiedSeeks is < 0 or > 3 || Detail.Length > 4096 ||
            State == MediaAssetReadinessState.Prepared ||
            RepresentativeVerificationPassed && (LengthBytes == 0 || VerifiedFrames == 0 || VerifiedSeeks == 0 || EnvironmentIdentity.Length != 64))
            throw new ArgumentException("Invalid media preflight observation; no readiness may be inferred.");
    }
    public string PhysicalQualification => "UNVERIFIED";
    public bool RepresentativeVerificationPassed =>
        !Running && !Cancelled && State == MediaAssetReadinessState.ConditionallyReady;
}
