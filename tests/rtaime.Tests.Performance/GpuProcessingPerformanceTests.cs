using System.Diagnostics;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Performance;

public sealed class GpuProcessingPerformanceTests
{
    private static readonly MediaSourceId SourceA =
        new(Identity.Parse("84000000-0000-0000-0000-000000000001"));

    private static readonly MediaSourceId SourceB =
        new(Identity.Parse("84000000-0000-0000-0000-000000000002"));

    private static readonly MediaSourceId LayerSource =
        new(Identity.Parse("84000000-0000-0000-0000-000000000003"));

    private static readonly MediaSourceId OutputSource =
        new(Identity.Parse("84000000-0000-0000-0000-000000000004"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Managed_reference_1080p_compositor_has_bounded_regression_guard(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        var timebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
        var timing = new FrameTiming(0, 0, timebase);
        var sourceA = new StaticRgbaSource(SourceA, RgbaFrameBuffer.Solid(format, 16, 32, 64));
        var sourceB = new StaticRgbaSource(SourceB, RgbaFrameBuffer.Solid(format, 192, 128, 64));
        var layerSource = new StaticRgbaSource(LayerSource, RgbaFrameBuffer.Solid(format, 255, 255, 255, 96));

        using var a = sourceA.Materialize(provider, timing);
        using var b = sourceB.Materialize(provider, timing);
        using var layer = layerSource.Materialize(provider, timing);
        var request = new GpuCompositeRequest(
            OutputSource,
            a,
            b,
            GpuTransition.Dissolve(128),
            new GpuKeyLayer(layer, 192));

        const int iterations = 6;
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < iterations; index++)
        {
            var result = provider.Composite(request);
            Assert.True(result.Succeeded, result.Failure?.ToString());
            using var output = result.Frame!;
        }
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"Managed 1080p GPU-provider reference compositor exceeded regression guard: {stopwatch.Elapsed}.");
        Assert.Equal(3, provider.ActiveSurfaceCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reusable_1080p_readback_has_no_full_frame_per_iteration_managed_allocation(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend(), readbackBufferCapacity: 2);
        provider.Start();

        var source = new StaticRgbaSource(SourceA, RgbaFrameBuffer.Solid(format, 16, 32, 64));
        var timebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
        using var frame = source.Materialize(provider, new FrameTiming(0, 0, timebase));

        for (var index = 0; index < 8; index++)
        {
            using var warmup = provider.RentReadback(frame);
            Assert.Equal(RgbaFrameBuffer.RequiredByteLength(format), warmup.Length);
        }

        const int iterations = 64;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            using var readback = provider.RentReadback(frame);
            _ = readback.Memory.Span[0];
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var fullFrameBytes = RgbaFrameBuffer.RequiredByteLength(format);

        Assert.True(
            allocated < 1_048_576,
            $"Reusable {format.Width}x{format.Height} readback allocated {allocated:N0} bytes across {iterations} iterations; one full RGBA frame is {fullFrameBytes:N0} bytes.");
        Assert.True(allocated < fullFrameBytes);
        Assert.Equal(1, provider.ReadbackPoolStatistics.AllocatedBuffers);
        Assert.Equal(0, provider.ReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal(1, provider.ReadbackPoolStatistics.AvailableBuffers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Managed_reference_1080p_multi_layer_compositor_reports_scaling_and_preserves_surface_lifetime(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        var timebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
        var timing = new FrameTiming(0, 0, timebase);
        var sourceA = new StaticRgbaSource(SourceA, RgbaFrameBuffer.Solid(format, 16, 32, 64));
        var sourceB = new StaticRgbaSource(SourceB, RgbaFrameBuffer.Solid(format, 192, 128, 64));
        var layerContent = RgbaFrameBuffer.Solid(format, 255, 255, 255, 96);

        using (var a = sourceA.Materialize(provider, timing))
        using (var b = sourceB.Materialize(provider, timing))
        {
            var persistentSurfaceBaseline = provider.ActiveSurfaceCount;
            Assert.Equal(2, persistentSurfaceBaseline);

            foreach (var layerCount in new[] { 0, 1, 2, 4, GpuCompositeLimits.MaxActiveLayers })
            {
                var layerFrames = new List<GpuFrame>(layerCount);
                try
                {
                    for (var index = 0; index < layerCount; index++)
                    {
                        var layerSource = new StaticRgbaSource(
                            new MediaSourceId(Identity.New()),
                            layerContent);
                        layerFrames.Add(layerSource.Materialize(provider, timing));
                    }

                    var inputSurfaceCount = provider.ActiveSurfaceCount;
                    var request = GpuCompositeRequest.WithLayers(
                        OutputSource,
                        a,
                        b,
                        GpuTransition.Dissolve(128),
                        layerFrames.Select((frame, index) =>
                            new GpuKeyLayer(frame, checked((byte)(224 - (index * 8))))));

                    var wallClock = Stopwatch.StartNew();
                    var result = provider.Composite(request);
                    wallClock.Stop();

                    Assert.True(result.Succeeded, result.Failure?.ToString());
                    Assert.Equal(layerCount, result.LayerCount);
                    Assert.True(result.Duration >= TimeSpan.Zero);

                    using (var output = result.Frame!)
                        Assert.Equal(inputSurfaceCount + 1, provider.ActiveSurfaceCount);

                    Assert.Equal(inputSurfaceCount, provider.ActiveSurfaceCount);
                    Console.WriteLine(
                        $"Managed {format.Width}x{format.Height} {format.FrameRate} layers={layerCount} " +
                        $"provider={result.Duration.TotalMilliseconds:0.###}ms wall={wallClock.Elapsed.TotalMilliseconds:0.###}ms " +
                        $"inputSurfaces={inputSurfaceCount}");
                }
                finally
                {
                    foreach (var frame in layerFrames)
                        frame.Dispose();
                }

                Assert.Equal(persistentSurfaceBaseline, provider.ActiveSurfaceCount);
            }
        }

        Assert.Equal(0, provider.ActiveSurfaceCount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reusable_static_1080p_source_avoids_repeated_full_frame_uploads(bool use5994)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        var source = new StaticRgbaSource(
            LayerSource,
            RgbaFrameBuffer.Solid(format, 48, 96, 144, 192));
        var timebase = new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator);
        const int iterations = 60;

        SurfaceId? surfaceId = null;
        for (var index = 0; index < iterations; index++)
        {
            using var frame = source.MaterializeReusable(
                provider,
                new FrameTiming(
                    checked((ulong)index),
                    index,
                    timebase));
            surfaceId ??= frame.SurfaceId;
            Assert.Equal(surfaceId.Value, frame.SurfaceId);
        }

        var transfers = provider.MemoryTransferStatistics;
        var frameBytes = checked((ulong)RgbaFrameBuffer.RequiredByteLength(format));

        Assert.Equal((ulong)1, transfers.UploadOperations);
        Assert.Equal(frameBytes, transfers.UploadBytes);
        Assert.Equal((ulong)0, transfers.HostToDeviceOperations);
        Assert.Equal((ulong)0, transfers.HostToDeviceBytes);
        Assert.Equal((ulong)(iterations - 1), transfers.ReusableUploadHits);
        Assert.Equal((ulong)1, transfers.ReusableUploadMisses);
        Assert.Equal(frameBytes * (iterations - 1), transfers.AvoidedUploadBytes);
        Assert.Equal((ulong)0, transfers.AvoidedHostToDeviceBytes);
        Assert.Equal(1, transfers.ReusableUploadSurfaces);
        Assert.Equal(1, provider.ActiveSurfaceCount);

        provider.Stop();

        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.Equal(0, provider.MemoryTransferStatistics.ReusableUploadSurfaces);
    }

}
