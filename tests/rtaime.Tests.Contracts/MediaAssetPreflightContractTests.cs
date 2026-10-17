// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Media.Contracts;
namespace rtaime.Tests.Contracts;
public sealed class MediaAssetPreflightContractTests
{
    private static MediaAssetPreflightObservation Valid() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"), @"C:\media\test.mp4", new string('A', 64), 1, 1, "1080p50", new string('B', 64),
        MediaAssetReadinessState.ConditionallyReady, false, false, "Representative only", 6, 3, true, DateTimeOffset.UtcNow);
    [Fact]
    public void Wire_roundtrip_is_metadata_only_and_never_grants_physical_or_resident_readiness()
    {
        var value = JsonSerializer.Deserialize<MediaAssetPreflightObservation>(JsonSerializer.Serialize(Valid()))!;
        value.Validate();
        Assert.True(value.RepresentativeVerificationPassed);
        Assert.Equal("UNVERIFIED", value.PhysicalQualification);
        Assert.Throws<ArgumentException>(() => (value with { State = MediaAssetReadinessState.Prepared }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { VerifiedFrames = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { EnvironmentIdentity = "" }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { FingerprintSha256 = null! }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { Detail = null! }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { VerifiedFrames = 13 }).Validate());
        Assert.Throws<ArgumentException>(() => (value with { TargetFormat = "2160p50" }).Validate());
        Assert.False((value with { Cancelled = true }).RepresentativeVerificationPassed);
        Assert.False((value with { Running = true }).RepresentativeVerificationPassed);
    }
    [Fact]
    public void Representative_budgets_are_bounded_before_admission()
    {
        new MediaAssetPreflightOptions().Validate();
        foreach (var invalid in new[] { new MediaAssetPreflightOptions(SeekPoints: 0), new(SeekPoints: 4),
            new(FramesPerPoint: 0), new(FramesPerPoint: 5), new(MaximumBytes: 0), new(MaximumBytes: long.MaxValue) })
            Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate);
    }
}
