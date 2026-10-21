// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class OutputMappingGpuTests
{
    private static readonly VideoFormat Small = Format(2, 2);

    private static VideoFormat Format(uint width, uint height) =>
        new(width, height, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static readonly SurfaceId SurfaceA =
        new(Identity.Parse("11000000-0000-0000-0000-000000000001"));
    private static readonly SurfaceId SurfaceB =
        new(Identity.Parse("11000000-0000-0000-0000-000000000002"));
    private static readonly MediaSourceId Program =
        new(Identity.Parse("11000000-0000-0000-0000-000000000003"));

    private static GpuOutputMappingSurface Quad(
        SurfaceId id, VideoFormat source, int width, int height,
        GpuOutputEdgeBlend? edge = null,
        int z = 0) =>
        new(id, source, 0, 0, 1, 1,
            new(0, 0), new(width, 0), new(width, height), new(0, height),
            edge ?? GpuOutputEdgeBlend.None, z);

    [Fact]
    public void Identity_samples_exact_original_centres_without_resampling_drift()
    {
        byte[] input = [255, 0, 0, 255, 0, 255, 0, 255,
                        0, 0, 255, 255, 255, 255, 255, 255];
        var destination = new byte[input.Length];
        GpuOutputMappingRaster.Render(
            Small, destination, [Quad(SurfaceA, Small, 2, 2)],
            id => id == SurfaceA ? input : throw new InvalidOperationException());
        Assert.Equal(input, destination);
    }

    [Fact]
    public void Normalized_crop_samples_only_the_requested_source_columns()
    {
        var sourceFormat = Format(4, 1);
        var sinkFormat = Format(2, 1);
        byte[] source = [255, 0, 0, 255, 0, 255, 0, 255,
                         0, 0, 255, 255, 255, 255, 255, 255];
        var geometry = Quad(SurfaceA, sourceFormat, 2, 1) with
        {
            CropX = 0.25, CropWidth = 0.5
        };
        var target = new byte[8];
        GpuOutputMappingRaster.Render(sinkFormat, target, [geometry], _ => source);
        Assert.Equal(new byte[] { 0, 255, 0, 255, 0, 0, 255, 255 }, target);
    }

    [Fact]
    public void Warped_quad_preserves_outside_transparent_black_and_inside_source()
    {
        var targetFormat = Format(4, 4);
        byte[] source = Enumerable.Range(0, 4)
            .SelectMany(_ => new byte[] { 80, 120, 160, 255 }).ToArray();
        var spec = Quad(SurfaceA, Small, 4, 4) with
        {
            TopLeft = new(1, 0),
            TopRight = new(3, 0),
            BottomRight = new(4, 4),
            BottomLeft = new(0, 4)
        };
        var target = new byte[4 * 4 * 4];
        GpuOutputMappingRaster.Render(targetFormat, target, [spec], _ => source);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, target.Take(4).ToArray());
        var centre = ((2 * 4 + 1) * 4);
        Assert.Equal(new byte[] { 80, 120, 160, 255 }, target.Skip(centre).Take(4).ToArray());
    }

    [Fact]
    public void Alpha_over_and_edge_blend_have_bounded_encoded_rgba_semantics()
    {
        var red = Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 255, 0, 0, 255 }).ToArray();
        var green = Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 0, 255, 0, 128 }).ToArray();
        var target = new byte[16];
        var front = Quad(SurfaceB, Small, 2, 2, z: 1);
        var back = Quad(SurfaceA, Small, 2, 2);
        GpuOutputMappingRaster.Render(Small, target, [front, back],
            id => id == SurfaceA ? red : green);
        Assert.Equal((byte)255, target[3]);
        Assert.InRange(target[0], 126, 128);
        Assert.InRange(target[1], 127, 129);
        Assert.Equal((byte)0, target[2]);

        var feather = new byte[16];
        var halfWidth = new GpuOutputEdgeBlend(0.5, 0, 0, 0);
        GpuOutputMappingRaster.Render(Small, feather,
            [Quad(SurfaceB, Small, 2, 2, halfWidth)], _ => green);
        Assert.InRange(feather[3], 63, 65);
        Assert.Equal((byte)0, feather[0]);
    }

    [Fact]
    public void Invalid_or_overbudget_mapping_fails_before_mutating_sink()
    {
        byte[] pixels = Enumerable.Repeat((byte)19, 16).ToArray();
        var invalid = Quad(SurfaceA, Small, 2, 2) with { BottomRight = new(0, 0) };
        Assert.Throws<ArgumentException>(() =>
            GpuOutputMappingRaster.Render(Small, pixels, [invalid], _ => new byte[16]));
        Assert.All(pixels, value => Assert.Equal((byte)19, value));
        Assert.Throws<ArgumentException>(() =>
            GpuOutputMappingRaster.Render(Small, pixels,
                Enumerable.Repeat(Quad(SurfaceA, Small, 2, 2), 9).ToArray(), _ => new byte[16]));
    }

    [Fact]
    public void Mapped_gpu_frame_releases_allocation_after_last_lease()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        var timing = new FrameTiming(0, 0, new Timebase(1, 50));
        var source = new MediaSourceId(Identity.Parse("11000000-0000-0000-0000-000000000004"));
        using var uploaded = provider.Upload(
            source, RgbaFrameBuffer.Solid(Small, 70, 90, 110), timing, Generation.Initial, "mapping-test");
        Assert.True(provider.SupportsOutputMapping);
        var input = new GpuOutputMappingInput(uploaded,
            Quad(uploaded.SurfaceId, Small, 2, 2));
        var mapped = provider.MapOutput(uploaded, [input]);
        Assert.True(mapped.Succeeded, mapped.Failure?.Message);
        using (var frame = mapped.Frame!)
        {
            Assert.Equal(2, provider.ActiveSurfaceCount);
            Assert.Equal(new byte[] { 70, 90, 110, 255 },
                provider.Readback(frame).Take(4).ToArray());
        }
        Assert.Equal(1, provider.ActiveSurfaceCount);
        var feathered = provider.MapOutput(uploaded,
            [new GpuOutputMappingInput(uploaded,
                Quad(uploaded.SurfaceId, Small, 2, 2,
                    new GpuOutputEdgeBlend(0.5, 0, 0, 0)))]);
        Assert.True(feathered.Succeeded, feathered.Failure?.Message);
        using (var featheredFrame = feathered.Frame!)
        {
            var submittedPixels = provider.Readback(featheredFrame);
            Assert.Equal((byte)35, submittedPixels[0]);
            Assert.Equal((byte)45, submittedPixels[1]);
            Assert.Equal((byte)55, submittedPixels[2]);
            Assert.Equal((byte)255, submittedPixels[3]);
        }
        uploaded.Dispose();
        Assert.Equal(0, provider.ActiveSurfaceCount);
        Assert.Equal(0, backend.ActiveAllocationCount);
    }
    [Fact]
    public void Canonical_surface_identity_order_agrees_with_existing_guid_string_order()
    {
        var random = new Random(29817);
        var identities = new Guid[1024];
        for (var index = 0; index < identities.Length; index++)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            identities[index] = new Guid(bytes);
        }

        var reference = identities.OrderBy(identity => identity.ToString("N"), StringComparer.Ordinal).ToArray();
        var withoutStrings = identities.OrderBy(identity => identity).ToArray();
        Assert.Equal(reference, withoutStrings);
    }

    [Fact]
    public void Overlapping_solid_surfaces_preserve_stable_z_and_identity_order()
    {
        var ids = new[]
        {
            Guid.Parse("81000000-0000-0000-0000-000000000001"),
            Guid.Parse("01000000-0000-0000-0000-000000000002"),
            Guid.Parse("ff000000-0000-0000-0000-000000000003"),
            Guid.Parse("40000000-0000-0000-0000-000000000004")
        };
        var surfaces = new GpuOutputMappingSurface[ids.Length];
        var inputs = new Dictionary<SurfaceId, ReadOnlyMemory<byte>>();
        var layers = new byte[] { 22, 44, 66, 88 };
        for (var index = 0; index < ids.Length; index++)
        {
            var id = new SurfaceId(Identity.New());
            surfaces[index] = Quad(id, Small, 2, 2, z: index == 0 ? -2 : 3) with
            {
                SurfaceIdentity = ids[index]
            };
            var rgba = new byte[16];
            for (var offset = 0; offset < rgba.Length; offset += 4)
            {
                rgba[offset] = layers[index];
                rgba[offset + 3] = 255;
            }
            inputs.Add(id, rgba);
        }

        var target = new byte[16];
        GpuOutputMappingRaster.Render(Small, target, surfaces, id => inputs[id],
            opaqueProgramSink: true);
        // The max z=3 layer with the max canonical Guid is on top.
        Assert.Equal(new byte[] { 66, 0, 0, 255 }, target.AsSpan(0, 4).ToArray());
        Assert.All(target.Where((_, index) => index % 4 == 3), alpha => Assert.Equal((byte)255, alpha));
    }

    [Fact]
    public void Invalid_later_source_and_degenerate_later_quad_keep_previous_sink_untouched()
    {
        var pixels = Enumerable.Repeat((byte)117, 16).ToArray();
        var good = Quad(SurfaceA, Small, 2, 2, z: 0);
        var later = Quad(SurfaceB, Small, 2, 2, z: 1);
        Assert.Throws<ArgumentException>(() =>
            GpuOutputMappingRaster.Render(Small, pixels, [good, later],
                id => id == SurfaceA ? new byte[16] : new byte[12]));
        Assert.All(pixels, value => Assert.Equal((byte)117, value));

        var invalid = later with { BottomRight = new(0, 0) };
        Assert.Throws<ArgumentException>(() =>
            GpuOutputMappingRaster.Render(Small, pixels, [good, invalid], _ => new byte[16]));
        Assert.All(pixels, value => Assert.Equal((byte)117, value));
    }

    [Fact]
    public void Warmed_mapping_preparation_and_raster_do_not_allocate_per_frame()
    {
        var format = Format(32, 18);
        var input = new byte[RgbaFrameBuffer.RequiredByteLength(format)];
        for (var offset = 0; offset < input.Length; offset += 4)
        {
            input[offset] = 180;
            input[offset + 1] = 24;
            input[offset + 2] = 90;
            input[offset + 3] = 192;
        }
        var surface = Quad(SurfaceA, format, 32, 18,
            new GpuOutputEdgeBlend(.12, .08, .15, .1), z: 1);
        GpuOutputMappingSurface[] geometry = [surface, surface with
        {
            ZOrder = 2,
            SurfaceIdentity = Guid.Parse("9a4efed1-0000-0000-0000-000000000000")
        }];
        var destination = new byte[input.Length];
        Func<SurfaceId, ReadOnlyMemory<byte>> resolve = _ => input;

        for (var i = 0; i < 32; i++)
            GpuOutputMappingRaster.Render(format, destination, geometry, resolve, opaqueProgramSink: true);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 256; i++)
            GpuOutputMappingRaster.Render(format, destination, geometry, resolve, opaqueProgramSink: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 4_096);
        Assert.Equal((byte)255, destination[3]);
        Assert.InRange(destination[0], (byte)0, (byte)180);
    }

    [Fact]
    public void Stale_mapping_sequence_is_rejected_before_allocating_or_reading_a_mapped_output()
    {
        var backend = new ManagedReferenceGpuBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        var source = MediaSourceId.New();
        var older = new FrameTiming(7, 7, new Timebase(1, 50));
        var current = new FrameTiming(8, 8, new Timebase(1, 50));
        using var input = provider.Upload(source,
            RgbaFrameBuffer.Solid(Small, 71, 82, 93), older, Generation.Initial, "old-mapping");
        using var program = provider.Upload(source,
            RgbaFrameBuffer.Solid(Small, 22, 33, 44), current, Generation.Initial, "program-mapping");
        var priorReadbacks = provider.MemoryTransferStatistics.ReadbackOperations;
        var result = provider.MapOutput(program,
            [new GpuOutputMappingInput(input, Quad(input.SurfaceId, Small, 2, 2))]);

        Assert.False(result.Succeeded);
        Assert.Equal("gpu.output_mapping.sequence_stale", result.Failure?.Code);
        Assert.Equal(2, provider.ActiveSurfaceCount);
        Assert.Equal(2, backend.ActiveAllocationCount);
        Assert.Equal(priorReadbacks, provider.MemoryTransferStatistics.ReadbackOperations);
    }

    [Fact]
    public void CUDA_does_not_advertise_output_mapping_without_an_implemented_mapping_backend()
    {
        using var backend = new CudaGpuProcessingBackend();
        using var provider = new GpuProcessingProvider(backend);
        Assert.False(provider.SupportsOutputMapping);
    }

}
