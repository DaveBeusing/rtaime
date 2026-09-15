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
}
