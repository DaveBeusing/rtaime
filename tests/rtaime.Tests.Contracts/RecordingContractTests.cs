using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Contracts;

public sealed class RecordingContractTests
{
    [Fact]
    public void Recording_contract_version_is_explicit_and_fails_closed()
    {
        Assert.Equal(new CompatibilityVersion(1, 0), RecordingContractVersion.Current);
        Assert.True(RecordingContractVersion.IsSupported(new CompatibilityVersion(1, 0)));
        Assert.False(RecordingContractVersion.IsSupported(new CompatibilityVersion(99, 0)));
        Assert.Throws<NotSupportedException>(() =>
            RecordingContractVersion.EnsureSupported(new CompatibilityVersion(99, 0)));
    }

    [Fact]
    public void Recording_output_identity_is_explicit_and_tied_to_program_sink()
    {
        var outputId = new RecordingOutputId(Identity.Parse("94000000-0000-0000-0000-000000000001"));
        var sinkId = new MediaSinkId(Identity.Parse("94000000-0000-0000-0000-000000000002"));
        var descriptor = new RecordingOutputDescriptor(outputId, sinkId, "Program Recording");

        Assert.Equal(outputId, descriptor.OutputId);
        Assert.Equal(sinkId, descriptor.ProgramSinkId);
        Assert.Equal("Program Recording", descriptor.Name);
    }

    [Fact]
    public void Recording_start_request_rejects_unknown_contract_version()
    {
        var output = new RecordingOutputDescriptor(RecordingOutputId.New(), MediaSinkId.New(), "Program");

        Assert.Throws<NotSupportedException>(() => new RecordingStartRequest(
            new CompatibilityVersion(2, 0),
            RecordingSessionId.New(),
            output));
    }

    [Fact]
    public void Recording_program_sample_carries_descriptors_not_bulk_media_payload()
    {
        var frame = Frame(7);
        var sample = new RecordingProgramSample(
            RecordingContractVersion.Current,
            RecordingOutputId.New(),
            frame,
            null);

        Assert.Same(frame, sample.Video);
        Assert.Equal(7UL, sample.SequenceNumber);
        Assert.Null(sample.Audio);
        Assert.DoesNotContain(
            typeof(RecordingProgramSample).GetProperties(),
            property => property.PropertyType == typeof(byte[]) || property.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    private static FrameDescriptor Frame(ulong sequence) => new(
        MediaContractVersion.Current,
        MediaSourceId.New(),
        new SurfaceDescriptor(
            SurfaceId.New(),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(new Generation(sequence), Identity.New()),
            new OpaqueSurfaceHandle("recording.contract.test", sequence.ToString())),
        new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));
}
