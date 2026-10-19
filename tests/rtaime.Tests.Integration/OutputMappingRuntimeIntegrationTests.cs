// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class OutputMappingRuntimeIntegrationTests
{
    [Fact]
    public async Task Two_sources_are_mapped_into_the_actual_program_sink_on_one_boundary()
    {
        await using var fixture = CreateFixture();
        var runtime = fixture.Runtime;
        var output = ProgramOutput(runtime);
        var revision = runtime.Snapshot.Runtime.ExecutionRevision.Value;

        var sourcePixels = new byte[checked((int)(runtime.Format.Width * runtime.Format.Height * 4))];
        for (var y = 0; y < (int)runtime.Format.Height; y++)
        for (var x = 0; x < (int)runtime.Format.Width; x++)
        {
            var at = (y * (int)runtime.Format.Width + x) * 4;
            var left = x < (int)runtime.Format.Width / 2;
            sourcePixels[at] = left ? (byte)90 : (byte)5;
            sourcePixels[at + 1] = 20;
            sourcePixels[at + 2] = left ? (byte)5 : (byte)90;
            sourcePixels[at + 3] = 255;
        }
        runtime.SetExternalInputContent(fixture.SourceA, sourcePixels);

        var configuration = Configuration(fixture, revision: 7, twoSurfaces: true);
        var prepared = runtime.PrepareOutputMapping(new OutputCanvasRuntimeRequest(
            configuration, revision, "integration-runtime"));
        Assert.Equal("PREPARED", prepared.ActivationState);
        var pending = runtime.CommitOutputMapping(
            configuration.ConfigurationId, configuration.EditRevision, revision, true);
        Assert.Equal("PENDING_BOUNDARY", pending.ActivationState);
        Assert.Null(pending.ActiveRenderRevision);
        Assert.Equal(7UL, pending.PendingRenderRevision);

        using var boundary = runtime.ProcessNextBoundary();
        Assert.Equal((ulong)0, boundary.SequenceNumber);
        Assert.Equal(fixture.SourceA, boundary.CommittedProgramSourceId);
        var pixels = boundary.ProgramPixels.Span;
        AssertPixel(pixels, runtime.Format, 240, 540, 90, 20, 5);
        AssertPixel(pixels, runtime.Format, 1500, 540, 196, 72, 32);
        Assert.Equal(boundary.ProgramFrame, Assert.Single(runtime.ProgramFrames).Frame);
        Assert.Equal(0, boundary.ActiveGpuSurfacesAfterBoundary);

        var applied = runtime.OutputMappingSnapshot;
        Assert.Equal("ACTIVE", applied.ActivationState);
        Assert.Equal(7UL, applied.ActiveRenderRevision);
        Assert.Equal((ulong)0, applied.LastRenderedSequence);
        Assert.Equal(output.TargetId.ToString(), applied.TargetId);
        Assert.Equal((int)runtime.Format.Width, applied.TargetWidth);
        Assert.Equal((int)runtime.Format.Height, applied.TargetHeight);
        Assert.Equal(revision, applied.RuntimeExecutionRevision);
    }

    [Fact]
    public async Task Identity_is_byte_exact_and_stale_update_does_not_disarm_active_output()
    {
        await using var fixture = CreateFixture();
        var runtime = fixture.Runtime;
        var revision = runtime.Snapshot.Runtime.ExecutionRevision.Value;
        var identity = Configuration(fixture, revision: 8, twoSurfaces: false);
        Assert.Equal("PREPARED", runtime.PrepareOutputMapping(
            new(identity, revision, "integration-runtime")).ActivationState);
        Assert.Equal("PENDING_BOUNDARY", runtime.CommitOutputMapping(
            identity.ConfigurationId, 8, revision, true).ActivationState);

        using (var first = runtime.ProcessNextBoundary())
        {
            AssertPixel(first.ProgramPixels.Span, runtime.Format, 240, 540, 32, 72, 196);
            Assert.Equal(0, first.ActiveGpuSurfacesAfterBoundary);
        }
        Assert.Equal(8UL, runtime.OutputMappingSnapshot.ActiveRenderRevision);
        var stale = runtime.PrepareOutputMapping(
            new(Configuration(fixture, revision: 9, twoSurfaces: true), revision + 1,
                "integration-runtime"));
        Assert.Equal("ACTIVE_REJECTED_UPDATE", stale.ActivationState);
        Assert.Equal("mapping.runtime_revision_changed", stale.FailureCode);
        Assert.Equal(8UL, runtime.OutputMappingSnapshot.ActiveRenderRevision);
        using var second = runtime.ProcessNextBoundary();
        AssertPixel(second.ProgramPixels.Span, runtime.Format, 240, 540, 32, 72, 196);
    }

    [Fact]
    public async Task Explicit_disable_is_committed_only_at_next_boundary()
    {
        await using var fixture = CreateFixture();
        var runtime = fixture.Runtime;
        var revision = runtime.Snapshot.Runtime.ExecutionRevision.Value;
        var identity = Configuration(fixture, revision: 11, twoSurfaces: false);
        Assert.Equal("PREPARED", runtime.PrepareOutputMapping(
            new(identity, revision, "integration-runtime")).ActivationState);
        Assert.Equal("PENDING_BOUNDARY", runtime.CommitOutputMapping(
            identity.ConfigurationId, 11, revision, true).ActivationState);
        using (runtime.ProcessNextBoundary()) { }

        Assert.Equal("PREPARED_DISABLE", runtime.PrepareOutputMapping(
            new(identity, revision, "integration-runtime", Enabled: false)).ActivationState);
        Assert.Equal("PENDING_BOUNDARY", runtime.CommitOutputMapping(
            identity.ConfigurationId, 11, revision, false).ActivationState);
        Assert.Equal(11UL, runtime.OutputMappingSnapshot.ActiveRenderRevision);
        using (runtime.ProcessNextBoundary()) { }
        Assert.Null(runtime.OutputMappingSnapshot.ActiveRenderRevision);
        Assert.Equal("UNMAPPED", runtime.OutputMappingSnapshot.ActivationState);
    }

    [Fact]
    public async Task Unknown_source_and_degenerate_quad_fail_closed_without_changing_program()
    {
        await using var fixture = CreateFixture();
        var runtime = fixture.Runtime;
        var revision = runtime.Snapshot.Runtime.ExecutionRevision.Value;

        var unknownSource = Configuration(fixture, revision: 12, twoSurfaces: true);
        unknownSource.Surfaces[1] = unknownSource.Surfaces[1] with
        {
            Input = new OutputCanvasInput(SourceId: Guid.NewGuid().ToString())
        };
        var rejectedSource = runtime.PrepareOutputMapping(
            new(unknownSource, revision, "integration-runtime"));
        Assert.Equal("REJECTED", rejectedSource.ActivationState);
        Assert.Equal("mapping.prepare_rejected.ArgumentException", rejectedSource.FailureCode);

        var degenerate = Configuration(fixture, revision: 13, twoSurfaces: true);
        degenerate.Surfaces[0] = degenerate.Surfaces[0] with
        {
            Destination = new OutputCanvasQuad(new(0, 0), new(0, 0),
                new(0, 0), new(0, 0))
        };
        var rejectedQuad = runtime.PrepareOutputMapping(
            new(degenerate, revision, "integration-runtime"));
        Assert.Equal("REJECTED", rejectedQuad.ActivationState);
        Assert.Equal("mapping.prepare_rejected.ArgumentException", rejectedQuad.FailureCode);

        using var boundary = runtime.ProcessNextBoundary();
        AssertPixel(boundary.ProgramPixels.Span, runtime.Format, 240, 540, 32, 72, 196);
        AssertPixel(boundary.ProgramPixels.Span, runtime.Format, 1500, 540, 32, 72, 196);
        Assert.Null(runtime.OutputMappingSnapshot.ActiveRenderRevision);
        Assert.Equal(0, boundary.ActiveGpuSurfacesAfterBoundary);
    }

    [Fact]
    public async Task Backend_without_mapping_capability_rejects_activation_and_preserves_program_sink()
    {
        await using var fixture = CreateFixture(new BackendWithoutMapping());
        var runtime = fixture.Runtime;
        var committedTarget = ProgramOutput(runtime).TargetId;
        var revision = runtime.Snapshot.Runtime.ExecutionRevision.Value;
        var configuration = Configuration(fixture, revision: 14, twoSurfaces: true);

        var rejected = runtime.PrepareOutputMapping(
            new(configuration, revision, "integration-runtime"));
        Assert.Equal("REJECTED", rejected.ActivationState);
        Assert.Equal("mapping.prepare_rejected.NotSupportedException", rejected.FailureCode);

        using var boundary = runtime.ProcessNextBoundary();
        AssertPixel(boundary.ProgramPixels.Span, runtime.Format, 240, 540, 32, 72, 196);
        Assert.Equal(committedTarget, ProgramOutput(runtime).TargetId);
        Assert.Null(runtime.OutputMappingSnapshot.ActiveRenderRevision);
        Assert.Equal(0, boundary.ActiveGpuSurfacesAfterBoundary);
    }

    [Fact]
    public async Task Mapping_provider_failure_falls_back_to_committed_program_and_releases_surfaces()
    {
        await using var fixture = CreateFixture(new FailingMappingBackend());
        var runtime = fixture.Runtime;
        var revision = runtime.Snapshot.Runtime.ExecutionRevision.Value;
        var configuration = Configuration(fixture, revision: 15, twoSurfaces: true);
        Assert.Equal("PREPARED", runtime.PrepareOutputMapping(
            new(configuration, revision, "integration-runtime")).ActivationState);
        Assert.Equal("PENDING_BOUNDARY", runtime.CommitOutputMapping(
            configuration.ConfigurationId, 15, revision, true).ActivationState);

        using var boundary = runtime.ProcessNextBoundary();
        AssertPixel(boundary.ProgramPixels.Span, runtime.Format, 240, 540, 32, 72, 196);
        AssertPixel(boundary.ProgramPixels.Span, runtime.Format, 1500, 540, 32, 72, 196);
        Assert.Equal(0, boundary.ActiveGpuSurfacesAfterBoundary);
        var state = runtime.OutputMappingSnapshot;
        Assert.Equal("STALE_EXECUTION", state.ActivationState);
        Assert.Equal("gpu.output_mapping.render_failed", state.FailureCode);
        Assert.Null(state.ActiveRenderRevision);
    }

    private static RuntimeOutputRoleSnapshot ProgramOutput(V1RuntimeHostService runtime) =>
        Assert.Single(runtime.Snapshot.OutputRoles!, o => o.RoleId == "program");

    private static OutputCanvasConfiguration Configuration(Fixture fixture, ulong revision, bool twoSurfaces)
    {
        var role = ProgramOutput(fixture.Runtime);
        var canvasId = Guid.NewGuid();
        var width = (int)fixture.Runtime.Format.Width;
        var height = (int)fixture.Runtime.Format.Height;
        var surfaces = twoSurfaces
            ? new[]
            {
                new OutputCanvasSurface(Guid.NewGuid(), canvasId, new(BusRoleId: "program"),
                    0, new(0,0,0.5,1), OutputCanvasQuad.Rectangle(0,0,width/2,height)),
                new OutputCanvasSurface(Guid.NewGuid(), canvasId,
                    new(SourceId: fixture.SourceB.ToString()),
                    1, new(0,0,1,1), OutputCanvasQuad.Rectangle(width/2,0,width/2,height))
            }
            : new[]
            {
                new OutputCanvasSurface(Guid.NewGuid(), canvasId,
                    new(BusRoleId: "program"), 0, new(0,0,1,1),
                    OutputCanvasQuad.Rectangle(0,0,width,height))
            };
        return new OutputCanvasConfiguration(OutputCanvasContract.Version, Guid.NewGuid(), revision,
            [new(canvasId,"Mapped Program",width,height)],
            surfaces,
            [new(Guid.NewGuid(),canvasId,role.ProviderId.ToString(),
                role.TargetId.ToString(),"production")]);
    }

    private static void AssertPixel(ReadOnlySpan<byte> pixels, VideoFormat format,
        int x, int y, byte red, byte green, byte blue)
    {
        var at = checked((y * (int)format.Width + x) * 4);
        Assert.Equal(red, pixels[at]);
        Assert.Equal(green, pixels[at + 1]);
        Assert.Equal(blue, pixels[at + 2]);
        Assert.Equal((byte)255, pixels[at + 3]);
    }

    private static Fixture CreateFixture(IGpuProcessingBackend? backend = null)
    {
        var productionId = new ProductionId(Identity.New());
        var sourceA = new ProductionSourceId(Identity.New());
        var sourceB = new ProductionSourceId(Identity.New());
        var spec = new ProductionSpecification(
            ControlContractVersion.Current, productionId, "Output Mapping Runtime",
            [new(sourceA, "Input A"), new(sourceB, "Input B")],
            new ProductionRoutingState(sourceA, sourceA),
            initialOutputRoles: [ProductionOutputRoleState.Program(sourceA)]);
        var runtime = new V1RuntimeHostService(
            new MediaSourceId(sourceA.Value), new MediaSourceId(sourceB.Value),
            VideoFormat.Hd1080p50Rgba8, new NullRecordingWriter(), gpuBackend: backend);
        var initialized = ControlDomainEngine.Initialize(spec);
        Assert.True(initialized.Succeeded);
        var planning = CapabilityPlanningEngine.Plan(spec,
            initialized.State!.Authoritative, new ProviderRegistry(runtime.ProviderDescriptors));
        Assert.True(planning.Succeeded);
        var prepared = planning.PreparedExecution!;
        var program = Assert.Single(prepared.Bindings, b => b.OutputRoleId == "program");
        var result = runtime.ApplyExecution(prepared, program.MediaSinkId!.Value);
        Assert.Equal(RuntimeCommitStatus.Committed, result.Commit?.Status);
        return new Fixture(runtime,
            new MediaSourceId(sourceA.Value), new MediaSourceId(sourceB.Value));
    }

    private sealed record Fixture(
        V1RuntimeHostService Runtime, MediaSourceId SourceA, MediaSourceId SourceB) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private sealed class ProviderRegistry(IReadOnlyList<ProviderDescriptor> providers)
        : IProviderCapabilityRegistry
    {
        private readonly ProviderDescriptor[] _providers = providers.ToArray();
        public IReadOnlyList<ProviderDescriptor> GetProviders() => _providers;
    }

    private class BackendWithoutMapping : IGpuProcessingBackend
    {
        private readonly ManagedReferenceGpuBackend _inner = new();
        protected ManagedReferenceGpuBackend Inner => _inner;
        public GpuBackendInfo Info => _inner.Info;
        public SurfaceStorageDomain StorageDomain => _inner.StorageDomain;
        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public void Allocate(SurfaceId id, VideoFormat format, ReadOnlySpan<byte> pixels) =>
            _inner.Allocate(id, format, pixels);
        public void Composite(SurfaceId id, VideoFormat format, GpuCompositeOperation operation) =>
            _inner.Composite(id, format, operation);
        public byte[] Readback(SurfaceId id, VideoFormat format) => _inner.Readback(id, format);
        public void ReadbackInto(SurfaceId id, VideoFormat format, Span<byte> destination) =>
            _inner.ReadbackInto(id, format, destination);
        public void Release(SurfaceId id) => _inner.Release(id);
        public void Dispose() => _inner.Dispose();
    }

    private sealed class FailingMappingBackend : BackendWithoutMapping, IGpuOutputMappingBackend
    {
        public void RenderOutputMapping(SurfaceId outputSurfaceId, VideoFormat format,
            IReadOnlyList<GpuOutputMappingSurface> surfaces)
        {
            Inner.Allocate(outputSurfaceId, format,
                new byte[RgbaFrameBuffer.RequiredByteLength(format)]);
            throw new InvalidOperationException("Injected output mapping failure.");
        }
    }

    private sealed class NullRecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
