// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Provider.Ndi;

public sealed class NativeNdiDiscoveryBackend : INdiDiscoveryBackend
{
    private readonly NdiNativeApi _api;
    private IntPtr _finder;
    private bool _disposed;

    public NativeNdiDiscoveryBackend(string? runtimeLibraryPath = null)
    {
        var path = NdiRuntimeDiscovery.ResolveLibraryPath(runtimeLibraryPath)
            ?? throw new DllNotFoundException("NDI runtime is unavailable.");
        _api = NdiNativeApi.Load(path);
        try
        {
            if (!_api.Initialize())
                throw new PlatformNotSupportedException("NDI runtime initialization rejected the current CPU/platform.");
            _finder = _api.CreateFinder();
            if (_finder == IntPtr.Zero)
                throw new InvalidOperationException("NDI finder creation failed.");
        }
        catch
        {
            _api.Dispose();
            throw;
        }
    }

    public IReadOnlyList<NdiDiscoveredSourceEndpoint> GetCurrentSources(TimeSpan waitForChange)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (waitForChange < TimeSpan.Zero || waitForChange > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(waitForChange));

        if (waitForChange > TimeSpan.Zero)
        {
            var milliseconds = checked((uint)Math.Ceiling(waitForChange.TotalMilliseconds));
            _ = _api.WaitForSources(_finder, milliseconds);
        }

        return _api.GetCurrentSources(_finder);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var finder = Interlocked.Exchange(ref _finder, IntPtr.Zero);
        if (finder != IntPtr.Zero)
            _api.DestroyFinder(finder);
        _api.Dispose();
    }
}
