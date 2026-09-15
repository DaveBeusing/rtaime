using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;

namespace rtaime.Tests.Behavioral;

public sealed class AudioFollowVideoBehaviorTests
{
    [Fact]
    public void Authoritative_cut_and_program_audio_follow_move_to_the_same_source()
    {
        var productionId = new ProductionId(Identity.Parse("96000000-0000-0000-0000-000000000001"));
        var sourceA = new ProductionSourceId(Identity.Parse("96000000-0000-0000-0000-000000000002"));
        var sourceB = new ProductionSourceId(Identity.Parse("96000000-0000-0000-0000-000000000003"));
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            productionId,
            "AFV Behavioral Proof",
            new[]
            {
                new ProductionSourceSpecification(sourceA, "Input 1"),
                new ProductionSourceSpecification(sourceB, "Input 2")
            },
            new ProductionRoutingState(sourceB, sourceA));

        var initialization = ControlDomainEngine.Initialize(specification);
        Assert.True(initialization.Succeeded);
        var authoritative = initialization.State!.Authoritative;

        var audioProvider = new VirtualEmbeddedAudioReferenceProvider(
            new MediaSourceId(sourceA.Value),
            new MediaSourceId(sourceB.Value),
            VideoFormat.Hd1080p50Rgba8);
        var afv = new AudioFollowVideoEngine(
            audioProvider.Streams,
            FrameRate.Fps50,
            new MediaSourceId(sourceA.Value));

        var beforePacket = audioProvider.SourceA.GeneratePacket(0);
        var before = afv.ProcessBoundary(
            new MediaSourceId(authoritative.Routing.ProgramSourceId.Value),
            0,
            beforePacket.Descriptor,
            beforePacket.PeakLevel);
        Assert.True(before.Emitted);
        Assert.Equal(audioProvider.SourceA.Descriptor.StreamId, before.StreamId);

        var cut = new CutProgramCommand(
            new ControlCommandMetadata(
                ControlContractVersion.Current,
                new CommandId(Identity.Parse("96000000-0000-0000-0000-000000000004")),
                productionId,
                authoritative.Revision),
            sourceB);
        var result = ControlDomainEngine.Apply(specification, authoritative, cut);

        Assert.True(result.Committed);
        Assert.Equal(sourceB, result.AuthoritativeState.Routing.ProgramSourceId);
        Assert.Equal(authoritative.Revision.Next(), result.AuthoritativeState.Revision);

        var afterPacket = audioProvider.SourceB.GeneratePacket(1);
        var after = afv.ProcessBoundary(
            new MediaSourceId(result.AuthoritativeState.Routing.ProgramSourceId.Value),
            1,
            afterPacket.Descriptor,
            afterPacket.PeakLevel);

        Assert.True(after.Emitted);
        Assert.Equal(audioProvider.SourceB.Descriptor.StreamId, after.StreamId);
        Assert.Equal(new MediaSourceId(sourceB.Value), afv.ActiveVideoSourceId);
        Assert.Equal(audioProvider.SourceB.Descriptor.StreamId, afv.ActiveStreamId);
        Assert.Equal((ulong)1, afv.Statistics.Switches);
    }
}
