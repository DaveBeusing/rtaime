// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using System.Text;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.Ndi;

public sealed record NdiDiscoveredSourceEndpoint(string NdiName, string UrlAddress)
{
    public string NdiName { get; } = string.IsNullOrWhiteSpace(NdiName)
        ? throw new ArgumentException("NDI source name is required.", nameof(NdiName))
        : NdiName.Trim();

    public string UrlAddress { get; } = string.IsNullOrWhiteSpace(UrlAddress)
        ? throw new ArgumentException("NDI source URL is required.", nameof(UrlAddress))
        : UrlAddress.Trim();
}

public interface INdiDiscoveryBackend : IDisposable
{
    IReadOnlyList<NdiDiscoveredSourceEndpoint> GetCurrentSources(TimeSpan waitForChange);
}

public sealed class NdiDiscoveryService : IDisposable
{
    public const int DefaultMaximumRetainedResults = 64;
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly INdiDiscoveryBackend _backend;
    private readonly int _maximumRetainedResults;
    private readonly TimeSpan _expiry;
    private readonly Dictionary<DiscoveredMediaSourceId, Entry> _entries = new();
    private bool _disposed;

    public NdiDiscoveryService(
        INdiDiscoveryBackend backend,
        int maximumRetainedResults = DefaultMaximumRetainedResults,
        TimeSpan? expiry = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        if (maximumRetainedResults is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedResults));
        _maximumRetainedResults = maximumRetainedResults;
        _expiry = expiry ?? DefaultExpiry;
        if (_expiry <= TimeSpan.Zero || _expiry > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(expiry), "NDI discovery expiry must be positive and no greater than five minutes.");
    }

    public MediaSourceDiscoverySnapshot Refresh(
        UtcTimestamp observedAt,
        TimeSpan? waitForChange = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<NdiDiscoveredSourceEndpoint> current;
        try
        {
            current = _backend.GetCurrentSources(waitForChange ?? TimeSpan.Zero)
                ?? throw new InvalidDataException("NDI discovery backend returned no source collection.");
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            PlatformNotSupportedException or
            InvalidOperationException or
            IOException)
        {
            return MarkUnavailable(observedAt, exception);
        }

        var now = observedAt.Value;
        var canonical = current
            .Select(endpoint => (Endpoint: endpoint, Id: CreateStableId(endpoint.NdiName)))
            .GroupBy(item => item.Id)
            .Select(group => group
                .OrderBy(item => item.Endpoint.UrlAddress, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.Id.ToString(), StringComparer.Ordinal)
            .Take(_maximumRetainedResults)
            .ToArray();

        lock (_gate)
        {
            var observedIds = canonical.Select(item => item.Id).ToHashSet();
            foreach (var item in canonical)
            {
                _entries[item.Id] = new Entry(
                    item.Endpoint,
                    now,
                    AvailableDescriptor(item.Id, item.Endpoint, observedAt));
            }

            foreach (var key in _entries.Keys.ToArray())
            {
                if (observedIds.Contains(key))
                    continue;

                var entry = _entries[key];
                if (now - entry.LastSeenAt > _expiry)
                {
                    _entries.Remove(key);
                    continue;
                }

                entry.Descriptor = UnavailableDescriptor(
                    key,
                    entry.Endpoint,
                    new UtcTimestamp(entry.LastSeenAt),
                    "network.input.ndi_source_missing",
                    "Previously discovered NDI source is currently unavailable.");
            }

            TrimToBound();
            return SnapshotUnsafe(
                observedAt,
                new ProviderAvailability(ProviderAvailabilityState.Available));
        }
    }

    public bool TryResolve(
        DiscoveredMediaSourceId sourceId,
        out NdiDiscoveredSourceEndpoint? endpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_entries.TryGetValue(sourceId, out var entry) &&
                entry.Descriptor.Availability.State == ProviderAvailabilityState.Available)
            {
                endpoint = entry.Endpoint;
                return true;
            }
        }

        endpoint = null;
        return false;
    }

    public static DiscoveredMediaSourceId CreateStableId(string ndiName)
    {
        if (string.IsNullOrWhiteSpace(ndiName))
            throw new ArgumentException("NDI source name is required.", nameof(ndiName));

        var canonical = ndiName.Trim().Normalize(NormalizationForm.FormC);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical), hash);
        return new DiscoveredMediaSourceId(new Identity(new Guid(hash[..16])));
    }

    internal static string DisplayName(string ndiName)
    {
        var canonical = ndiName.Trim();
        var open = canonical.LastIndexOf(" (", StringComparison.Ordinal);
        if (open >= 0 && canonical.EndsWith(')') && open + 2 < canonical.Length - 1)
            return canonical[(open + 2)..^1].Trim();
        return canonical;
    }

    internal static string SafeIdentity(string ndiName)
    {
        var sanitized = new string(ndiName
            .Trim()
            .Where(character => !char.IsControl(character))
            .Select(character => character is '/' or '\\' or '?' or '#' ? '-' : character)
            .ToArray());
        return $"ndi://{sanitized}";
    }

    private MediaSourceDiscoverySnapshot MarkUnavailable(UtcTimestamp observedAt, Exception exception)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Descriptor = UnavailableDescriptor(
                    entry.Descriptor.SourceId,
                    entry.Endpoint,
                    new UtcTimestamp(entry.LastSeenAt),
                    "network.input.ndi_discovery_unavailable",
                    $"NDI discovery is unavailable: {exception.GetType().Name}.");
            }

            return SnapshotUnsafe(
                observedAt,
                new ProviderAvailability(
                    ProviderAvailabilityState.Unavailable,
                    new Failure(
                        "network.input.ndi_discovery_unavailable",
                        $"NDI discovery is unavailable: {exception.GetType().Name}.")));
        }
    }

    private MediaSourceDiscoverySnapshot SnapshotUnsafe(
        UtcTimestamp observedAt,
        ProviderAvailability availability) =>
        new(
            NdiNetworkOutputProvider.ProviderIdentity,
            availability,
            observedAt,
            _entries.Values
                .Select(entry => entry.Descriptor)
                .OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(descriptor => descriptor.SourceId.ToString(), StringComparer.Ordinal)
                .ToArray(),
            _maximumRetainedResults);

    private static DiscoveredMediaSourceDescriptor AvailableDescriptor(
        DiscoveredMediaSourceId id,
        NdiDiscoveredSourceEndpoint endpoint,
        UtcTimestamp observedAt) =>
        new(
            id,
            NdiNetworkOutputProvider.ProviderIdentity,
            DisplayName(endpoint.NdiName),
            SafeIdentity(endpoint.NdiName),
            new ProviderAvailability(ProviderAvailabilityState.Available),
            new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
            AudioFormat.Stereo48kFloat32,
            observedAt);

    private static DiscoveredMediaSourceDescriptor UnavailableDescriptor(
        DiscoveredMediaSourceId id,
        NdiDiscoveredSourceEndpoint endpoint,
        UtcTimestamp lastSeenAt,
        string code,
        string message) =>
        new(
            id,
            NdiNetworkOutputProvider.ProviderIdentity,
            DisplayName(endpoint.NdiName),
            SafeIdentity(endpoint.NdiName),
            new ProviderAvailability(
                ProviderAvailabilityState.Unavailable,
                new Failure(code, message)),
            new[] { VideoFormat.Hd1080p50Rgba8, VideoFormat.Hd1080p59_94Rgba8 },
            AudioFormat.Stereo48kFloat32,
            lastSeenAt);

    private void TrimToBound()
    {
        if (_entries.Count <= _maximumRetainedResults)
            return;

        foreach (var key in _entries
            .OrderByDescending(pair => pair.Value.Descriptor.Availability.State == ProviderAvailabilityState.Available)
            .ThenByDescending(pair => pair.Value.LastSeenAt)
            .ThenBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Skip(_maximumRetainedResults)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _entries.Remove(key);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _backend.Dispose();
    }

    private sealed class Entry
    {
        public Entry(
            NdiDiscoveredSourceEndpoint endpoint,
            DateTimeOffset lastSeenAt,
            DiscoveredMediaSourceDescriptor descriptor)
        {
            Endpoint = endpoint;
            LastSeenAt = lastSeenAt;
            Descriptor = descriptor;
        }

        public NdiDiscoveredSourceEndpoint Endpoint { get; }
        public DateTimeOffset LastSeenAt { get; }
        public DiscoveredMediaSourceDescriptor Descriptor { get; set; }
    }
}
