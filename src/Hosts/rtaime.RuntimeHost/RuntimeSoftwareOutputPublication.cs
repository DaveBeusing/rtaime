// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Collections.ObjectModel;
using rtaime.Provider.Gpu;

namespace rtaime.RuntimeHost;

/// <summary>
/// A single immutable software output-generation visible to all target readers.
/// Retains the GPU frames until the final reader closes, even after replacement.
/// </summary>
internal sealed class RuntimePublishedOutputGeneration(
    RuntimeMappedOutputFrameSet frames, ulong executionRevision, ulong programSequence)
{
    internal RuntimeMappedOutputFrameSet Frames { get; } = frames;
    internal ulong ExecutionRevision { get; } = executionRevision;
    internal ulong ProgramSequence { get; } = programSequence;
    internal int Readers;
    internal bool Retired;
}

public sealed record RuntimeSoftwareOutputPublicationResult(
    bool Succeeded, string Code, string Detail, ulong? PublishedProgramSequence);

public sealed record RuntimeSoftwareOutputPublicationSnapshot(
    ulong? ActiveExecutionRevision, ulong? ActiveProgramSequence,
    int ActiveTargets, bool HasPreparedGroup, int RetainedOldGenerations,
    int ActiveReaders, int RetainedReaders, bool Closed)
{
    public int DeferredDisposalGroups { get; init; }
}

/// <summary>
/// A bounded lease on one complete multi-target frame generation. Consumers
/// observe all output target frames from the same Program slot and revision.
/// </summary>
public sealed class RuntimeSoftwareOutputPublicationLease : IDisposable
{
    private RuntimeSoftwareOutputPublication? _owner;
    private readonly RuntimePublishedOutputGeneration _generation;

    internal RuntimeSoftwareOutputPublicationLease(
        RuntimeSoftwareOutputPublication owner, RuntimePublishedOutputGeneration generation)
    {
        _owner = owner;
        _generation = generation;
    }

    public ulong ExecutionRevision => _generation.ExecutionRevision;
    public ulong ProgramSequence => _generation.ProgramSequence;

    public IReadOnlyDictionary<string, GpuFrame> Frames =>
        _owner is null
            ? throw new ObjectDisposedException(nameof(RuntimeSoftwareOutputPublicationLease))
            : _generation.Frames.Frames;

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_generation);
}

/// <summary>
/// Atomic, in-process publication of one multi-target software frame set.
/// It has no Program scheduler, hardware-output permission or Control authority:
/// callers must invoke CommitAtBoundary from the existing authorized frame slot.
/// </summary>
public sealed class RuntimeSoftwareOutputPublication : IDisposable
{
    private readonly object _gate = new();
    private readonly Guid _runtimeEpoch;
    private readonly int _maximumRetainedOldGenerations;
    private readonly List<RuntimePublishedOutputGeneration> _retired;
    private readonly Queue<RuntimeMappedOutputFrameSet> _deferredDisposals;
    private RuntimeMappedOutputFrameSet? _prepared;
    private ulong _preparedRevision;
    private RuntimePublishedOutputGeneration? _active;
    private ulong _lastSequence;
    private bool _closed;

    public RuntimeSoftwareOutputPublication(Guid runtimeEpoch, int maximumRetainedOldGenerations = 2)
    {
        if (runtimeEpoch == Guid.Empty)
            throw new ArgumentException("A current RuntimeHost epoch is required.", nameof(runtimeEpoch));
        if (maximumRetainedOldGenerations is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedOldGenerations));
        _runtimeEpoch = runtimeEpoch;
        _maximumRetainedOldGenerations = maximumRetainedOldGenerations;
        _retired = new(maximumRetainedOldGenerations);
        _deferredDisposals = new(maximumRetainedOldGenerations);
    }

    public RuntimeSoftwareOutputPublicationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_active?.ExecutionRevision, _active?.ProgramSequence,
                    _active?.Frames.TargetCount ?? 0, _prepared is not null, _retired.Count,
                    _active?.Readers ?? 0, _retired.Sum(x => x.Readers), _closed)
                { DeferredDisposalGroups = _deferredDisposals.Count };
        }
    }

    /// <summary>
    /// Successful staging transfers disposal ownership of the entire mapped group.
    /// Rejected groups remain owned by the caller and must be disposed there.
    /// </summary>
    public RuntimeSoftwareOutputPublicationResult Stage(
        Guid runtimeEpoch, ulong expectedExecutionRevision, RuntimeMappedOutputFrameSet frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        // Validate outside the lock. A disposed or empty group cannot be staged.
        try
        {
            if (frames.TargetCount == 0)
                return Rejected("runtime.software_output.empty",
                    "At least one mapped target frame must be prepared.");
        }
        catch (ObjectDisposedException)
        {
            return Rejected("runtime.software_output.disposed_group",
                "A disposed output frame group cannot be staged.");
        }
        lock (_gate)
        {
            if (_closed) return Rejected("runtime.software_output.closed", "Output publication is closed.");
            if (runtimeEpoch != _runtimeEpoch)
                return Rejected("runtime.software_output.epoch_changed", "The RuntimeHost epoch has changed.");
            if (_prepared is not null)
                return Rejected("runtime.software_output.busy", "Cancel or commit the existing staged frame set.");
            if (_retired.Count + _deferredDisposals.Count >= _maximumRetainedOldGenerations)
                return Rejected("runtime.software_output.reader_pressure",
                    "Retained or not-yet-drained old frames block further preparation.");
            _prepared = frames;
            _preparedRevision = expectedExecutionRevision;
            return new(true, "runtime.software_output.prepared",
                "Software frames are staged but not published to target readers.", _active?.ProgramSequence);
        }
    }

    /// <summary>
    /// Called on the one existing Program boundary after authoritative revision
    /// verification. A swap is all-or-none for all software target readers.
    /// </summary>
    public RuntimeSoftwareOutputPublicationResult CommitAtBoundary(
        Guid runtimeEpoch, ulong currentExecutionRevision, ulong programSequence)
    {
        lock (_gate)
        {
            if (_closed) return Rejected("runtime.software_output.closed", "Output publication is closed.");
            if (runtimeEpoch != _runtimeEpoch)
                return Rejected("runtime.software_output.epoch_changed", "The RuntimeHost epoch has changed.");
            if (_prepared is null)
                return Rejected("runtime.software_output.not_prepared", "No output frame group has been staged.");
            if (currentExecutionRevision != _preparedRevision || programSequence <= _lastSequence)
                return Rejected("runtime.software_output.stale_boundary",
                    "The committed execution revision or monotonic Program slot changed.");
            // All mapped target frames must be from the same exact Program slot;
            // publishing old source pixels under a newer boundary is forbidden.
            if (!_prepared.MatchesProgramSlot(programSequence))
                return Rejected("runtime.software_output.frame_slot_changed",
                    "One or more mapped target frames do not match the committed Program slot.");
            if (_active is not null &&
                _retired.Count + _deferredDisposals.Count >= _maximumRetainedOldGenerations)
                return Rejected("runtime.software_output.reader_pressure",
                    "A prior frame generation must drain before another group can commit.");

            var next = new RuntimePublishedOutputGeneration(_prepared, _preparedRevision, programSequence);
            _prepared = null;
            if (_active is { } previous)
            {
                previous.Retired = true;
                if (previous.Readers == 0) _deferredDisposals.Enqueue(previous.Frames);
                else _retired.Add(previous);
            }
            _active = next;
            _lastSequence = programSequence;
            return new(true, "runtime.software_output.published",
                "All software targets switched at one committed Program boundary.", programSequence);
        }
    }

    /// <summary>
    /// Drain old GPU frames on a management/cleanup thread, never from the
    /// real-time Program boundary. New staging fails closed while this finite
    /// disposal queue is full.
    /// </summary>
    public int DrainRetiredOnManagementThread()
    {
        List<RuntimeMappedOutputFrameSet> ready;
        lock (_gate)
        {
            ready = new(_deferredDisposals.Count);
            while (_deferredDisposals.TryDequeue(out var group))
                ready.Add(group);
        }
        foreach (var group in ready)
            group.Dispose();
        return ready.Count;
    }

    public bool TryAcquire(out RuntimeSoftwareOutputPublicationLease? lease)
    {
        lock (_gate)
        {
            lease = null;
            if (_closed || _active is null) return false;
            _active.Readers = checked(_active.Readers + 1);
            lease = new RuntimeSoftwareOutputPublicationLease(this, _active);
            return true;
        }
    }

    public bool CancelPrepared()
    {
        RuntimeMappedOutputFrameSet? prepared;
        lock (_gate)
        {
            prepared = _prepared;
            _prepared = null;
        }
        prepared?.Dispose();
        return prepared is not null;
    }

    internal void Release(RuntimePublishedOutputGeneration generation)
    {
        RuntimeMappedOutputFrameSet? retireNow = null;
        lock (_gate)
        {
            if (generation.Readers <= 0)
                throw new InvalidOperationException("Output generation reader lease underflow.");
            generation.Readers--;
            if (generation.Readers == 0 && generation.Retired)
            {
                _retired.Remove(generation);
                retireNow = generation.Frames;
            }
        }
        retireNow?.Dispose();
    }

    public void Dispose()
    {
        var dispose = new List<RuntimeMappedOutputFrameSet>();
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            if (_prepared is { } pending) dispose.Add(pending);
            _prepared = null;
            while (_deferredDisposals.TryDequeue(out var old))
                dispose.Add(old);
            if (_active is { } active)
            {
                active.Retired = true;
                if (active.Readers == 0) dispose.Add(active.Frames);
                else _retired.Add(active);
                _active = null;
            }
            foreach (var previous in _retired.Where(g => g.Readers == 0).ToArray())
            {
                _retired.Remove(previous);
                dispose.Add(previous.Frames);
            }
        }
        foreach (var group in dispose) group.Dispose();
    }

    private RuntimeSoftwareOutputPublicationResult Rejected(string code, string detail) =>
        new(false, code, detail, _active?.ProgramSequence);
}
