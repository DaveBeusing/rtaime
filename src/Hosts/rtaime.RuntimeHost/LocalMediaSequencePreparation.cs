// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Security.Cryptography;
using rtaime.Core;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

/// <summary>
/// One unique decoded source/time-offset owner from a Control-validated sequence.
/// Only the existing Runtime ledger and native decode worker may own resources.
/// </summary>
internal sealed record SequenceMediaOwnerRequest(
    MediaSourceId SourceId, MediaAssetId AssetId, string LocalPath, string FingerprintSha256,
    long SourceInTicks, long TimelineStartTicks);

internal readonly record struct SequenceMediaPreparationReceipt(
    bool Prepared, Guid SequenceId, ulong AuthoringRevision, ulong Generation, ulong LoopPass,
    int DecoderOwners, Failure? Failure);

/// <summary>
/// Retained native slot evidence. Consumers must recheck current sequence/loop
/// authority at their final output sink; possessing old pixels is not permission
/// to render them after seek, replacement or host recovery.
/// </summary>
internal sealed class SequenceMediaFrameLease(
    LocalMediaFrameLease native, Guid sequenceId, ulong authoringRevision,
    MediaSourceId sourceId, string runtimeEpoch, SequenceRuntimePosition position) : IDisposable
{
    private LocalMediaFrameLease? _native = native;
    internal Guid SequenceId { get; } = sequenceId;
    internal ulong AuthoringRevision { get; } = authoringRevision;
    internal MediaSourceId SourceId { get; } = sourceId;
    internal string RuntimeEpoch { get; } = runtimeEpoch;
    internal SequenceRuntimePosition Position { get; } = position;
    internal long NativeGeneration { get; } = native.Generation;
    internal LocalMediaDecodedFrame Frame =>
        _native?.Frame ?? throw new ObjectDisposedException(nameof(SequenceMediaFrameLease));
    public void Dispose() => Interlocked.Exchange(ref _native, null)?.Dispose();
}

/// <summary>
/// Sequence decoder admission stays inside the already-owned local media registry.
/// Staged native owners are not Program sources and carry no output authority.
/// </summary>
public sealed partial class LocalMediaDeckRuntimeService
{
    private readonly object _sequenceGate = new();
    private PreparedSequenceMedia? _sequenceOwners;
    private const long SequenceTicksPerSecond = 240_000;

    /// <summary>Native open, fingerprint verification and preroll: management thread only, never a Program boundary.</summary>
    internal SequenceMediaPreparationReceipt PrepareSequenceMedia(
        PreparedExecutionContract execution,
        SequenceRuntimePositionMap map,
        IReadOnlyList<SequenceMediaOwnerRequest> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(sources);
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_sequenceGate)
            {
                if (_sequenceOwners is not null)
                    return RejectSequence(map, "runtime.sequence.already_prepared",
                        "Close the prior prepared sequence explicitly before preparing another revision.");
            }
            var error = ValidateSequencePreparation(execution, map, sources);
            if (error is { } problem)
                return RejectSequence(map, problem.Code, problem.Message);

            var requests = sources.Select(source => new LocalMediaResourceRequest(Guid.NewGuid(),
                new(_ledger.RuntimeEpoch, source.SourceId, execution.PreparedExecutionId,
                    execution.AuthoritySnapshot.Revision.Value, execution.PlanGeneration.Value,
                    LocalMediaResourcePurpose.Resident)
                { AuthorityStateId = execution.AuthoritySnapshot.StateId })).ToArray();

            if (!_ledger.TryReserveGroup(requests, out var tickets, out var failure))
                return RejectSequence(map, failure!.Value.Code, failure.Value.Message);

            var staged = new List<SequenceMediaDecodeOwner>(sources.Count);
            var encountered = 0;
            try
            {
                for (; encountered < sources.Count; encountered++)
                {
                    // The current sequence's entire resource group has been charged
                    // before its first path is examined or native decoder is opened.
                    cancellationToken.ThrowIfCancellationRequested();
                    staged.Add(OpenSequenceOwner(sources[encountered], tickets[encountered], cancellationToken));
                }
                var ready = new PreparedSequenceMedia(execution.PreparedExecutionId,
                    execution.AuthoritySnapshot.Revision.Value, map.SequenceId,
                    map.AuthoringRevision, map.Generation, 0, staged.ToArray());
                lock (_sequenceGate)
                    _sequenceOwners = ready;
                return new(true, map.SequenceId, map.AuthoringRevision, map.Generation, 0,
                    staged.Count, null);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                ArgumentException or InvalidOperationException or TimeoutException or OverflowException or OperationCanceledException)
            {
                // Existing workers own the joined native cleanup. Retained leases or
                // quarantined handles keep their charges through the same ledger.
                var cleanupFailure = CleanupSequenceOwners(staged);
                // The failing index may have been cancelled before it opened.
                // OwnerCompleted is idempotent even when the worker already joined;
                // quarantine always retains its charge.
                for (var index = encountered; index < tickets.Count; index++)
                    tickets[index].OwnerCompleted();
                if (cleanupFailure is not null || _ledger.Snapshot.QuarantinedOwners > 0)
                    return RejectSequence(map, "runtime.sequence.cleanup_quarantined",
                        "Native cleanup could not be confirmed. Restart RuntimeHost; sequence admission remains blocked.");
                return RejectSequence(map, "runtime.sequence.prepare_failed",
                    "Sequence source preparation failed without modifying Program: " + exception.Message);
            }
        }
    }

    /// <summary>
    /// Returns an existing decoder slot lease, never a copied frame or a fabricated
    /// frame when the worker is starved. Caller must Dispose the returned lease.
    /// Source timing is validated against the saved per-owner offset.
    /// </summary>
    internal bool TryTakeSequenceMedia(
        Guid sequenceId, ulong authoringRevision, MediaSourceId source,
        SequenceRuntimePosition position, long requestedSourceTick,
        out SequenceMediaFrameLease? result, out Failure? failure)
    {
        result = null;
        lock (_sequenceGate)
        {
            var prepared = _sequenceOwners;
            if (prepared is null || prepared.SequenceId != sequenceId ||
                prepared.AuthoringRevision != authoringRevision ||
                position.Generation != prepared.Generation || position.LoopPass != prepared.LoopPass)
            {
                failure = new("runtime.sequence.stale", "Sequence revision or media generation is not prepared. Revalidate and repreroll off Program.");
                return false;
            }
            var owner = prepared.Owners.SingleOrDefault(candidate => candidate.Request.SourceId == source);
            if (owner is null || requestedSourceTick < 0 ||
                requestedSourceTick > 24L * 3600 * SequenceTicksPerSecond ||
                position.Tick < 0 || position.Tick >= 24L * 3600 * SequenceTicksPerSecond ||
                (Int128)requestedSourceTick - position.Tick !=
                    (Int128)owner.Request.SourceInTicks - owner.Request.TimelineStartTicks)
            {
                failure = new("runtime.sequence.source_mismatch",
                    "Requested frame is not the prepared source/time offset.");
                return false;
            }

            var targetTicks = ToMediaTicks(requestedSourceTick);
            var selected = owner.Decode.TakeDue(targetTicks, out var ended, out var nativeFailure);
            if (nativeFailure is { } fault)
            {
                failure = fault;
                return false;
            }
            if (selected is null)
            {
                failure = new(ended ? "runtime.sequence.ended" : "runtime.sequence.buffering",
                    ended ? "Sequence decoder reached end of source." : "No prepared frame is due; do not block Program.");
                return false;
            }
            var maximumFrameAge = checked((long)(((Int128)TimeSpan.TicksPerSecond *
                _format.FrameRate.Denominator + _format.FrameRate.Numerator - 1) /
                _format.FrameRate.Numerator));
            if (selected.Generation != owner.Decode.Snapshot.Generation ||
                selected.Frame.Video.SourceId != source ||
                selected.PresentationTicks > targetTicks ||
                targetTicks - selected.PresentationTicks >= maximumFrameAge ||
                selected.Frame.Audio is { } audio &&
                    audio.Timing.PresentationTimestamp != selected.Frame.Video.Timing.PresentationTimestamp)
            {
                selected.Dispose();
                failure = new("runtime.sequence.stale_sample",
                    "Decoder returned a stale generation or mismatched source/audio observation.");
                return false;
            }
            result = new SequenceMediaFrameLease(selected, sequenceId, authoringRevision,
                source, _ledger.RuntimeEpoch, position);
            failure = null;
            return true;
        }
    }

    /// <summary>
    /// Final sink fence for a previously acquired native frame. The caller must
    /// check this after any potentially delayed compositor/audio scheduling step.
    /// </summary>
    internal bool IsCurrentSequenceFrame(SequenceMediaFrameLease frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_sequenceGate)
        {
            var prepared = _sequenceOwners;
            if (prepared is null || prepared.SequenceId != frame.SequenceId ||
                prepared.AuthoringRevision != frame.AuthoringRevision ||
                prepared.Generation != frame.Position.Generation ||
                prepared.LoopPass != frame.Position.LoopPass ||
                frame.RuntimeEpoch != _ledger.RuntimeEpoch)
                return false;
            var owner = prepared.Owners.SingleOrDefault(o => o.Request.SourceId == frame.SourceId);
            return owner is not null && owner.Decode.Snapshot.Generation == frame.NativeGeneration;
        }
    }

    /// <summary>
    /// Seek and loop repreroll are performed outside Program cadence. Until all
    /// owners confirm a new native decode epoch the old selection is unavailable.
    /// </summary>
    internal SequenceMediaPreparationReceipt RepositionSequenceMedia(
        Guid sequenceId, ulong authoringRevision, SequenceRuntimePosition next)
    {
        lock (_management)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PreparedSequenceMedia? original;
            lock (_sequenceGate)
            {
                original = _sequenceOwners;
                if (original is null || original.SequenceId != sequenceId ||
                    original.AuthoringRevision != authoringRevision ||
                    next.Tick < 0 || next.Generation < original.Generation ||
                    next.Generation == original.Generation && next.LoopPass <= original.LoopPass ||
                    next.Generation > original.Generation && next.LoopPass != 0)
                    return new(false, sequenceId, authoringRevision, next.Generation,
                        next.LoopPass, 0,
                        new("runtime.sequence.reposition_rejected", "Sequence identity, generation or loop pass is not the next valid prepared position."));
                _sequenceOwners = null;
            }
            try
            {
                foreach (var owner in original.Owners)
                {
                    var sourceTick = Math.Max(0, checked((long)((Int128)next.Tick +
                        owner.Request.SourceInTicks - owner.Request.TimelineStartTicks)));
                    var frame = ToSourceFrame(sourceTick);
                    var fault = owner.Decode.Reposition(frame);
                    if (fault is { } rejected) throw new IOException(rejected.Message);
                }
                foreach (var owner in original.Owners)
                {
                    if (!owner.Decode.WaitForPreroll(TimeSpan.FromSeconds(10)) ||
                        owner.Decode.Snapshot.Failure is not null)
                        throw new IOException(owner.Decode.Snapshot.Failure?.Message ??
                            "Native sequence worker did not complete repreroll.");
                }
                lock (_sequenceGate)
                    _sequenceOwners = original with { Generation = next.Generation, LoopPass = next.LoopPass };
                return new(true, sequenceId, authoringRevision, next.Generation,
                    next.LoopPass, original.Owners.Length, null);
            }
            catch (Exception exception) when (exception is IOException or OverflowException or InvalidOperationException)
            {
                var cleanup = CleanupSequenceOwners(original.Owners);
                return new(false, sequenceId, authoringRevision, next.Generation,
                    next.LoopPass, 0, new(cleanup is null ? "runtime.sequence.reposition_failed" :
                    "runtime.sequence.cleanup_quarantined",
                    cleanup is null ? "Native repreroll failed and the staged owners were closed: " + exception.Message :
                    "Native repreroll failed; uncertain cleanup was quarantined. Restart RuntimeHost."));
            }
        }
    }

    internal void CloseSequenceMedia()
    {
        lock (_management)
        {
            PreparedSequenceMedia? prepared;
            lock (_sequenceGate)
            {
                prepared = _sequenceOwners;
                _sequenceOwners = null;
            }
            if (prepared is null) return;
            var error = CleanupSequenceOwners(prepared.Owners);
            if (error is not null)
                throw new IOException("Sequence cleanup retained uncertain native owners.", error);
        }
    }

    private Failure? ValidateSequencePreparation(PreparedExecutionContract execution,
        SequenceRuntimePositionMap map, IReadOnlyList<SequenceMediaOwnerRequest> sources)
    {
        if (map.State != SequenceRuntimeTransportState.Stopped ||
            map.ProgramRate != _format.FrameRate || map.AuthoringRevision == 0 ||
            map.Generation != execution.PlanGeneration.Value)
            // Revision.Initial (0) is a valid committed production revision.
            // RuntimeHost checks the exact live authority state and revision
            // before invoking this admission; never substitute a positive
            // production revision solely to obtain a decoder ticket.
            return new("runtime.sequence.authority",
                "A stopped, source-bound sequence at the current Runtime rate and prepared authority is required.");
        if (sources.Count == 0 || sources.Count > _ledger.Capacity ||
            sources.Any(s => s is null || s.SourceId.Value.IsEmpty || s.AssetId.Value.IsEmpty ||
                string.IsNullOrWhiteSpace(s.LocalPath) ||
                s.FingerprintSha256 is null || s.FingerprintSha256.Length != 64 ||
                s.FingerprintSha256.Any(h => !Uri.IsHexDigit(h)) ||
                s.SourceInTicks < 0 || s.SourceInTicks > 24L * 3600 * SequenceTicksPerSecond ||
                s.TimelineStartTicks < 0 || s.TimelineStartTicks >= map.DurationTicks) ||
            sources.Select(s => s.SourceId).Distinct().Count() != sources.Count)
            return new("runtime.sequence.sources",
                "Sources must be finite, uniquely mapped, catalogue-fingerprinted media owners.");
        var bindings = new List<PreparedExecutionBinding>(sources.Count);
        foreach (var source in sources)
        {
            var matches = execution.Bindings.Where(b =>
                b.MediaSourceId == source.SourceId &&
                b.Resource.ProviderId == _provider.Descriptor.ProviderId &&
                b.Resource.Kind == LocalMediaCapabilityKinds.MediaRoute &&
                _provider.Descriptor.Capabilities.Any(c => c.Kind == LocalMediaCapabilityKinds.MediaRoute &&
                    c.CapabilityId == b.CapabilityId) &&
                _provider.Descriptor.Resources.Any(r => r.ResourceId == b.Resource.ResourceId &&
                    r.ProviderId == b.Resource.ProviderId && r.Kind == b.Resource.Kind)).ToArray();
            if (matches.Length != 1)
                return new("runtime.sequence.binding",
                    "Every sequence media owner requires exactly one genuine prepared local-media resource binding.");
            bindings.Add(matches[0]);
        }
        if (bindings.Select(b => b.Resource.ResourceId).Distinct().Count() != bindings.Count)
            return new("runtime.sequence.binding_conflict", "Sequence owners must use distinct prepared provider resource identities.");
        return null;
    }

    private SequenceMediaDecodeOwner OpenSequenceOwner(SequenceMediaOwnerRequest request,
        LocalMediaResourceLedger.LocalMediaResourceTicket ticket, CancellationToken cancellationToken)
    {
        FileStream? lockedIdentity = null;
        LocalMediaFileSource? nativeSource = null;
        LocalMediaDecodeWorker? decode = null;
        // This extra file lease keeps the entire group charged until its fingerprint
        // guard is released; worker cleanup alone does not free the native reservation.
        ticket.RetainPoolLease();
        try
        {
            var fullPath = Path.GetFullPath(request.LocalPath);
            if (!OperatingSystem.IsWindows() || Path.GetPathRoot(fullPath)?.Length != 3)
                throw new IOException("Sequence input must be a qualified direct local Windows file.");
            for (FileSystemInfo? item = new FileInfo(fullPath); item is not null;
                item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Sequence preparation cannot follow a reparse path.");
            lockedIdentity = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (lockedIdentity.Length is < 1 or > 32L * 1024 * 1024)
                throw new IOException("This bounded resident preparation accepts media files up to 32 MiB.");
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var scratch = new byte[65_536];
                var deadline = Stopwatch.StartNew();
                int count;
                while ((count = lockedIdentity.Read(scratch)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deadline.Elapsed > TimeSpan.FromSeconds(5))
                        throw new IOException("Sequence source identity verification exceeded its five-second budget.");
                    hash.AppendData(scratch, 0, count);
                }
                if (!Convert.ToHexString(hash.GetHashAndReset())
                    .Equals(request.FingerprintSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The local file no longer matches the saved sequence fingerprint.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var opened = _provider.TryOpen(fullPath, request.SourceId, request.AssetId);
            if (opened.Source is null)
                throw new IOException(opened.Failure?.Message ?? "Native decoder was not available.");
            nativeSource = opened.Source;
            if (nativeSource.Probe.SourceId != request.SourceId || nativeSource.Probe.AssetId != request.AssetId)
                throw new IOException("Native probe source/asset identity differs from the prepared owner.");
            decode = new LocalMediaDecodeWorker(nativeSource, null, ticket);
            nativeSource = null;
            var frame = ToSourceFrame(request.SourceInTicks);
            if (decode.Reposition(frame) is { } failure)
                throw new IOException(failure.Message);
            if (!decode.WaitForPreroll(TimeSpan.FromSeconds(10)) ||
                decode.Snapshot.Failure is not null)
                throw new IOException(decode.Snapshot.Failure?.Message ?? "Sequence decoder preroll timed out.");
            cancellationToken.ThrowIfCancellationRequested();
            var owner = new SequenceMediaDecodeOwner(request, lockedIdentity, decode, ticket);
            lockedIdentity = null;
            decode = null;
            return owner;
        }
        catch
        {
            Exception? cleanupError = null;
            try
            {
                if (decode is not null) decode.Dispose();
                else nativeSource?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupError = exception;
                ticket.Quarantine(new object[] { decode ?? (object?)nativeSource!, lockedIdentity! });
            }
            if (cleanupError is null)
            {
                try { lockedIdentity?.Dispose(); }
                catch (Exception exception)
                {
                    cleanupError = exception;
                    ticket.Quarantine(lockedIdentity);
                }
            }
            if (cleanupError is null)
            {
                ticket.ReleasePoolLease();
                ticket.OwnerCompleted();
            }
            if (cleanupError is not null)
                throw new IOException("Native sequence preparation failed and its cleanup remains quarantined.", cleanupError);
            throw;
        }
    }

    private long ToSourceFrame(long ticks) =>
        checked((long)((Int128)ticks * _format.FrameRate.Numerator /
            ((Int128)SequenceTicksPerSecond * _format.FrameRate.Denominator)));

    private static long ToMediaTicks(long sourceTick) =>
        checked((long)((Int128)sourceTick * TimeSpan.TicksPerSecond / SequenceTicksPerSecond));

    private static Exception? CleanupSequenceOwners(IEnumerable<SequenceMediaDecodeOwner> owners)
    {
        Exception? first = null;
        foreach (var owner in owners)
            try { owner.Dispose(); }
            catch (Exception exception) { first ??= exception; }
        return first;
    }

    private static SequenceMediaPreparationReceipt RejectSequence(
        SequenceRuntimePositionMap map, string code, string message) =>
        new(false, map.SequenceId, map.AuthoringRevision, map.Generation, 0, 0, new(code, message));

    private sealed record PreparedSequenceMedia(
        PreparedExecutionId PreparedExecution, ulong AuthorityRevision,
        Guid SequenceId, ulong AuthoringRevision, ulong Generation, ulong LoopPass,
        SequenceMediaDecodeOwner[] Owners);

    private sealed class SequenceMediaDecodeOwner(
        SequenceMediaOwnerRequest request, FileStream identityGuard,
        LocalMediaDecodeWorker decode, LocalMediaResourceLedger.LocalMediaResourceTicket ticket) : IDisposable
    {
        private bool _closed;
        internal SequenceMediaOwnerRequest Request { get; } = request;
        internal LocalMediaDecodeWorker Decode { get; } = decode;
        internal void CheckCurrent() => ObjectDisposedException.ThrowIf(_closed, this);
        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { decode.Dispose(); }
            catch
            {
                ticket.Quarantine(new object[] { decode, identityGuard });
                throw;
            }
            try { identityGuard.Dispose(); }
            catch
            {
                ticket.Quarantine(identityGuard);
                throw;
            }
            ticket.ReleasePoolLease();
            ticket.OwnerCompleted();
        }
    }
}
