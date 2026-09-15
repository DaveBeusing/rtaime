using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Integration;

public sealed class AudioFollowVideoIntegrationTests
{
    private static readonly ProductionId ProductionId =
        new(Identity.Parse("93000000-0000-0000-0000-000000000001"));
    private static readonly ProductionSourceId SourceAId =
        new(Identity.Parse("93000000-0000-0000-0000-000000000002"));
    private static readonly ProductionSourceId SourceBId =
        new(Identity.Parse("93000000-0000-0000-0000-000000000003"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Committed_program_cut_switches_video_and_followed_audio_on_same_boundary(bool use5994)
    {
        var videoFormat = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var specification = CreateSpecification(SourceAId);
        var initialization = ControlDomainEngine.Initialize(specification);
        Assert.True(initialization.Succeeded);
        var authoritative = initialization.State!.Authoritative;

        var videoProvider = new VirtualMediaReferenceProvider(
            new MediaSourceId(SourceAId.Value),
            new MediaSourceId(SourceBId.Value),
            videoFormat);
        var audioProvider = new VirtualEmbeddedAudioReferenceProvider(
            videoProvider.SourceA.SourceId,
            videoProvider.SourceB.SourceId,
            videoFormat);
        var registry = new SingleProviderCapabilityRegistry(videoProvider.Descriptor);

        var planning = CapabilityPlanningEngine.Plan(specification, authoritative, registry);
        Assert.True(planning.Succeeded);
        var programSinkId = planning.Graph!.Nodes
            .Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink)
            .MediaSinkId!.Value;

        var runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());
        PrepareAndCommit(runtime, planning.PreparedExecution!, Revision.Initial);

        var afv = new AudioFollowVideoEngine(
            audioProvider.Streams,
            videoFormat.FrameRate,
            videoProvider.SourceA.SourceId);

        var beforeCut = ProcessCommittedProgramBoundary(runtime, programSinkId, audioProvider, afv, 0);
        Assert.Equal(videoProvider.SourceA.SourceId, beforeCut.VideoSourceId);
        Assert.Equal(audioProvider.SourceA.Descriptor.StreamId, beforeCut.StreamId);
        Assert.True(beforeCut.Emitted);

        var cut = new CutProgramCommand(
            new ControlCommandMetadata(
                ControlContractVersion.Current,
                new CommandId(Identity.Parse("93000000-0000-0000-0000-000000000004")),
                specification.ProductionId,
                authoritative.Revision),
            SourceBId);
        var cutResult = ControlDomainEngine.Apply(specification, authoritative, cut);
        Assert.True(cutResult.Committed);
        authoritative = cutResult.AuthoritativeState;

        var cutPlanning = CapabilityPlanningEngine.Plan(specification, authoritative, registry);
        Assert.True(cutPlanning.Succeeded);
        PrepareAndCommit(runtime, cutPlanning.PreparedExecution!, runtime.State.ExecutionRevision);

        var afterCut = ProcessCommittedProgramBoundary(runtime, programSinkId, audioProvider, afv, 1);

        Assert.Equal(videoProvider.SourceB.SourceId, afterCut.VideoSourceId);
        Assert.Equal(audioProvider.SourceB.Descriptor.StreamId, afterCut.StreamId);
        Assert.True(afterCut.Emitted);
        Assert.Equal((ulong)1, afv.Statistics.Switches);
        Assert.Equal((ulong)2, afv.Statistics.Emitted);
        Assert.Equal((ulong)0, afv.Statistics.Underruns);
        Assert.Contains(afv.Observations, observation =>
            observation.Code == "audio.afv.switched" && observation.VideoFrameSequence == 1);

        var expectedWindow = AudioVideoTimingRelationship.GetSampleWindow(
            videoFormat.FrameRate,
            audioProvider.AudioFormat.SampleRate,
            1);
        Assert.Equal(expectedWindow.SamplePosition, afterCut.SamplePosition);
        Assert.Equal(expectedWindow.SampleCount, afterCut.SampleCount);
    }

    [Fact]
    public void Per_input_gain_and_mute_follow_the_stream_selected_by_committed_program_state()
    {
        var videoFormat = VideoFormat.Hd1080p50Rgba8;
        var specification = CreateSpecification(SourceBId);
        var initialization = ControlDomainEngine.Initialize(specification);
        Assert.True(initialization.Succeeded);
        var authoritative = initialization.State!.Authoritative;

        var videoProvider = new VirtualMediaReferenceProvider(
            new MediaSourceId(SourceAId.Value),
            new MediaSourceId(SourceBId.Value),
            videoFormat);
        var audioProvider = new VirtualEmbeddedAudioReferenceProvider(
            videoProvider.SourceA.SourceId,
            videoProvider.SourceB.SourceId,
            videoFormat);
        var registry = new SingleProviderCapabilityRegistry(videoProvider.Descriptor);
        var planning = CapabilityPlanningEngine.Plan(specification, authoritative, registry);
        Assert.True(planning.Succeeded);
        var programSinkId = planning.Graph!.Nodes
            .Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink)
            .MediaSinkId!.Value;

        var runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());
        PrepareAndCommit(runtime, planning.PreparedExecution!, Revision.Initial);

        var afv = new AudioFollowVideoEngine(
            audioProvider.Streams,
            videoFormat.FrameRate,
            videoProvider.SourceB.SourceId);
        afv.SetInputState(audioProvider.SourceB.Descriptor.StreamId, new AudioGain(0.5), muted: true);

        var result = ProcessCommittedProgramBoundary(runtime, programSinkId, audioProvider, afv, 0);

        Assert.Equal(audioProvider.SourceB.Descriptor.StreamId, result.StreamId);
        Assert.True(result.Muted);
        Assert.Equal(0.5, result.Gain.Linear, 6);
        Assert.Equal(0, result.PeakLevel);
    }

    private static ProductionSpecification CreateSpecification(ProductionSourceId programSourceId)
    {
        var preview = programSourceId == SourceAId ? SourceBId : SourceAId;
        return new ProductionSpecification(
            ControlContractVersion.Current,
            ProductionId,
            "Audio Follow Video Integration",
            new[]
            {
                new ProductionSourceSpecification(SourceAId, "Source A"),
                new ProductionSourceSpecification(SourceBId, "Source B")
            },
            new ProductionRoutingState(preview, programSourceId));
    }

    private static AudioFollowVideoResult ProcessCommittedProgramBoundary(
        TransactionalRuntime runtime,
        MediaSinkId programSinkId,
        VirtualEmbeddedAudioReferenceProvider audioProvider,
        AudioFollowVideoEngine afv,
        ulong sequence)
    {
        var active = runtime.ActiveExecution ?? throw new Xunit.Sdk.XunitException("Committed execution is required.");
        var programBinding = active.PreparedExecution.Bindings.Single(binding => binding.MediaSinkId == programSinkId);
        var videoSourceId = programBinding.MediaSourceId
            ?? throw new Xunit.Sdk.XunitException("Committed Program binding requires a media source.");
        var packet = audioProvider.GetSource(videoSourceId.Value).GeneratePacket(sequence);

        return afv.ProcessBoundary(
            videoSourceId.Value,
            sequence,
            packet.Descriptor,
            packet.PeakLevel);
    }

    private static void PrepareAndCommit(
        TransactionalRuntime runtime,
        PreparedExecutionContract prepared,
        Revision expectedExecutionRevision)
    {
        var prepare = runtime.Prepare(prepared);
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);
        var commit = runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            prepared.PreparedExecutionId,
            prepare.ReservationId!.Value,
            expectedExecutionRevision));
        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);
    }

    private sealed class SingleProviderCapabilityRegistry : IProviderCapabilityRegistry
    {
        private readonly IReadOnlyList<ProviderDescriptor> _providers;

        public SingleProviderCapabilityRegistry(ProviderDescriptor descriptor)
        {
            _providers = new[] { descriptor };
        }

        public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
    }
}
