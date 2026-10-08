using System.Collections.ObjectModel;
using System.Diagnostics;
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
    public const string SharedMonitoringResource = "gpu.monitoring.shared-resource";
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

    void ReadbackInto(SurfaceId surfaceId, VideoFormat format, Span<byte> destination)
    {
        var copy = Readback(surfaceId, format);
        if (copy.Length != destination.Length)
            throw new InvalidOperationException("GPU readback payload length does not match the supplied destination.");
        copy.AsSpan().CopyTo(destination);
    }

    void Release(SurfaceId surfaceId);
}

public readonly record struct GpuReadbackPoolStatistics(
    int Capacity,
    int AllocatedBuffers,
    int AvailableBuffers,
    int ActiveBuffers,
    ulong TotalRents,
    ulong ExhaustedRents);

public readonly record struct GpuSharedMonitoringResourceStatistics(
    int Capacity,
    int ActiveResources,
    ulong TotalExports,
    ulong RejectedExports);

public sealed class GpuSharedMonitoringResourceLease : IDisposable
{
    private readonly object _gate = new();
    private Action<MonitoringResourceId>? _release;

    internal GpuSharedMonitoringResourceLease(
        MonitoringSharedResourceDescriptor descriptor,
        Action<MonitoringResourceId> release)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public MonitoringSharedResourceDescriptor Descriptor { get; }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
                return _release is null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            var release = _release;
            if (release is null)
                return;

            release(Descriptor.ResourceId);
            _release = null;
        }
    }
}

public sealed class GpuReadbackLease : IDisposable
{
    private GpuReadbackBufferOwner? _owner;

    internal GpuReadbackLease(GpuReadbackBufferOwner owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public int Length => Owner.Length;
    public ReadOnlyMemory<byte> Memory => Owner.Buffer.AsMemory(0, Owner.Length);
    public bool IsDisposed => Volatile.Read(ref _owner) is null;

    public GpuReadbackLease Retain()
    {
        var owner = Owner;
        owner.Retain();
        return new GpuReadbackLease(owner);
    }

    internal Span<byte> WritableSpan => Owner.Buffer.AsSpan(0, Owner.Length);

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private GpuReadbackBufferOwner Owner =>
        Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(GpuReadbackLease));
}

internal sealed class GpuReadbackBufferOwner
{
    private readonly GpuReadbackBufferPool _pool;
    private int _references = 1;

    public GpuReadbackBufferOwner(GpuReadbackBufferPool pool, byte[] buffer, int length)
    {
        _pool = pool;
        Buffer = buffer;
        Length = length;
    }

    public byte[] Buffer { get; }
    public int Length { get; }

    public void Retain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            if (current <= 0)
                throw new ObjectDisposedException(nameof(GpuReadbackLease));
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                return;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
            _pool.Return(Buffer);
    }
}

internal sealed class GpuReadbackBufferPool : IDisposable
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<int, Stack<byte[]>> _available = new();
    private int _allocated;
    private int _active;
    private ulong _totalRents;
    private ulong _exhaustedRents;
    private bool _disposed;

    public GpuReadbackBufferPool(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public GpuReadbackPoolStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new GpuReadbackPoolStatistics(
                    _capacity,
                    _allocated,
                    _available.Values.Sum(stack => stack.Count),
                    _active,
                    _totalRents,
                    _exhaustedRents);
            }
        }
    }

    public GpuReadbackLease Rent(int length)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] buffer;
            if (_available.TryGetValue(length, out var buffers) && buffers.TryPop(out var pooled))
            {
                buffer = pooled;
                if (buffers.Count == 0)
                    _available.Remove(length);
            }
            else if (_allocated < _capacity)
            {
                buffer = new byte[length];
                _allocated++;
            }
            else
            {
                if (_exhaustedRents < ulong.MaxValue)
                    _exhaustedRents++;
                throw new InvalidOperationException(
                    $"GPU readback buffer pool capacity '{_capacity}' is exhausted. All reusable Program buffers are still leased.");
            }

            _active++;
            if (_totalRents < ulong.MaxValue)
                _totalRents++;
            return new GpuReadbackLease(new GpuReadbackBufferOwner(this, buffer, length));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var availableCount = _available.Values.Sum(stack => stack.Count);
            _available.Clear();
            _allocated -= availableCount;
        }
    }

    internal void Return(byte[] buffer)
    {
        lock (_gate)
        {
            if (_active <= 0)
                throw new InvalidOperationException("GPU readback buffer pool release accounting underflow.");
            _active--;

            if (_disposed)
            {
                _allocated--;
                return;
            }

            if (!_available.TryGetValue(buffer.Length, out var buffers))
            {
                buffers = new Stack<byte[]>();
                _available.Add(buffer.Length, buffers);
            }
            buffers.Push(buffer);
        }
    }
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

public static class GpuCompositeLimits
{
    public const int MaxActiveLayers = 8;
}

public sealed record GpuCompositeRequest
{
    private GpuCompositeRequest(
        MediaSourceId outputSourceId,
        GpuFrame backgroundA,
        GpuFrame backgroundB,
        GpuTransition transition,
        IReadOnlyList<GpuKeyLayer> layers)
    {
        OutputSourceId = outputSourceId;
        BackgroundA = backgroundA ?? throw new ArgumentNullException(nameof(backgroundA));
        BackgroundB = backgroundB ?? throw new ArgumentNullException(nameof(backgroundB));
        Transition = transition;
        Layers = layers ?? throw new ArgumentNullException(nameof(layers));
    }

    public GpuCompositeRequest(
        MediaSourceId outputSourceId,
        GpuFrame backgroundA,
        GpuFrame backgroundB,
        GpuTransition transition,
        GpuKeyLayer? layer = null)
        : this(
            outputSourceId,
            backgroundA,
            backgroundB,
            transition,
            layer is null
                ? Array.Empty<GpuKeyLayer>()
                : Array.AsReadOnly(new[] { layer }))
    {
    }

    public MediaSourceId OutputSourceId { get; }
    public GpuFrame BackgroundA { get; }
    public GpuFrame BackgroundB { get; }
    public GpuTransition Transition { get; }
    public IReadOnlyList<GpuKeyLayer> Layers { get; }
    public GpuKeyLayer? Layer => Layers.Count == 1 ? Layers[0] : null;

    public static GpuCompositeRequest WithLayers(
        MediaSourceId outputSourceId,
        GpuFrame backgroundA,
        GpuFrame backgroundB,
        GpuTransition transition,
        IEnumerable<GpuKeyLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var bounded = layers.ToArray();
        return new GpuCompositeRequest(
            outputSourceId,
            backgroundA,
            backgroundB,
            transition,
            Array.AsReadOnly(bounded));
    }
}

public enum GpuProviderState
{
    Unavailable = 1,
    Stopped = 2,
    Starting = 3,
    Ready = 4,
    Degraded = 5,
    Failed = 6,
    Recovering = 7,
    Disposed = 8
}

public static class GpuProviderLifecycleReasonCodes
{
    public const string BackendUnavailable = "gpu.provider.backend_unavailable";
    public const string Stopped = "gpu.provider.stopped";
    public const string Starting = "gpu.provider.starting";
    public const string Ready = "gpu.provider.ready";
    public const string StartFailed = "gpu.provider.start_failed";
    public const string Recovering = "gpu.provider.recovering";
    public const string RecoveryFailed = "gpu.provider.recovery_failed";
    public const string MonitoringUnavailable = "gpu.monitoring.resource.export_unavailable";
    public const string MonitoringCapacityExhausted = "gpu.monitoring.resource.capacity_exhausted";
    public const string MonitoringReleaseFailed = "gpu.monitoring.resource.release_failed";
    public const string SurfaceReleaseFailed = "gpu.surface.release_failed";
    public const string ReadbackPoolExhausted = "gpu.readback.pool_exhausted";
    public const string UploadFailed = "gpu.upload.backend_failure";
    public const string CompositeFailed = "gpu.composite.backend_failure";
    public const string ReadbackFailed = "gpu.readback.backend_failure";
    public const string StopFailed = "gpu.provider.stop_failed";
    public const string Disposed = "gpu.provider.disposed";
}

public sealed record GpuProviderLifecycleSnapshot(
    GpuProviderState State,
    string ReasonCode,
    Failure? Failure,
    ulong Generation);

public sealed record GpuObservation(
    ulong Ordinal,
    string Code,
    ulong? SequenceNumber,
    Failure? Failure);

public sealed class GpuProcessingResult
{
    private GpuProcessingResult(GpuFrame? frame, Failure? failure, TimeSpan duration, int layerCount)
    {
        if ((frame is null) == (failure is null))
            throw new ArgumentException("GPU processing result requires exactly one of frame or failure.");
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (layerCount < 0 || (frame is not null && layerCount > GpuCompositeLimits.MaxActiveLayers))
            throw new ArgumentOutOfRangeException(nameof(layerCount));

        Frame = frame;
        Failure = failure;
        Duration = duration;
        LayerCount = layerCount;
    }

    public GpuFrame? Frame { get; }
    public Failure? Failure { get; }
    public TimeSpan Duration { get; }
    public int LayerCount { get; }
    public bool Succeeded => Frame is not null;

    internal static GpuProcessingResult Success(GpuFrame frame, TimeSpan duration, int layerCount) =>
        new(frame, null, duration, layerCount);

    internal static GpuProcessingResult Rejected(Failure failure, TimeSpan duration, int layerCount) =>
        new(null, failure, duration, layerCount);
}

public sealed class GpuProcessingProvider : IDisposable
{
    public const int RetainedObservationCapacity = 512;
    public const int PublishedMonitoringResourceSetSize = 2;
    public const int SharedMonitoringResourceCapacity = PublishedMonitoringResourceSetSize * 2;

    private static readonly VideoFormat[] V1Formats =
    {
        VideoFormat.Hd1080p50Rgba8,
        VideoFormat.Hd1080p59_94Rgba8
    };

    private readonly object _gate = new();
    private readonly IGpuProcessingBackend _backend;
    private readonly GpuReadbackBufferPool _readbackPool;
    private readonly Dictionary<SurfaceId, GpuFrame> _activeFrames = new();
    private readonly HashSet<SurfaceId> _unreleasedBackendSurfaces = new();
    private readonly Dictionary<MonitoringResourceId, SharedMonitoringResourceEntry> _sharedMonitoringResources = new();
    private readonly Dictionary<SurfaceId, int> _sharedMonitoringSurfaceReferences = new();
    private readonly HashSet<SurfaceId> _deferredMonitoringSurfaceReleases = new();
    private readonly BoundedDiagnosticHistory<GpuObservation> _observations = new(RetainedObservationCapacity);
    private int _observableActiveSurfaceCount;
    private int _observableUnreleasedBackendSurfaceCount;
    private int _observableActiveSharedMonitoringResourceCount;
    private ulong _sharedMonitoringResourceOrdinal;
    private ulong _totalSharedMonitoringExports;
    private ulong _rejectedSharedMonitoringExports;
    private Identity _monitoringProviderInstanceId = Identity.New();
    private GpuProviderState _state;
    private string _lifecycleReasonCode;
    private Failure? _lifecycleFailure;
    private ulong _lifecycleGeneration;
    private ulong _surfaceOrdinal;
    private ulong _observationOrdinal;

    public GpuProcessingProvider(IGpuProcessingBackend backend, int readbackBufferCapacity = 4)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _readbackPool = new GpuReadbackBufferPool(readbackBufferCapacity);
        _state = backend.Info.Available ? GpuProviderState.Stopped : GpuProviderState.Unavailable;
        _lifecycleReasonCode = backend.Info.Available
            ? GpuProviderLifecycleReasonCodes.Stopped
            : GpuProviderLifecycleReasonCodes.BackendUnavailable;
        _lifecycleFailure = backend.Info.Available ? null : backend.Info.Failure;
    }

    public ProviderDescriptor Descriptor
    {
        get
        {
            lock (_gate)
                return CreateDescriptor(_backend, _state, _lifecycleReasonCode, _lifecycleFailure);
        }
    }

    public GpuBackendInfo BackendInfo => _backend.Info;
    public Identity MonitoringProviderInstanceId
    {
        get
        {
            lock (_gate)
                return _monitoringProviderInstanceId;
        }
    }

    public GpuProviderState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public GpuProviderLifecycleSnapshot Lifecycle
    {
        get
        {
            lock (_gate)
                return new GpuProviderLifecycleSnapshot(
                    _state,
                    _lifecycleReasonCode,
                    _lifecycleFailure,
                    _lifecycleGeneration);
        }
    }

    public int ActiveSurfaceCount => Volatile.Read(ref _observableActiveSurfaceCount);

    public int UnreleasedBackendSurfaceCount =>
        Volatile.Read(ref _observableUnreleasedBackendSurfaceCount);

    public int ActiveSharedMonitoringResourceCount =>
        Volatile.Read(ref _observableActiveSharedMonitoringResourceCount);

    public IReadOnlyList<GpuObservation> Observations => _observations.Snapshot();
    public ulong OverwrittenObservationCount => _observations.OverwrittenCount;
    public GpuReadbackPoolStatistics ReadbackPoolStatistics => _readbackPool.Statistics;
    public bool CanExportSharedMonitoringResources
    {
        get
        {
            lock (_gate)
            {
                return _state is GpuProviderState.Ready or GpuProviderState.Degraded &&
                    _backend.Info.Available &&
                    _backend.Info.HardwareAccelerated &&
                    _backend.StorageDomain == SurfaceStorageDomain.Device &&
                    _backend is IGpuSharedMonitoringBackend
                    {
                        SupportsSharedMonitoringResources: true,
                        IsSharedMonitoringExportAvailable: true
                    };
            }
        }
    }
    public GpuSharedMonitoringResourceStatistics SharedMonitoringResourceStatistics
    {
        get
        {
            lock (_gate)
            {
                return new GpuSharedMonitoringResourceStatistics(
                    SharedMonitoringResourceCapacity,
                    _sharedMonitoringResources.Count,
                    _totalSharedMonitoringExports,
                    _rejectedSharedMonitoringExports);
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state is GpuProviderState.Ready or GpuProviderState.Degraded)
                return;
            if (_state == GpuProviderState.Failed)
                throw new InvalidOperationException("GPU provider is failed; use Recover before resuming production work.");

            TransitionStateUnsafe(
                GpuProviderState.Starting,
                GpuProviderLifecycleReasonCodes.Starting,
                null);

            try
            {
                if (!_backend.Info.Available)
                    throw new InvalidOperationException(_backend.Info.Failure?.Message ?? "GPU backend is unavailable.");

                _backend.Start();
                _monitoringProviderInstanceId = Identity.New();
                TransitionStateUnsafe(
                    GpuProviderState.Ready,
                    GpuProviderLifecycleReasonCodes.Ready,
                    null,
                    advanceGeneration: true);
                Observe("gpu.provider.started", null, null);
            }
            catch (Exception exception)
            {
                Exception? cleanupFailure = null;
                try
                {
                    _backend.Stop();
                }
                catch (Exception cleanupException)
                {
                    cleanupFailure = cleanupException;
                }

                var detail = cleanupFailure is null
                    ? $"GPU backend start failed: {exception.GetType().Name}."
                    : $"GPU backend start failed: {exception.GetType().Name}; cleanup also failed: {cleanupFailure.GetType().Name}.";
                var failure = new Failure(GpuProviderLifecycleReasonCodes.StartFailed, detail);
                TransitionStateUnsafe(GpuProviderState.Failed, GpuProviderLifecycleReasonCodes.StartFailed, failure);
                throw;
            }
        }
    }

    public void Recover()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state == GpuProviderState.Ready)
                return;
            if (_state is GpuProviderState.Stopped or GpuProviderState.Unavailable)
            {
                Start();
                return;
            }
            if (_state is not (GpuProviderState.Degraded or GpuProviderState.Failed))
                throw new InvalidOperationException($"GPU provider cannot recover from state '{_state}'.");

            TransitionStateUnsafe(
                GpuProviderState.Recovering,
                GpuProviderLifecycleReasonCodes.Recovering,
                _lifecycleFailure);

            var cleanupFailure = DrainResourcesAndStopBackendUnsafe();
            if (cleanupFailure is not null)
            {
                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.RecoveryFailed,
                    $"GPU provider recovery cleanup failed: {cleanupFailure.GetType().Name}.");
                TransitionStateUnsafe(GpuProviderState.Failed, GpuProviderLifecycleReasonCodes.RecoveryFailed, failure);
                throw new InvalidOperationException(failure.Message, cleanupFailure);
            }

            try
            {
                _backend.Start();
                _monitoringProviderInstanceId = Identity.New();
                TransitionStateUnsafe(
                    GpuProviderState.Ready,
                    GpuProviderLifecycleReasonCodes.Ready,
                    null,
                    advanceGeneration: true);
            }
            catch (Exception exception)
            {
                try
                {
                    _backend.Stop();
                }
                catch (Exception cleanupException)
                {
                    Observe(
                        "gpu.provider.recovery_cleanup_failed",
                        null,
                        new Failure(
                            "gpu.provider.recovery_cleanup_failed",
                            $"GPU recovery cleanup failed: {cleanupException.GetType().Name}."));
                }

                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.RecoveryFailed,
                    $"GPU backend recovery failed: {exception.GetType().Name}.");
                TransitionStateUnsafe(GpuProviderState.Failed, GpuProviderLifecycleReasonCodes.RecoveryFailed, failure);
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
            if (_state == GpuProviderState.Unavailable)
            {
                TransitionStateUnsafe(GpuProviderState.Stopped, GpuProviderLifecycleReasonCodes.Stopped, null);
                return;
            }

            var failure = DrainResourcesAndStopBackendUnsafe();
            if (failure is not null)
            {
                var lifecycleFailure = new Failure(
                    GpuProviderLifecycleReasonCodes.StopFailed,
                    $"GPU provider stop failed: {failure.GetType().Name}.");
                TransitionStateUnsafe(GpuProviderState.Failed, GpuProviderLifecycleReasonCodes.StopFailed, lifecycleFailure);
                throw new InvalidOperationException(lifecycleFailure.Message, failure);
            }

            TransitionStateUnsafe(GpuProviderState.Stopped, GpuProviderLifecycleReasonCodes.Stopped, null);
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

            try
            {
                _backend.Allocate(surfaceId, content.Format, content.Pixels.Span);
            }
            catch (Exception exception)
            {
                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.UploadFailed,
                    $"GPU upload failed: {exception.GetType().Name}.");
                if (_backend.Info.Kind == GpuBackendKind.NvidiaCuda)
                    TransitionStateUnsafe(GpuProviderState.Failed, GpuProviderLifecycleReasonCodes.UploadFailed, failure);
                Observe("gpu.upload.failed", timing.SequenceNumber, failure);
                throw;
            }

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
            PublishResourceCountsUnsafe();
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
            var stopwatch = Stopwatch.StartNew();

            var validationFailure = ValidateCompositeRequest(request);
            if (validationFailure is not null)
            {
                stopwatch.Stop();
                Observe("gpu.composite.rejected", request.BackgroundA.Descriptor.Timing.SequenceNumber, validationFailure.Value);
                return GpuProcessingResult.Rejected(validationFailure.Value, stopwatch.Elapsed, request.Layers.Count);
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
                request.Layers.Count.ToString(),
                ordinal.ToString()));

            SurfaceId? intermediateSurfaceId = null;
            SurfaceId? pendingSurfaceId = null;
            try
            {
                if (request.Layers.Count == 0)
                {
                    pendingSurfaceId = outputSurfaceId;
                    _backend.Composite(
                        outputSurfaceId,
                        format,
                        new GpuCompositeOperation(
                            request.BackgroundA.SurfaceId,
                            request.BackgroundB.SurfaceId,
                            request.Transition,
                            null,
                            byte.MaxValue,
                            false));
                    pendingSurfaceId = null;
                }
                else
                {
                    for (var index = 0; index < request.Layers.Count; index++)
                    {
                        var layer = request.Layers[index];
                        var isFirst = index == 0;
                        var isFinal = index == request.Layers.Count - 1;
                        var targetSurfaceId = isFinal
                            ? outputSurfaceId
                            : new SurfaceId(GpuIdentity.Create(
                                "gpu-composite-intermediate",
                                _backend.Info.Kind.ToString(),
                                timing.SequenceNumber.ToString(),
                                ordinal.ToString(),
                                index.ToString()));

                        var previousSurfaceId = intermediateSurfaceId;
                        pendingSurfaceId = targetSurfaceId;
                        _backend.Composite(
                            targetSurfaceId,
                            format,
                            new GpuCompositeOperation(
                                isFirst ? request.BackgroundA.SurfaceId : previousSurfaceId!.Value,
                                isFirst ? request.BackgroundB.SurfaceId : previousSurfaceId!.Value,
                                isFirst ? request.Transition : GpuTransition.CutToA,
                                layer.Frame.SurfaceId,
                                layer.Opacity,
                                layer.Visible));
                        pendingSurfaceId = null;

                        if (previousSurfaceId is { } previous)
                            TryReleaseBackendSurface(previous);

                        intermediateSurfaceId = isFinal ? null : targetSurfaceId;
                    }
                }

                stopwatch.Stop();
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
                PublishResourceCountsUnsafe();
                Observe(
                    request.Transition.Kind == GpuTransitionKind.Cut ? "gpu.composite.cut" : "gpu.composite.dissolve",
                    timing.SequenceNumber,
                    null);
                Observe($"gpu.composite.layers:{request.Layers.Count}", timing.SequenceNumber, null);
                return GpuProcessingResult.Success(frame, stopwatch.Elapsed, request.Layers.Count);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                if (pendingSurfaceId is { } pending)
                    TryReleaseBackendSurface(pending);
                if (intermediateSurfaceId is { } intermediate && intermediate != pendingSurfaceId)
                    TryReleaseBackendSurface(intermediate);
                if (outputSurfaceId != pendingSurfaceId && outputSurfaceId != intermediateSurfaceId)
                    TryReleaseBackendSurface(outputSurfaceId);
                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.CompositeFailed,
                    $"GPU composite operation failed: {exception.GetType().Name}.");
                if (_backend.Info.Kind == GpuBackendKind.NvidiaCuda)
                    TransitionStateUnsafe(GpuProviderState.Failed, GpuProviderLifecycleReasonCodes.CompositeFailed, failure);
                Observe("gpu.composite.failed", timing.SequenceNumber, failure);
                return GpuProcessingResult.Rejected(failure, stopwatch.Elapsed, request.Layers.Count);
            }
        }
    }

    public bool TryExportMonitoringResource(
        GpuFrame frame,
        out GpuSharedMonitoringResourceLease? lease)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            EnsureRunning();
            lease = null;

            if (!CanExportSharedMonitoringResources)
            {
                if (_backend.Info.HardwareAccelerated &&
                    _backend is IGpuSharedMonitoringBackend { SupportsSharedMonitoringResources: true })
                {
                    var failure = new Failure(
                        GpuProviderLifecycleReasonCodes.MonitoringUnavailable,
                        "GPU shared monitoring export is unavailable; Program execution remains isolated.");
                    MarkDegradedUnsafe(GpuProviderLifecycleReasonCodes.MonitoringUnavailable, failure);
                    Observe("gpu.monitoring.resource.export_unavailable", frame.Descriptor.Timing.SequenceNumber, failure);
                }
                return false;
            }
            if (frame.IsDisposed || !_activeFrames.ContainsKey(frame.SurfaceId))
                throw new ObjectDisposedException(nameof(frame));

            if (_sharedMonitoringResources.Count >= SharedMonitoringResourceCapacity)
            {
                if (_rejectedSharedMonitoringExports < ulong.MaxValue)
                    _rejectedSharedMonitoringExports++;
                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.MonitoringCapacityExhausted,
                    "GPU shared monitoring resource capacity is exhausted; Program execution remains available.");
                MarkDegradedUnsafe(GpuProviderLifecycleReasonCodes.MonitoringCapacityExhausted, failure);
                Observe("gpu.monitoring.resource.capacity_exhausted", frame.Descriptor.Timing.SequenceNumber, failure);
                return false;
            }

            if (_sharedMonitoringResourceOrdinal == ulong.MaxValue)
                throw new InvalidOperationException("GPU monitoring resource ordinal is exhausted.");

            var sharingBackend = (IGpuSharedMonitoringBackend)_backend;
            GpuBackendMonitoringResource? backendResource;
            try
            {
                if (!sharingBackend.TryExportMonitoringResource(
                        frame.SurfaceId,
                        frame.Descriptor.Surface.Format,
                        out backendResource) ||
                    backendResource is null)
                {
                    var failure = new Failure(
                        GpuProviderLifecycleReasonCodes.MonitoringUnavailable,
                        "GPU shared monitoring export failed; Program execution remains isolated.");
                    MarkDegradedUnsafe(GpuProviderLifecycleReasonCodes.MonitoringUnavailable, failure);
                    Observe("gpu.monitoring.resource.export_unavailable", frame.Descriptor.Timing.SequenceNumber, failure);
                    return false;
                }
            }
            catch (Exception exception)
            {
                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.MonitoringUnavailable,
                    $"GPU shared monitoring export failed: {exception.GetType().Name}; Program execution remains isolated.");
                MarkDegradedUnsafe(GpuProviderLifecycleReasonCodes.MonitoringUnavailable, failure);
                Observe("gpu.monitoring.resource.export_failed", frame.Descriptor.Timing.SequenceNumber, failure);
                return false;
            }

            RestoreReadyFromDegradationUnsafe(
                GpuProviderLifecycleReasonCodes.MonitoringUnavailable,
                GpuProviderLifecycleReasonCodes.MonitoringCapacityExhausted,
                GpuProviderLifecycleReasonCodes.MonitoringReleaseFailed);

            var resourceId = new MonitoringResourceId(GpuIdentity.Create(
                "gpu-monitoring-resource",
                _monitoringProviderInstanceId.ToString(),
                frame.SurfaceId.ToString(),
                frame.Descriptor.Surface.Lifetime.Generation.ToString(),
                frame.Descriptor.Timing.SequenceNumber.ToString(),
                _sharedMonitoringResourceOrdinal++.ToString()));

            var descriptor = new MonitoringSharedResourceDescriptor(
                resourceId,
                _monitoringProviderInstanceId,
                frame.SurfaceId,
                frame.Descriptor.Surface.Format,
                SurfaceStorageDomain.Shared,
                MonitoringResourceAccessMode.ReadOnly,
                new SurfaceLifetimeDescriptor(
                    frame.Descriptor.Surface.Lifetime.Generation,
                    resourceId.Value),
                backendResource.Interop);

            _sharedMonitoringResources.Add(
                resourceId,
                new SharedMonitoringResourceEntry(descriptor, frame.Descriptor.Timing.SequenceNumber, backendResource));
            _sharedMonitoringSurfaceReferences[frame.SurfaceId] =
                _sharedMonitoringSurfaceReferences.GetValueOrDefault(frame.SurfaceId) + 1;
            if (_totalSharedMonitoringExports < ulong.MaxValue)
                _totalSharedMonitoringExports++;
            Volatile.Write(ref _observableActiveSharedMonitoringResourceCount, _sharedMonitoringResources.Count);
            Observe("gpu.monitoring.resource.exported", frame.Descriptor.Timing.SequenceNumber, null);

            lease = new GpuSharedMonitoringResourceLease(descriptor, ReleaseMonitoringResource);
            return true;
        }
    }

    public bool IsMonitoringResourceActive(MonitoringSharedResourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        lock (_gate)
        {
            if (_state is not (GpuProviderState.Ready or GpuProviderState.Degraded) ||
                descriptor.ProviderInstanceId != _monitoringProviderInstanceId ||
                !_sharedMonitoringResources.TryGetValue(descriptor.ResourceId, out var entry))
            {
                return false;
            }

            return entry.Descriptor == descriptor &&
                entry.Descriptor.SurfaceId == descriptor.SurfaceId &&
                entry.Descriptor.Lifetime.Generation == descriptor.Lifetime.Generation;
        }
    }

    public GpuReadbackLease RentReadback(GpuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            EnsureRunning();
            if (frame.IsDisposed || !_activeFrames.ContainsKey(frame.SurfaceId))
                throw new ObjectDisposedException(nameof(frame));

            var byteLength = RgbaFrameBuffer.RequiredByteLength(frame.Descriptor.Surface.Format);
            GpuReadbackLease lease;
            try
            {
                lease = _readbackPool.Rent(byteLength);
            }
            catch (Exception exception)
            {
                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.ReadbackPoolExhausted,
                    $"GPU readback lease acquisition failed: {exception.GetType().Name}.");
                MarkDegradedUnsafe(GpuProviderLifecycleReasonCodes.ReadbackPoolExhausted, failure);
                Observe("gpu.readback.pool_exhausted", frame.Descriptor.Timing.SequenceNumber, failure);
                throw;
            }

            try
            {
                _backend.ReadbackInto(frame.SurfaceId, frame.Descriptor.Surface.Format, lease.WritableSpan);
                RestoreReadyFromDegradationUnsafe(GpuProviderLifecycleReasonCodes.ReadbackPoolExhausted);
                return lease;
            }
            catch (Exception exception)
            {
                lease.Dispose();
                var failure = new Failure(
                    GpuProviderLifecycleReasonCodes.ReadbackFailed,
                    $"GPU readback failed: {exception.GetType().Name}.");
                if (_backend.Info.Kind == GpuBackendKind.NvidiaCuda)
                    TransitionStateUnsafe(GpuProviderState.Failed, GpuProviderLifecycleReasonCodes.ReadbackFailed, failure);
                Observe("gpu.readback.failed", frame.Descriptor.Timing.SequenceNumber, failure);
                throw;
            }
        }
    }

    public byte[] Readback(GpuFrame frame)
    {
        using var lease = RentReadback(frame);
        return lease.Memory.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == GpuProviderState.Disposed)
                return;

            if (_state is not (GpuProviderState.Stopped or GpuProviderState.Unavailable))
                Stop();

            _backend.Dispose();
            _readbackPool.Dispose();
            TransitionStateUnsafe(GpuProviderState.Disposed, GpuProviderLifecycleReasonCodes.Disposed, null);
        }
    }

    private Failure? ValidateCompositeRequest(GpuCompositeRequest request)
    {
        if (request.Layers.Count > GpuCompositeLimits.MaxActiveLayers)
        {
            return new Failure(
                "gpu.composite.layer_limit",
                $"GPU composite requests support at most '{GpuCompositeLimits.MaxActiveLayers}' ordered layers.");
        }

        if (request.Layers
            .Select(layer => layer.Frame.SurfaceId)
            .Distinct()
            .Count() != request.Layers.Count)
        {
            return new Failure("gpu.composite.layer_duplicate", "GPU composite layer surfaces must be unique within one request.");
        }

        if (request.BackgroundA.IsDisposed ||
            request.BackgroundB.IsDisposed ||
            request.Layers.Any(layer => layer.Frame.IsDisposed))
        {
            return new Failure("gpu.composite.surface_disposed", "GPU composite inputs must still own live surfaces.");
        }

        if (!_activeFrames.ContainsKey(request.BackgroundA.SurfaceId) || !_activeFrames.ContainsKey(request.BackgroundB.SurfaceId))
            return new Failure("gpu.composite.surface_foreign", "GPU composite backgrounds must belong to this provider instance.");

        if (request.Layers.Any(layer => !_activeFrames.ContainsKey(layer.Frame.SurfaceId)))
            return new Failure("gpu.composite.layer_foreign", "GPU composite layers must belong to this provider instance.");

        var format = request.BackgroundA.Descriptor.Surface.Format;
        if (request.BackgroundB.Descriptor.Surface.Format != format ||
            request.Layers.Any(layer => layer.Frame.Descriptor.Surface.Format != format))
        {
            return new Failure("gpu.composite.format_mismatch", "GPU composite inputs must use the same video format.");
        }

        var timing = request.BackgroundA.Descriptor.Timing;
        if (request.BackgroundB.Descriptor.Timing != timing ||
            request.Layers.Any(layer => layer.Frame.Descriptor.Timing != timing))
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
                if (_sharedMonitoringSurfaceReferences.ContainsKey(frame.SurfaceId))
                {
                    _deferredMonitoringSurfaceReleases.Add(frame.SurfaceId);
                    PublishResourceCountsUnsafe();
                    Observe("gpu.surface.monitoring_retained", frame.Descriptor.Timing.SequenceNumber, null);
                }
                else
                {
                    PublishResourceCountsUnsafe();
                    TryReleaseBackendSurface(frame.SurfaceId);
                    Observe("gpu.surface.released", frame.Descriptor.Timing.SequenceNumber, null);
                }
            }
        }
    }

    private void ReleaseMonitoringResource(MonitoringResourceId resourceId)
    {
        lock (_gate)
        {
            if (TryReleaseMonitoringResourceUnsafe(resourceId))
                return;

            throw new InvalidOperationException("GPU monitoring resource release failed and remains tracked for retry.");
        }
    }

    private bool TryReleaseMonitoringResourceUnsafe(MonitoringResourceId resourceId)
    {
        if (!_sharedMonitoringResources.TryGetValue(resourceId, out var entry))
            return true;

        try
        {
            entry.BackendResource.Dispose();
        }
        catch (Exception exception)
        {
            var failure = new Failure(
                GpuProviderLifecycleReasonCodes.MonitoringReleaseFailed,
                $"GPU monitoring resource release failed: {exception.GetType().Name}.");
            MarkDegradedUnsafe(GpuProviderLifecycleReasonCodes.MonitoringReleaseFailed, failure);
            Observe("gpu.monitoring.resource.release_failed", entry.SequenceNumber, failure);
            return false;
        }

        _sharedMonitoringResources.Remove(resourceId);
        var surfaceId = entry.Descriptor.SurfaceId;
        if (_sharedMonitoringSurfaceReferences.TryGetValue(surfaceId, out var references))
        {
            if (references <= 1)
            {
                _sharedMonitoringSurfaceReferences.Remove(surfaceId);
                if (_deferredMonitoringSurfaceReleases.Remove(surfaceId))
                    TryReleaseBackendSurface(surfaceId);
            }
            else
            {
                _sharedMonitoringSurfaceReferences[surfaceId] = references - 1;
            }
        }

        Volatile.Write(ref _observableActiveSharedMonitoringResourceCount, _sharedMonitoringResources.Count);
        PublishResourceCountsUnsafe();
        Observe("gpu.monitoring.resource.released", entry.SequenceNumber, null);
        RestoreReadyFromDegradationUnsafe(
            GpuProviderLifecycleReasonCodes.MonitoringReleaseFailed,
            GpuProviderLifecycleReasonCodes.MonitoringCapacityExhausted);
        return true;
    }

    private bool TryReleaseBackendSurface(SurfaceId surfaceId)
    {
        try
        {
            _backend.Release(surfaceId);
            _unreleasedBackendSurfaces.Remove(surfaceId);
            PublishResourceCountsUnsafe();
            return true;
        }
        catch (Exception exception)
        {
            _unreleasedBackendSurfaces.Add(surfaceId);
            PublishResourceCountsUnsafe();
            var failure = new Failure(
                GpuProviderLifecycleReasonCodes.SurfaceReleaseFailed,
                $"GPU surface release failed: {exception.GetType().Name}.");
            MarkDegradedUnsafe(GpuProviderLifecycleReasonCodes.SurfaceReleaseFailed, failure);
            Observe("gpu.surface.release_failed", null, failure);
            return false;
        }
    }

    private void PublishResourceCountsUnsafe()
    {
        Volatile.Write(
            ref _observableActiveSurfaceCount,
            checked(_activeFrames.Count + _deferredMonitoringSurfaceReleases.Count + _unreleasedBackendSurfaces.Count));
        Volatile.Write(
            ref _observableUnreleasedBackendSurfaceCount,
            _unreleasedBackendSurfaces.Count);
    }

    private static ProviderDescriptor CreateDescriptor(
        IGpuProcessingBackend backend,
        GpuProviderState state,
        string lifecycleReasonCode,
        Failure? lifecycleFailure)
    {
        var info = backend.Info;
        var providerId = new ProviderId(GpuIdentity.Create("gpu-provider", info.Kind.ToString(), info.DeviceName));
        var formats = info.Available ? V1Formats : Array.Empty<VideoFormat>();
        var capabilities = info.Available
            ? new List<ProviderCapabilityDescriptor>
            {
                CreateCapability("static", GpuCapabilityKinds.StaticRgbaSource, formats),
                CreateCapability("dynamic", GpuCapabilityKinds.DynamicRgbaSource, formats),
                CreateCapability("composite", GpuCapabilityKinds.CompositeRgba, formats),
                CreateCapability("cut", GpuCapabilityKinds.Cut, formats),
                CreateCapability("dissolve", GpuCapabilityKinds.Dissolve, formats)
            }
            : new List<ProviderCapabilityDescriptor>();
        if (info.Available &&
            info.HardwareAccelerated &&
            backend is IGpuSharedMonitoringBackend { SupportsSharedMonitoringResources: true })
        {
            capabilities.Add(CreateCapability("monitoring-shared-resource", GpuCapabilityKinds.SharedMonitoringResource, formats));
        }

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
        if (!info.Available || state == GpuProviderState.Unavailable)
        {
            availability = new ProviderAvailability(
                ProviderAvailabilityState.Unavailable,
                lifecycleFailure ?? info.Failure ?? new Failure(
                    GpuProviderLifecycleReasonCodes.BackendUnavailable,
                    "GPU backend is unavailable."));
        }
        else if (state is GpuProviderState.Failed or GpuProviderState.Disposed)
        {
            availability = new ProviderAvailability(
                ProviderAvailabilityState.Unavailable,
                lifecycleFailure ?? new Failure(lifecycleReasonCode, $"GPU provider lifecycle state is {state}."));
        }
        else if (state is GpuProviderState.Starting or GpuProviderState.Recovering or GpuProviderState.Degraded)
        {
            availability = new ProviderAvailability(
                ProviderAvailabilityState.Degraded,
                lifecycleFailure ?? new Failure(lifecycleReasonCode, $"GPU provider lifecycle state is {state}."));
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

    private Exception? DrainResourcesAndStopBackendUnsafe()
    {
        Exception? firstFailure = null;

        foreach (var resourceId in _sharedMonitoringResources.Keys.ToArray())
        {
            if (!TryReleaseMonitoringResourceUnsafe(resourceId) && firstFailure is null)
                firstFailure = new InvalidOperationException("One or more GPU monitoring resources could not be released.");
        }

        foreach (var surfaceId in _unreleasedBackendSurfaces.ToArray())
        {
            if (!TryReleaseBackendSurface(surfaceId) && firstFailure is null)
                firstFailure = new InvalidOperationException("One or more previously failed GPU surface releases could not be retried.");
        }

        var surfaces = new HashSet<SurfaceId>(_activeFrames.Keys);
        surfaces.UnionWith(_deferredMonitoringSurfaceReleases);

        foreach (var frame in _activeFrames.Values)
            frame.MarkReleased();
        _activeFrames.Clear();
        _deferredMonitoringSurfaceReleases.Clear();

        foreach (var surfaceId in surfaces)
        {
            if (!TryReleaseBackendSurface(surfaceId) && firstFailure is null)
                firstFailure = new InvalidOperationException("One or more GPU surfaces could not be released.");
        }

        try
        {
            _backend.Stop();
            _unreleasedBackendSurfaces.Clear();
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        Volatile.Write(ref _observableActiveSharedMonitoringResourceCount, _sharedMonitoringResources.Count);
        PublishResourceCountsUnsafe();
        return firstFailure;
    }

    private void MarkDegradedUnsafe(string reasonCode, Failure failure)
    {
        if (_state is GpuProviderState.Ready or GpuProviderState.Degraded)
            TransitionStateUnsafe(GpuProviderState.Degraded, reasonCode, failure);
    }

    private void RestoreReadyFromDegradationUnsafe(params string[] recoverableReasons)
    {
        if (_state != GpuProviderState.Degraded ||
            !recoverableReasons.Contains(_lifecycleReasonCode, StringComparer.Ordinal))
        {
            return;
        }

        TransitionStateUnsafe(
            GpuProviderState.Ready,
            GpuProviderLifecycleReasonCodes.Ready,
            null);
    }

    private void TransitionStateUnsafe(
        GpuProviderState next,
        string reasonCode,
        Failure? failure,
        bool advanceGeneration = false)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("GPU provider lifecycle reason code is required.", nameof(reasonCode));

        if (_state != next && !IsAllowedTransition(_state, next))
            throw new InvalidOperationException($"GPU provider lifecycle transition '{_state}' -> '{next}' is not allowed.");

        _state = next;
        _lifecycleReasonCode = reasonCode.Trim();
        _lifecycleFailure = failure;
        if (advanceGeneration)
        {
            if (_lifecycleGeneration == ulong.MaxValue)
                throw new InvalidOperationException("GPU provider lifecycle generation is exhausted.");
            _lifecycleGeneration++;
        }

        Observe($"gpu.provider.state.{next.ToString().ToLowerInvariant()}", null, failure);
    }

    private static bool IsAllowedTransition(GpuProviderState current, GpuProviderState next) =>
        (current, next) switch
        {
            (GpuProviderState.Unavailable, GpuProviderState.Starting) => true,
            (GpuProviderState.Unavailable, GpuProviderState.Stopped) => true,
            (GpuProviderState.Unavailable, GpuProviderState.Disposed) => true,
            (GpuProviderState.Stopped, GpuProviderState.Starting) => true,
            (GpuProviderState.Stopped, GpuProviderState.Disposed) => true,
            (GpuProviderState.Starting, GpuProviderState.Ready) => true,
            (GpuProviderState.Starting, GpuProviderState.Failed) => true,
            (GpuProviderState.Starting, GpuProviderState.Stopped) => true,
            (GpuProviderState.Ready, GpuProviderState.Degraded) => true,
            (GpuProviderState.Ready, GpuProviderState.Failed) => true,
            (GpuProviderState.Ready, GpuProviderState.Stopped) => true,
            (GpuProviderState.Degraded, GpuProviderState.Ready) => true,
            (GpuProviderState.Degraded, GpuProviderState.Failed) => true,
            (GpuProviderState.Degraded, GpuProviderState.Recovering) => true,
            (GpuProviderState.Degraded, GpuProviderState.Stopped) => true,
            (GpuProviderState.Failed, GpuProviderState.Recovering) => true,
            (GpuProviderState.Failed, GpuProviderState.Stopped) => true,
            (GpuProviderState.Recovering, GpuProviderState.Ready) => true,
            (GpuProviderState.Recovering, GpuProviderState.Failed) => true,
            (GpuProviderState.Recovering, GpuProviderState.Stopped) => true,
            (GpuProviderState.Stopped, GpuProviderState.Stopped) => true,
            (_, GpuProviderState.Disposed) when current != GpuProviderState.Disposed => true,
            _ => false
        };

    private void Observe(string code, ulong? sequenceNumber, Failure? failure) =>
        _observations.Add(new GpuObservation(_observationOrdinal++, code, sequenceNumber, failure));

    private sealed record SharedMonitoringResourceEntry(
        MonitoringSharedResourceDescriptor Descriptor,
        ulong SequenceNumber,
        GpuBackendMonitoringResource BackendResource);

    private void EnsureRunning()
    {
        ThrowIfDisposed();
        if (_state is not (GpuProviderState.Ready or GpuProviderState.Degraded))
            throw new InvalidOperationException($"GPU processing provider is not available for production work while state is '{_state}'.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_state == GpuProviderState.Disposed, this);
    }
}

public sealed class ManagedReferenceGpuBackend : IGpuProcessingBackend
{
    private const int MaxPooledAllocations = 8;

    private readonly object _gate = new();
    private readonly Dictionary<SurfaceId, Allocation> _surfaces = new();
    private readonly Dictionary<int, Stack<byte[]>> _freeAllocations = new();
    private int _pooledAllocationCount;
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

            foreach (var allocation in _surfaces.Values)
                ReturnBuffer(allocation.Pixels);
            _surfaces.Clear();
            _freeAllocations.Clear();
            _pooledAllocationCount = 0;
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

            var pixels = RentBuffer(rgbaPixels.Length);
            try
            {
                rgbaPixels.CopyTo(pixels);
                _surfaces.Add(surfaceId, new Allocation(format, pixels));
            }
            catch
            {
                ReturnBuffer(pixels);
                throw;
            }
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
            var output = RentBuffer(a.Pixels.Length);
            var weight = operation.Transition.BlendWeight;

            try
            {
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
            catch
            {
                ReturnBuffer(output);
                throw;
            }
        }
    }

    public byte[] Readback(SurfaceId surfaceId, VideoFormat format)
    {
        var result = new byte[RgbaFrameBuffer.RequiredByteLength(format)];
        ReadbackInto(surfaceId, format, result);
        return result;
    }

    public void ReadbackInto(SurfaceId surfaceId, VideoFormat format, Span<byte> destination)
    {
        lock (_gate)
        {
            EnsureRunning();
            var allocation = Get(surfaceId, format);
            if (destination.Length != allocation.Pixels.Length)
                throw new ArgumentException("GPU readback destination length does not match the surface.", nameof(destination));
            allocation.Pixels.AsSpan().CopyTo(destination);
        }
    }

    public void Release(SurfaceId surfaceId)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_surfaces.Remove(surfaceId, out var allocation))
                ReturnBuffer(allocation.Pixels);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var allocation in _surfaces.Values)
                ReturnBuffer(allocation.Pixels);
            _surfaces.Clear();
            _freeAllocations.Clear();
            _pooledAllocationCount = 0;
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

    private byte[] RentBuffer(int length)
    {
        if (_freeAllocations.TryGetValue(length, out var pool) && pool.TryPop(out var buffer))
        {
            _pooledAllocationCount--;
            if (pool.Count == 0)
                _freeAllocations.Remove(length);
            return buffer;
        }

        return new byte[length];
    }

    private void ReturnBuffer(byte[] buffer)
    {
        if (_pooledAllocationCount >= MaxPooledAllocations)
            return;

        if (!_freeAllocations.TryGetValue(buffer.Length, out var pool))
        {
            pool = new Stack<byte[]>();
            _freeAllocations.Add(buffer.Length, pool);
        }

        pool.Push(buffer);
        _pooledAllocationCount++;
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
