// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public sealed partial class LocalMediaDeckRuntimeService
{
    private readonly string _preparationEpoch = Guid.NewGuid().ToString("N");
    private FileStream? _preparationFile;
    private MediaSourceId? _preparationSource;
    private string? _preparationFingerprint;
    private PreparedExecutionContract? _preparationExecution;
    private MediaDeckPreparation? _preparation;
    private long _preparationStarted;

    // Explicit, bounded two-deck preparation. Hash/open/wait never hold the Program registry gate.
    public MediaDeckRuntimeSnapshot Preload(MediaDeckOpenRequest request, PreparedExecutionContract execution, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(execution);
        if (fingerprint is null || fingerprint.Length != 64 || fingerprint.Any(c => !Uri.IsHexDigit(c)))
            return Reject("runtime.media_preload.identity_invalid", "An exact catalogue SHA-256 is required.");
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate)
            {
                if (_program == request.SourceId)
                    return Reject("runtime.media_preload.source_on_program", "Preload requires an off-Program source.");
                if ((_allowedSources is not null && !_allowedSources.Contains(request.SourceId)) ||
                    (_knownSources.Count == 2 && !_knownSources.Contains(request.SourceId)))
                    return Reject("runtime.media_deck.source_invalid", "Source is outside production A/B.");
                if (_probeReservations != 0 || _preflight is { Running: true } ||
                    _preflightQuarantine is not null || _preflightIdentityQuarantine is not null ||
                    _entries.Any(e => e?.Deck.Snapshot.Failure?.Code == "runtime.media_deck.cleanup_failed"))
                    return Reject("runtime.media_preload.capacity", "Optional preparation or uncertain cleanup occupies capacity.");
                if (Find(request.SourceId) is { } occupied &&
                    occupied.Deck.Snapshot.State is not (MediaDeckState.Ready or MediaDeckState.Paused or MediaDeckState.Ended))
                    return Reject("runtime.media_preload.deck_busy", "The selected target is active or has an uncertain owner; stop/close it deliberately.");
                if (Find(request.SourceId) is null && _entries.All(e => e is not null))
                    return Reject("runtime.media_preload.capacity", "No free A/B decoder slot; preload never evicts.");
            }
            if (Find(request.SourceId) is not null)
            {
                ClearPreparation(null);
                var closed = Close(request.SourceId);
                if (closed.State == MediaDeckState.Error) return closed;
            }
            lock (_gate) _probeReservations++;
            var reservationHeld = true;
            FileStream? identity = null;
            try
            {
                // Keep the exact selected file read-locked until handoff/cancel/close.
                // The initial slice deliberately rejects large files rather than increasing IPC deadlines.
                var full = Path.GetFullPath(request.Path);
                if (!OperatingSystem.IsWindows() || Path.GetPathRoot(full)?.Length != 3)
                    return Reject("runtime.media_preload.path_unsupported", "Use a directly addressed local Windows file.");
                for (FileSystemInfo? item = new FileInfo(full); item is not null;
                    item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        return Reject("runtime.media_preload.path_unsupported", "Preload does not follow reparse paths.");
                identity = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                if (identity.Length is < 1 or > 32L * 1024 * 1024)
                    return Reject("runtime.media_preload.file_budget", "Resident preload verification is limited to 32 MiB; use ordinary deck open for larger files.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                var deadline = Stopwatch.StartNew();
                int read;
                while ((read = identity.Read(buffer)) != 0)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                        return Reject("runtime.media_preload.identity_timeout", "File identity verification exceeded its bounded budget.");
                    hash.AppendData(buffer, 0, read);
                    Thread.Sleep(8); // Existing optional-work rate: approximately 8 MiB/s requested reads.
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                    return Reject("runtime.media_preload.identity_changed", "File no longer matches the selected catalogue asset.");
                ClearPreparation(null);
                lock (_gate) _probeReservations--;
                reservationHeld = false;
                var opened = Open(request, execution);
                if (opened.State != MediaDeckState.Ready) return opened;
                _preparationFile = identity;
                identity = null;
                _preparationSource = request.SourceId;
                _preparationFingerprint = fingerprint.ToUpperInvariant();
                _preparationExecution = execution;
                return opened;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Reject("runtime.media_preload.identity_failed", exception.Message);
            }
            finally
            {
                identity?.Dispose();
                if (reservationHeld) lock (_gate) _probeReservations--;
            }
        }
    }

    public MediaDeckPreparation PrepareLoadedNext(MediaSourceId source, MediaAssetId asset)
    {
        lock (_management)
        {
            var entry = Find(source) ?? throw new InvalidOperationException("Preload owner is absent.");
            if (_preparationSource != source || _preparationFile is null || _preparationExecution is null)
                throw new InvalidOperationException("No verified preload owns this source.");
            lock (_gate)
            {
                if (_program == source || entry.Retiring || entry.Deck.Snapshot.Probe?.AssetId != asset)
                    throw new InvalidOperationException("Preload source changed or is on Program.");
            }
            entry.Deck.PrepareNext();
            var snapshot = entry.Deck.Snapshot;
            var decode = entry.Deck.DecodeSnapshot ?? throw new InvalidOperationException("Decode evidence is absent.");
            if (snapshot.Probe?.AudioCodec == MediaAudioCodec.None || !entry.Deck.HasPreparedAudio)
                throw new InvalidOperationException("Prepared-next requires actual decoded audio; video-only remains an ordinary manual deck workflow.");
            _preparationStarted = Stopwatch.GetTimestamp();
            return _preparation = new(Guid.NewGuid().ToString("N"), _preparationEpoch, source.ToString(),
                asset.ToString(), _preparationExecution.PreparedExecutionId.ToString(),
                _preparationExecution.AuthoritySnapshot.Revision.Value, decode.Generation,
                snapshot.Transport!.Position.CurrentFrame, snapshot.Transport.EffectiveEndFrame,
                _preparationFingerprint!, DateTimeOffset.UtcNow.AddSeconds(30));
        }
    }

    public void CancelPreparation(string ticket)
    {
        lock (_management)
            if (_preparation?.TicketId == ticket) ClearPreparation(_preparationSource);
    }

    // Existing execution commit, protected atomically against deck mutation/replacement.
    // Caller supplies the ordinary authorized Runtime apply; this creates no routing authority.
    public T WithPreparedNext<T>(MediaDeckPreparation expected, bool consume, Func<T> apply, Func<T, bool> committed)
    {
        lock (_management)
        lock (_gate)
        {
            var source = new MediaSourceId(Identity.Parse(expected.SourceId));
            var entry = Find(source);
            var snapshot = entry?.Deck.Snapshot;
            var decode = entry?.Deck.DecodeSnapshot;
            if (_preparation != expected || expected.RuntimeEpoch != _preparationEpoch ||
                DateTimeOffset.UtcNow >= expected.ExpiresAtUtc || Stopwatch.GetElapsedTime(_preparationStarted) >= TimeSpan.FromSeconds(30) || _program == source ||
                entry is null || entry.Retiring || snapshot?.Probe?.AssetId.ToString() != expected.AssetId ||
                snapshot.Transport?.State is not (MediaTransportState.Ready or MediaTransportState.Paused) ||
                snapshot.Transport.Position.CurrentFrame != expected.PositionFrame ||
                snapshot.Transport.EffectiveEndFrame != expected.EndFrame ||
                decode?.Generation != expected.DecodeGeneration || decode.Value.QueuedFrames < 3 ||
                decode.Value.Failure is not null || !entry.Deck.HasPreparedAudio)
                throw new InvalidOperationException("Prepared-next evidence is stale, replaced, expired or not ready. Prepare again.");
            var result = apply();
            if (consume && committed(result)) _preparation = null;
            return result;
        }
    }

    private void InvalidatePreparation(MediaSourceId source)
    {
        if (_preparationSource == source) _preparation = null;
    }

    // Management callers only; retained native/session ownership is handled by the existing deck lifecycle.
    private void ClearPreparation(MediaSourceId? source)
    {
        if (source is not null && _preparationSource != source) return;
        _preparation = null;
        _preparationFile?.Dispose();
        _preparationFile = null;
        _preparationSource = null;
        _preparationFingerprint = null;
        _preparationExecution = null;
    }
}
