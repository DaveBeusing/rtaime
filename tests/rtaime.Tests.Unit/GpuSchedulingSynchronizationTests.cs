// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class GpuSchedulingSynchronizationTests
{
    private static readonly VideoFormat TestFormat =
        new(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static readonly MediaSourceId SourceA =
        new(Identity.Parse("a2000000-0000-0000-0000-000000000001"));

    private static readonly MediaSourceId SourceB =
        new(Identity.Parse("a2000000-0000-0000-0000-000000000002"));

    private static readonly MediaSourceId LayerA =
        new(Identity.Parse("a2000000-0000-0000-0000-000000000003"));

    private static readonly MediaSourceId LayerB =
        new(Identity.Parse("a2000000-0000-0000-0000-000000000004"));

    private static readonly MediaSourceId Output =
        new(Identity.Parse("a2000000-0000-0000-0000-000000000005"));

    [Fact]
    public async Task Stop_serializes_behind_inflight_composite_and_releases_all_surfaces()
    {
        var backend = new BlockingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        using var a = Upload(provider, SourceA, 1, 2, 3, 0);
        using var b = Upload(provider, SourceB, 4, 5, 6, 0);
        var request = new GpuCompositeRequest(Output, a, b, GpuTransition.CutToA);

        var compositeTask = Task.Run(() => provider.Composite(request));
        Assert.True(backend.CompositeStarted.Wait(TimeSpan.FromSeconds(5)));

        var stopTask = Task.Run(provider.Stop);
        Assert.False(stopTask.Wait(TimeSpan.FromMilliseconds(100)));

        backend.AllowCompositeToComplete.Set();

        var result = await compositeTask;
        await stopTask;

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.True(result.Frame!.IsDisposed);
        Assert.Equal(GpuProviderState.Stopped, provider.State);
        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.Equal(0, backend.ActiveAllocationCount);
    }

    [Fact]
    public void Repeated_layer_reconfiguration_preserves_pixels_order_and_surface_bound()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();

        using var a = Upload(provider, SourceA, 0, 0, 0, 0);
        using var b = Upload(provider, SourceB, 0, 0, 0, 0);
        using var red = Upload(provider, LayerA, 255, 0, 0, 0);
        using var green = Upload(provider, LayerB, 0, 255, 0, 0);
        var baseline = provider.ActiveSurfaceCount;
        var expectedByConfiguration = new Dictionary<int, byte[]>();

        for (var iteration = 0; iteration < 48; iteration++)
        {
            var configuration = iteration % 3;
            var layers = configuration switch
            {
                0 => Array.Empty<GpuKeyLayer>(),
                1 => new[] { new GpuKeyLayer(red, 160) },
                _ => new[] { new GpuKeyLayer(red, 160), new GpuKeyLayer(green, 192) }
            };

            var result = provider.Composite(GpuCompositeRequest.WithLayers(
                Output,
                a,
                b,
                GpuTransition.CutToA,
                layers));

            Assert.True(result.Succeeded, result.Failure?.ToString());
            using (var output = result.Frame!)
            {
                var pixels = provider.Readback(output);
                if (expectedByConfiguration.TryGetValue(configuration, out var expected))
                    Assert.Equal(expected, pixels);
                else
                    expectedByConfiguration.Add(configuration, pixels);
            }

            Assert.Equal(baseline, provider.ActiveSurfaceCount);
        }

        Assert.Equal(3, expectedByConfiguration.Count);
    }

    private static GpuFrame Upload(
        GpuProcessingProvider provider,
        MediaSourceId sourceId,
        byte red,
        byte green,
        byte blue,
        ulong sequence) =>
        provider.Upload(
            sourceId,
            RgbaFrameBuffer.Solid(TestFormat, red, green, blue),
            new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)),
            new Generation(sequence),
            "synchronization-test");

    private sealed class BlockingCompositeBackend : IGpuProcessingBackend
    {
        private readonly ManagedReferenceGpuBackend _inner = new();

        public ManualResetEventSlim CompositeStarted { get; } = new(false);
        public ManualResetEventSlim AllowCompositeToComplete { get; } = new(false);

        public GpuBackendInfo Info { get; } = new(
            GpuBackendKind.NvidiaCuda,
            "Blocking CUDA Structural Backend",
            hardwareAccelerated: true,
            available: true);

        public SurfaceStorageDomain StorageDomain => SurfaceStorageDomain.Device;
        public int ActiveAllocationCount => _inner.ActiveAllocationCount;

        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();

        public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels) =>
            _inner.Allocate(surfaceId, format, rgbaPixels);

        public void Composite(
            SurfaceId outputSurfaceId,
            VideoFormat format,
            GpuCompositeOperation operation)
        {
            CompositeStarted.Set();
            if (!AllowCompositeToComplete.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting to release the structural composite.");
            _inner.Composite(outputSurfaceId, format, operation);
        }

        public byte[] Readback(SurfaceId surfaceId, VideoFormat format) =>
            _inner.Readback(surfaceId, format);

        public void ReadbackInto(
            SurfaceId surfaceId,
            VideoFormat format,
            Span<byte> destination) =>
            _inner.ReadbackInto(surfaceId, format, destination);

        public void Release(SurfaceId surfaceId) => _inner.Release(surfaceId);

        public void Dispose()
        {
            CompositeStarted.Dispose();
            AllowCompositeToComplete.Dispose();
            _inner.Dispose();
        }
    }
}
