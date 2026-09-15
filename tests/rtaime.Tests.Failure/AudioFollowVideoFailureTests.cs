using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Failure;

public sealed class AudioFollowVideoFailureTests
{
    [Fact]
    public void Cut_to_new_source_with_missing_audio_reports_underrun_and_recovers_without_fallback()
    {
        var sourceA = new MediaSourceId(Identity.Parse("94000000-0000-0000-0000-000000000001"));
        var sourceB = new MediaSourceId(Identity.Parse("94000000-0000-0000-0000-000000000002"));
        var provider = new VirtualEmbeddedAudioReferenceProvider(
            sourceA,
            sourceB,
            VideoFormat.Hd1080p50Rgba8);
        var afv = new AudioFollowVideoEngine(
            provider.Streams,
            FrameRate.Fps50,
            sourceA);

        var firstPacket = provider.SourceA.GeneratePacket(0);
        var first = afv.ProcessBoundary(
            sourceA,
            0,
            firstPacket.Descriptor,
            firstPacket.PeakLevel);
        Assert.True(first.Emitted);

        var underrun = afv.ProcessBoundary(sourceB, 1, null, 0);

        Assert.Equal(AudioFollowVideoStatus.Underrun, underrun.Status);
        Assert.Equal("audio.afv.underrun", underrun.Failure!.Value.Code);
        Assert.Equal(sourceB, afv.ActiveVideoSourceId);
        Assert.Equal(provider.SourceB.Descriptor.StreamId, afv.ActiveStreamId);
        Assert.Equal((ulong)1, afv.Statistics.Switches);
        Assert.Equal((ulong)1, afv.Statistics.Underruns);
        Assert.Contains(afv.Observations, observation => observation.Code == "audio.afv.underrun");

        var recoveredPacket = provider.SourceB.GeneratePacket(2);
        var recovered = afv.ProcessBoundary(
            sourceB,
            2,
            recoveredPacket.Descriptor,
            recoveredPacket.PeakLevel);

        Assert.True(recovered.Emitted);
        Assert.Equal(provider.SourceB.Descriptor.StreamId, recovered.StreamId);
        Assert.Equal((ulong)2, afv.Statistics.Emitted);
        Assert.Equal((ulong)1, afv.Statistics.Underruns);
        Assert.Equal(0.75, recovered.PeakLevel, 6);
    }
}
