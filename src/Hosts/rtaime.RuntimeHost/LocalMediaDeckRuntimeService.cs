// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public readonly record struct LocalMediaDeckResources(int Capacity, int Reserved, long Rejected, long Evicted);

/// <summary>Bounded source-keyed sessions sharing the host's single Program cadence.</summary>
public sealed class LocalMediaDeckRuntimeService : IDisposable
{
    private sealed record Entry(MediaSourceId Source, LocalMediaDeckSlot Deck, long Order)
    {
        public bool Retiring { get; set; }
    }
    private readonly object _gate = new();
    private readonly object _management = new();
    private readonly Entry?[] _entries;
    private readonly VideoFormat _format;
    private readonly LocalMediaFileProvider _provider;
    private readonly MediaSourceId[]? _allowedSources;
    private readonly HashSet<MediaSourceId> _knownSources = [];
    private MediaSourceId? _selected;
    private MediaSourceId? _program;
    private long _order, _rejected, _evicted;
    private bool _disposed;

    public LocalMediaDeckRuntimeService() : this(VideoFormat.Hd1080p50Rgba8) { }
    public LocalMediaDeckRuntimeService(VideoFormat outputFormat, int maximumDecoders = 2,
        MediaSourceId[]? allowedSources = null)
    {
        if (maximumDecoders is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(maximumDecoders));
        _format = outputFormat;
        _provider = new LocalMediaFileProvider(outputFormat);
        _entries = new Entry[maximumDecoders];
        _allowedSources = allowedSources?.ToArray();
        if (_allowedSources is not null && (_allowedSources.Length != 2 || _allowedSources[0] == _allowedSources[1]))
            throw new ArgumentException("Two distinct production sources are required.", nameof(allowedSources));
    }
    internal LocalMediaDeckRuntimeService(LocalMediaFileProvider provider, LocalMediaRuntimeSession session)
        : this(session.Probe.VideoFormat)
    {
        _provider = provider;
        _selected = session.Probe.SourceId;
        _entries[0] = new Entry(session.Probe.SourceId, new LocalMediaDeckSlot(provider, session), ++_order);
    }
    public ProviderDescriptor ProviderDescriptor => _provider.Descriptor;
    public LocalMediaDeckResources Resources
    {
        get { lock (_gate) return new(_entries.Length, _entries.Count(e => e is not null), _rejected, _evicted); }
    }
    private Entry? Find(MediaSourceId? source)
    {
        lock (_gate)
        {
            var target = source ?? _selected;
            for (var i = 0; i < _entries.Length; i++)
                if (_entries[i]?.Source == target) return _entries[i];
            return null;
        }
    }
    public MediaDeckRuntimeSnapshot Snapshot => GetSnapshot(null);
    public MediaDeckRuntimeSnapshot GetSnapshot(MediaSourceId? source) => Find(source)?.Deck.Snapshot ?? MediaDeckRuntimeSnapshot.Unloaded;
    public LocalMediaDecodeSnapshot? DecodeSnapshot => Find(null)?.Deck.DecodeSnapshot;
    public LocalMediaPlaybackMeasurements? PlaybackMeasurements => Find(null)?.Deck.PlaybackMeasurements;
    public LocalMediaRuntimeBoundaryResult? LatestBoundary => Find(null)?.Deck.LatestBoundary;

    public MediaAssetProbeResult Probe(string path, MediaAssetId assetId)
    {
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate)
            {
                foreach (var entry in _entries)
                    if (entry?.Deck.Snapshot.Failure is { Code: "runtime.media_deck.cleanup_failed" } failure)
                        return MediaAssetProbeResult.Rejected(failure.Code, failure.Message);
                if (_entries.Count(e => e is not null) >= _entries.Length)
                {
                    _rejected++;
                    return MediaAssetProbeResult.Rejected("runtime.media_deck.capacity",
                        "All local decoder reservations are occupied; close an inactive deck before probing.");
                }
            }
            using var probe = new LocalMediaDeckSlot(_format);
            return probe.Probe(path, assetId);
        }
    }

    public MediaDeckRuntimeSnapshot Open(MediaDeckOpenRequest request, PreparedExecutionContract preparedExecution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preparedExecution);
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Entry? previous = null;
            var index = -1;
            lock (_gate)
            {
                if ((_allowedSources is not null && !_allowedSources.Contains(request.SourceId)) ||
                    _knownSources.Count == 2 && !_knownSources.Contains(request.SourceId))
                    return Reject("runtime.media_deck.source_invalid", "Deck source is outside the production source slots.");
                for (var i = 0; i < _entries.Length; i++)
                    if (_entries[i]?.Source == request.SourceId) { index = i; previous = _entries[i]; break; }
                if (previous is not null && (previous.Source == _program ||
                    previous.Deck.Snapshot.Transport?.State == MediaTransportState.Playing))
                    return Reject("runtime.media_deck.source_active", "Stop an off-Program deck before replacing its clip.");
                if (index < 0)
                    for (var i = 0; i < _entries.Length; i++)
                        if (_entries[i] is null) { index = i; break; }
                if (index < 0)
                {
                    // Stable oldest admission, only prepared inactive entries. Failed owners remain reserved.
                    for (var i = 0; i < _entries.Length; i++)
                    {
                        var entry = _entries[i]!;
                        if (entry.Source == _program || entry.Deck.Snapshot.State is not (MediaDeckState.Ready or MediaDeckState.Paused or MediaDeckState.Ended))
                            continue;
                        if (previous is null || entry.Order < previous.Order) { previous = entry; index = i; }
                    }
                    if (index < 0) { _rejected++; return Reject("runtime.media_deck.capacity", "No inactive prepared decoder reservation can be evicted."); }
                }
            }
            // Worker join, file IO, allocations and preroll never hold the registry/admission gate.
            if (previous is not null)
            {
                lock (_gate) previous.Retiring = true;
                var closed = previous.Deck.Close();
                if (closed.State == MediaDeckState.Error) { lock (_gate) previous.Retiring = false; return closed; }
                previous.Deck.Dispose();
                if (previous.Source != request.SourceId) Interlocked.Increment(ref _evicted);
            }
            var candidate = new Entry(request.SourceId, new LocalMediaDeckSlot(_format), ++_order);
            lock (_gate) { _entries[index] = candidate; _selected = request.SourceId; _knownSources.Add(request.SourceId); }
            return candidate.Deck.Open(request, preparedExecution);
        }
    }

    private static MediaDeckRuntimeSnapshot Reject(string code, string message) =>
        new(MediaContractVersion.Current, MediaDeckState.Error, failure: new Failure(code, message));

    public MediaTransportCommandResult ApplyTransport(MediaTransportCommand command)
    {
        Entry? target = null;
        lock (_gate)
        {
            for (var i = 0; i < _entries.Length; i++)
            {
                var entry = _entries[i];
                if (entry is null || entry.Deck.Snapshot.Probe?.AssetId != command.AssetId ||
                    command.SourceId is { } source && source != entry.Source) continue;
                if (target is not null) throw new InvalidOperationException("Ambiguous asset; explicit deck source is required.");
                target = entry;
            }
        }
        // The slot validates the asset again, fencing concurrent replacement.
        if (target is null) throw new InvalidOperationException("No loaded deck matches the command source and asset.");
        return target.Deck.ApplyTransport(command);
    }

    public MediaDeckRuntimeSnapshot ObserveProgramSource(MediaSourceId? source)
    {
        lock (_gate) _program = source;
        for (var i = 0; i < _entries.Length; i++)
        {
            Entry? entry;
            lock (_gate) entry = _entries[i];
            try { if (entry is { Retiring: false }) entry.Deck.ObserveProgramSource(source); } catch (ObjectDisposedException) { }
        }
        return Snapshot;
    }
    public MediaDeckRuntimeSnapshot ProcessBoundary(ulong? programSlot = null)
    {
        var snapshot = ProcessBoundary(out var boundary, programSlot);
        boundary?.Dispose();
        return snapshot;
    }
    public MediaDeckRuntimeSnapshot ProcessBoundary(out LocalMediaRuntimeBoundaryResult? boundary, ulong? programSlot = null) =>
        ProcessBoundary(null, out boundary, programSlot);
    public MediaDeckRuntimeSnapshot ProcessBoundary(MediaSourceId? source,
        out LocalMediaRuntimeBoundaryResult? boundary, ulong? programSlot = null)
    {
        var entry = Find(source);
        if (entry is null || entry.Retiring) { boundary = null; return MediaDeckRuntimeSnapshot.Unloaded; }
        try { return entry.Deck.ProcessBoundary(out boundary, programSlot); }
        catch (ObjectDisposedException) { boundary = null; return MediaDeckRuntimeSnapshot.Unloaded; }
    }

    public void AdmitBoundary(V1RuntimeHostService runtime, MediaSourceId source, ulong programSlot)
    {
        // Publication is fenced against slot replacement. All work here is bounded queue admission/copy.
        lock (_gate)
        {
            var entry = Find(source);
            if (entry is null || entry.Retiring)
            {
                if (_knownSources.Contains(source) || entry is not null)
                {
                    runtime.ClearExternalAudioMeter(source);
                    runtime.SetInputSignalState(source, V1InputSignalState.Lost);
                }
                return;
            }
            var snapshot = entry.Deck.ProcessBoundary(out var admitted, programSlot);
            using var boundary = admitted;
            if (boundary is { Succeeded: true, Video: not null } && !boundary.RgbaPixels.IsEmpty)
                runtime.SetExternalInputContent(source, boundary.RgbaPixels.Span,
                    boundary.Video.Surface.Format == runtime.Format ? V1InputSignalState.Valid : V1InputSignalState.Unstable);
            if (snapshot.State is MediaDeckState.Loading or MediaDeckState.Error ||
                boundary?.Status == LocalMediaRuntimeBoundaryStatus.Buffering)
                runtime.SetInputSignalState(source, V1InputSignalState.Lost);
            if (boundary is { Succeeded: true } && !boundary.AudioPayload.IsEmpty)
                runtime.SetExternalAudioInput(source, boundary.AudioPayload.Span, available: true);
            else
                runtime.SetExternalAudioMeter(source, 0, 0,
                    snapshot.State is not (MediaDeckState.Error or MediaDeckState.Loading));
        }
    }

    public MediaDeckRuntimeSnapshot Close() => Close(null);
    public MediaDeckRuntimeSnapshot Close(MediaSourceId? source)
    {
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = Find(source);
            if (entry is null) return MediaDeckRuntimeSnapshot.Unloaded;
            lock (_gate) entry.Retiring = true;
            var snapshot = entry.Deck.Close();
            if (snapshot.State == MediaDeckState.Error)
            {
                lock (_gate) entry.Retiring = false;
                return snapshot;
            }
            entry.Deck.Dispose();
            lock (_gate)
                for (var i = 0; i < _entries.Length; i++)
                    if (ReferenceEquals(_entries[i], entry)) _entries[i] = null;
            return snapshot;
        }
    }
    public void Dispose()
    {
        lock (_management)
        {
            _disposed = true;
            List<Exception>? failures = null;
            for (var i = 0; i < _entries.Length; i++)
            {
                Entry? entry;
                lock (_gate) entry = _entries[i];
                try
                {
                    lock (_gate) { if (entry is not null) entry.Retiring = true; }
                    entry?.Deck.Dispose();
                    lock (_gate) _entries[i] = null;
                }
                catch (Exception exception) { lock (_gate) _entries[i] = entry; (failures ??= []).Add(exception); }
            }
            if (failures is not null) throw new AggregateException("Local deck shutdown retained failed owners.", failures);
        }
    }
}
