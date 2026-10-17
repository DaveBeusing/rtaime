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
    public string PhysicalQualification => "UNVERIFIED";
    public bool RepresentativeVerificationPassed =>
        !Running && !Cancelled && State == MediaAssetReadinessState.ConditionallyReady;
}
