// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text.Json;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Performance;

public sealed class OutputMappingSoftwarePerformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bounded_software_warp_reports_CPU_P50_P95_P99_and_managed_allocations(bool use5994)
    {
        var rate = use5994 ? FrameRate.Fps59_94 : FrameRate.Fps50;
        var format = new VideoFormat(96, 54, rate, PixelFormat.Rgba8, ScanMode.Progressive);
        var firstId = new SurfaceId(Identity.New());
        var secondId = new SurfaceId(Identity.New());
        var pixelsA = new byte[RgbaFrameBuffer.RequiredByteLength(format)];
        var pixelsB = new byte[pixelsA.Length];
        var target = new byte[pixelsA.Length];
        for (var offset = 0; offset < pixelsA.Length; offset += 4)
        {
            pixelsA[offset] = 48;
            pixelsA[offset + 1] = 120;
            pixelsA[offset + 3] = 255;
            pixelsB[offset] = 196;
            pixelsB[offset + 1] = 50;
            pixelsB[offset + 2] = 160;
            pixelsB[offset + 3] = 160;
        }
        GpuOutputMappingSurface[] layers =
        [
            new(firstId, format, 0, 0, 1, 1,
                new(0, 0), new(96, 0), new(96, 54), new(0, 54),
                GpuOutputEdgeBlend.None, 1),
            new(secondId, format, .125, .125, .75, .75,
                new(16, 4), new(92, 9), new(80, 50), new(9, 48),
                new GpuOutputEdgeBlend(.1, .08, .15, .08), 2)
        ];
        Func<SurfaceId, ReadOnlyMemory<byte>> resolve = id =>
            id == firstId ? pixelsA : id == secondId ? pixelsB :
            throw new InvalidOperationException("Unknown synthetic mapping surface.");

        const int warmup = 12, samples = 96;
        for (var index = 0; index < warmup; index++)
            GpuOutputMappingRaster.Render(format, target, layers, resolve, opaqueProgramSink: true);

        var callerThread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var processBefore = GC.GetTotalAllocatedBytes(precise: false);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        var elapsed = new double[samples];
        for (var index = 0; index < samples; index++)
        {
            var start = Stopwatch.GetTimestamp();
            GpuOutputMappingRaster.Render(format, target, layers, resolve, opaqueProgramSink: true);
            elapsed[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        var threadBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var processBytes = GC.GetTotalAllocatedBytes(precise: false) - processBefore;
        var gc = new
        {
            gen0 = GC.CollectionCount(0) - gen0,
            gen1 = GC.CollectionCount(1) - gen1,
            gen2 = GC.CollectionCount(2) - gen2,
            pauseMilliseconds = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds
        };
        Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
        Assert.InRange(threadBytes, 0, 16 * 1024);
        Assert.Equal((byte)255, target[3]);
        Array.Sort(elapsed);
        static double P(double[] observations, double rank) =>
            observations[Math.Clamp((int)Math.Ceiling(observations.Length * rank) - 1, 0, observations.Length - 1)];

        Console.WriteLine("OUTPUT MAPPING SOFTWARE QUALIFICATION: " + JsonSerializer.Serialize(new
        {
            scope = "BOUNDED_MANAGED_SOFTWARE_RASTER_NOT_CUDA_OR_PHYSICAL_SCANOUT",
            sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT") ?? "UNAVAILABLE",
            format = use5994 ? "96x54@59.94" : "96x54@50",
            activeSurfaces = layers.Length, warmup, samples,
            p50Milliseconds = P(elapsed, .5), p95Milliseconds = P(elapsed, .95),
            p99Milliseconds = P(elapsed, .99), maximumMilliseconds = elapsed[^1],
            allocation = new
            {
                measuredThreadBytes = threadBytes,
                measuredThreadBytesPerFrame = threadBytes / (double)samples,
                approximateProcessBytes = processBytes,
                approximateProcessBytesPerFrame = processBytes / (double)samples
            },
            garbageCollection = gc,
            actualDeviceTransfers = "UNVERIFIED", nativeCudaMapping = "UNSUPPORTED",
            physicalScanout = "UNVERIFIED"
        }));
    }
}
