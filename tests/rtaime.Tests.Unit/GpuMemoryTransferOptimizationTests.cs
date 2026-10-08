using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class GpuMemoryTransferOptimizationTests
{
    private static readonly VideoFormat TestFormat =
        new(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    [Fact]
    public void Static_source_reuses_one_uploaded_surface_across_frame_timings()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var source = new StaticRgbaSource(
            MediaSourceId.New(),
            RgbaFrameBuffer.Solid(TestFormat, 10, 20, 30));

        using var first = source.MaterializeReusable(provider, Timing(1));
        using var second = source.MaterializeReusable(provider, Timing(2));

        Assert.Equal(first.SurfaceId, second.SurfaceId);
        Assert.NotEqual(first.Descriptor.Timing, second.Descriptor.Timing);
        Assert.Equal(1, backend.ActiveAllocationCount);
        Assert.Equal(1, provider.ActiveSurfaceCount);

        var transfers = provider.MemoryTransferStatistics;
        Assert.Equal((ulong)1, transfers.HostToDeviceOperations);
        Assert.Equal((ulong)TestFormatBytes, transfers.HostToDeviceBytes);
        Assert.Equal((ulong)1, transfers.ReusableUploadHits);
        Assert.Equal((ulong)1, transfers.ReusableUploadMisses);
        Assert.Equal((ulong)TestFormatBytes, transfers.AvoidedHostToDeviceBytes);
        Assert.Equal(1, transfers.ReusableUploadSurfaces);
    }

    [Fact]
    public void Static_source_content_mutation_replaces_cached_surface_without_stale_pixels()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var source = new StaticRgbaSource(
            MediaSourceId.New(),
            RgbaFrameBuffer.Solid(TestFormat, 1, 2, 3));
        using var first = source.MaterializeReusable(provider, Timing(1));

        source.Content.CopyPixelsFrom(
            RgbaFrameBuffer.Solid(TestFormat, 90, 80, 70).Pixels.Span);

        using var second = source.MaterializeReusable(provider, Timing(2));

        Assert.NotEqual(first.SurfaceId, second.SurfaceId);
        Assert.Equal((byte)90, provider.Readback(second)[0]);
        Assert.Equal(2, backend.ActiveAllocationCount);

        first.Dispose();
        Assert.Equal(1, backend.ActiveAllocationCount);

        var transfers = provider.MemoryTransferStatistics;
        Assert.Equal((ulong)2, transfers.HostToDeviceOperations);
        Assert.Equal((ulong)2, transfers.ReusableUploadMisses);
        Assert.Equal((ulong)1, transfers.ReusableUploadEvictions);
    }

    [Fact]
    public void Dynamic_source_reuses_unchanged_generation_and_replaces_after_update()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var source = new DynamicRgbaSource(
            MediaSourceId.New(),
            RgbaFrameBuffer.Solid(TestFormat, 4, 5, 6));

        using var first = source.MaterializeReusable(provider, Timing(1));
        using var second = source.MaterializeReusable(provider, Timing(2));
        Assert.Equal(first.SurfaceId, second.SurfaceId);

        source.Update(RgbaFrameBuffer.Solid(TestFormat, 40, 50, 60));
        using var third = source.MaterializeReusable(provider, Timing(3));

        Assert.NotEqual(first.SurfaceId, third.SurfaceId);
        Assert.Equal(new Generation(1), third.Descriptor.Surface.Lifetime.Generation);
        Assert.Equal((byte)40, provider.Readback(third)[0]);

        var transfers = provider.MemoryTransferStatistics;
        Assert.Equal((ulong)2, transfers.HostToDeviceOperations);
        Assert.Equal((ulong)1, transfers.ReusableUploadHits);
        Assert.Equal((ulong)2, transfers.ReusableUploadMisses);
        Assert.Equal((ulong)1, transfers.ReusableUploadEvictions);
        Assert.Equal((ulong)1, transfers.DeviceToHostOperations);
        Assert.Equal((ulong)TestFormatBytes, transfers.DeviceToHostBytes);
    }

    [Fact]
    public void Reusable_upload_cache_is_bounded_and_evicts_oldest_retention()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        for (var index = 0; index < GpuProcessingProvider.ReusableUploadSurfaceCapacity + 1; index++)
        {
            var source = new StaticRgbaSource(
                MediaSourceId.New(),
                RgbaFrameBuffer.Solid(TestFormat, (byte)index, 0, 0));
            using var frame = source.MaterializeReusable(provider, Timing((ulong)index));
        }

        var transfers = provider.MemoryTransferStatistics;
        Assert.Equal(GpuProcessingProvider.ReusableUploadSurfaceCapacity, transfers.ReusableUploadSurfaces);
        Assert.Equal((ulong)1, transfers.ReusableUploadEvictions);
        Assert.Equal(GpuProcessingProvider.ReusableUploadSurfaceCapacity, provider.ActiveSurfaceCount);
        Assert.Equal(GpuProcessingProvider.ReusableUploadSurfaceCapacity, backend.ActiveAllocationCount);

        provider.Stop();

        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.Equal(0, backend.ActiveAllocationCount);
        Assert.Equal(0, provider.MemoryTransferStatistics.ReusableUploadSurfaces);
    }

    [Fact]
    public void Reusable_surface_is_not_released_until_last_frame_reference_is_disposed()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var source = new DynamicRgbaSource(
            MediaSourceId.New(),
            RgbaFrameBuffer.Solid(TestFormat, 7, 8, 9));

        var oldFrame = source.MaterializeReusable(provider, Timing(1));
        source.Update(RgbaFrameBuffer.Solid(TestFormat, 70, 80, 90));
        using var currentFrame = source.MaterializeReusable(provider, Timing(2));

        Assert.Equal(2, backend.ActiveAllocationCount);

        oldFrame.Dispose();

        Assert.Equal(1, backend.ActiveAllocationCount);
        Assert.Equal((byte)70, provider.Readback(currentFrame)[0]);
    }

    private const int TestFormatBytes = 2 * 1 * 4;

    private static FrameTiming Timing(ulong sequence) =>
        new(sequence, checked((long)sequence), new Timebase(1, 50));
}
