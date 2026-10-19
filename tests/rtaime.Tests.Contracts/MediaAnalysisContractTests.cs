// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media.Contracts;
using Xunit;

namespace rtaime.Tests.Contracts;

public sealed class MediaAnalysisContractTests
{
    private static MediaAnalysisRequest Frame(int width = 320, int height = 180) =>
        new(MediaAnalysisContract.Version, Guid.NewGuid().ToString(), "library-hover", 100,
            MediaAnalysisKind.Frame, TimeSpan.FromSeconds(3).Ticks, 0, width, height, 0, 0);

    [Fact]
    public void Frame_rejects_unbounded_geometry_unknown_version_and_invalid_generation()
    {
        Frame().Validate();
        Assert.Throws<ArgumentException>(() => Frame(321).Validate());
        Assert.Throws<ArgumentException>(() => Frame(320, 181).Validate());
        Assert.Throws<ArgumentException>(() => (Frame() with { Version = "v2" }).Validate());
        Assert.Throws<ArgumentException>(() => (Frame() with { Generation = -1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Frame() with { ConsumerId = "bad\\nconsumer" }).Validate());
    }

    [Fact]
    public void Waveform_requires_finite_rational_time_range_and_bounded_resolution()
    {
        var request = Frame() with {
            Kind = MediaAnalysisKind.Waveform, EndTicks = TimeSpan.FromSeconds(60).Ticks,
            Width = 0, Height = 0, BucketCount = 256, Level = 4
        };
        request.Validate();
        Assert.Throws<ArgumentException>(() => (request with { BucketCount = 257 }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { EndTicks = request.PositionTicks }).Validate());
        Assert.Throws<ArgumentException>(() => (request with {
            EndTicks = TimeSpan.FromMinutes(10).Ticks }).Validate());
    }

    [Fact]
    public void File_identity_must_be_a_complete_fingerprint()
    {
        new MediaAnalysisFileIdentity(new string('A', 64), 100, DateTime.UtcNow.Ticks).Validate();
        Assert.Throws<ArgumentException>(() => new MediaAnalysisFileIdentity("bad", 100, 1).Validate());
        Assert.Throws<ArgumentException>(() => new MediaAnalysisFileIdentity(new string('A', 64), 0, 1).Validate());
    }

    [Fact]
    public void Observation_rejects_unbounded_frame_payload_or_fake_running_data()
    {
        var request = Frame(8, 8);
        var response = new MediaAnalysisObservation(MediaAnalysisContract.Version, Guid.NewGuid().ToString(),
            request.AssetId, request.ConsumerId, request.Generation, Guid.NewGuid().ToString("N"),
            MediaAnalysisKind.Frame, MediaAnalysisState.Complete, request.PositionTicks,
            request.PositionTicks, 8, 8, new byte[8 * 8 * 4], 0, 0, null, false, false, null, "");
        response.Validate();
        Assert.Throws<InvalidDataException>(() => (response with { RgbaPixels = new byte[257] }).Validate());
        Assert.Throws<InvalidDataException>(() => (response with { State = MediaAnalysisState.Running }).Validate());
    }

    [Fact]
    public void Waveform_preserves_gaps_instead_of_inventing_zero_amplitude()
    {
        var request = Frame() with { Kind = MediaAnalysisKind.Waveform,
            Width = 0, Height = 0, EndTicks = TimeSpan.FromSeconds(4).Ticks,
            BucketCount = 2 };
        request.Validate();
        var missing = new MediaAnalysisWaveformBucket(0, 100, 0, 0, false, true,
            [0, 0], [0, 0], null);
        var response = new MediaAnalysisObservation(MediaAnalysisContract.Version, Guid.NewGuid().ToString(),
            request.AssetId, request.ConsumerId, request.Generation, Guid.NewGuid().ToString("N"),
            MediaAnalysisKind.Waveform, MediaAnalysisState.Partial, request.PositionTicks, null,
            0, 0, null, 48000, 2, [missing], false, false, null, "Samples unavailable");
        response.Validate();
    }
}
