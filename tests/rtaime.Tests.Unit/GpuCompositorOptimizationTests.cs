using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;

namespace rtaime.Tests.Unit;

public sealed class GpuCompositorOptimizationTests
{
    private static readonly VideoFormat OddFormat =
        new(3, 3, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);

    private static readonly MediaSourceId OutputSource =
        new(Identity.Parse("a1000000-0000-0000-0000-000000000001"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Ordered_layer_matrix_matches_independent_integer_reference(int layerCount)
    {
        var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var timing = Timing(7);
        var backgroundAContent = Pattern(11);
        var backgroundBContent = Pattern(73);
        using var backgroundA = new StaticRgbaSource(
            new MediaSourceId(Identity.Parse("a1000000-0000-0000-0000-000000000010")),
            backgroundAContent).Materialize(provider, timing);
        using var backgroundB = new StaticRgbaSource(
            new MediaSourceId(Identity.Parse("a1000000-0000-0000-0000-000000000011")),
            backgroundBContent).Materialize(provider, timing);

        var layerContents = new List<RgbaFrameBuffer>();
        var layerFrames = new List<GpuFrame>();
        var layerDescriptors = new List<(RgbaFrameBuffer Content, byte Opacity, bool Visible)>();
        try
        {
            for (var index = 0; index < layerCount; index++)
            {
                var content = Pattern(checked((byte)(101 + index * 13)));
                layerContents.Add(content);
                var frame = new StaticRgbaSource(
                    MediaSourceId.New(),
                    content).Materialize(provider, timing);
                layerFrames.Add(frame);

                var opacity = checked((byte)(255 - index * 19));
                layerDescriptors.Add((content, opacity, true));
            }

            var request = GpuCompositeRequest.WithLayers(
                OutputSource,
                backgroundA,
                backgroundB,
                GpuTransition.Dissolve(137),
                layerFrames.Select((frame, index) =>
                    new GpuKeyLayer(frame, layerDescriptors[index].Opacity, visible: true)));

            var result = provider.Composite(request);
            Assert.True(result.Succeeded, result.Failure?.ToString());
            Assert.Equal(layerCount, result.LayerCount);

            using var output = result.Frame!;
            var actual = provider.Readback(output);
            var expected = CompositeReference(
                backgroundAContent.Pixels.Span,
                backgroundBContent.Pixels.Span,
                GpuTransition.Dissolve(137).BlendWeight,
                layerDescriptors);

            Assert.Equal(expected, actual);
            Assert.Equal(Math.Max(1, layerCount), backend.CompositeCalls);
        }
        finally
        {
            foreach (var frame in layerFrames)
                frame.Dispose();
        }
    }

    [Fact]
    public void Hidden_and_zero_opacity_layers_skip_backend_passes_with_exact_pixels()
    {
        var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var timing = Timing(3);
        var backgroundAContent = Pattern(17);
        var backgroundBContent = Pattern(91);
        using var backgroundA = new StaticRgbaSource(MediaSourceId.New(), backgroundAContent)
            .Materialize(provider, timing);
        using var backgroundB = new StaticRgbaSource(MediaSourceId.New(), backgroundBContent)
            .Materialize(provider, timing);

        var contents = new[] { Pattern(31), Pattern(47), Pattern(63), Pattern(79) };
        var frames = contents
            .Select(content => new StaticRgbaSource(MediaSourceId.New(), content).Materialize(provider, timing))
            .ToArray();

        try
        {
            var layers = new[]
            {
                new GpuKeyLayer(frames[0], byte.MaxValue, visible: false),
                new GpuKeyLayer(frames[1], 0, visible: true),
                new GpuKeyLayer(frames[2], byte.MaxValue, visible: true),
                new GpuKeyLayer(frames[3], 0, visible: false)
            };
            var result = provider.Composite(GpuCompositeRequest.WithLayers(
                OutputSource,
                backgroundA,
                backgroundB,
                GpuTransition.Dissolve(99),
                layers));

            Assert.True(result.Succeeded, result.Failure?.ToString());
            Assert.Equal(4, result.LayerCount);
            Assert.Equal(1, backend.CompositeCalls);

            using var output = result.Frame!;
            var actual = provider.Readback(output);
            var expected = CompositeReference(
                backgroundAContent.Pixels.Span,
                backgroundBContent.Pixels.Span,
                99,
                new[]
                {
                    (contents[0], byte.MaxValue, false),
                    (contents[1], (byte)0, true),
                    (contents[2], byte.MaxValue, true),
                    (contents[3], (byte)0, false)
                });

            Assert.Equal(expected, actual);
            Assert.Contains(
                provider.Observations,
                observation => observation.Code == "gpu.composite.passes:1:skipped:3");
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    [Fact]
    public void Transparent_boundaries_and_opacity_extremes_are_byte_exact()
    {
        var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();

        var timing = Timing(1);
        var a = new RgbaFrameBuffer(
            OddFormat,
            RepeatPixels(
                (10, 20, 30, 0),
                (40, 50, 60, 255),
                (70, 80, 90, 128)));
        var b = new RgbaFrameBuffer(
            OddFormat,
            RepeatPixels(
                (200, 190, 180, 255),
                (170, 160, 150, 0),
                (140, 130, 120, 255)));
        var transparent = new RgbaFrameBuffer(
            OddFormat,
            RepeatPixels(
                (255, 0, 0, 0),
                (0, 255, 0, 0),
                (0, 0, 255, 0)));
        var opaque = new RgbaFrameBuffer(
            OddFormat,
            RepeatPixels(
                (255, 0, 0, 255),
                (0, 255, 0, 255),
                (0, 0, 255, 255)));

        using var frameA = new StaticRgbaSource(MediaSourceId.New(), a).Materialize(provider, timing);
        using var frameB = new StaticRgbaSource(MediaSourceId.New(), b).Materialize(provider, timing);
        using var transparentFrame = new StaticRgbaSource(MediaSourceId.New(), transparent).Materialize(provider, timing);
        using var zeroOpacityFrame = new StaticRgbaSource(MediaSourceId.New(), opaque).Materialize(provider, timing);
        using var opaqueFrame = new StaticRgbaSource(MediaSourceId.New(), opaque).Materialize(provider, timing);

        var layers = new[]
        {
            new GpuKeyLayer(transparentFrame, byte.MaxValue, visible: true),
            new GpuKeyLayer(zeroOpacityFrame, 0, visible: true),
            new GpuKeyLayer(opaqueFrame, byte.MaxValue, visible: true)
        };

        var result = provider.Composite(GpuCompositeRequest.WithLayers(
            OutputSource,
            frameA,
            frameB,
            GpuTransition.Dissolve(128),
            layers));

        Assert.True(result.Succeeded, result.Failure?.ToString());
        Assert.Equal(2, backend.CompositeCalls);

        using var output = result.Frame!;
        var actual = provider.Readback(output);
        var expected = CompositeReference(
            a.Pixels.Span,
            b.Pixels.Span,
            128,
            new[]
            {
                (transparent, byte.MaxValue, true),
                (opaque, (byte)0, true),
                (opaque, byte.MaxValue, true)
            });

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(255, false)]
    [InlineData(127, true)]
    public void Noop_transition_retains_selected_surface_without_backend_work(int weight, bool sameBackground)
    {
        var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        var timing = Timing(9);
        var contentA = Pattern(19);
        var contentB = Pattern(131);
        var a = new StaticRgbaSource(MediaSourceId.New(), contentA).Materialize(provider, timing);
        var b = new StaticRgbaSource(MediaSourceId.New(), contentB).Materialize(provider, timing);
        var selected = weight == 255 ? b : a;
        var surface = selected.SurfaceId;
        var result = provider.Composite(new GpuCompositeRequest(OutputSource, a, sameBackground ? a : b,
            GpuTransition.Dissolve((byte)weight)));
        Assert.True(result.Succeeded, result.Failure?.ToString());
        var output = result.Frame!;
        Assert.NotSame(selected, output);
        Assert.Equal(OutputSource, output.Descriptor.SourceId);
        Assert.Equal(timing, output.Descriptor.Timing);
        Assert.Equal(surface, output.SurfaceId);
        Assert.Equal(0, backend.CompositeCalls);
        Assert.Equal(2, provider.ActiveSurfaceCount);
        a.Dispose();
        b.Dispose();
        Assert.Equal(1, provider.ActiveSurfaceCount);
        Assert.Equal((weight == 255 ? contentB : contentA).Pixels.ToArray(), provider.Readback(output));
        provider.Stop();
        Assert.True(output.IsDisposed);
        Assert.Equal(0, provider.ActiveSurfaceCount);
        output.Dispose();
    }

    [Fact]
    public void Noop_transition_validates_hidden_layers_and_unselected_background_before_retaining()
    {
        var backend = new CountingCompositeBackend();
        using var provider = new GpuProcessingProvider(backend);
        provider.Start();
        using var a = new StaticRgbaSource(MediaSourceId.New(), Pattern(11)).Materialize(provider, Timing(1));
        using var b = new StaticRgbaSource(MediaSourceId.New(), Pattern(31)).Materialize(provider, Timing(2));
        Assert.Equal("gpu.composite.timing_mismatch", provider.Composite(
            new GpuCompositeRequest(OutputSource, a, b, GpuTransition.CutToA)).Failure?.Code);
        using var hidden = new StaticRgbaSource(MediaSourceId.New(), Pattern(51)).Materialize(provider, Timing(1));
        hidden.Dispose();
        Assert.Equal("gpu.composite.surface_disposed", provider.Composite(
            new GpuCompositeRequest(OutputSource, a, a, GpuTransition.CutToA,
                new GpuKeyLayer(hidden, 0, visible: false))).Failure?.Code);
        Assert.Equal(0, backend.CompositeCalls);
        Assert.Equal(2, provider.ActiveSurfaceCount);
    }

    [Fact]
    public void Noop_transition_rejects_foreign_frame_with_colliding_surface_identity()
    {
        using var first = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        using var second = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        first.Start(); second.Start();
        var source = MediaSourceId.New();
        using var owned = new StaticRgbaSource(source, Pattern(11)).Materialize(first, Timing(1));
        using var foreign = new StaticRgbaSource(source, Pattern(31)).Materialize(second, Timing(1));
        Assert.Equal(owned.SurfaceId, foreign.SurfaceId);
        var result = first.Composite(new GpuCompositeRequest(OutputSource, owned, foreign, GpuTransition.CutToA));
        Assert.False(result.Succeeded);
        Assert.Equal("gpu.composite.surface_foreign", result.Failure?.Code);
    }

    [Fact]
    public void Dissolve_matches_independent_integer_equation_for_every_byte_pair_and_weight()
    {
        var format = new VideoFormat(128, 128, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
        var pixelsA = new byte[65_536];
        var pixelsB = new byte[65_536];
        for (var index = 0; index < pixelsA.Length; index++)
        {
            pixelsA[index] = (byte)(index >> 8);
            pixelsB[index] = (byte)(index & 255);
        }
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();
        using var a = new StaticRgbaSource(MediaSourceId.New(), new RgbaFrameBuffer(format, pixelsA)).Materialize(provider, Timing(1));
        using var b = new StaticRgbaSource(MediaSourceId.New(), new RgbaFrameBuffer(format, pixelsB)).Materialize(provider, Timing(1));
        for (var weight = 0; weight <= 255; weight++)
        {
            var result = provider.Composite(new GpuCompositeRequest(OutputSource, a, b, GpuTransition.Dissolve((byte)weight)));
            Assert.True(result.Succeeded, result.Failure?.ToString());
            using var output = result.Frame!;
            using var readback = provider.RentReadback(output);
            var actual = readback.Memory.Span;
            for (var index = 0; index < actual.Length; index++)
                Assert.Equal((byte)((pixelsA[index] * (255 - weight) + pixelsB[index] * weight + 127) / 255), actual[index]);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(67, 3)]
    public void Vector_dissolve_preserves_scalar_tail_and_alpha(int width, int height)
    {
        var format = new VideoFormat((uint)width, (uint)height, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
        var length = RgbaFrameBuffer.RequiredByteLength(format);
        var pixelsA = Enumerable.Range(0, length).Select(index => (byte)(index % 256)).ToArray();
        var pixelsB = Enumerable.Range(0, length).Select(index => (byte)(255 - index % 256)).ToArray();
        using var provider = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        provider.Start();
        using var a = new StaticRgbaSource(MediaSourceId.New(), new RgbaFrameBuffer(format, pixelsA)).Materialize(provider, Timing(1));
        using var b = new StaticRgbaSource(MediaSourceId.New(), new RgbaFrameBuffer(format, pixelsB)).Materialize(provider, Timing(1));
        var result = provider.Composite(new GpuCompositeRequest(OutputSource, a, b, GpuTransition.Dissolve(137)));
        using var output = result.Frame!;
        Assert.Equal(CompositeReference(pixelsA, pixelsB, 137, []), provider.Readback(output));
    }

    private static FrameTiming Timing(ulong sequence) =>
        new(sequence, checked((long)sequence), new Timebase(1, 50));

    private static RgbaFrameBuffer Pattern(byte seed)
    {
        var pixels = new byte[RgbaFrameBuffer.RequiredByteLength(OddFormat)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var pixel = offset / 4;
            pixels[offset] = unchecked((byte)(seed + pixel * 3));
            pixels[offset + 1] = unchecked((byte)(seed + 17 + pixel * 5));
            pixels[offset + 2] = unchecked((byte)(seed + 41 + pixel * 7));
            pixels[offset + 3] = unchecked((byte)(31 + seed + pixel * 23));
        }

        return new RgbaFrameBuffer(OddFormat, pixels);
    }

    private static byte[] RepeatPixels(params (byte R, byte G, byte B, byte A)[] values)
    {
        var result = new byte[RgbaFrameBuffer.RequiredByteLength(OddFormat)];
        var pixelCount = checked((int)(OddFormat.Width * OddFormat.Height));
        for (var index = 0; index < pixelCount; index++)
        {
            var value = values[index % values.Length];
            var offset = index * 4;
            result[offset] = value.R;
            result[offset + 1] = value.G;
            result[offset + 2] = value.B;
            result[offset + 3] = value.A;
        }

        return result;
    }

    private static byte[] CompositeReference(
        ReadOnlySpan<byte> backgroundA,
        ReadOnlySpan<byte> backgroundB,
        byte transitionWeight,
        IReadOnlyList<(RgbaFrameBuffer Content, byte Opacity, bool Visible)> layers)
    {
        var output = new byte[backgroundA.Length];
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            output[offset] = Blend(backgroundA[offset], backgroundB[offset], transitionWeight);
            output[offset + 1] = Blend(backgroundA[offset + 1], backgroundB[offset + 1], transitionWeight);
            output[offset + 2] = Blend(backgroundA[offset + 2], backgroundB[offset + 2], transitionWeight);
            output[offset + 3] = Blend(backgroundA[offset + 3], backgroundB[offset + 3], transitionWeight);
        }

        foreach (var layer in layers)
        {
            if (!layer.Visible)
                continue;

            var pixels = layer.Content.Pixels.Span;
            for (var offset = 0; offset < output.Length; offset += 4)
            {
                var effectiveAlpha = ScaleAlpha(pixels[offset + 3], layer.Opacity);
                output[offset] = AlphaComposite(output[offset], pixels[offset], effectiveAlpha);
                output[offset + 1] = AlphaComposite(output[offset + 1], pixels[offset + 1], effectiveAlpha);
                output[offset + 2] = AlphaComposite(output[offset + 2], pixels[offset + 2], effectiveAlpha);
                output[offset + 3] = CompositeAlpha(output[offset + 3], effectiveAlpha);
            }
        }

        return output;
    }

    private static byte Blend(byte a, byte b, byte weight) =>
        (byte)((a * (255 - weight) + b * weight + 127) / 255);

    private static byte ScaleAlpha(byte alpha, byte opacity) =>
        (byte)((alpha * opacity + 127) / 255);

    private static byte AlphaComposite(byte background, byte foreground, byte alpha) =>
        (byte)((background * (255 - alpha) + foreground * alpha + 127) / 255);

    private static byte CompositeAlpha(byte backgroundAlpha, byte foregroundAlpha) =>
        (byte)(foregroundAlpha + (backgroundAlpha * (255 - foregroundAlpha) + 127) / 255);

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
