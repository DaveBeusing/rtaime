using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class GpuReadbackOwnershipConcurrencyTests
{
    private static readonly VideoFormat Format =
        new(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static readonly MediaSourceId Source =
        new(Identity.Parse("9e000000-0000-0000-0000-000000000001"));

    [Fact]
    public void Multiple_consumers_prevent_early_buffer_reuse()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend(), readbackBufferCapacity: 1);
        provider.Start();
        using var frame = provider.Upload(
            Source,
            RgbaFrameBuffer.Solid(Format, 21, 42, 63),
            new FrameTiming(0, 0, new Timebase(1, 50)),
            Generation.Initial,
            "ownership-test");

        var primary = provider.RentReadback(frame);
        using var monitoring = primary.Retain();
        using var recording = primary.Retain();
        primary.Dispose();

        Assert.Equal(1, provider.ReadbackPoolStatistics.ActiveBuffers);
        Assert.Throws<InvalidOperationException>(() => provider.RentReadback(frame));

        monitoring.Dispose();
        Assert.Equal(1, provider.ReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal((byte)21, recording.Memory.Span[0]);

        recording.Dispose();
        Assert.Equal(0, provider.ReadbackPoolStatistics.ActiveBuffers);

        using var reused = provider.RentReadback(frame);
        Assert.Equal((byte)21, reused.Memory.Span[0]);
        Assert.Equal(1, provider.ReadbackPoolStatistics.AllocatedBuffers);
    }

    [Fact]
    public void Concurrent_disposal_of_retained_readers_releases_buffer_once()
    {
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend(), readbackBufferCapacity: 1);
        provider.Start();
        using var frame = provider.Upload(
            Source,
            RgbaFrameBuffer.Solid(Format, 7, 8, 9),
            new FrameTiming(0, 0, new Timebase(1, 50)),
            Generation.Initial,
            "ownership-test");

        var primary = provider.RentReadback(frame);
        var readers = Enumerable.Range(0, 32).Select(_ => primary.Retain()).ToArray();
        primary.Dispose();

        Parallel.ForEach(readers, reader =>
        {
            reader.Dispose();
            reader.Dispose();
        });

        Assert.Equal(0, provider.ReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal(1, provider.ReadbackPoolStatistics.AvailableBuffers);
        Assert.Equal(1, provider.ReadbackPoolStatistics.AllocatedBuffers);
        Assert.All(readers, reader => Assert.True(reader.IsDisposed));
    }
}
