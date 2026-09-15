using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Integration;

public sealed class VirtualMediaRuntimeSliceTests
{
    [Fact]
    public void Source_A_flows_to_program_output()
    {
        var harness = CreateHarness(VideoFormat.Hd1080p50Rgba8, programSource: SourceAId);

        ProcessFrames(harness, 3);

        Assert.Equal(3, harness.ProgramOutput.Frames.Count);
        Assert.All(harness.ProgramOutput.Frames, frame => Assert.Equal(harness.Provider.SourceA.SourceId, frame.Frame.SourceId));
        Assert.Equal(new ulong[] { 0, 1, 2 }, harness.ProgramOutput.Frames.Select(frame => frame.Frame.Timing.SequenceNumber));
    }

    [Fact]
    public void Source_B_flows_to_program_output()
    {
        var harness = CreateHarness(VideoFormat.Hd1080p50Rgba8, programSource: SourceBId);

        ProcessFrames(harness, 3);

        Assert.Equal(3, harness.ProgramOutput.Frames.Count);
        Assert.All(harness.ProgramOutput.Frames, frame => Assert.Equal(harness.Provider.SourceB.SourceId, frame.Frame.SourceId));
    }

    [Fact]
    public void Cut_from_A_to_B_activates_on_the_next_frame_boundary()
    {
        var harness = CreateHarness(VideoFormat.Hd1080p50Rgba8, programSource: SourceAId);

        var beforeCut = harness.MediaRuntime.ProcessNextFrameBoundary();
        Assert.True(beforeCut.Succeeded);
        Assert.Equal((ulong)0, beforeCut.SequenceNumber);
        Assert.Equal(harness.Provider.SourceA.SourceId, harness.ProgramOutput.LastFrame!.Frame.SourceId);
        Assert.Equal(new Revision(1), beforeCut.ExecutionRevision);

        CommitCut(harness, SourceBId);

        var afterCut = harness.MediaRuntime.ProcessNextFrameBoundary();
        Assert.True(afterCut.Succeeded);
        Assert.Equal((ulong)1, afterCut.SequenceNumber);
        Assert.Equal(harness.Provider.SourceB.SourceId, harness.ProgramOutput.LastFrame!.Frame.SourceId);
        Assert.Equal(new Revision(2), afterCut.ExecutionRevision);

        Assert.Equal(
            new[] { harness.Provider.SourceA.SourceId, harness.Provider.SourceB.SourceId },
            harness.ProgramOutput.Frames.Select(frame => frame.Frame.SourceId));
    }

    [Fact]
    public void Frame_sequence_remains_continuous_across_cut()
    {
        var harness = CreateHarness(VideoFormat.Hd1080p50Rgba8, programSource: SourceAId);

        ProcessFrames(harness, 5);
        CommitCut(harness, SourceBId);
        ProcessFrames(harness, 5);

        Assert.Equal(
            Enumerable.Range(0, 10).Select(value => (ulong)value),
            harness.ProgramOutput.Frames.Select(frame => frame.Frame.Timing.SequenceNumber));
        Assert.All(harness.ProgramOutput.Frames.Take(5), frame => Assert.Equal(harness.Provider.SourceA.SourceId, frame.Frame.SourceId));
        Assert.All(harness.ProgramOutput.Frames.Skip(5), frame => Assert.Equal(harness.Provider.SourceB.SourceId, frame.Frame.SourceId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Required_V1_development_formats_execute_with_exact_virtual_timing(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var harness = CreateHarness(format, programSource: SourceAId);

        var step = harness.MediaRuntime.ProcessNextFrameBoundary();

        Assert.True(step.Succeeded);
        var frame = harness.ProgramOutput.LastFrame!.Frame;
        Assert.Equal(format, frame.Surface.Format);
        Assert.Equal((ulong)0, frame.Timing.SequenceNumber);
        Assert.Equal(
            new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator),
            frame.Timing.Timebase);
    }

    [Fact]
    public void Repeated_execution_is_deterministic()
    {
        var first = RunDeterministicScenario();
        var second = RunDeterministicScenario();

        Assert.Equal(first.InitialPreparedExecutionId, second.InitialPreparedExecutionId);
        Assert.Equal(first.CutPreparedExecutionId, second.CutPreparedExecutionId);
        Assert.Equal(first.InitialExecutionInstanceId, second.InitialExecutionInstanceId);
        Assert.Equal(first.CutExecutionInstanceId, second.CutExecutionInstanceId);
        Assert.Equal(first.ProgramFrames, second.ProgramFrames);
    }

    [Fact]
    public void Failed_prepare_leaves_current_program_execution_intact()
    {
        var harness = CreateHarness(VideoFormat.Hd1080p50Rgba8, programSource: SourceAId);
        ProcessFrames(harness, 1);

        var activeBefore = harness.Runtime.ActiveExecution!;
        var stalePrepared = new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            new PreparedExecutionId(Identity.Parse("60000000-0000-0000-0000-000000000001")),
            activeBefore.PreparedExecution.AuthoritySnapshot,
            activeBefore.PreparedExecution.PlanGeneration,
            activeBefore.PreparedExecution.Bindings);

        var prepare = harness.Runtime.Prepare(stalePrepared);

        Assert.Equal(RuntimePrepareStatus.Rejected, prepare.Status);
        Assert.Equal("runtime.prepare.stale_authority_revision", prepare.Failure!.Value.Code);
        Assert.Equal(activeBefore.ExecutionInstanceId, harness.Runtime.ActiveExecution!.ExecutionInstanceId);
        Assert.Equal(activeBefore.ExecutionRevision, harness.Runtime.ActiveExecution!.ExecutionRevision);

        var next = harness.MediaRuntime.ProcessNextFrameBoundary();
        Assert.True(next.Succeeded);
        Assert.Equal((ulong)1, next.SequenceNumber);
        Assert.Equal(harness.Provider.SourceA.SourceId, harness.ProgramOutput.LastFrame!.Frame.SourceId);
    }

    [Fact]
    public void Virtual_provider_advertises_deterministic_route_capability_and_resources()
    {
        var provider = new VirtualMediaReferenceProvider(
            new MediaSourceId(SourceAId.Value),
            new MediaSourceId(SourceBId.Value),
            VideoFormat.Hd1080p50Rgba8);

        var descriptor = provider.Descriptor;

        Assert.Equal(ProviderAvailabilityState.Available, descriptor.Availability.State);
        var capability = Assert.Single(descriptor.Capabilities);
        Assert.Equal(VirtualMediaCapabilityKinds.MediaRoute, capability.Kind);
        Assert.Contains(VideoFormat.Hd1080p50Rgba8, capability.VideoFormats);
        Assert.Contains(VideoFormat.Hd1080p59_94Rgba8, capability.VideoFormats);
        Assert.Equal(2, descriptor.Resources.Count);
        Assert.All(descriptor.Resources, resource =>
        {
            Assert.True(resource.Reservable);
            Assert.Equal(VirtualMediaCapabilityKinds.MediaRoute, resource.Kind);
            Assert.Equal((uint)1, resource.CapacityUnits);
        });

        var second = new VirtualMediaReferenceProvider(
            new MediaSourceId(SourceAId.Value),
            new MediaSourceId(SourceBId.Value),
            VideoFormat.Hd1080p59_94Rgba8);

        Assert.Equal(descriptor.ProviderId, second.Descriptor.ProviderId);
        Assert.Equal(
            descriptor.Resources.Select(resource => resource.ResourceId),
            second.Descriptor.Resources.Select(resource => resource.ResourceId));
    }

    private static readonly ProductionId ProductionId =
        new(Identity.Parse("10000000-0000-0000-0000-000000000001"));

    private static readonly ProductionSourceId SourceAId =
        new(Identity.Parse("20000000-0000-0000-0000-000000000001"));

    private static readonly ProductionSourceId SourceBId =
        new(Identity.Parse("20000000-0000-0000-0000-000000000002"));

    private static Harness CreateHarness(VideoFormat format, ProductionSourceId programSource)
    {
        var previewSource = programSource == SourceAId ? SourceBId : SourceAId;
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            ProductionId,
            "Virtual Architecture Proof",
            new[]
            {
                new ProductionSourceSpecification(SourceAId, "Source A"),
                new ProductionSourceSpecification(SourceBId, "Source B")
            },
            new ProductionRoutingState(previewSource, programSource));

        var initialization = ControlDomainEngine.Initialize(specification);
        Assert.True(initialization.Succeeded);
        var authoritative = initialization.State!.Authoritative;

        var provider = new VirtualMediaReferenceProvider(
            new MediaSourceId(SourceAId.Value),
            new MediaSourceId(SourceBId.Value),
            format);
        var registry = new SingleProviderCapabilityRegistry(provider.Descriptor);
        var planning = CapabilityPlanningEngine.Plan(specification, authoritative, registry);
        Assert.True(planning.Succeeded);

        var previewSinkId = planning.Graph!.Nodes
            .Single(node => node.Kind == LogicalProductionNodeKind.PreviewSink)
            .MediaSinkId!.Value;
        var programSinkId = planning.Graph.Nodes
            .Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink)
            .MediaSinkId!.Value;

        var previewOutput = provider.CreateOutput(previewSinkId);
        var programOutput = provider.CreateOutput(programSinkId);
        var endpoints = new RuntimeMediaEndpointRegistry(
            new IRuntimeMediaSource[]
            {
                new VirtualSourceAdapter(provider.SourceA),
                new VirtualSourceAdapter(provider.SourceB)
            },
            new IRuntimeMediaOutput[]
            {
                new VirtualOutputAdapter(previewOutput),
                new VirtualOutputAdapter(programOutput)
            });

        var clock = new DeterministicClock();
        var runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager(), clock);
        var prepared = planning.PreparedExecution!;
        var prepare = runtime.Prepare(prepared);
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);

        var commit = runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            prepared.PreparedExecutionId,
            prepare.ReservationId!.Value,
            Revision.Initial));
        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);

        return new Harness(
            specification,
            authoritative,
            provider,
            registry,
            runtime,
            new CommittedMediaRuntime(runtime, endpoints, clock),
            previewOutput,
            programOutput,
            prepared.PreparedExecutionId,
            commit.ExecutionInstanceId!.Value);
    }

    private static void CommitCut(Harness harness, ProductionSourceId targetSource)
    {
        var command = new CutProgramCommand(
            new ControlCommandMetadata(
                ControlContractVersion.Current,
                new CommandId(Identity.Parse("30000000-0000-0000-0000-000000000001")),
                harness.Specification.ProductionId,
                harness.Authoritative.Revision),
            targetSource);

        var commandResult = ControlDomainEngine.Apply(
            harness.Specification,
            harness.Authoritative,
            command);
        Assert.True(commandResult.Committed);
        harness.Authoritative = commandResult.AuthoritativeState;

        var planning = CapabilityPlanningEngine.Plan(
            harness.Specification,
            harness.Authoritative,
            harness.Registry);
        Assert.True(planning.Succeeded);
        harness.CutPreparedExecutionId = planning.PreparedExecution!.PreparedExecutionId;

        var prepare = harness.Runtime.Prepare(planning.PreparedExecution);
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);

        var commit = harness.Runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            planning.PreparedExecution.PreparedExecutionId,
            prepare.ReservationId!.Value,
            harness.Runtime.State.ExecutionRevision));
        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);
        harness.CutExecutionInstanceId = commit.ExecutionInstanceId!.Value;
    }

    private static void ProcessFrames(Harness harness, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var result = harness.MediaRuntime.ProcessNextFrameBoundary();
            Assert.True(result.Succeeded, result.Failure?.ToString());
            Assert.Equal(2, result.Emissions.Count);
        }
    }

    private static DeterministicScenarioResult RunDeterministicScenario()
    {
        var harness = CreateHarness(VideoFormat.Hd1080p59_94Rgba8, SourceAId);
        ProcessFrames(harness, 2);
        CommitCut(harness, SourceBId);
        ProcessFrames(harness, 2);

        var programFrames = harness.ProgramOutput.Frames
            .Select(frame => string.Join(
                "|",
                frame.Frame.SourceId,
                frame.Frame.Timing.SequenceNumber,
                frame.Frame.Timing.PresentationTimestamp,
                frame.Frame.Timing.Timebase,
                frame.Frame.Surface.SurfaceId,
                frame.Frame.Surface.Handle!.Value))
            .ToArray();

        return new DeterministicScenarioResult(
            harness.InitialPreparedExecutionId,
            harness.CutPreparedExecutionId!.Value,
            harness.InitialExecutionInstanceId,
            harness.CutExecutionInstanceId!.Value,
            programFrames);
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

    private sealed class VirtualSourceAdapter : IRuntimeMediaSource
    {
        private readonly VirtualSyntheticVideoSource _source;

        public VirtualSourceAdapter(VirtualSyntheticVideoSource source)
        {
            _source = source;
        }

        public MediaSourceId SourceId => _source.SourceId;
        public VideoFormat Format => _source.Format;
        public FrameDescriptor ReadFrame(ulong sequenceNumber) => _source.GenerateFrame(sequenceNumber);
    }

    private sealed class VirtualOutputAdapter : IRuntimeMediaOutput
    {
        private readonly VirtualVideoOutput _output;

        public VirtualOutputAdapter(VirtualVideoOutput output)
        {
            _output = output;
        }

        public MediaSinkId SinkId => _output.SinkId;
        public VideoFormat Format => _output.Format;
        public void WriteFrame(FrameDescriptor frame) => _output.WriteFrame(frame);
    }

    private sealed class DeterministicClock : IRuntimeClock
    {
        private long _milliseconds;

        public UtcTimestamp GetUtcNow() => UtcTimestamp.FromUnixTimeMilliseconds(_milliseconds++);
    }

    private sealed class Harness
    {
        public Harness(
            ProductionSpecification specification,
            AuthoritativeProductionState authoritative,
            VirtualMediaReferenceProvider provider,
            IProviderCapabilityRegistry registry,
            TransactionalRuntime runtime,
            CommittedMediaRuntime mediaRuntime,
            VirtualVideoOutput previewOutput,
            VirtualVideoOutput programOutput,
            PreparedExecutionId initialPreparedExecutionId,
            ExecutionInstanceId initialExecutionInstanceId)
        {
            Specification = specification;
            Authoritative = authoritative;
            Provider = provider;
            Registry = registry;
            Runtime = runtime;
            MediaRuntime = mediaRuntime;
            PreviewOutput = previewOutput;
            ProgramOutput = programOutput;
            InitialPreparedExecutionId = initialPreparedExecutionId;
            InitialExecutionInstanceId = initialExecutionInstanceId;
        }

        public ProductionSpecification Specification { get; }
        public AuthoritativeProductionState Authoritative { get; set; }
        public VirtualMediaReferenceProvider Provider { get; }
        public IProviderCapabilityRegistry Registry { get; }
        public TransactionalRuntime Runtime { get; }
        public CommittedMediaRuntime MediaRuntime { get; }
        public VirtualVideoOutput PreviewOutput { get; }
        public VirtualVideoOutput ProgramOutput { get; }
        public PreparedExecutionId InitialPreparedExecutionId { get; }
        public ExecutionInstanceId InitialExecutionInstanceId { get; }
        public PreparedExecutionId? CutPreparedExecutionId { get; set; }
        public ExecutionInstanceId? CutExecutionInstanceId { get; set; }
    }

    private sealed record DeterministicScenarioResult(
        PreparedExecutionId InitialPreparedExecutionId,
        PreparedExecutionId CutPreparedExecutionId,
        ExecutionInstanceId InitialExecutionInstanceId,
        ExecutionInstanceId CutExecutionInstanceId,
        IReadOnlyList<string> ProgramFrames);
}
