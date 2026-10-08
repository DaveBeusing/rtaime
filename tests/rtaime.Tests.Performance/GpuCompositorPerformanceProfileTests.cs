using System.Diagnostics;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Performance;

public sealed class GpuCompositorPerformanceProfileTests
{
    private static readonly VideoFormat ProfileFormat =
        new(96, 55, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Managed_reference_profiles_pass_count_logical_traffic_and_tail_latency(bool noopHeavy)
    {
        using var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var timing = new FrameTiming(1, 1, new Timebase(1, 50));
        using var backgroundA = new StaticRgbaSource(MediaSourceId.New(), Pattern(13))
            .Materialize(provider, timing);
        using var backgroundB = new StaticRgbaSource(MediaSourceId.New(), Pattern(61))
            .Materialize(provider, timing);

        foreach (var layerCount in new[] { 0, 1, 2, 4, 8 })
        {
            var contents = new List<RgbaFrameBuffer>(layerCount);
            var frames = new List<GpuFrame>(layerCount);
            try
            {
                for (var index = 0; index < layerCount; index++)
                {
                    var content = Pattern(checked((byte)(97 + index * 11)));
                    contents.Add(content);
                    frames.Add(new StaticRgbaSource(MediaSourceId.New(), content)
                        .Materialize(provider, timing));
                }

                var layers = frames
                    .Select((frame, index) =>
                    {
                        if (!noopHeavy)
                            return new GpuKeyLayer(frame, checked((byte)(255 - index * 9)), visible: true);

                        return index % 3 switch
                        {
                            0 => new GpuKeyLayer(frame, byte.MaxValue, visible: true),
                            1 => new GpuKeyLayer(frame, byte.MaxValue, visible: false),
                            _ => new GpuKeyLayer(frame, 0, visible: true)
                        };
                    })
                    .ToArray();

                var contributingLayers = layers.Count(layer => layer.Visible && layer.Opacity != 0);
                var expectedPassesPerRequest = Math.Max(1, contributingLayers);
                var frameBytes = checked((ulong)RgbaFrameBuffer.RequiredByteLength(ProfileFormat));
                var logicalBytesPerRequest = contributingLayers == 0
                    ? checked(frameBytes * 3UL)
                    : checked(frameBytes * 4UL * (ulong)contributingLayers);

                const int warmupIterations = 3;
                const int measuredIterations = 12;
                for (var index = 0; index < warmupIterations; index++)
                {
                    using var warmup = provider.Composite(GpuCompositeRequest.WithLayers(
                        MediaSourceId.New(),
                        backgroundA,
                        backgroundB,
                        GpuTransition.Dissolve(127),
                        layers)).Frame;
                    Assert.NotNull(warmup);
                }

                var beforePasses = backend.CompositeCalls;
                var samples = new double[measuredIterations];
                for (var index = 0; index < measuredIterations; index++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var result = provider.Composite(GpuCompositeRequest.WithLayers(
                        MediaSourceId.New(),
                        backgroundA,
                        backgroundB,
                        GpuTransition.Dissolve(127),
                        layers));
                    samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Assert.True(result.Succeeded, result.Failure?.ToString());
                    result.Frame!.Dispose();
                }

                var measuredPasses = backend.CompositeCalls - beforePasses;
                Assert.Equal(expectedPassesPerRequest * measuredIterations, measuredPasses);

                Array.Sort(samples);
                var p95 = Percentile(samples, 0.95);
                var p99 = Percentile(samples, 0.99);
                Assert.True(p95 <= p99);
                Assert.True(
                    p99 < 2_000,
                    $"Managed compositor microbenchmark runaway: layers={layerCount}, noopHeavy={noopHeavy}, p99={p99:0.###}ms.");

                Console.WriteLine(
                    $"Managed compositor profile {ProfileFormat.Width}x{ProfileFormat.Height} " +
                    $"layers={layerCount} contributing={contributingLayers} passes={expectedPassesPerRequest} " +
                    $"logicalBytesPerRequest={logicalBytesPerRequest} p95={p95:0.###}ms p99={p99:0.###}ms");
            }
            finally
            {
                foreach (var frame in frames)
                    frame.Dispose();
            }
        }
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        if (ordered.Length == 0)
            throw new ArgumentException("At least one sample is required.", nameof(ordered));
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length));
        return ordered[Math.Min(ordered.Length - 1, rank - 1)];
    }

    private static RgbaFrameBuffer Pattern(byte seed)
    {
        var pixels = new byte[RgbaFrameBuffer.RequiredByteLength(ProfileFormat)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var pixel = offset / 4;
            pixels[offset] = unchecked((byte)(seed + pixel * 3));
            pixels[offset + 1] = unchecked((byte)(seed + 23 + pixel * 5));
            pixels[offset + 2] = unchecked((byte)(seed + 47 + pixel * 7));
            pixels[offset + 3] = unchecked((byte)(seed + 71 + pixel * 11));
        }

        return new RgbaFrameBuffer(ProfileFormat, pixels);
    }

    private sealed class CountingCompositeBackend : IGpuProcessingBackend
    {
        private readonly ManagedReferenceGpuBackend _inner = new();

        public GpuBackendInfo Info => _inner.Info;
        public SurfaceStorageDomain StorageDomain => _inner.StorageDomain;
        public int CompositeCalls { get; private set; }

        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();

        public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels) =>
            _inner.Allocate(surfaceId, format, rgbaPixels);

        public void Composite(
            SurfaceId outputSurfaceId,
            VideoFormat format,
            GpuCompositeOperation operation)
        {
            CompositeCalls++;
            _inner.Composite(outputSurfaceId, format, operation);
        }

        public byte[] Readback(SurfaceId surfaceId, VideoFormat format) =>
            _inner.Readback(surfaceId, format);

        public void ReadbackInto(SurfaceId surfaceId, VideoFormat format, Span<byte> destination) =>
            _inner.ReadbackInto(surfaceId, format, destination);

        public void Release(SurfaceId surfaceId) => _inner.Release(surfaceId);
        public void Dispose() => _inner.Dispose();
    }
}
