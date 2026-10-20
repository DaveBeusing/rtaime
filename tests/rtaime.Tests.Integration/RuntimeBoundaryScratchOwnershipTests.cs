// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Security.Cryptography;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RuntimeBoundaryScratchOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Program_and_Aux_reuse_bounded_frame_maps_without_retaining_a_consumer_lease(bool use5994)
    {
        await using var runtime = await CreateRuntimeAsync(use5994);
        var frames = FrameScratch(runtime);
        var gpu = GpuScratch(runtime);
        Assert.Empty(frames);
        Assert.Empty(gpu);

        byte[]? retainedAudio = null;
        byte[]? retainedAudioHash = null;
        for (var index = 0; index < 12; index++)
        {
            if (index == 4)
            {
                // The current admitted source registry is finite. Introduce a
                // new source between boundaries to exercise map rebind/capacity.
                var external = MediaSourceId.New();
                runtime.RegisterExternalSource(external);
            }

            using (var boundary = runtime.ProcessNextBoundary())
            {
                Assert.Equal((ulong)index, boundary.SequenceNumber);
                Assert.NotEmpty(boundary.ProgramAudioPayload);
                Assert.Empty(frames);
                Assert.Empty(gpu);
                Assert.Equal(0, runtime.Snapshot.ActiveGpuSurfaces);
                Assert.Equal(1, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
                if (index == 0)
                {
                    retainedAudio = boundary.ProgramAudioPayload;
                    retainedAudioHash = SHA256.HashData(boundary.ProgramAudioPayload);
                }
                else
                {
                    Assert.NotSame(retainedAudio, boundary.ProgramAudioPayload);
                    Assert.Equal(retainedAudioHash, SHA256.HashData(retainedAudio!));
                }
            }

            Assert.Equal(0, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
            Assert.Same(frames, FrameScratch(runtime));
            Assert.Same(gpu, GpuScratch(runtime));
        }

        Assert.True(frames.EnsureCapacity(0) >= AudioProductionLimits.MaximumSources);
        Assert.True(gpu.EnsureCapacity(0) >= AudioProductionLimits.MaximumSources);
        Assert.Equal(12UL, runtime.Snapshot.NextSequenceNumber);
        Assert.Equal(0, runtime.Snapshot.ActiveGpuSurfaces);
    }

    [Fact]
    public async Task Invalid_layer_after_source_upload_releases_scratch_and_allows_a_clean_retry()
    {
        await using var runtime = await CreateRuntimeAsync(use5994: false);
        var frames = FrameScratch(runtime);
        var gpu = GpuScratch(runtime);
        var visualModeField = typeof(RuntimeProgramExecutionService)
            .GetField("_visualLayerMode", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(visualModeField);

        // A fault injected *after* live input uploads must leave no native
        // source surface trapped inside reusable boundary scratch.
        visualModeField.SetValue(runtime, (RuntimeVisualLayerMode)int.MaxValue);
        Assert.Throws<InvalidOperationException>(() => runtime.ProcessNextBoundary());
        Assert.Empty(frames);
        Assert.Empty(gpu);
        Assert.Equal(0, runtime.Snapshot.ActiveGpuSurfaces);
        Assert.Equal(0, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal(0UL, runtime.Snapshot.NextSequenceNumber);

        visualModeField.SetValue(runtime, RuntimeVisualLayerMode.Static);
        using var recovered = runtime.ProcessNextBoundary();
        Assert.Equal(0UL, recovered.SequenceNumber);
        Assert.Equal(0, recovered.ActiveGpuSurfacesAfterBoundary);
        Assert.Empty(frames);
        Assert.Empty(gpu);
    }

    private static Dictionary<MediaSourceId, FrameDescriptor> FrameScratch(RuntimeProgramExecutionService runtime) =>
        Assert.IsType<Dictionary<MediaSourceId, FrameDescriptor>>(
            typeof(RuntimeProgramExecutionService).GetField("_boundaryFrameScratch",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));

    private static Dictionary<MediaSourceId, GpuFrame> GpuScratch(RuntimeProgramExecutionService runtime) =>
        Assert.IsType<Dictionary<MediaSourceId, GpuFrame>>(
            typeof(RuntimeProgramExecutionService).GetField("_boundaryGpuScratch",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime));

    private static async ValueTask<RuntimeProgramExecutionService> CreateRuntimeAsync(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        var sourceA = new ProductionSourceId(Identity.New());
        var sourceB = new ProductionSourceId(Identity.New());
        var specification = new ProductionSpecification(
            ControlContractVersion.Current,
            new ProductionId(Identity.New()),
            "Program boundary scratch",
            [
                new ProductionSourceSpecification(sourceA, "A"),
                new ProductionSourceSpecification(sourceB, "B")
            ],
            new ProductionRoutingState(sourceA, sourceA),
            initialOutputRoles:
            [
                ProductionOutputRoleState.Program(sourceA),
                ProductionOutputRoleState.Aux(sourceB)
            ]);
        var runtime = new RuntimeProgramExecutionService(
            new MediaSourceId(sourceA.Value), new MediaSourceId(sourceB.Value), format,
            new NoopRecordingWriter(), new ManagedReferenceGpuBackend());
        try
        {
            var state = ControlDomainEngine.Initialize(specification).State!.Authoritative;
            var plan = CapabilityPlanningEngine.Plan(specification, state, new Registry(runtime.ProviderDescriptors));
            Assert.True(plan.Succeeded);
            var sink = plan.Graph!.Nodes.Single(node => node.Kind == LogicalProductionNodeKind.ProgramSink)
                .MediaSinkId!.Value;
            Assert.True(runtime.ApplyExecution(plan.PreparedExecution!, sink).Committed);
            runtime.SetVisualLayerMode(RuntimeVisualLayerMode.Static);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    private sealed class Registry(IReadOnlyList<ProviderDescriptor> providers) : IProviderCapabilityRegistry
    {
        public IReadOnlyList<ProviderDescriptor> GetProviders() => providers;
    }

    private sealed class NoopRecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
