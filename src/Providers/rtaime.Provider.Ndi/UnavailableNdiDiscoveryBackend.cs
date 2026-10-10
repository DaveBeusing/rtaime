// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Provider.Ndi;

internal sealed class UnavailableNdiDiscoveryBackend : INdiDiscoveryBackend
{
    public IReadOnlyList<NdiDiscoveredSourceEndpoint> GetCurrentSources(TimeSpan waitForChange) =>
        throw new DllNotFoundException("NDI runtime is unavailable.");

    public void Dispose()
    {
    }
}
