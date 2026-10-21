// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class OutputMappingSurfaceOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mapped_output_releases_surface_but_retains_one_shared_host_readback_for_consumers(bool use5994)
    {
        var format = Format(use5994);
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend, readbackBufferCapacity: 1);
        provider.Start();
        var source = MediaSourceId.New();
        var timing = new FrameTiming(17, 17, new Timebase(
            use5994 ? 1001u : 1u, use5994 ? 60000u : 50u));
        var rgba = RgbaFrameBuffer.Solid(format, 91, 37, 145);
        var input = provider.Upload(source, rgba, timing, Generation.Initial, "output-lease");

        var mapped = provider.MapOutput(input,
            [new GpuOutputMappingInput(input, Mapping(input.SurfaceId, format))]);
        Assert.True(mapped.Succeeded, mapped.Failure?.Message);
        var mappedFrame = mapped.Frame!;
        var monitoring = provider.RentReadback(mappedFrame);
        var recording = monitoring.Retain();
        Assert.Equal(2, provider.ActiveSurfaceCount);
        Assert.Equal(2, backend.ActiveAllocationCount);

        mappedFrame.Dispose();
        input.Dispose();
        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.Equal(0, backend.ActiveAllocationCount);
        Assert.Equal(1, provider.ReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal((byte)91, recording.Memory.Span[0]);
        Assert.Equal((byte)37, monitoring.Memory.Span[1]);
        Assert.Equal((ulong)0, provider.MemoryTransferStatistics.DeviceToHostBytes);

        monitoring.Dispose();
        Assert.Equal(1, provider.ReadbackPoolStatistics.ActiveBuffers);
        provider.Stop();
        Assert.Equal((byte)145, recording.Memory.Span[2]);
        recording.Dispose();
        Assert.Equal(0, provider.ReadbackPoolStatistics.ActiveBuffers);
        Assert.Equal(0, provider.UnreleasedBackendSurfaceCount);
    }

    [Fact]
    public void Failed_mapping_with_failed_immediate_release_is_tracked_until_stop_retry()
    {
        var format = Format(use5994: false);
        var backend = new FailingOutputMappingBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        var timing = new FrameTiming(1, 1, new Timebase(1, 50));
        using var input = provider.Upload(MediaSourceId.New(),
            RgbaFrameBuffer.Solid(format, 22, 60, 98), timing,
            Generation.Initial, "mapping-release");
        var previousReadback = provider.MemoryTransferStatistics.ReadbackOperations;

        var result = provider.MapOutput(input,
            [new GpuOutputMappingInput(input, Mapping(input.SurfaceId, format))]);

        Assert.False(result.Succeeded);
        Assert.Equal("gpu.output_mapping.render_failed", result.Failure?.Code);
        Assert.Equal(GpuProviderState.Degraded, provider.State);
        Assert.Equal(1, provider.UnreleasedBackendSurfaceCount);
        Assert.Equal(2, backend.ActiveAllocationCount);
        Assert.Equal(previousReadback, provider.MemoryTransferStatistics.ReadbackOperations);
        using (var original = provider.RentReadback(input))
            Assert.Equal((byte)22, original.Memory.Span[0]);

        input.Dispose();
        provider.Stop();
        Assert.Equal(0, backend.ActiveAllocationCount);
        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.Equal(0, provider.UnreleasedBackendSurfaceCount);
        Assert.Equal(0, provider.ReadbackPoolStatistics.ActiveBuffers);
    }

    [Fact]
    public void Unqualified_hardware_mapping_interface_cannot_advertise_or_render_GPU_warp()
    {
        var backend = new UnqualifiedHardwareMappingBackend();
        using var provider = new GpuProcessingProvider(backend);
        Assert.False(provider.SupportsOutputMapping);
        provider.Start();
        var format = Format(use5994: false);
        using var source = provider.Upload(MediaSourceId.New(),
            RgbaFrameBuffer.Solid(format, 7, 8, 9),
            new FrameTiming(1, 1, new Timebase(1, 50)),
            Generation.Initial, "hardware-capability-guard");
        var result = provider.MapOutput(source,
            [new GpuOutputMappingInput(source, Mapping(source.SurfaceId, format))]);

        Assert.False(result.Succeeded);
        Assert.Equal("gpu.output_mapping.unsupported", result.Failure?.Code);
        Assert.Equal(0, backend.MappingCalls);
        Assert.Equal(1, provider.ActiveSurfaceCount);
        Assert.Equal(1, backend.ActiveAllocationCount);
    }

    [Fact]
    public void Oversized_mapping_source_is_rejected_before_allocating_an_extra_output_surface()
    {
        // Source ownership is independent of the mapping-output budget.
        // 2048x2049 RGBA8 is only one scanline above the 16 MiB limit.
        var format = new VideoFormat(2048, 2049, FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive);
        Assert.True((ulong)format.Width * format.Height * 4UL > 16UL * 1024 * 1024);
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        var frame = provider.Upload(MediaSourceId.New(),
            new RgbaFrameBuffer(format, new byte[RgbaFrameBuffer.RequiredByteLength(format)]),
            new FrameTiming(0, 0, new Timebase(1, 50)),
            Generation.Initial, "oversized-mapping-budget");
        try
        {
            var mapping = new GpuOutputMappingSurface(
                frame.SurfaceId, format, 0, 0, 1, 1,
                new(0, 0), new(2048, 0), new(2048, 2049), new(0, 2049),
                GpuOutputEdgeBlend.None, 0);
            Assert.Equal(1, backend.ActiveAllocationCount);
            var result = provider.MapOutput(frame, [new GpuOutputMappingInput(frame, mapping)]);
            Assert.False(result.Succeeded);
            Assert.Equal("gpu.output_mapping.capacity", result.Failure?.Code);
            Assert.Equal(1, backend.ActiveAllocationCount);
            Assert.Equal(1, provider.ActiveSurfaceCount);
            Assert.Equal((ulong)0, provider.MemoryTransferStatistics.ReadbackOperations);
        }
        finally
        {
            frame.Dispose();
        }
        Assert.Equal(0, backend.ActiveAllocationCount);
    }

    private static VideoFormat Format(bool use5994) =>
        new(2, 2, use5994 ? FrameRate.Fps59_94 : FrameRate.Fps50,
            PixelFormat.Rgba8, ScanMode.Progressive);

    private static GpuOutputMappingSurface Mapping(SurfaceId surfaceId, VideoFormat format) =>
        new(surfaceId, format, 0, 0, 1, 1,
            new(0, 0), new(2, 0), new(2, 2), new(0, 2),
            GpuOutputEdgeBlend.None, 0);

    private sealed class UnqualifiedHardwareMappingBackend : IGpuProcessingBackend, IGpuOutputMappingBackend
    {
        private readonly ManagedReferenceGpuBackend _inner = new();

        public GpuBackendInfo Info { get; } = new(
            GpuBackendKind.NvidiaCuda, "Unqualified mapping test adapter",
            hardwareAccelerated: true, available: true);
        public SurfaceStorageDomain StorageDomain => _inner.StorageDomain;
        public int MappingCalls { get; private set; }
        public int ActiveAllocationCount => _inner.ActiveAllocationCount;
        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public void Allocate(SurfaceId id, VideoFormat format, ReadOnlySpan<byte> pixels) =>
            _inner.Allocate(id, format, pixels);
        public void Composite(SurfaceId id, VideoFormat format, GpuCompositeOperation operation) =>
            _inner.Composite(id, format, operation);
        public byte[] Readback(SurfaceId id, VideoFormat format) => _inner.Readback(id, format);
        public void ReadbackInto(SurfaceId id, VideoFormat format, Span<byte> destination) =>
            _inner.ReadbackInto(id, format, destination);
        public void Release(SurfaceId id) => _inner.Release(id);
        public void RenderOutputMapping(SurfaceId id, VideoFormat format,
            IReadOnlyList<GpuOutputMappingSurface> surfaces)
        {
            MappingCalls++;
            throw new InvalidOperationException("Unqualified hardware mapping must not execute.");
        }
        public void Dispose() => _inner.Dispose();
    }

    private sealed class FailingOutputMappingBackend : IGpuProcessingBackend, IGpuOutputMappingBackend
    {
        private readonly ManagedReferenceGpuBackend _inner = new();
        private SurfaceId? _failedMappedOutput;
        private bool _failReleaseOnce = true;

        public int ActiveAllocationCount => _inner.ActiveAllocationCount;
        public GpuBackendInfo Info => _inner.Info;
        public SurfaceStorageDomain StorageDomain => _inner.StorageDomain;
        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> pixels) =>
            _inner.Allocate(surfaceId, format, pixels);
        public void Composite(SurfaceId surfaceId, VideoFormat format, GpuCompositeOperation operation) =>
            _inner.Composite(surfaceId, format, operation);
        public byte[] Readback(SurfaceId surfaceId, VideoFormat format) =>
            _inner.Readback(surfaceId, format);
        public void ReadbackInto(SurfaceId surfaceId, VideoFormat format, Span<byte> destination) =>
            _inner.ReadbackInto(surfaceId, format, destination);

        public void RenderOutputMapping(SurfaceId outputSurfaceId, VideoFormat format,
            IReadOnlyList<GpuOutputMappingSurface> surfaces)
        {
            _failedMappedOutput = outputSurfaceId;
            _inner.Allocate(outputSurfaceId, format,
                new byte[RgbaFrameBuffer.RequiredByteLength(format)]);
            throw new InvalidOperationException("Injected failure after output surface allocation.");
        }

        public void Release(SurfaceId surfaceId)
        {
            if (_failedMappedOutput == surfaceId && _failReleaseOnce)
            {
                _failReleaseOnce = false;
                throw new InvalidOperationException("Injected transient native release failure.");
            }
            _inner.Release(surfaceId);
        }

        public void Dispose() => _inner.Dispose();
    }
}
