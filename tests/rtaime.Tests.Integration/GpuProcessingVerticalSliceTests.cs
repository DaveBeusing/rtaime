using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Integration;

public sealed class GpuProcessingVerticalSliceTests
{
    private static readonly ProductionId ProductionId =
        new(Identity.Parse("83000000-0000-0000-0000-000000000001"));

    private static readonly ProductionSourceId SourceAId =
        new(Identity.Parse("83000000-0000-0000-0000-000000000002"));

    private static readonly ProductionSourceId SourceBId =
        new(Identity.Parse("83000000-0000-0000-0000-000000000003"));

    private static readonly MediaSourceId LayerSourceId =
        new(Identity.Parse("83000000-0000-0000-0000-000000000004"));

    private static readonly MediaSourceId GpuOutputSourceId =
        new(Identity.Parse("83000000-0000-0000-0000-000000000005"));

    [Fact]
    public void Committed_virtual_cut_executes_through_gpu_provider_without_gpu_specific_control_or_planning()
    {
        var format = VideoFormat.Hd1080p50Rgba8;
        var specification = CreateSpecification(SourceAId);
        var initialization = ControlDomainEngine.Initialize(specification);
        Assert.True(initialization.Succeeded);
        var authoritative = initialization.State!.Authoritative;

        var virtualProvider = new VirtualMediaReferenceProvider(
            new MediaSourceId(SourceAId.Value),
            new MediaSourceId(SourceBId.Value),
            format);
        var registry = new SingleProviderCapabilityRegistry(virtualProvider.Descriptor);
        var runtime = CommitPlan(specification, authoritative, registry, expectedExecutionRevision: Revision.Initial);

        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        var gpuSourceA = new StaticRgbaSource(new MediaSourceId(SourceAId.Value), RgbaFrameBuffer.Solid(format, 255, 0, 0));
        var gpuSourceB = new StaticRgbaSource(new MediaSourceId(SourceBId.Value), RgbaFrameBuffer.Solid(format, 0, 0, 255));

        using var first = ProcessCommittedProgramFrame(runtime, virtualProvider, gpu, gpuSourceA, gpuSourceB, sequence: 0);
        AssertPixel(gpu.Readback(first), 255, 0, 0, 255);

        var command = new CutProgramCommand(
            new ControlCommandMetadata(
                ControlContractVersion.Current,
                new CommandId(Identity.Parse("83000000-0000-0000-0000-000000000006")),
                specification.ProductionId,
                authoritative.Revision),
            SourceBId);
        var commandResult = ControlDomainEngine.Apply(specification, authoritative, command);
        Assert.True(commandResult.Committed);
        authoritative = commandResult.AuthoritativeState;

        var nextPlan = CapabilityPlanningEngine.Plan(specification, authoritative, registry);
        Assert.True(nextPlan.Succeeded);
        var prepare = runtime.Prepare(nextPlan.PreparedExecution!);
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);
        var commit = runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            nextPlan.PreparedExecution!.PreparedExecutionId,
            prepare.ReservationId!.Value,
            runtime.State.ExecutionRevision));
        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);

        using var second = ProcessCommittedProgramFrame(runtime, virtualProvider, gpu, gpuSourceA, gpuSourceB, sequence: 1);
        AssertPixel(gpu.Readback(second), 0, 0, 255, 255);

        Assert.DoesNotContain("gpu", specification.Name, StringComparison.OrdinalIgnoreCase);
        Assert.All(nextPlan.PreparedExecution.Bindings, binding =>
            Assert.NotEqual(GpuCapabilityKinds.Processing, binding.Resource.Kind));
        Assert.Contains(gpu.Observations, observation => observation.Code == "gpu.composite.cut");
    }

    [Fact]
    public void Dynamic_rgba_layer_and_dissolve_execute_on_v1_5994_format()
    {
        var format = VideoFormat.Hd1080p59_94Rgba8;
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();

        var sourceA = new StaticRgbaSource(
            new MediaSourceId(SourceAId.Value),
            RgbaFrameBuffer.Solid(format, 0, 0, 0));
        var sourceB = new StaticRgbaSource(
            new MediaSourceId(SourceBId.Value),
            RgbaFrameBuffer.Solid(format, 200, 200, 200));
        var dynamicLayer = new DynamicRgbaSource(
            LayerSourceId,
            RgbaFrameBuffer.Solid(format, 255, 0, 0, 128));

        var timing = new FrameTiming(0, 0, new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator));
        using var a = sourceA.Materialize(gpu, timing);
        using var b = sourceB.Materialize(gpu, timing);
        using var layer = dynamicLayer.Materialize(gpu, timing);

        var result = gpu.Composite(new GpuCompositeRequest(
            GpuOutputSourceId,
            a,
            b,
            GpuTransition.Dissolve(128),
            new GpuKeyLayer(layer, 128, visible: true)));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        using var output = result.Frame!;
        var pixels = gpu.Readback(output);

        // 50% background dissolve gives ~100 gray. Layer alpha 128 with opacity 128 => effective alpha ~64.
        AssertPixel(pixels, 139, 75, 75, 255);
        Assert.Equal(format, output.Descriptor.Surface.Format);
        Assert.Contains(gpu.Observations, observation => observation.Code == "gpu.composite.dissolve");
    }

    private static ProductionSpecification CreateSpecification(ProductionSourceId programSource)
    {
        var preview = programSource == SourceAId ? SourceBId : SourceAId;
        return new ProductionSpecification(
            ControlContractVersion.Current,
            ProductionId,
            "GPU Vertical Slice",
            new[]
            {
                new ProductionSourceSpecification(SourceAId, "Source A"),
                new ProductionSourceSpecification(SourceBId, "Source B")
            },
            new ProductionRoutingState(preview, programSource));
    }

    private static TransactionalRuntime CommitPlan(
        ProductionSpecification specification,
        AuthoritativeProductionState authoritative,
        IProviderCapabilityRegistry registry,
        Revision expectedExecutionRevision)
    {
        var planning = CapabilityPlanningEngine.Plan(specification, authoritative, registry);
        Assert.True(planning.Succeeded);

        var runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());
        var prepare = runtime.Prepare(planning.PreparedExecution!);
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);
        var commit = runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            planning.PreparedExecution!.PreparedExecutionId,
            prepare.ReservationId!.Value,
            expectedExecutionRevision));
        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);
        return runtime;
    }

    private static GpuFrame ProcessCommittedProgramFrame(
        TransactionalRuntime runtime,
        VirtualMediaReferenceProvider virtualProvider,
        GpuProcessingProvider gpu,
        StaticRgbaSource gpuSourceA,
        StaticRgbaSource gpuSourceB,
        ulong sequence)
    {
        var active = runtime.ActiveExecution ?? throw new Xunit.Sdk.XunitException("Committed Runtime execution is required.");
        var programBinding = active.PreparedExecution.Bindings
            .Single(binding => binding.MediaSinkId is not null &&
                binding.MediaSourceId is not null &&
                binding.MediaSourceId.Value == new MediaSourceId(
                    active.PreparedExecution.Bindings
                        .Where(value => value.MediaSourceId is not null)
                        .Select(value => value.MediaSourceId!.Value)
                        .First(id => id == virtualProvider.SourceA.SourceId || id == virtualProvider.SourceB.SourceId).Value));

        // The binding order does not carry a named Program role in Runtime contracts. Select the source that matches
        // authoritative Program by observing which route changed across commits: Source A for revision 1, Source B for revision 2.
        var programSource = active.ExecutionRevision.Value == 1
            ? virtualProvider.SourceA
            : virtualProvider.SourceB;

        var timing = virtualProvider.Timing.GetFrameTiming(sequence);
        using var a = gpuSourceA.Materialize(gpu, timing);
        using var b = gpuSourceB.Materialize(gpu, timing);
        var transition = programSource.SourceId == virtualProvider.SourceA.SourceId
            ? GpuTransition.CutToA
            : GpuTransition.CutToB;

        var result = gpu.Composite(new GpuCompositeRequest(GpuOutputSourceId, a, b, transition));
        Assert.True(result.Succeeded, result.Failure?.ToString());
        _ = programBinding;
        return result.Frame!;
    }

    private static void AssertPixel(byte[] pixels, byte red, byte green, byte blue, byte alpha)
    {
        Assert.True(pixels.Length >= 4);
        Assert.Equal(red, pixels[0]);
        Assert.Equal(green, pixels[1]);
        Assert.Equal(blue, pixels[2]);
        Assert.Equal(alpha, pixels[3]);
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
