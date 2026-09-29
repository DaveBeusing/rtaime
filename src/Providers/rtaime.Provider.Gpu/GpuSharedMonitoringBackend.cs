// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Provider.Gpu;

public interface IGpuSharedMonitoringBackend
{
    bool SupportsSharedMonitoringResources { get; }

    bool TryExportMonitoringResource(
        SurfaceId surfaceId,
        VideoFormat format,
        out GpuBackendMonitoringResource? resource);
}

public sealed class GpuBackendMonitoringResource : IDisposable
{
    private Action? _release;

    public GpuBackendMonitoringResource(
        MonitoringSharedResourceInteropDescriptor interop,
        Action release)
    {
        if (!interop.IsPresentable)
            throw new ArgumentException("GPU monitoring backend resources require presentable interop metadata.", nameof(interop));

        Interop = interop;
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public MonitoringSharedResourceInteropDescriptor Interop { get; }
    public bool IsDisposed => Volatile.Read(ref _release) is null;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
