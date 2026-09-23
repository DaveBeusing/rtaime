using rtaime.Control;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class GpuProcessingTests
{
    private static readonly VideoFormat TestFormat =
        new(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static readonly MediaSourceId SourceA =
        new(Identity.Parse("81000000-0000-0000-0000-000000000001"));

    private static readonly MediaSourceId SourceB =
        new(Identity.Parse("81000000-0000-0000-0000-000000000002"));

    private static readonly MediaSourceId LayerSource =
        new(Identity.Parse("81000000-0000-0000-0000-000000000003"));

    private static readonly MediaSourceId OutputSource =
        new(Identity.Parse("81000000-0000-0000-0000-000000000004"));

    [Fact]
    public void Capability_model_advertises_gpu_processing_and_resource_capacity()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());

        Assert.Equal(ProviderAvailabilityState.Degraded, provider.Descriptor.Availability.State);
        Assert.Equal("gpu.backend.reference_only", provider.Descriptor.Availability.Failure!.Value.Code);
        Assert.Equal(5, provider.Descriptor.Capabilities.Count);
        Assert.Contains(provider.Descriptor.Capabilities, capability => capability.Kind == GpuCapabilityKinds.StaticRgbaSource);
        Assert.Contains(provider.Descriptor.Capabilities, capability => capability.Kind == GpuCapabilityKinds.DynamicRgbaSource);
        Assert.Contains(provider.Descriptor.Capabilities, capability => capability.Kind == GpuCapabilityKinds.CompositeRgba);
        Assert.Contains(provider.Descriptor.Capabilities, capability => capability.Kind == GpuCapabilityKinds.Cut);
        Assert.Contains(provider.Descriptor.Capabilities, capability => capability.Kind == GpuCapabilityKinds.Dissolve);

        var resource = Assert.Single(provider.Descriptor.Resources);
        Assert.Equal(GpuCapabilityKinds.Processing, resource.Kind);
        Assert.Equal((uint)1, resource.CapacityUnits);
        Assert.True(resource.Reservable);

        var requirement = new CapabilityRequirement(
            ProviderContractVersion.Current,
            Identity.Parse("82000000-0000-0000-0000-000000000001"),
            GpuCapabilityKinds.CompositeRgba,
            1,
            new[] { VideoFormat.Hd1080p50Rgba8 });
        var capability = provider.Descriptor.Capabilities.Single(item => item.Kind == GpuCapabilityKinds.CompositeRgba);

        Assert.True(CapabilityRequirementMatcher.Matches(requirement, capability));
        Assert.True(resource.CapacityUnits >= requirement.RequiredCapacityUnits);
    }

    [Fact]
    public void Cuda_capability_detection_is_fail_closed_and_never_invents_hardware()
    {
        var info = CudaGpuProcessingBackend.Detect();

        Assert.Equal(GpuBackendKind.NvidiaCuda, info.Kind);
        Assert.True(info.HardwareAccelerated);
        if (info.Available)
        {
            Assert.Null(info.Failure);
            Assert.False(string.IsNullOrWhiteSpace(info.DeviceName));
        }
        else
        {
            Assert.NotNull(info.Failure);
            Assert.Equal("gpu.cuda.unavailable", info.Failure!.Value.Code);
        }
    }

    [Fact]
    public void Rgba_frame_buffer_can_update_pixels_without_replacing_storage()
    {
        var buffer = Solid(1, 2, 3, 255);
        var storage = buffer.Pixels;
        var replacement = Solid(40, 50, 60, 255).Pixels.ToArray();

        buffer.CopyPixelsFrom(replacement);

        Assert.Equal(replacement, buffer.Pixels.ToArray());
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(storage, out var before));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer.Pixels, out var after));
        Assert.Same(before.Array, after.Array);
    }

    [Fact]
    public void Static_rgba_source_materializes_exact_content()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var content = Solid(10, 20, 30, 255);
        var source = new StaticRgbaSource(SourceA, content);
        using var frame = source.Materialize(provider, Timing(0));

        Assert.Equal(content.Pixels.ToArray(), provider.Readback(frame));
        Assert.Equal(Generation.Initial, frame.Descriptor.Surface.Lifetime.Generation);
        Assert.Equal(1, backend.ActiveAllocationCount);
    }

    [Fact]
    public void Dynamic_rgba_source_advances_generation_and_content()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        var source = new DynamicRgbaSource(SourceA, Solid(1, 2, 3, 255));
        using var first = source.Materialize(provider, Timing(0));

        source.Update(Solid(40, 50, 60, 255));
        using var second = source.Materialize(provider, Timing(1));

        Assert.Equal(new Generation(1), source.Generation);
        Assert.Equal(Generation.Initial, first.Descriptor.Surface.Lifetime.Generation);
        Assert.Equal(new Generation(1), second.Descriptor.Surface.Lifetime.Generation);
        Assert.Equal(Solid(40, 50, 60, 255).Pixels.ToArray(), provider.Readback(second));
    }

    [Fact]
    public void Compositor_applies_one_rgba_key_layer_with_opacity()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        using var backgroundA = Upload(provider, SourceA, Solid(0, 0, 0, 255), 0);
        using var backgroundB = Upload(provider, SourceB, Solid(0, 0, 0, 255), 0);
        using var layer = Upload(provider, LayerSource, Solid(255, 0, 0, 128), 0);

        var result = provider.Composite(new GpuCompositeRequest(
            OutputSource,
            backgroundA,
            backgroundB,
            GpuTransition.CutToA,
            new GpuKeyLayer(layer, byte.MaxValue, visible: true)));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        using var output = result.Frame!;
        AssertPixel(provider.Readback(output), red: 128, green: 0, blue: 0, alpha: 255);
    }

    [Fact]
    public void Compositor_applies_multiple_rgba_layers_in_declared_order()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        using var backgroundA = Upload(provider, SourceA, Solid(0, 0, 0, 255), 0);
        using var backgroundB = Upload(provider, SourceB, Solid(0, 0, 0, 255), 0);
        using var red = Upload(provider, new MediaSourceId(Identity.Parse("53000000-0000-0000-0000-000000000010")), Solid(255, 0, 0, 128), 0);
        using var green = Upload(provider, new MediaSourceId(Identity.Parse("53000000-0000-0000-0000-000000000011")), Solid(0, 255, 0, 128), 0);

        var request = GpuCompositeRequest.WithLayers(
            OutputSource,
            backgroundA,
            backgroundB,
            GpuTransition.CutToA,
            new[]
            {
                new GpuKeyLayer(red),
                new GpuKeyLayer(green)
            });

        var result = provider.Composite(request);

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(2, result.LayerCount);
        Assert.True(result.Duration >= TimeSpan.Zero);
        var output = result.Frame!;
        AssertPixel(provider.Readback(output), red: 64, green: 128, blue: 0, alpha: 255);
        Assert.Equal(5, backend.ActiveAllocationCount);
        output.Dispose();
        Assert.Equal(4, backend.ActiveAllocationCount);
    }

    [Fact]
    public void Compositor_rejects_duplicate_layer_surfaces()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        using var backgroundA = Upload(provider, SourceA, Solid(0, 0, 0, 255), 0);
        using var backgroundB = Upload(provider, SourceB, Solid(0, 0, 0, 255), 0);
        using var layer = Upload(provider, LayerSource, Solid(255, 255, 255, 255), 0);

        var result = provider.Composite(GpuCompositeRequest.WithLayers(
            OutputSource,
            backgroundA,
            backgroundB,
            GpuTransition.CutToA,
            new[] { new GpuKeyLayer(layer), new GpuKeyLayer(layer) }));

        Assert.False(result.Succeeded);
        Assert.Equal("gpu.composite.layer_duplicate", result.Failure?.Code);
    }

    [Fact]
    public void Compositor_rejects_layer_counts_above_the_bounded_limit()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        using var backgroundA = Upload(provider, SourceA, Solid(0, 0, 0, 255), 0);
        using var backgroundB = Upload(provider, SourceB, Solid(0, 0, 0, 255), 0);
        using var layer = Upload(provider, LayerSource, Solid(255, 255, 255, 255), 0);

        var request = GpuCompositeRequest.WithLayers(
            OutputSource,
            backgroundA,
            backgroundB,
            GpuTransition.CutToA,
            Enumerable.Repeat(new GpuKeyLayer(layer), GpuCompositeLimits.MaxActiveLayers + 1));

        var result = provider.Composite(request);

        Assert.False(result.Succeeded);
        Assert.Equal("gpu.composite.layer_limit", result.Failure?.Code);
        Assert.Equal(GpuCompositeLimits.MaxActiveLayers + 1, result.LayerCount);
    }

    [Fact]
    public void Cut_selects_target_background_without_blending()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        using var a = Upload(provider, SourceA, Solid(10, 20, 30, 255), 0);
        using var b = Upload(provider, SourceB, Solid(90, 80, 70, 255), 0);

        var result = provider.Composite(new GpuCompositeRequest(OutputSource, a, b, GpuTransition.CutToB));

        Assert.True(result.Succeeded);
        using var output = result.Frame!;
        Assert.Equal(Solid(90, 80, 70, 255).Pixels.ToArray(), provider.Readback(output));
    }

    [Fact]
    public void Dissolve_uses_deterministic_integer_blend_weight()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        using var a = Upload(provider, SourceA, Solid(0, 0, 0, 255), 0);
        using var b = Upload(provider, SourceB, Solid(255, 255, 255, 255), 0);

        var result = provider.Composite(new GpuCompositeRequest(
            OutputSource,
            a,
            b,
            GpuTransition.Dissolve(128)));

        Assert.True(result.Succeeded);
        using var output = result.Frame!;
        AssertPixel(provider.Readback(output), 128, 128, 128, 255);
    }

    [Fact]
    public void Backend_failure_is_observed_and_next_composite_can_recover()
    {
        using var backend = new FailOnceCompositeBackend(new ManagedReferenceGpuBackend());
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        using var a = Upload(provider, SourceA, Solid(10, 20, 30, 255), 0);
        using var b = Upload(provider, SourceB, Solid(40, 50, 60, 255), 0);
        var request = new GpuCompositeRequest(OutputSource, a, b, GpuTransition.CutToA);

        var failed = provider.Composite(request);
        var recovered = provider.Composite(request);

        Assert.False(failed.Succeeded);
        Assert.Equal("gpu.composite.backend_failure", failed.Failure!.Value.Code);
        Assert.True(recovered.Succeeded, recovered.Failure?.ToString());
        using var output = recovered.Frame!;
        Assert.Contains(provider.Observations, observation => observation.Code == "gpu.composite.failed");
        Assert.Contains(provider.Observations, observation => observation.Code == "gpu.composite.cut");
    }

    [Fact]
    public void Surface_memory_is_released_explicitly_and_on_stop()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var first = Upload(provider, SourceA, Solid(1, 1, 1, 255), 0);
        var second = Upload(provider, SourceB, Solid(2, 2, 2, 255), 0);
        Assert.Equal(2, provider.ActiveSurfaceCount);
        Assert.Equal(2, backend.ActiveAllocationCount);

        first.Dispose();
        Assert.Equal(1, provider.ActiveSurfaceCount);
        Assert.Equal(1, backend.ActiveAllocationCount);

        provider.Stop();
        Assert.True(second.IsDisposed);
        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.Equal(0, backend.ActiveAllocationCount);
    }

    [Fact]
    public void Provider_supports_repeated_start_stop_cycles()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            provider.Start();
            using var frame = Upload(provider, SourceA, Solid((byte)cycle, 0, 0, 255), (ulong)cycle);
            Assert.Equal((byte)cycle, provider.Readback(frame)[0]);
            provider.Stop();
            Assert.Equal(GpuProviderState.Stopped, provider.State);
            Assert.Equal(0, backend.ActiveAllocationCount);
        }
    }

    private static GpuFrame Upload(
        GpuProcessingProvider provider,
        MediaSourceId sourceId,
        RgbaFrameBuffer buffer,
        ulong sequenceNumber) =>
        provider.Upload(sourceId, buffer, Timing(sequenceNumber), new Generation(sequenceNumber), "test");

    private static RgbaFrameBuffer Solid(byte red, byte green, byte blue, byte alpha) =>
        RgbaFrameBuffer.Solid(TestFormat, red, green, blue, alpha);

    private static FrameTiming Timing(ulong sequenceNumber) =>
        new(sequenceNumber, checked((long)sequenceNumber), new Timebase(1, 50));

    private static void AssertPixel(byte[] pixels, byte red, byte green, byte blue, byte alpha)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.Equal(red, pixels[offset]);
            Assert.Equal(green, pixels[offset + 1]);
            Assert.Equal(blue, pixels[offset + 2]);
            Assert.Equal(alpha, pixels[offset + 3]);
        }
    }

    private sealed class FailOnceCompositeBackend : IGpuProcessingBackend
    {
        private readonly IGpuProcessingBackend _inner;
        private bool _failNext = true;

        public FailOnceCompositeBackend(IGpuProcessingBackend inner)
        {
            _inner = inner;
        }

        public GpuBackendInfo Info => _inner.Info;
        public SurfaceStorageDomain StorageDomain => _inner.StorageDomain;
        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels) =>
            _inner.Allocate(surfaceId, format, rgbaPixels);

        public void Composite(SurfaceId outputSurfaceId, VideoFormat format, GpuCompositeOperation operation)
        {
            if (_failNext)
            {
                _failNext = false;
                throw new InvalidOperationException("Injected GPU composite failure.");
            }

            _inner.Composite(outputSurfaceId, format, operation);
        }

        public byte[] Readback(SurfaceId surfaceId, VideoFormat format) => _inner.Readback(surfaceId, format);
        public void Release(SurfaceId surfaceId) => _inner.Release(surfaceId);
        public void Dispose() => _inner.Dispose();
    }
}
