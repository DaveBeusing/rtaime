using rtaime.Control;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class GpuBackendResourceSemanticsTests
{
    private static readonly VideoFormat TestFormat =
        new(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static readonly MediaSourceId SourceId =
        new(Identity.Parse("a1000000-0000-0000-0000-000000000001"));

    [Fact]
    public void Managed_reference_and_cuda_structural_paths_preserve_the_same_resource_semantics()
    {
        using var managed = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        using var cuda = new GpuProcessingProvider(new CudaStructuralBackend());

        managed.Start();
        cuda.Start();

        var generation = new Generation(9);
        var pixels = RgbaFrameBuffer.Solid(TestFormat, 10, 20, 30);
        var timing = new FrameTiming(11, 11, new Timebase(1, 50));

        using var managedFrame = managed.Upload(SourceId, pixels, timing, generation, "semantic-parity");
        using var cudaFrame = cuda.Upload(SourceId, pixels, timing, generation, "semantic-parity");

        Assert.Equal(SurfaceStorageDomain.Host, managedFrame.Descriptor.Surface.StorageDomain);
        Assert.Equal(SurfaceStorageDomain.Device, cudaFrame.Descriptor.Surface.StorageDomain);

        AssertEquivalentSemantics(managedFrame.Descriptor.Surface, generation);
        AssertEquivalentSemantics(cudaFrame.Descriptor.Surface, generation);

        Assert.Equal(ProviderAvailabilityState.Degraded, managed.Descriptor.Availability.State);
        Assert.Equal("gpu.backend.reference_only", managed.Descriptor.Availability.Failure?.Code);
        Assert.Equal(ProviderAvailabilityState.Available, cuda.Descriptor.Availability.State);
        Assert.Null(cuda.Descriptor.Availability.Failure);

        var managedComposite = managed.Descriptor.Capabilities.Single(
            capability => capability.Kind == GpuCapabilityKinds.CompositeRgba);
        var cudaComposite = cuda.Descriptor.Capabilities.Single(
            capability => capability.Kind == GpuCapabilityKinds.CompositeRgba);

        Assert.Equal(managedComposite.VideoFormats, cudaComposite.VideoFormats);
        Assert.True(managedComposite.SupportsVideoFormat(VideoFormat.Hd1080p50Rgba8));
        Assert.True(cudaComposite.SupportsVideoFormat(VideoFormat.Hd1080p50Rgba8));
    }

    [Fact]
    public void Capability_matcher_rejects_formats_not_advertised_by_the_provider()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        var capability = provider.Descriptor.Capabilities.Single(
            item => item.Kind == GpuCapabilityKinds.CompositeRgba);
        var unsupported = new VideoFormat(
            1280,
            720,
            FrameRate.Fps50,
            PixelFormat.Rgba8,
            ScanMode.Progressive);

        var requirement = new CapabilityRequirement(
            ProviderContractVersion.Current,
            Identity.Parse("a1000000-0000-0000-0000-000000000002"),
            GpuCapabilityKinds.CompositeRgba,
            1,
            new[] { unsupported });

        Assert.False(capability.SupportsVideoFormat(unsupported));
        Assert.False(CapabilityRequirementMatcher.Matches(requirement, capability));
    }

    private static void AssertEquivalentSemantics(SurfaceDescriptor surface, Generation generation)
    {
        Assert.Equal(SurfaceOwnership.ProducerOwned, surface.Ownership);
        Assert.Equal(generation, surface.Lifetime.Generation);
        Assert.NotNull(surface.Lifetime.LeaseId);
        Assert.False(surface.Lifetime.LeaseId!.Value.IsEmpty);
        Assert.Equal(TestFormat, surface.Format);
        Assert.True(SurfaceContractSemantics.IsCompatibleForRead(surface, TestFormat, generation));
        Assert.Equal(
            SurfaceCompletionSemantics.ProducerCompletedBeforePublication,
            SurfaceContractSemantics.Completion);
    }

    private sealed class CudaStructuralBackend : IGpuProcessingBackend
    {
        private readonly ManagedReferenceGpuBackend _inner = new();

        public GpuBackendInfo Info { get; } = new(
            GpuBackendKind.NvidiaCuda,
            "CUDA Structural Contract Backend",
            hardwareAccelerated: true,
            available: true);

        public SurfaceStorageDomain StorageDomain => SurfaceStorageDomain.Device;

        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();

        public void Allocate(
            SurfaceId surfaceId,
            VideoFormat format,
            ReadOnlySpan<byte> rgbaPixels) =>
            _inner.Allocate(surfaceId, format, rgbaPixels);

        public void Composite(
            SurfaceId outputSurfaceId,
            VideoFormat format,
            GpuCompositeOperation operation) =>
            _inner.Composite(outputSurfaceId, format, operation);

        public byte[] Readback(SurfaceId surfaceId, VideoFormat format) =>
            _inner.Readback(surfaceId, format);

        public void ReadbackInto(
            SurfaceId surfaceId,
            VideoFormat format,
            Span<byte> destination) =>
            _inner.ReadbackInto(surfaceId, format, destination);

        public void Release(SurfaceId surfaceId) => _inner.Release(surfaceId);
        public void Dispose() => _inner.Dispose();
    }
}
