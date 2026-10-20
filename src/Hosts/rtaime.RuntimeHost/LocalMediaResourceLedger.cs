// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.RuntimeHost;

public enum LocalMediaCapacityProfile { Conservative = 1, DevelopmentFour = 2 }
public enum LocalMediaResourcePurpose { Resident = 1, Probe = 2, Preflight = 3, Analysis = 4 }

public sealed record LocalMediaResourceIdentity(
    string RuntimeEpoch, MediaSourceId SourceId, PreparedExecutionId? ExecutionId,
    ulong? AuthorityRevision, ulong? PlanGeneration, LocalMediaResourcePurpose Purpose)
{
    public Identity? AuthorityStateId { get; init; }
}

/// <summary>A single prepared native owner intent, reserved with its siblings as one admission group.</summary>
public sealed record LocalMediaResourceRequest(Guid Intent, LocalMediaResourceIdentity Identity);

public sealed record LocalMediaResourceSnapshot(
    int Capacity, int ReservedOwners, int WorkerReservations, long KnownManagedBytes,
    int RetainedPoolOwners, int QuarantinedOwners, int UnknownNativeOwners,
    long? NativeBytes, long? GpuBytes, string StoragePressure, string? LastRejection);

/// <summary>
/// One Runtime registry ledger. Reservations precede native open and pool allocation.
/// Unknown native/GPU/storage evidence is explicit, never substituted with zero bytes.
/// </summary>
public sealed class LocalMediaResourceLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Owner> _owners = [];
    private readonly long _bytesPerOwner;
    private MediaSourceId? _program;
    private string? _lastRejection;
    private bool _closed;
    internal void CloseAdmission() { lock (_gate) _closed = true; }
    public string RuntimeEpoch { get; } = Guid.NewGuid().ToString("N");
    public int Capacity { get; }
    public LocalMediaResourceLedger(VideoFormat format, int capacity,
        LocalMediaCapacityProfile profile = LocalMediaCapacityProfile.Conservative)
        : this(format, capacity,
            Enum.IsDefined(profile)
                ? (profile == LocalMediaCapacityProfile.DevelopmentFour ? 4 : 2)
                : throw new ArgumentOutOfRangeException(nameof(profile)), null)
    {
    }

    /// <summary>
    /// Admit a provider-qualified finite profile without rewriting the historic
    /// conservative and opt-in-four decoder semantics. Profile selection is a
    /// host/provider decision; management input alone cannot establish hardware evidence.
    /// </summary>
    public LocalMediaResourceLedger(VideoFormat format, int capacity,
        RuntimeResourceCapabilityProfile admittedProfile)
        : this(format, capacity,
            admittedProfile?.MaximumDecoderOwners ?? throw new ArgumentNullException(nameof(admittedProfile)),
            admittedProfile)
    {
    }

    private LocalMediaResourceLedger(VideoFormat format, int capacity, int maximum,
        RuntimeResourceCapabilityProfile? admittedProfile)
    {
        if (capacity < 1 || capacity > maximum)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        // Ten video/audio slots plus one known RGBA decoder scratch frame. Native allocations are unknown.
        _bytesPerOwner = checked((long)format.Width * format.Height * 4 * 11 + 10 * 32_768L);
        var retainedPoolBudget = checked(_bytesPerOwner * capacity);
        if (admittedProfile is not null)
        {
            var candidate = new RuntimeResourceCapacityRequest(format, capacity,
                Math.Min(capacity, admittedProfile.MaximumSequenceOwners),
                Array.Empty<RuntimeOutputCapacityRequest>());
            var decision = RuntimeResourceCapacityAdmission.Evaluate(admittedProfile, candidate);
            if (!decision.Admitted || retainedPoolBudget > admittedProfile.ManagedBudgetBytes)
                throw new ArgumentException(
                    "Decoder owners are not admitted by this provider/device profile: " + decision.Code,
                    nameof(admittedProfile));
        }
        Capacity = capacity;
    }

    public LocalMediaResourceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(Capacity, _owners.Count,
                    _owners.Values.Count(o => o.Identity.Purpose != LocalMediaResourcePurpose.Probe && !o.Completed),
                    checked(_bytesPerOwner * _owners.Count),
                    _owners.Values.Count(o => o.Completed && o.Leases > 0),
                    _owners.Values.Count(o => o.Quarantined),
                    _owners.Values.Count(o => !o.Completed || o.Quarantined),
                    null, null, "UNKNOWN: no source-bound storage pressure measurement", _lastRejection);
        }
    }
    internal void ObserveProgram(MediaSourceId? program) { lock (_gate) _program = program; }

    public bool TryReserve(Guid intent, LocalMediaResourceIdentity identity, out LocalMediaResourceTicket? ticket, out Failure? failure)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            ticket = null;
            if (_closed)
            {
                failure = Reject("runtime.media_resources.stopped", "Runtime admission is closed; reconnect to the active RuntimeHost.");
                return false;
            }
            if (intent == Guid.Empty || identity.RuntimeEpoch != RuntimeEpoch || identity.SourceId.Value.IsEmpty || !Enum.IsDefined(identity.Purpose))
            {
                failure = Reject("runtime.media_resources.identity", "Admission identity/epoch is invalid; obtain current Runtime authority before retrying.");
                return false;
            }
            if (_owners.TryGetValue(intent, out var existing))
            {
                if (existing.Identity == identity) { ticket = existing.Ticket; failure = null; return true; }
                failure = Reject("runtime.media_resources.intent_conflict", "Admission intent already belongs to different authority.");
                return false;
            }
            if (_owners.Values.Any(o => o.Quarantined))
            {
                failure = Reject("runtime.media_resources.quarantined", "Native cleanup is uncertain; restart RuntimeHost through its supported lifecycle.");
                return false;
            }
            if (_owners.Count >= Capacity)
            {
                failure = Reject("runtime.media_resources.capacity", "Decoder/pool reservations are full, including retained old pools. Close an inactive deck and release its consumers before retrying.");
                return false;
            }
            // Unknown/stale pressure cannot justify concurrent optional reads. The legacy idle-only lane remains available.
            if (identity.Purpose != LocalMediaResourcePurpose.Resident &&
                _owners.Values.Any(o => o.Identity.Purpose == LocalMediaResourcePurpose.Resident || o.Identity.SourceId == _program))
            {
                failure = Reject("runtime.media_resources.pressure_unknown", "Optional work is unavailable beside resident playback without measured storage pressure. Close inactive decks deliberately; Program is never evicted.");
                return false;
            }
            if (identity.Purpose == LocalMediaResourcePurpose.Resident &&
                _owners.Values.Any(o => o.Identity.Purpose != LocalMediaResourcePurpose.Resident))
            {
                failure = Reject("runtime.media_resources.optional_releasing", "Optional native work still owns capacity; cancel it and retry after confirmed cleanup.");
                return false;
            }
            var owner = new Owner(intent, identity);
            owner.Ticket = new LocalMediaResourceTicket(this, owner);
            _owners.Add(intent, owner);
            ticket = owner.Ticket;
            failure = null;
            return true;
        }
    }
    /// <summary>
    /// All-or-none reservation on the existing Runtime ledger. Tickets are native/pool
    /// ownership charges, not an authorization to publish or mutate Program.
    /// No decoder is opened while the ledger gate is held.
    /// </summary>
    public bool TryReserveGroup(
        IReadOnlyList<LocalMediaResourceRequest> requested,
        out IReadOnlyList<LocalMediaResourceTicket> tickets,
        out Failure? failure)
    {
        ArgumentNullException.ThrowIfNull(requested);
        lock (_gate)
        {
            tickets = Array.Empty<LocalMediaResourceTicket>();
            if (requested.Count == 0 || requested.Count > Capacity ||
                requested.Any(r => r is null || r.Identity is null || r.Intent == Guid.Empty) ||
                requested.Select(r => r.Intent).Distinct().Count() != requested.Count ||
                requested.Select(r => r.Identity.SourceId).Distinct().Count() != requested.Count)
            {
                failure = Reject("runtime.media_resources.sequence_identity",
                    "A sequence group requires one unique nonempty intent and source identity per native owner within the finite profile.");
                return false;
            }

            // A group never adopts an unrelated, previously issued ticket. Avoiding
            // idempotent group admission keeps rollback ownership unambiguous.
            if (requested.Any(r => _owners.ContainsKey(r.Intent)))
            {
                failure = Reject("runtime.media_resources.sequence_intent_conflict",
                    "A sequence group cannot reuse a previously reserved admission intent.");
                return false;
            }
            if ((long)_owners.Count + requested.Count > Capacity)
            {
                failure = Reject("runtime.media_resources.capacity",
                    "All sequence decoder/pool owners must fit together with retained, resident and quarantined owners.");
                return false;
            }

            var granted = new List<LocalMediaResourceTicket>(requested.Count);
            foreach (var request in requested)
            {
                if (!TryReserve(request.Intent, request.Identity, out var ticket, out failure))
                {
                    // Reentrant lock: group reservations are not observable between
                    // admission and complete rollback. No native worker owns a grant yet.
                    foreach (var issued in granted)
                        issued.OwnerCompleted();
                    return false;
                }
                granted.Add(ticket!);
            }
            tickets = granted.AsReadOnly();
            failure = null;
            return true;
        }
    }

    private Failure Reject(string code, string message)
    {
        _lastRejection = code;
        return new Failure(code, message);
    }
    internal sealed class Owner(Guid intent, LocalMediaResourceIdentity identity)
    {
        internal Guid Intent { get; } = intent;
        internal LocalMediaResourceIdentity Identity { get; } = identity;
        internal LocalMediaResourceTicket Ticket { get; set; } = null!;
        internal int Leases;
        internal bool Completed, Quarantined, Released;
        internal object? RetainedOwner;
    }
    private bool Current(Owner owner) => _owners.TryGetValue(owner.Intent, out var current) && ReferenceEquals(current, owner);
    internal void Retain(Owner owner)
    {
        lock (_gate)
        {
            if (!Current(owner) || owner.Completed) throw new ObjectDisposedException(nameof(LocalMediaResourceTicket));
            owner.Leases = checked(owner.Leases + 1);
        }
    }
    internal void ReleaseLease(Owner owner)
    {
        lock (_gate)
        {
            if (owner.Leases <= 0) throw new InvalidOperationException("Pool lease accounting underflow.");
            owner.Leases--;
            ReleaseIfFinished(owner);
        }
    }
    internal void Complete(Owner owner)
    {
        lock (_gate) { if (!Current(owner)) return; owner.Completed = true; ReleaseIfFinished(owner); }
    }
    internal void Quarantine(Owner owner, object? retainedOwner)
    {
        lock (_gate)
        {
            if (!Current(owner)) return;
            owner.Quarantined = true;
            owner.RetainedOwner ??= retainedOwner;
        }
    }
    private void ReleaseIfFinished(Owner owner)
    {
        if (Current(owner) && owner.Completed && owner.Leases == 0 && !owner.Quarantined)
        {
            _owners.Remove(owner.Intent);
            owner.Released = true;
        }
    }

    public sealed class LocalMediaResourceTicket
    {
        private readonly LocalMediaResourceLedger _ledger;
        private readonly Owner _owner;
        internal LocalMediaResourceTicket(LocalMediaResourceLedger ledger, Owner owner) { _ledger = ledger; _owner = owner; }
        public LocalMediaResourceIdentity Identity => _owner.Identity;
        internal void RetainPoolLease() => _ledger.Retain(_owner);
        internal void ReleasePoolLease() => _ledger.ReleaseLease(_owner);
        // Cancellation is not completion. Call only after native cleanup and worker join/event disposal.
        internal void OwnerCompleted() => _ledger.Complete(_owner);
        internal void Quarantine(object? retainedOwner = null) => _ledger.Quarantine(_owner, retainedOwner);
    }
}
