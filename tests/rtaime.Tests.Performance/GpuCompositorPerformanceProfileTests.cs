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

                        return (index % 3) switch
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Managed_1080p_low_latency_path_reports_comparable_tail_latency_and_allocations(bool use5994, bool dissolve)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        using var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend, readbackBufferCapacity: 1);
        provider.Start();
        var timing = new FrameTiming(1, 1, new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator));
        var aContent = RgbaFrameBuffer.Solid(format, 11, 29, 67, 113);
        var bContent = RgbaFrameBuffer.Solid(format, 231, 151, 97, 201);
        using var a = new StaticRgbaSource(MediaSourceId.New(), aContent).Materialize(provider, timing);
        using var b = new StaticRgbaSource(MediaSourceId.New(), bContent).Materialize(provider, timing);
        var weight = dissolve ? (byte)137 : (byte)0;
        var request = new GpuCompositeRequest(MediaSourceId.New(), a, b, GpuTransition.Dissolve(weight));
        var scalarOutput = new byte[aContent.ByteLength];
        var scalarReadback = new byte[aContent.ByteLength];
        void Scalar()
        {
            ScalarBlendBaseline(aContent.Pixels.Span, bContent.Pixels.Span, scalarOutput, weight);
            scalarOutput.AsSpan().CopyTo(scalarReadback);
        }
        void Optimized()
        {
            var result = provider.Composite(request);
            if (!result.Succeeded) throw new InvalidOperationException(result.Failure?.ToString());
            using var output = result.Frame!;
            using var pixels = provider.RentReadback(output);
            Assert.Equal(scalarReadback.AsSpan(0, 4).ToArray(), pixels.Memory.Span[..4].ToArray());
        }
        for (var index = 0; index < 3; index++) { Scalar(); Optimized(); }
        const int samples = 12;
        var scalarMs = new double[samples];
        var optimizedMs = new double[samples];
        var allocations = new long[samples];
        var passes = backend.CompositeCalls;
        for (var index = 0; index < samples; index++)
        {
            void MeasureScalar()
            {
                var start = Stopwatch.GetTimestamp();
                Scalar();
                scalarMs[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            void MeasureOptimized()
            {
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                Optimized();
                optimizedMs[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocations[index] = GC.GetAllocatedBytesForCurrentThread() - bytes;
            }
            // Alternate order to avoid consistently giving either path the warmer cache.
            if (index % 2 == 0) { MeasureScalar(); MeasureOptimized(); }
            else { MeasureOptimized(); MeasureScalar(); }
        }
        Array.Sort(scalarMs); Array.Sort(optimizedMs);
        Assert.Equal(dissolve ? samples : 0, backend.CompositeCalls - passes);
        Assert.Equal(2, provider.ActiveSurfaceCount);
        Assert.Equal(1, provider.ReadbackPoolStatistics.AllocatedBuffers);
        Assert.InRange(allocations.Max(), 0, 16 * 1024);
        if (System.Numerics.Vector.IsHardwareAccelerated)
            Assert.True(Percentile(optimizedMs, 0.50) < Percentile(scalarMs, 0.50),
                $"Managed low-latency median regressed: scalar={Percentile(scalarMs, 0.50):0.###}ms optimized={Percentile(optimizedMs, 0.50):0.###}ms.");
        Console.WriteLine(
            $"LOW LATENCY SOFTWARE {format.FrameRate} {(dissolve ? "DISSOLVE" : "CUT")} samples={samples} " +
            $"scalar-p50={Percentile(scalarMs, 0.50):0.###}ms scalar-p95={Percentile(scalarMs, 0.95):0.###}ms scalar-p99={Percentile(scalarMs, 0.99):0.###}ms " +
            $"optimized-p50={Percentile(optimizedMs, 0.50):0.###}ms optimized-p95={Percentile(optimizedMs, 0.95):0.###}ms optimized-p99={Percentile(optimizedMs, 0.99):0.###}ms " +
            $"maxAllocated={allocations.Max()} passes={backend.CompositeCalls - passes} vector={System.Numerics.Vector.IsHardwareAccelerated}; " +
            "resident inputs + composite + one readback; excludes upload, output devices and physical GPU timing.");
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Managed_1080p_matrix_reports_warmup_steady_state_and_full_readback(bool use5994, bool dissolve)
    {
        var format = use5994 ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
        using var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend, readbackBufferCapacity: 1);
        provider.Start();
        var timing = new FrameTiming(0, 0, new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator));
        using var a = new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, 16, 32, 64)).Materialize(provider, timing);
        using var b = new StaticRgbaSource(MediaSourceId.New(), RgbaFrameBuffer.Solid(format, 192, 128, 64)).Materialize(provider, timing);
        var frames = new List<GpuFrame>();
        var reports = new List<object>();
        try
        {
            for (var index = 0; index < 8; index++)
                frames.Add(new StaticRgbaSource(MediaSourceId.New(),
                    RgbaFrameBuffer.Solid(format, (byte)(32 + index * 19), (byte)(220 - index * 17), (byte)(64 + index * 11), (byte)(64 + index * 20))).Materialize(provider, timing));
            foreach (var layerCount in new[] { 0, 1, 2, 4, 8 })
            {
                var request = GpuCompositeRequest.WithLayers(MediaSourceId.New(), a, b,
                    dissolve ? GpuTransition.Dissolve(128) : GpuTransition.CutToA,
                    frames.Take(layerCount).Select((frame, index) => new GpuKeyLayer(frame, (byte)(255 - index * 9), true)));
                void Boundary()
                {
                    var result = provider.Composite(request);
                    Assert.True(result.Succeeded, result.Failure?.ToString());
                    using var output = result.Frame!;
                    using var readback = provider.RentReadback(output);
                    Assert.Equal(RgbaFrameBuffer.RequiredByteLength(format), readback.Memory.Length);
                }
                const int warmup = 3;
                const int count = 12;
                for (var index = 0; index < warmup; index++) Boundary();
                var passes = backend.CompositeCalls;
                var samples = new double[count];
                for (var index = 0; index < count; index++)
                {
                    var started = Stopwatch.GetTimestamp();
                    Boundary();
                    samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                var expectedPasses = layerCount == 0 ? (dissolve ? 1 : 0) : layerCount;
                Assert.Equal(expectedPasses * count, backend.CompositeCalls - passes);
                Assert.Equal(10, provider.ActiveSurfaceCount);
                Assert.Equal(0, provider.ReadbackPoolStatistics.ActiveBuffers);
                Array.Sort(samples);
                var report = new
                {
                    format = use5994 ? "1080p59.94" : "1080p50",
                    transition = dissolve ? "DISSOLVE" : "CUT", layerCount,
                    warmupBoundaries = warmup, samples = count, measurementKind = "CPU_WALL",
                    scope = "RESIDENT_INPUTS_COMPOSITE_AND_ONE_READBACK",
                    p50Milliseconds = Percentile(samples, .50), p95Milliseconds = Percentile(samples, .95),
                    p99Milliseconds = Percentile(samples, .99), maximumMilliseconds = samples[^1],
                    passes = backend.CompositeCalls - passes,
                    provider.MemoryTransferStatistics
                };
                reports.Add(report);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
            }
            ProgramPipelineEvidence.Write($"compositor-{use5994}-{dissolve}", reports);
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static void ScalarBlendBaseline(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> output, byte weight)
    {
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            output[offset] = (byte)((a[offset] * (255 - weight) + b[offset] * weight + 127) / 255);
            output[offset + 1] = (byte)((a[offset + 1] * (255 - weight) + b[offset + 1] * weight + 127) / 255);
            output[offset + 2] = (byte)((a[offset + 2] * (255 - weight) + b[offset + 2] * weight + 127) / 255);
            output[offset + 3] = (byte)((a[offset + 3] * (255 - weight) + b[offset + 3] * weight + 127) / 255);
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
