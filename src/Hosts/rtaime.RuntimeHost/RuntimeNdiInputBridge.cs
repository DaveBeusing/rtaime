// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.Ndi;

namespace rtaime.RuntimeHost;

/// <summary>
/// Runtime-owned NDI discovery/input bridge. Native lifecycle work is serialized independently
/// of Program execution; every publication is fenced by the committed input binding owner.
/// </summary>
internal sealed class RuntimeNdiInputBridge : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _discoveryGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly RuntimeProgramExecutionService _runtime;
    private readonly NdiDiscoveryService _discovery;
    private readonly Func<NdiInputConfiguration, NdiInputSession> _sessionFactory;
    private readonly Action<MediaSourceId, NdiInputMediaKind>? _beforePublication;
    private readonly Dictionary<MediaSourceId, SessionState> _sessions = [];
    private ulong _synchronizedGeneration;
    private bool _disposed;
    private Task? _disposeTask;

    public RuntimeNdiInputBridge(
        NdiNetworkOutputProvider provider,
        RuntimeProgramExecutionService runtime,
        INdiDiscoveryBackend? discoveryBackend = null,
        Func<NdiInputConfiguration, NdiInputSession>? sessionFactory = null,
        Action<MediaSourceId, NdiInputMediaKind>? beforePublication = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _discovery = provider.CreateDiscoveryService(discoveryBackend);
        _sessionFactory = sessionFactory ?? (configuration => provider.CreateInputSession(configuration));
        _beforePublication = beforePublication;
    }

    public MediaSourceDiscoverySnapshot RefreshDiscovery()
    {
        lock (_discoveryGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            return _discovery.Refresh(new UtcTimestamp(DateTimeOffset.UtcNow));
        }
    }

    public IReadOnlyList<MediaInputHealthSnapshot> InputHealth =>
        Array.AsReadOnly(SnapshotSessions()
            .Where(state => _runtime.IsNdiInputCurrent(state.Owner))
            .Select(state => state.Session.Snapshot).ToArray());

    // The caller must release Runtime state/capture/execution locks before entering.
    public void SynchronizeCommittedBindings(RuntimeNdiBindingSnapshot committed)
    {
        ArgumentNullException.ThrowIfNull(committed);
        _lifecycleGate.Wait();
        try
        {
            if (Volatile.Read(ref _disposed) || committed.Generation <= _synchronizedGeneration)
                return;
            _synchronizedGeneration = committed.Generation;

            var required = committed.Bindings
                .Where(_runtime.IsNdiInputCurrent)
                .ToDictionary(owner => owner.SourceId);
            foreach (var state in SnapshotSessions())
            {
                if (required.TryGetValue(state.Owner.SourceId, out var current) &&
                    ReferenceEquals(current, state.Owner))
                    continue;
                lock (_gate) _sessions.Remove(state.Owner.SourceId);
                try { RetireSessionAsync(state).GetAwaiter().GetResult(); }
                catch { /* Retirement cannot invalidate committed production authority. */ }
            }

            if (required.Count == 0)
                return;
            try { _ = RefreshDiscovery(); }
            catch { /* Discovery remains observational after commit. */ }

            foreach (var owner in required.Values)
            {
                lock (_gate)
                    if (_sessions.ContainsKey(owner.SourceId))
                        continue;
                try
                {
                    if (!_runtime.TryRegisterNdiSource(owner))
                        continue;
                    var discoveredId = new DiscoveredMediaSourceId(owner.Binding.ExternalSourceId!.Value);
                    if (!_discovery.TryResolve(discoveredId, out var endpoint) || endpoint is null)
                    {
                        MarkUnavailable(owner);
                        continue;
                    }

                    var configuration = new NdiInputConfiguration(
                        owner.SourceId, discoveredId, owner.Binding.SafeSourceIdentity!, endpoint);
                    var session = _sessionFactory(configuration);
                    var state = new SessionState(owner, session);
                    if (Volatile.Read(ref _disposed) || !_runtime.IsNdiInputCurrent(owner))
                    {
                        try { RetireSessionAsync(state).GetAwaiter().GetResult(); }
                        catch { /* A superseded receiver remains outside production authority. */ }
                        continue;
                    }
                    lock (_gate)
                    {
                        _sessions.Add(owner.SourceId, state);
                        state.Pump = Task.Run(() => PumpAsync(state));
                    }
                }
                catch
                {
                    MarkUnavailable(owner);
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task PumpAsync(SessionState state)
    {
        var cancellationToken = state.Stop.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var drained = false;
                while (!cancellationToken.IsCancellationRequested &&
                    state.Session.TryDequeue(out var sample) && sample is not null)
                {
                    drained = true;
                    _beforePublication?.Invoke(state.Owner.SourceId, sample.Kind);
                    if (sample.Kind == NdiInputMediaKind.Video)
                        _runtime.TrySetNdiInputContent(state.Owner, sample.Video!.RgbaPixels.Span);
                    else
                        _runtime.TrySetNdiAudioInput(state.Owner, sample.Audio!.InterleavedFloat32.Span);
                }

                var health = state.Session.Snapshot;
                if (!health.Connected)
                {
                    _runtime.TrySetNdiInputHealth(state.Owner,
                        health.Lifecycle is MediaInputLifecycleState.Lost or MediaInputLifecycleState.Faulted
                            ? RuntimeInputSignalState.Lost
                            : RuntimeInputSignalState.Recovering);
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
            MarkUnavailable(state.Owner);
        }
    }

    private SessionState[] SnapshotSessions()
    {
        lock (_gate)
            return _sessions.Values.ToArray();
    }

    private void MarkUnavailable(RuntimeNdiInputBinding owner)
    {
        try { _runtime.TrySetNdiInputHealth(owner, RuntimeInputSignalState.Lost); }
        catch { /* Input failure cannot change committed routing or a replacement owner. */ }
    }

    private static async Task RetireSessionAsync(SessionState state)
    {
        Exception? failure = null;
        try { state.Stop.Cancel(); }
        catch (Exception exception) { failure = exception; }
        try { await state.Pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { failure ??= exception; }
        try { await state.Session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure ??= exception; }
        state.Stop.Dispose();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            // Runtime disposal may begin under its admission locks. Native cleanup and
            // worker joins start on the management pool and never inherit those locks.
            return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Exception? failure = null;
            foreach (var state in SnapshotSessions())
            {
                lock (_gate) _sessions.Remove(state.Owner.SourceId);
                try { await RetireSessionAsync(state).ConfigureAwait(false); }
                catch (Exception exception) { failure ??= exception; }
            }
            try { lock (_discoveryGate) _discovery.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            // A commit that already released Runtime admission may still be waiting here.
            // Let it observe _disposed instead of disposing the semaphore under its waiter.
            _lifecycleGate.Release();
        }
    }

    private sealed class SessionState(RuntimeNdiInputBinding owner, NdiInputSession session)
    {
        public RuntimeNdiInputBinding Owner { get; } = owner;
        public NdiInputSession Session { get; } = session;
        public CancellationTokenSource Stop { get; } = new();
        public Task Pump { get; set; } = Task.CompletedTask;
    }
}
