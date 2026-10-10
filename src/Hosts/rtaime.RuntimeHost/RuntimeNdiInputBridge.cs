// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Ndi;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// Runtime-owned NDI discovery/input bridge. Discovery is observational; receive sessions are created only
/// for provider-bound sources present in an already prepared/committed execution.
/// </summary>
internal sealed class RuntimeNdiInputBridge : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly NdiNetworkOutputProvider _provider;
    private readonly V1RuntimeHostService _runtime;
    private readonly NdiDiscoveryService _discovery;
    private readonly Dictionary<MediaSourceId, SessionState> _sessions = [];
    private bool _disposed;

    public RuntimeNdiInputBridge(NdiNetworkOutputProvider provider, V1RuntimeHostService runtime)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _discovery = _provider.CreateDiscoveryService();
    }

    public MediaSourceDiscoverySnapshot RefreshDiscovery() =>
        _discovery.Refresh(new UtcTimestamp(DateTimeOffset.UtcNow));

    public IReadOnlyList<MediaInputHealthSnapshot> InputHealth
    {
        get
        {
            lock (_gate)
                return Array.AsReadOnly(_sessions.Values.Select(state => state.Session.Snapshot).ToArray());
        }
    }

    public void SynchronizeCommittedBindings(IReadOnlyList<PreparedExecutionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var required = bindings
            .Where(binding =>
                binding.MediaSourceId is not null &&
                binding.ExternalSourceId is not null &&
                binding.Resource.ProviderId == _provider.Descriptor.ProviderId &&
                string.Equals(binding.Resource.Kind, MediaSourceCapabilityKinds.Input, StringComparison.Ordinal))
            .GroupBy(binding => binding.MediaSourceId!.Value)
            .Select(group => group.First())
            .ToArray();

        var requiredIds = required.Select(binding => binding.MediaSourceId!.Value).ToHashSet();
        foreach (var state in SnapshotSessions().Where(pair => !requiredIds.Contains(pair.Key)))
            TryRemoveSession(state.Key, state.Value);

        if (required.Length == 0)
            return;

        try
        {
            _ = RefreshDiscovery();
        }
        catch
        {
            // Discovery is observational. A provider/discovery failure after Runtime commit must not
            // invalidate already committed production authority.
        }

        foreach (var binding in required)
        {
            var sourceId = binding.MediaSourceId!.Value;
            if (HasSession(sourceId))
                continue;

            try
            {
                _runtime.RegisterExternalSource(sourceId);
                var discoveredId = new DiscoveredMediaSourceId(binding.ExternalSourceId!.Value);
                if (!_discovery.TryResolve(discoveredId, out var endpoint) || endpoint is null)
                {
                    MarkUnavailable(sourceId);
                    continue;
                }

                var configuration = new NdiInputConfiguration(
                    sourceId,
                    discoveredId,
                    binding.SafeSourceIdentity!,
                    endpoint);
                var session = _provider.CreateInputSession(configuration);
                var stop = new CancellationTokenSource();
                var pump = Task.Run(() => PumpAsync(session, sourceId, stop.Token));
                lock (_gate)
                    _sessions.Add(sourceId, new SessionState(session, stop, pump));
            }
            catch
            {
                MarkUnavailable(sourceId);
            }
        }
    }

    private async Task PumpAsync(NdiInputSession session, MediaSourceId sourceId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var drained = false;
                while (session.TryDequeue(out var sample) && sample is not null)
                {
                    drained = true;
                    if (sample.Kind == NdiInputMediaKind.Video)
                    {
                        var video = sample.Video!;
                        _runtime.SetExternalInputContent(sourceId, video.RgbaPixels.Span, V1InputSignalState.Valid);
                    }
                    else
                    {
                        var audio = sample.Audio!;
                        _runtime.SetExternalAudioInput(sourceId, audio.InterleavedFloat32.Span, available: true);
                    }
                }

                var health = session.Snapshot;
                if (!health.Connected)
                {
                    _runtime.SetInputSignalState(
                        sourceId,
                        health.Lifecycle is MediaInputLifecycleState.Lost or MediaInputLifecycleState.Faulted
                            ? V1InputSignalState.Lost
                            : V1InputSignalState.Recovering);
                    if (health.Lifecycle is MediaInputLifecycleState.Lost or MediaInputLifecycleState.Faulted)
                        _runtime.ClearExternalAudioMeter(sourceId);
                }

                if (!drained)
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            MarkUnavailable(sourceId);
        }
    }

    private bool HasSession(MediaSourceId sourceId)
    {
        lock (_gate)
            return _sessions.ContainsKey(sourceId);
    }

    private KeyValuePair<MediaSourceId, SessionState>[] SnapshotSessions()
    {
        lock (_gate)
            return _sessions.ToArray();
    }

    private void MarkUnavailable(MediaSourceId sourceId)
    {
        try
        {
            _runtime.RegisterExternalSource(sourceId);
            _runtime.SetInputSignalState(sourceId, V1InputSignalState.Lost);
            _runtime.ClearExternalAudioMeter(sourceId);
        }
        catch
        {
            // Input/provider failures remain observational after the committed Runtime transition.
        }
    }

    private void TryRemoveSession(MediaSourceId sourceId, SessionState state)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(sourceId))
                return;
        }

        try
        {
            state.Stop.Cancel();
            try { state.Pump.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            state.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Session retirement cannot invalidate production authority or reroute a committed source.
        }
        finally
        {
            state.Stop.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var pair in SnapshotSessions())
        {
            lock (_gate) _sessions.Remove(pair.Key);
            pair.Value.Stop.Cancel();
            try { await pair.Value.Pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await pair.Value.Session.DisposeAsync().ConfigureAwait(false);
            pair.Value.Stop.Dispose();
        }
        _discovery.Dispose();
    }

    private sealed record SessionState(NdiInputSession Session, CancellationTokenSource Stop, Task Pump);
}
