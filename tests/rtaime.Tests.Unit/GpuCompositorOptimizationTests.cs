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
        using var opaqueFrame = new StaticRgbaSource(MediaSourceId.New(), opaque).Materialize(provider, timing);

        var layers = new[]
        {
            new GpuKeyLayer(transparentFrame, byte.MaxValue, visible: true),
            new GpuKeyLayer(opaqueFrame, 0, visible: true),
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
