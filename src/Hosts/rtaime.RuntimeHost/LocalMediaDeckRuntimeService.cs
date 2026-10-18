// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public readonly record struct LocalMediaDeckResources(int Capacity, int Reserved, long Rejected, long Evicted);

/// <summary>Bounded source-keyed sessions sharing the host's single Program cadence.</summary>
public sealed partial class LocalMediaDeckRuntimeService : IDisposable
{
    private sealed record Entry(MediaSourceId Source, LocalMediaDeckSlot Deck, long Order)
    {
        public MediaDeckOpenRequest? Request { get; init; }
        public PreparedExecutionContract? Execution { get; init; }
        public bool Retiring { get; set; }
        public bool NeedsPublication { get; set; } = true;
        public long? PublishedGeneration { get; set; }
    }
    private readonly object _gate = new();
    private readonly object _management = new();
    private readonly Entry?[] _entries;
    private readonly VideoFormat _format;
    private readonly LocalMediaResourceLedger _ledger;
    private readonly LocalMediaCapacityProfile _profile;
    private readonly LocalMediaFileProvider _provider;
    private readonly MediaSourceId[]? _allowedSources;
    private readonly HashSet<MediaSourceId> _knownSources = [];
    private readonly HashSet<MediaSourceId> _pendingInputClears = [];
    private MediaSourceId? _selected;
    private MediaSourceId? _program;
    private long _order, _rejected, _evicted;
    private int _probeReservations;
    private bool _disposed;

    public LocalMediaDeckRuntimeService() : this(VideoFormat.Hd1080p50Rgba8) { }
    public LocalMediaDeckRuntimeService(VideoFormat outputFormat, int maximumDecoders = 2,
        MediaSourceId[]? allowedSources = null, LocalMediaCapacityProfile profile = LocalMediaCapacityProfile.Conservative)
    {
        _ledger = new LocalMediaResourceLedger(outputFormat, maximumDecoders, profile);
        _profile = profile;
        _format = outputFormat;
        _provider = new LocalMediaFileProvider(outputFormat);
        _entries = new Entry[maximumDecoders];
        _allowedSources = allowedSources?.ToArray();
        if (_allowedSources is not null && (_allowedSources.Length < 1 || _allowedSources.Length > 8 ||
            profile == LocalMediaCapacityProfile.Conservative && _allowedSources.Length != 2 ||
            _allowedSources.Any(source => source.Value.IsEmpty) || _allowedSources.Distinct().Count() != _allowedSources.Length))
            throw new ArgumentException("Supply distinct configured source identities within the selected profile.", nameof(allowedSources));
    }
    internal LocalMediaDeckRuntimeService(LocalMediaFileProvider provider, LocalMediaRuntimeSession session)
        : this(VideoFormat.Hd1080p50Rgba8)
    {
        _provider = provider;
        AdoptSession(session);
        _selected = session.Probe.SourceId;
        _entries[0] = new Entry(session.Probe.SourceId, new LocalMediaDeckSlot(provider, session), ++_order);
    }
    internal LocalMediaDeckRuntimeService(LocalMediaFileProvider provider,
        LocalMediaRuntimeSession first, LocalMediaRuntimeSession second) : this(VideoFormat.Hd1080p50Rgba8)
    {
        if (first.Probe.SourceId == second.Probe.SourceId) throw new ArgumentException("Deck sources must differ.");
        _provider = provider;
        AdoptSession(first);
        AdoptSession(second);
        _selected = first.Probe.SourceId;
        _entries[0] = new Entry(first.Probe.SourceId, new LocalMediaDeckSlot(provider, first), ++_order);
        _entries[1] = new Entry(second.Probe.SourceId, new LocalMediaDeckSlot(provider, second), ++_order);
        _knownSources.Add(first.Probe.SourceId);
        _knownSources.Add(second.Probe.SourceId);
    }
    private void AdoptSession(LocalMediaRuntimeSession session)
    {
        if (!_ledger.TryReserve(Guid.NewGuid(), new(_ledger.RuntimeEpoch, session.Probe.SourceId,
            null, null, null, LocalMediaResourcePurpose.Resident), out var ticket, out var failure))
            throw new InvalidOperationException(failure!.Value.Message);
        session.AttachResources(ticket!);
    }
    internal bool AllowsAdditionalSources => _profile == LocalMediaCapacityProfile.DevelopmentFour;

    public void AdmitBoundary(V1RuntimeHostService runtime, ulong programSlot)
    {
        lock (_gate)
        {
            for (var i = 0; i < _entries.Length; i++)
                if (_entries[i] is { } entry) AdmitBoundary(runtime, entry.Source, programSlot);
            foreach (var source in _pendingInputClears)
            {
                runtime.ClearExternalAudioMeter(source);
                runtime.SetInputSignalState(source, V1InputSignalState.Lost);
            }
            _pendingInputClears.Clear();
        }
    }
    public LocalMediaResourceSnapshot ResourceSnapshot => _ledger.Snapshot;
    internal LocalMediaResourceLedger ResourceLedger => _ledger;
    private bool SourceAllowed(MediaSourceId source) =>
        _allowedSources is not null ? _allowedSources.Contains(source) :
        _knownSources.Count < (_profile == LocalMediaCapacityProfile.DevelopmentFour ? 8 : 2) || _knownSources.Contains(source);

    public LocalMediaDecodeSnapshot? GetDecodeSnapshot(MediaSourceId source) => Find(source)?.Deck.DecodeSnapshot;
    public ProviderDescriptor ProviderDescriptor => _provider.Descriptor;
    public LocalMediaDeckResources Resources
    {
        get { lock (_gate) return new(_entries.Length, _ledger.Snapshot.ReservedOwners, _rejected, _evicted); }
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
    public MediaDeckRuntimeSnapshot GetSnapshot(MediaSourceId? source)
    {
        lock (_gate)
        {
            var entry = Find(source);
            return entry is null ? MediaDeckRuntimeSnapshot.Unloaded :
                entry.Retiring ? new(MediaContractVersion.Current, MediaDeckState.Loading, sourceId: entry.Source) : entry.Deck.Snapshot;
        }
    }
    public LocalMediaDecodeSnapshot? DecodeSnapshot => Find(null)?.Deck.DecodeSnapshot;
    public LocalMediaPlaybackMeasurements? PlaybackMeasurements => Find(null)?.Deck.PlaybackMeasurements;
    public LocalMediaPlaybackMeasurements? GetPlaybackMeasurements(MediaSourceId source) => Find(source)?.Deck.PlaybackMeasurements;
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
                if (_probeReservations != 0 || _entries.Count(e => e is not null) >= _entries.Length)
                {
                    _rejected++;
                    return MediaAssetProbeResult.Rejected("runtime.media_deck.capacity",
                        "All local decoder reservations are occupied; close an inactive deck before probing.");
                }
            }
            lock (_gate) _probeReservations++;
            try
            {
                using var probe = new LocalMediaDeckSlot(_format, _ledger);
                var result = probe.Probe(path, assetId);
                if (result.Failure?.Code.StartsWith("runtime.media_resources.", StringComparison.Ordinal) == true)
                    Interlocked.Increment(ref _rejected);
                return result;
            }
            finally { lock (_gate) _probeReservations--; }
        }
    }

    public MediaDeckRuntimeSnapshot Open(MediaDeckOpenRequest request, PreparedExecutionContract preparedExecution)
        => OpenCore(request, preparedExecution, null);

    private MediaDeckRuntimeSnapshot OpenCore(MediaDeckOpenRequest request, PreparedExecutionContract preparedExecution,
        LocalMediaResourceLedger.LocalMediaResourceTicket? resources)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preparedExecution);
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MediaAssetPreflightObservation? activePreflight;
            lock (_gate)
            {
                if (_preflightQuarantine is not null || _preflightIdentityQuarantine is not null)
                    return Reject("runtime.media_deck.cleanup_failed", "Preflight retained a failed native owner; restart RuntimeHost.");
                activePreflight = _preflight is { Running: true } observation ? observation : null;
            }
            if (activePreflight is not null)
            {
                CancelPreflight(activePreflight.JobId);
                return Reject("runtime.media_deck.preflight_cancelling",
                    "Live deck admission cancelled optional preflight. Retry after its native owner releases capacity.");
            }
            if (!preparedExecution.Bindings.Any(binding => binding.MediaSourceId == request.SourceId &&
                binding.Resource.ProviderId == _provider.Descriptor.ProviderId &&
                binding.Resource.Kind == LocalMediaCapabilityKinds.MediaRoute &&
                _provider.Descriptor.Resources.Any(resource => resource == binding.Resource)))
                return Reject("runtime.local_media.binding_missing", "Prepare a current authoritative local media route for this source before retrying.");
            var existing = Find(request.SourceId);
            if (existing is { Retiring: false, Execution: not null } && existing.Request == request &&
                existing.Execution.PreparedExecutionId == preparedExecution.PreparedExecutionId &&
                existing.Execution.AuthoritySnapshot == preparedExecution.AuthoritySnapshot &&
                existing.Execution.PlanGeneration == preparedExecution.PlanGeneration &&
                existing.Execution.Bindings.SequenceEqual(preparedExecution.Bindings))
                return existing.Deck.Snapshot;
            ClearPreparation(request.SourceId);
            Entry? previous = null;
            var index = -1;
            lock (_gate)
            {
                if (!SourceAllowed(request.SourceId))
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
                if (previous.Source != request.SourceId)
                {
                    lock (_gate) _pendingInputClears.Add(previous.Source);
                    Interlocked.Increment(ref _evicted);
                }
            }
            var candidate = new Entry(request.SourceId, new LocalMediaDeckSlot(_format, _ledger), ++_order)
            { Request = request, Execution = preparedExecution };
            lock (_gate) { _entries[index] = candidate; _selected = request.SourceId; _knownSources.Add(request.SourceId); }
            var opened = candidate.Deck.Open(request, preparedExecution, resources);
            if (opened.Failure?.Code.StartsWith("runtime.media_resources.", StringComparison.Ordinal) == true)
                Interlocked.Increment(ref _rejected);
            return opened;
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
                if (entry is null || entry.Retiring || entry.Deck.Snapshot.Probe?.AssetId != command.AssetId ||
                    command.SourceId is { } source && source != entry.Source) continue;
                if (target is not null) throw new InvalidOperationException("Ambiguous asset; explicit deck source is required.");
                target = entry;
            }

            if (target is null) throw new InvalidOperationException("No loaded deck matches the command source and asset.");
            InvalidatePreparation(target.Source);
            var result = target.Deck.ApplyTransport(command);
            if (result.Succeeded && command.Kind is MediaTransportCommandKind.Seek or MediaTransportCommandKind.Stop or
                MediaTransportCommandKind.JumpToStart or MediaTransportCommandKind.StepBackward or MediaTransportCommandKind.StepForward)
                target.NeedsPublication = true;
            return result;
        }
    }

    public MediaDeckRuntimeSnapshot ObserveProgramSource(MediaSourceId? source)
    {
        lock (_gate) { _program = source; _ledger.ObserveProgram(source); }
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
                if (entry is not null || _pendingInputClears.Remove(source))
                {
                    runtime.ClearExternalAudioMeter(source);
                    runtime.SetInputSignalState(source, V1InputSignalState.Lost);
                }
                return;
            }
            var snapshot = entry.Deck.ProcessBoundary(out var admitted, programSlot);
            using var boundary = admitted;

            if (boundary is { Succeeded: true, Video: not null } && !boundary.RgbaPixels.IsEmpty)
            {
                // Fence residual samples even when a seek/loop prerolls between consecutive Program slots.
                var generation = boundary.Ownership?.Generation;
                if (entry.NeedsPublication || entry.PublishedGeneration != generation)
                    runtime.SetExternalAudioMeter(source, 0, 0, available: false);
                entry.PublishedGeneration = generation;
                runtime.SetExternalInputContent(source, boundary.RgbaPixels.Span,
                    boundary.Video.Surface.Format == runtime.Format ? V1InputSignalState.Valid : V1InputSignalState.Unstable);
                entry.NeedsPublication = false;
            }
            if (entry.NeedsPublication || snapshot.State is MediaDeckState.Loading or MediaDeckState.Error ||
                boundary?.Status == LocalMediaRuntimeBoundaryStatus.Buffering)
                runtime.SetInputSignalState(source, V1InputSignalState.Lost);
            if (boundary is { Succeeded: true } && !boundary.AudioPayload.IsEmpty)
                runtime.SetExternalAudioInput(source, boundary.AudioPayload.Span, available: true);
            else
                runtime.SetExternalAudioMeter(source, 0, 0,
                    !entry.NeedsPublication && snapshot.State is not (MediaDeckState.Error or MediaDeckState.Loading));
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
            ClearPreparation(entry.Source);
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
                    if (ReferenceEquals(_entries[i], entry))
                    {
                        _entries[i] = null;
                        _pendingInputClears.Add(entry.Source);
                    }
            return snapshot;
        }
    }
    public void Dispose()
    {
        lock (_management)
        {
            _disposed = true;
            _ledger.CloseAdmission();
            List<Exception>? failures = null;
            try { ClearPreparation(null); } catch (Exception exception) { (failures ??= []).Add(exception); }
            try { StopPreflight(); } catch (Exception exception) { (failures ??= []).Add(exception); }
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
            if (failures is not null) throw new IOException("Local deck shutdown retained failed owners.", new AggregateException(failures));
        }
    }
}
