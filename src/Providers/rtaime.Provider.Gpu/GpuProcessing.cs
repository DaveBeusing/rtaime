using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Gpu;

public static class GpuCapabilityKinds
{
    public const string Processing = "gpu.processing";
    public const string StaticRgbaSource = "gpu.rgba.static";
    public const string DynamicRgbaSource = "gpu.rgba.dynamic";
    public const string CompositeRgba = "gpu.rgba.composite";
    public const string Cut = "gpu.transition.cut";
    public const string Dissolve = "gpu.transition.dissolve";
}

public enum GpuBackendKind
{
    ManagedReference = 1,
    NvidiaCuda = 2
}

public sealed record GpuBackendInfo
{
    public GpuBackendInfo(
        GpuBackendKind kind,
        string deviceName,
        bool hardwareAccelerated,
        bool available,
        ulong? totalMemoryBytes = null,
        Failure? failure = null)
    {
        if (!Enum.IsDefined(typeof(GpuBackendKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(deviceName))
            throw new ArgumentException("GPU backend device name is required.", nameof(deviceName));
        if (available && failure is not null)
            throw new ArgumentException("Available GPU backends must not carry a failure.", nameof(failure));
        if (!available && failure is null)
            throw new ArgumentException("Unavailable GPU backends require a failure.", nameof(failure));

        Kind = kind;
        DeviceName = deviceName.Trim();
        HardwareAccelerated = hardwareAccelerated;
        Available = available;
        TotalMemoryBytes = totalMemoryBytes;
        Failure = failure;
    }

    public GpuBackendKind Kind { get; }
    public string DeviceName { get; }
    public bool HardwareAccelerated { get; }
    public bool Available { get; }
    public ulong? TotalMemoryBytes { get; }
    public Failure? Failure { get; }
}

public sealed class RgbaFrameBuffer
{
    private readonly byte[] _pixels;

    public RgbaFrameBuffer(VideoFormat format, ReadOnlySpan<byte> pixels)
    {
        if (format.PixelFormat != PixelFormat.Rgba8)
            throw new ArgumentException("GPU RGBA frame buffers require the Rgba8 pixel format.", nameof(format));

        var expectedLength = RequiredByteLength(format);
        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"RGBA frame buffer requires exactly '{expectedLength}' bytes for format '{format.Width}x{format.Height}'.",
                nameof(pixels));
        }

        Format = format;
        _pixels = pixels.ToArray();
    }

    public VideoFormat Format { get; }
    public ReadOnlyMemory<byte> Pixels => _pixels;
    public int ByteLength => _pixels.Length;

    public void CopyPixelsFrom(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != _pixels.Length)
            throw new ArgumentException("RGBA update payload length does not match the existing frame buffer.", nameof(pixels));

        pixels.CopyTo(_pixels);
    }

    public void CopyRegionFrom(
        ReadOnlySpan<byte> rgbaPixels,
        int x,
        int y,
        int width,
        int height)
    {
        if (x < 0 || y < 0)
            throw new ArgumentOutOfRangeException(nameof(x), "RGBA region origin must not be negative.");
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "RGBA region dimensions must be greater than zero.");
        if ((long)x + width > Format.Width || (long)y + height > Format.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "RGBA region must fit inside the frame buffer.");

        var rowBytes = checked(width * 4);
        var expectedLength = checked(rowBytes * height);
        if (rgbaPixels.Length != expectedLength)
            throw new ArgumentException("RGBA region payload length does not match the requested region.", nameof(rgbaPixels));

        var frameWidth = checked((int)Format.Width);
        for (var row = 0; row < height; row++)
        {
            var source = rgbaPixels.Slice(checked(row * rowBytes), rowBytes);
            var destinationOffset = checked((((y + row) * frameWidth) + x) * 4);
            source.CopyTo(_pixels.AsSpan(destinationOffset, rowBytes));
        }
    }

    public static RgbaFrameBuffer Solid(VideoFormat format, byte red, byte green, byte blue, byte alpha = byte.MaxValue)
    {
        var pixels = new byte[RequiredByteLength(format)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
            pixels[offset + 3] = alpha;
        }

        return new RgbaFrameBuffer(format, pixels);
    }

    public static int RequiredByteLength(VideoFormat format)
    {
        var bytes = checked((ulong)format.Width * format.Height * 4UL);
        if (bytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(format), "RGBA frame buffer exceeds the managed implementation limit.");
        return (int)bytes;
    }
}

public enum GpuTransitionKind
{
    Cut = 1,
    Dissolve = 2
}

public readonly record struct GpuTransition
{
    public GpuTransition(GpuTransitionKind kind, byte blendWeight)
    {
        if (!Enum.IsDefined(typeof(GpuTransitionKind), kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == GpuTransitionKind.Cut && blendWeight is not 0 and not byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(blendWeight), "CUT requires a blend weight of either 0 or 255.");

        Kind = kind;
        BlendWeight = blendWeight;
    }

    public GpuTransitionKind Kind { get; }
    public byte BlendWeight { get; }

    public static GpuTransition CutToA => new(GpuTransitionKind.Cut, 0);
    public static GpuTransition CutToB => new(GpuTransitionKind.Cut, byte.MaxValue);
    public static GpuTransition Dissolve(byte blendWeight) => new(GpuTransitionKind.Dissolve, blendWeight);
}

public sealed record GpuCompositeOperation
{
    public GpuCompositeOperation(
        SurfaceId backgroundA,
        SurfaceId backgroundB,
        GpuTransition transition,
        SurfaceId? layer,
        byte layerOpacity,
        bool layerVisible)
    {
        BackgroundA = backgroundA;
        BackgroundB = backgroundB;
        Transition = transition;
        Layer = layer;
        LayerOpacity = layerOpacity;
        LayerVisible = layerVisible;

        if (layerVisible && layer is null)
            throw new ArgumentException("Visible GPU layers require a surface.", nameof(layer));
    }

    public SurfaceId BackgroundA { get; }
    public SurfaceId BackgroundB { get; }
    public GpuTransition Transition { get; }
    public SurfaceId? Layer { get; }
    public byte LayerOpacity { get; }
    public bool LayerVisible { get; }
}

public interface IGpuProcessingBackend : IDisposable
{
    GpuBackendInfo Info { get; }
    SurfaceStorageDomain StorageDomain { get; }
    void Start();
    void Stop();
    void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels);
    void Composite(SurfaceId outputSurfaceId, VideoFormat format, GpuCompositeOperation operation);
    byte[] Readback(SurfaceId surfaceId, VideoFormat format);
    void Release(SurfaceId surfaceId);
}

public sealed class GpuFrame : IDisposable
{
    private readonly Action<GpuFrame> _release;
    private int _disposed;

    internal GpuFrame(FrameDescriptor descriptor, Action<GpuFrame> release)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public FrameDescriptor Descriptor { get; }
    public SurfaceId SurfaceId => Descriptor.Surface.SurfaceId;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _release(this);
    }

    internal void MarkReleased() => Interlocked.Exchange(ref _disposed, 1);
}

public sealed class StaticRgbaSource
{
    public StaticRgbaSource(MediaSourceId sourceId, RgbaFrameBuffer content)
    {
        SourceId = sourceId;
        Content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public MediaSourceId SourceId { get; }
    public RgbaFrameBuffer Content { get; }

    public GpuFrame Materialize(GpuProcessingProvider provider, FrameTiming timing) =>
        (provider ?? throw new ArgumentNullException(nameof(provider)))
            .Upload(SourceId, Content, timing, Generation.Initial, "static");
}

public sealed class DynamicRgbaSource
{
    private readonly object _gate = new();
    private RgbaFrameBuffer _content;
    private Generation _generation = Generation.Initial;

    public DynamicRgbaSource(MediaSourceId sourceId, RgbaFrameBuffer initialContent)
    {
        SourceId = sourceId;
        _content = initialContent ?? throw new ArgumentNullException(nameof(initialContent));
    }

    public MediaSourceId SourceId { get; }

    public Generation Generation
    {
        get
        {
            lock (_gate)
                return _generation;
        }
    }

    public void Update(RgbaFrameBuffer content)
    {
        ArgumentNullException.ThrowIfNull(content);

        lock (_gate)
        {
            if (content.Format != _content.Format)
                throw new ArgumentException("Dynamic RGBA source format cannot change during its lifetime.", nameof(content));

            _content = content;
            _generation = _generation.Next();
        }
    }

    public GpuFrame Materialize(GpuProcessingProvider provider, FrameTiming timing)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_gate)
            return provider.Upload(SourceId, _content, timing, _generation, "dynamic");
    }
}

public sealed record GpuKeyLayer
{
    public GpuKeyLayer(GpuFrame frame, byte opacity = byte.MaxValue, bool visible = true)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        Opacity = opacity;
        Visible = visible;
    }

    public GpuFrame Frame { get; }
    public byte Opacity { get; }
    public bool Visible { get; }
}

public sealed record GpuCompositeRequest
{
    public GpuCompositeRequest(
        MediaSourceId outputSourceId,
        GpuFrame backgroundA,
        GpuFrame backgroundB,
        GpuTransition transition,
        GpuKeyLayer? layer = null)
    {
        OutputSourceId = outputSourceId;
        BackgroundA = backgroundA ?? throw new ArgumentNullException(nameof(backgroundA));
        BackgroundB = backgroundB ?? throw new ArgumentNullException(nameof(backgroundB));
        Transition = transition;
        Layer = layer;
    }

    public MediaSourceId OutputSourceId { get; }
    public GpuFrame BackgroundA { get; }
    public GpuFrame BackgroundB { get; }
    public GpuTransition Transition { get; }
    public GpuKeyLayer? Layer { get; }
}

public enum GpuProviderState
{
    Stopped = 1,
    Running = 2,
    Disposed = 3
}

public sealed record GpuObservation(
    ulong Ordinal,
    string Code,
    ulong? SequenceNumber,
    Failure? Failure);

public sealed class GpuProcessingResult
{
    private GpuProcessingResult(GpuFrame? frame, Failure? failure)
    {
        if ((frame is null) == (failure is null))
            throw new ArgumentException("GPU processing result requires exactly one of frame or failure.");

        Frame = frame;
        Failure = failure;
    }

    public GpuFrame? Frame { get; }
    public Failure? Failure { get; }
    public bool Succeeded => Frame is not null;

    internal static GpuProcessingResult Success(GpuFrame frame) => new(frame, null);
    internal static GpuProcessingResult Rejected(Failure failure) => new(null, failure);
}

public sealed class GpuProcessingProvider : IDisposable
{
    private static readonly VideoFormat[] V1Formats =
    {
        VideoFormat.Hd1080p50Rgba8,
        VideoFormat.Hd1080p59_94Rgba8
    };

    private readonly object _gate = new();
    private readonly IGpuProcessingBackend _backend;
    private readonly Dictionary<SurfaceId, GpuFrame> _activeFrames = new();
    private readonly List<GpuObservation> _observations = new();
    private GpuProviderState _state = GpuProviderState.Stopped;
    private ulong _surfaceOrdinal;
    private ulong _observationOrdinal;

    public GpuProcessingProvider(IGpuProcessingBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Descriptor = CreateDescriptor(backend.Info);
    }

    public ProviderDescriptor Descriptor { get; }
    public GpuBackendInfo BackendInfo => _backend.Info;

    public GpuProviderState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public int ActiveSurfaceCount
    {
        get
        {
            lock (_gate)
                return _activeFrames.Count;
        }
    }

    public IReadOnlyList<GpuObservation> Observations
    {
        get
        {
            lock (_gate)
                return new ReadOnlyCollection<GpuObservation>(_observations.ToArray());
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state == GpuProviderState.Running)
                return;
            if (!_backend.Info.Available)
                throw new InvalidOperationException(_backend.Info.Failure?.Message ?? "GPU backend is unavailable.");

            try
            {
                _backend.Start();
                _state = GpuProviderState.Running;
                Observe("gpu.provider.started", null, null);
            }
            catch (Exception exception)
            {
                var failure = new Failure(
                    "gpu.provider.start_failed",
                    $"GPU backend start failed: {exception.GetType().Name}.");
                Observe("gpu.provider.start_failed", null, failure);
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_state is GpuProviderState.Stopped or GpuProviderState.Disposed)
                return;

            foreach (var frame in _activeFrames.Values.ToArray())
            {
                TryReleaseBackendSurface(frame.SurfaceId);
                frame.MarkReleased();
            }

            _activeFrames.Clear();
            _backend.Stop();
            _state = GpuProviderState.Stopped;
            Observe("gpu.provider.stopped", null, null);
        }
    }

    public GpuFrame Upload(
        MediaSourceId sourceId,
        RgbaFrameBuffer content,
        FrameTiming timing,
        Generation contentGeneration,
        string sourceKind)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(sourceKind))
            throw new ArgumentException("GPU source kind is required.", nameof(sourceKind));

        lock (_gate)
        {
            EnsureRunning();
            EnsureV1Compatible(content.Format);

            var ordinal = NextSurfaceOrdinal();
            var surfaceId = new SurfaceId(GpuIdentity.Create(
                "gpu-surface",
                _backend.Info.Kind.ToString(),
                sourceKind.Trim(),
                sourceId.ToString(),
                timing.SequenceNumber.ToString(),
                contentGeneration.ToString(),
                ordinal.ToString()));

            _backend.Allocate(surfaceId, content.Format, content.Pixels.Span);

            var surface = new SurfaceDescriptor(
                surfaceId,
                content.Format,
                _backend.StorageDomain,
                SurfaceOwnership.ProducerOwned,
                new SurfaceLifetimeDescriptor(contentGeneration, surfaceId.Value),
                new OpaqueSurfaceHandle(
                    _backend.Info.HardwareAccelerated ? "rtaime.gpu.hardware.surface" : "rtaime.gpu.reference.surface",
                    surfaceId.ToString()));

            var descriptor = new FrameDescriptor(
                MediaContractVersion.Current,
                sourceId,
                surface,
                timing);

            var frame = new GpuFrame(descriptor, ReleaseFrame);
            _activeFrames.Add(surfaceId, frame);
            Observe("gpu.surface.allocated", timing.SequenceNumber, null);
            return frame;
        }
    }

    public GpuProcessingResult Composite(GpuCompositeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            EnsureRunning();

            var validationFailure = ValidateCompositeRequest(request);
            if (validationFailure is not null)
            {
                Observe("gpu.composite.rejected", request.BackgroundA.Descriptor.Timing.SequenceNumber, validationFailure.Value);
                return GpuProcessingResult.Rejected(validationFailure.Value);
            }

            var format = request.BackgroundA.Descriptor.Surface.Format;
            var timing = request.BackgroundA.Descriptor.Timing;
            var ordinal = NextSurfaceOrdinal();
            var outputSurfaceId = new SurfaceId(GpuIdentity.Create(
                "gpu-composite-output",
                _backend.Info.Kind.ToString(),
                timing.SequenceNumber.ToString(),
                request.Transition.Kind.ToString(),
                request.Transition.BlendWeight.ToString(),
                ordinal.ToString()));

            var operation = new GpuCompositeOperation(
                request.BackgroundA.SurfaceId,
                request.BackgroundB.SurfaceId,
                request.Transition,
                request.Layer?.Frame.SurfaceId,
                request.Layer?.Opacity ?? byte.MaxValue,
                request.Layer?.Visible == true);

            try
            {
                _backend.Composite(outputSurfaceId, format, operation);

                var surface = new SurfaceDescriptor(
                    outputSurfaceId,
                    format,
                    _backend.StorageDomain,
                    SurfaceOwnership.ProducerOwned,
                    new SurfaceLifetimeDescriptor(new Generation(ordinal), outputSurfaceId.Value),
                    new OpaqueSurfaceHandle(
                        _backend.Info.HardwareAccelerated ? "rtaime.gpu.hardware.composite" : "rtaime.gpu.reference.composite",
                        outputSurfaceId.ToString()));

                var descriptor = new FrameDescriptor(
                    MediaContractVersion.Current,
                    request.OutputSourceId,
                    surface,
                    timing);

                var frame = new GpuFrame(descriptor, ReleaseFrame);
                _activeFrames.Add(outputSurfaceId, frame);
                Observe(
                    request.Transition.Kind == GpuTransitionKind.Cut ? "gpu.composite.cut" : "gpu.composite.dissolve",
                    timing.SequenceNumber,
                    null);
                return GpuProcessingResult.Success(frame);
            }
            catch (Exception exception)
            {
                TryReleaseBackendSurface(outputSurfaceId);
                var failure = new Failure(
                    "gpu.composite.backend_failure",
                    $"GPU composite operation failed: {exception.GetType().Name}.");
                Observe("gpu.composite.failed", timing.SequenceNumber, failure);
                return GpuProcessingResult.Rejected(failure);
            }
        }
    }

    public byte[] Readback(GpuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            EnsureRunning();
            if (frame.IsDisposed || !_activeFrames.ContainsKey(frame.SurfaceId))
                throw new ObjectDisposedException(nameof(frame));

            return _backend.Readback(frame.SurfaceId, frame.Descriptor.Surface.Format);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == GpuProviderState.Disposed)
                return;

            if (_state == GpuProviderState.Running)
                Stop();

            _backend.Dispose();
            _state = GpuProviderState.Disposed;
        }
    }

    private Failure? ValidateCompositeRequest(GpuCompositeRequest request)
    {
        if (request.BackgroundA.IsDisposed || request.BackgroundB.IsDisposed || request.Layer?.Frame.IsDisposed == true)
            return new Failure("gpu.composite.surface_disposed", "GPU composite inputs must still own live surfaces.");

        if (!_activeFrames.ContainsKey(request.BackgroundA.SurfaceId) || !_activeFrames.ContainsKey(request.BackgroundB.SurfaceId))
            return new Failure("gpu.composite.surface_foreign", "GPU composite backgrounds must belong to this provider instance.");

        if (request.Layer is not null && !_activeFrames.ContainsKey(request.Layer.Frame.SurfaceId))
            return new Failure("gpu.composite.layer_foreign", "GPU composite layer must belong to this provider instance.");

        var format = request.BackgroundA.Descriptor.Surface.Format;
        if (request.BackgroundB.Descriptor.Surface.Format != format ||
            (request.Layer is not null && request.Layer.Frame.Descriptor.Surface.Format != format))
        {
            return new Failure("gpu.composite.format_mismatch", "GPU composite inputs must use the same video format.");
        }

        var timing = request.BackgroundA.Descriptor.Timing;
        if (request.BackgroundB.Descriptor.Timing != timing ||
            (request.Layer is not null && request.Layer.Frame.Descriptor.Timing != timing))
        {
            return new Failure("gpu.composite.timing_mismatch", "GPU composite inputs must share the same frame timing.");
        }

        return null;
    }

    private static void EnsureV1Compatible(VideoFormat format)
    {
        if (format.PixelFormat != PixelFormat.Rgba8)
            throw new NotSupportedException("GPU processing foundation supports RGBA8 only.");
    }

    private ulong NextSurfaceOrdinal()
    {
        if (_surfaceOrdinal == ulong.MaxValue)
            throw new InvalidOperationException("GPU surface ordinal is exhausted.");
        return _surfaceOrdinal++;
    }

    private void ReleaseFrame(GpuFrame frame)
    {
        lock (_gate)
        {
            if (_activeFrames.Remove(frame.SurfaceId))
            {
                TryReleaseBackendSurface(frame.SurfaceId);
                Observe("gpu.surface.released", frame.Descriptor.Timing.SequenceNumber, null);
            }
        }
    }

    private void TryReleaseBackendSurface(SurfaceId surfaceId)
    {
        try
        {
            _backend.Release(surfaceId);
        }
        catch (Exception exception)
        {
            Observe(
                "gpu.surface.release_failed",
                null,
                new Failure("gpu.surface.release_failed", $"GPU surface release failed: {exception.GetType().Name}."));
        }
    }

    private static ProviderDescriptor CreateDescriptor(GpuBackendInfo info)
    {
        var providerId = new ProviderId(GpuIdentity.Create("gpu-provider", info.Kind.ToString(), info.DeviceName));
        var formats = info.Available ? V1Formats : Array.Empty<VideoFormat>();
        var capabilities = info.Available
            ? new[]
            {
                CreateCapability("static", GpuCapabilityKinds.StaticRgbaSource, formats),
                CreateCapability("dynamic", GpuCapabilityKinds.DynamicRgbaSource, formats),
                CreateCapability("composite", GpuCapabilityKinds.CompositeRgba, formats),
                CreateCapability("cut", GpuCapabilityKinds.Cut, formats),
                CreateCapability("dissolve", GpuCapabilityKinds.Dissolve, formats)
            }
            : Array.Empty<ProviderCapabilityDescriptor>();

        var resources = info.Available
            ? new[]
            {
                new ProviderResourceDescriptor(
                    new ProviderResourceId(GpuIdentity.Create("gpu-resource", info.Kind.ToString(), info.DeviceName, "0")),
                    providerId,
                    GpuCapabilityKinds.Processing,
                    1,
                    true)
            }
            : Array.Empty<ProviderResourceDescriptor>();

        ProviderAvailability availability;
        if (!info.Available)
        {
            availability = new ProviderAvailability(
                ProviderAvailabilityState.Unavailable,
                info.Failure ?? new Failure("gpu.backend.unavailable", "GPU backend is unavailable."));
        }
        else if (!info.HardwareAccelerated)
        {
            availability = new ProviderAvailability(
                ProviderAvailabilityState.Degraded,
                new Failure(
                    "gpu.backend.reference_only",
                    "Managed reference backend is functional but does not provide hardware-accelerated qualification evidence."));
        }
        else
        {
            availability = new ProviderAvailability(ProviderAvailabilityState.Available);
        }

        return new ProviderDescriptor(
            ProviderContractVersion.Current,
            providerId,
            $"rtaime GPU Provider ({info.DeviceName})",
            availability,
            capabilities,
            resources);
    }

    private static ProviderCapabilityDescriptor CreateCapability(
        string identityPart,
        string kind,
        IReadOnlyList<VideoFormat> formats) =>
        new(
            new CapabilityId(GpuIdentity.Create("gpu-capability", identityPart)),
            kind,
            formats);

    private void Observe(string code, ulong? sequenceNumber, Failure? failure) =>
        _observations.Add(new GpuObservation(_observationOrdinal++, code, sequenceNumber, failure));

    private void EnsureRunning()
    {
        ThrowIfDisposed();
        if (_state != GpuProviderState.Running)
            throw new InvalidOperationException("GPU processing provider must be started before use.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_state == GpuProviderState.Disposed, this);
    }
}

public sealed class ManagedReferenceGpuBackend : IGpuProcessingBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<SurfaceId, Allocation> _surfaces = new();
    private bool _running;
    private bool _disposed;

    public GpuBackendInfo Info { get; } = new(
        GpuBackendKind.ManagedReference,
        "Managed RGBA Reference Backend",
        hardwareAccelerated: false,
        available: true);

    public SurfaceStorageDomain StorageDomain => SurfaceStorageDomain.Host;

    public int ActiveAllocationCount
    {
        get
        {
            lock (_gate)
                return _surfaces.Count;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _running = true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _surfaces.Clear();
            _running = false;
        }
    }

    public void Allocate(SurfaceId surfaceId, VideoFormat format, ReadOnlySpan<byte> rgbaPixels)
    {
        lock (_gate)
        {
            EnsureRunning();
            if (_surfaces.ContainsKey(surfaceId))
                throw new InvalidOperationException("GPU surface identity is already allocated.");
            if (rgbaPixels.Length != RgbaFrameBuffer.RequiredByteLength(format))
                throw new ArgumentException("RGBA allocation payload length does not match the target format.", nameof(rgbaPixels));

            _surfaces.Add(surfaceId, new Allocation(format, rgbaPixels.ToArray()));
        }
    }

    public void Composite(SurfaceId outputSurfaceId, VideoFormat format, GpuCompositeOperation operation)
    {
        lock (_gate)
        {
            EnsureRunning();
            if (_surfaces.ContainsKey(outputSurfaceId))
                throw new InvalidOperationException("GPU output surface identity is already allocated.");

            var a = Get(operation.BackgroundA, format);
            var b = Get(operation.BackgroundB, format);
            var layer = operation.Layer is { } layerId ? Get(layerId, format) : null;
            var output = new byte[a.Pixels.Length];
            var weight = operation.Transition.BlendWeight;

            for (var offset = 0; offset < output.Length; offset += 4)
            {
                var baseR = Blend(a.Pixels[offset], b.Pixels[offset], weight);
                var baseG = Blend(a.Pixels[offset + 1], b.Pixels[offset + 1], weight);
                var baseB = Blend(a.Pixels[offset + 2], b.Pixels[offset + 2], weight);
                var baseA = Blend(a.Pixels[offset + 3], b.Pixels[offset + 3], weight);

                if (operation.LayerVisible && layer is not null)
                {
                    var effectiveAlpha = ScaleAlpha(layer.Pixels[offset + 3], operation.LayerOpacity);
                    output[offset] = AlphaComposite(baseR, layer.Pixels[offset], effectiveAlpha);
                    output[offset + 1] = AlphaComposite(baseG, layer.Pixels[offset + 1], effectiveAlpha);
                    output[offset + 2] = AlphaComposite(baseB, layer.Pixels[offset + 2], effectiveAlpha);
                    output[offset + 3] = CompositeAlpha(baseA, effectiveAlpha);
                }
                else
                {
                    output[offset] = baseR;
                    output[offset + 1] = baseG;
                    output[offset + 2] = baseB;
                    output[offset + 3] = baseA;
                }
            }

            _surfaces.Add(outputSurfaceId, new Allocation(format, output));
        }
    }

    public byte[] Readback(SurfaceId surfaceId, VideoFormat format)
    {
        lock (_gate)
        {
            EnsureRunning();
            return Get(surfaceId, format).Pixels.ToArray();
        }
    }

    public void Release(SurfaceId surfaceId)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _surfaces.Remove(surfaceId);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _surfaces.Clear();
            _running = false;
            _disposed = true;
        }
    }

    private Allocation Get(SurfaceId id, VideoFormat format)
    {
        if (!_surfaces.TryGetValue(id, out var allocation))
            throw new InvalidOperationException($"GPU surface '{id}' is not allocated.");
        if (allocation.Format != format)
            throw new InvalidOperationException("GPU surface format does not match the composite target format.");
        return allocation;
    }

    private static byte Blend(byte a, byte b, byte weight) =>
        (byte)((a * (255 - weight) + b * weight + 127) / 255);

    private static byte ScaleAlpha(byte alpha, byte opacity) =>
        (byte)((alpha * opacity + 127) / 255);

    private static byte AlphaComposite(byte background, byte foreground, byte alpha) =>
        (byte)((background * (255 - alpha) + foreground * alpha + 127) / 255);

    private static byte CompositeAlpha(byte backgroundAlpha, byte foregroundAlpha) =>
        (byte)(foregroundAlpha + (backgroundAlpha * (255 - foregroundAlpha) + 127) / 255);

    private void EnsureRunning()
    {
        ThrowIfDisposed();
        if (!_running)
            throw new InvalidOperationException("Managed GPU reference backend is not running.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record Allocation(VideoFormat Format, byte[] Pixels);
}

internal static class GpuIdentity
{
    public static Identity Create(string scope, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("GPU identity scope is required.", nameof(scope));
        ArgumentNullException.ThrowIfNull(parts);

        var canonical = string.Join('\u001f', new[] { scope }.Concat(parts));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var guidText = Convert.ToHexString(hash.AsSpan(0, 16));
        return new Identity(Guid.ParseExact(guidText, "N"));
    }
}
