// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.St2110;

/// <summary>
/// Software-only ST 2022-7 packet-merge reference. No physical dual-NIC,
/// independent path, clock, or interoperable protection capability is admitted.
/// The owner supplies monotonic arrivals and drains the resulting RTP datagrams
/// through the existing ST 2110 video/audio receivers.
/// </summary>
public enum St2022RtpLeg
{
    Primary,
    Secondary
}

public enum St2022RtpMediaKind
{
    Video,
    Audio
}

public enum St2022SoftwarePathState
{
    Unknown,
    Healthy,
    Degraded,
    Failed
}

public sealed class St2022SoftwareMergeOptions
{
    public St2022SoftwareMergeOptions(
        int reorderCapacity = 256,
        int pressureThreshold = 128,
        TimeSpan? repairWindow = null,
        TimeSpan? legTimeout = null)
    {
        if (reorderCapacity is < 2 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(reorderCapacity));
        if (pressureThreshold < 1 || pressureThreshold > reorderCapacity)
            throw new ArgumentOutOfRangeException(nameof(pressureThreshold));
        var window = repairWindow ?? TimeSpan.FromMilliseconds(3);
        var timeout = legTimeout ?? TimeSpan.FromMilliseconds(100);
        if (window < TimeSpan.Zero || window > TimeSpan.FromMilliseconds(250))
            throw new ArgumentOutOfRangeException(nameof(repairWindow));
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(legTimeout));
        ReorderCapacity = reorderCapacity;
        PressureThreshold = pressureThreshold;
        RepairWindow = window;
        LegTimeout = timeout;
    }

    public int ReorderCapacity { get; }
    public int PressureThreshold { get; }
    public TimeSpan RepairWindow { get; }
    public TimeSpan LegTimeout { get; }
}

public sealed record St2022MergedDatagram(
    uint ExtendedSequence, uint Timestamp, St2022RtpLeg SelectedLeg, byte[] Datagram);

public sealed record St2022SoftwareLegHealth(
    ulong Received, ulong OutstandingGaps, ulong Reordered,
    ulong DuplicatesOrLate, double JitterTicks, TimeSpan? LastArrival);

/// <summary>Software-observed path state is not hardware admission evidence.</summary>
public sealed record St2022SoftwareMergeHealth(
    St2022SoftwarePathState ObservedPathState,
    string HardwareQualification,
    St2022SoftwareLegHealth Primary,
    St2022SoftwareLegHealth Secondary,
    ulong Emitted, ulong SelectedPrimary, ulong SelectedSecondary,
    ulong PostMergeObservedLoss, ulong Duplicates, ulong Late,
    ulong ConflictingCopies, ulong InvalidPackets, ulong OutOfWindow,
    int BufferedPackets, int RetainedDatagrams, int MaximumBufferedPackets);

/// <summary>
/// Deterministic, sequence-aware two-leg RTP selection, not an RTP codec or sender.
/// One shared extended sequence/timestamp/SSRC identifies copies on both legs.
/// Buffers and recent-copy evidence are each capped at the configured capacity.
/// A discontinuity beyond that capacity fails closed until a new merge epoch is
/// explicitly constructed; there is no implicit re-anchor or timebase jump.
/// </summary>
public sealed class St2022SoftwarePacketMerger
{
    private sealed record Pending(byte[] Datagram, uint Timestamp, St2022RtpLeg Leg, TimeSpan Arrival);

    private readonly object _gate = new();
    private readonly St2022RtpMediaKind _kind;
    private readonly uint _ssrc;
    private readonly St2022SoftwareMergeOptions _options;
    private readonly Dictionary<uint, Pending> _pending = new();
    private readonly Dictionary<uint, Pending> _recentCopies = new();
    private readonly Queue<uint> _recentOrder = new();
    private readonly St2110RtpSequenceMonitor _primary = new();
    private readonly St2110RtpSequenceMonitor _secondary = new();

    private uint? _next;
    private uint? _highest;
    private TimeSpan? _gapSince;
    private TimeSpan? _lastObservedTime;
    private TimeSpan? _primaryLastArrival;
    private TimeSpan? _secondaryLastArrival;
    private ulong _emitted;
    private ulong _selectedPrimary;
    private ulong _selectedSecondary;
    private ulong _postMergeLoss;
    private ulong _duplicates;
    private ulong _late;
    private ulong _conflicts;
    private ulong _invalid;
    private ulong _outOfWindow;
    private int _maximumBuffered;

    public St2022SoftwarePacketMerger(
        St2022RtpMediaKind kind, uint ssrc, St2022SoftwareMergeOptions? options = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (ssrc == 0) throw new ArgumentOutOfRangeException(nameof(ssrc));
        _kind = kind;
        _ssrc = ssrc;
        _options = options ?? new St2022SoftwareMergeOptions();
    }

    public IReadOnlyList<St2022MergedDatagram> Observe(
        St2022RtpLeg leg, ReadOnlyMemory<byte> datagram, TimeSpan monotonicArrival)
    {
        if (!Enum.IsDefined(leg)) throw new ArgumentOutOfRangeException(nameof(leg));
        lock (_gate)
        {
            ValidateTime(monotonicArrival);
            var payloadType = _kind == St2022RtpMediaKind.Video
                ? St2110SoftwareProfile.VideoPayloadType
                : St2110SoftwareProfile.AudioPayloadType;
            if (!St2110RtpWire.TryParse(datagram, payloadType, _ssrc, out var packet) ||
                _kind == St2022RtpMediaKind.Video && packet.Payload.Length < 8 ||
                _kind == St2022RtpMediaKind.Audio &&
                    (packet.Marker || packet.Payload.Length != St2110PcmRtpPacketizer.AudioPayloadBytes))
            {
                _invalid++;
                return Drain(monotonicArrival);
            }

            var sequence = _kind == St2022RtpMediaKind.Video
                ? ((uint)BinaryPrimitives.ReadUInt16BigEndian(packet.Payload.Span) << 16) | packet.Sequence
                : ExtendAudioSequence(packet.Sequence);

            if (_next is { } expected)
            {
                var distance = unchecked((int)(sequence - expected));
                if (distance < 0)
                {
                    ObserveLeg(leg, sequence, packet.Timestamp, monotonicArrival, accepted: false);
                    if (_recentCopies.TryGetValue(sequence, out var prior))
                    {
                        if (!datagram.Span.SequenceEqual(prior.Datagram))
                            _conflicts++;
                        else
                        {
                            // A slightly delayed copy is normal on an active B leg.
                            // Only recent, identical RTP contributes to its liveness;
                            // replaying old packets after timeout cannot revive a leg.
                            if (monotonicArrival - prior.Arrival <= _options.LegTimeout)
                                MarkLegActive(leg, monotonicArrival);
                            _late++;
                        }
                    }
                    else
                        _late++;
                    return Drain(monotonicArrival);
                }
                if (distance >= _options.ReorderCapacity)
                {
                    _outOfWindow++;
                    return Drain(monotonicArrival);
                }
            }
            else
            {
                _next = sequence;
            }

            ObserveLeg(leg, sequence, packet.Timestamp, monotonicArrival);
            if (_highest is null || unchecked((int)(sequence - _highest.Value)) > 0)
                _highest = sequence;

            if (_pending.TryGetValue(sequence, out var stored))
            {
                // Copies of one RTP packet must have identical bytes, not merely
                // matching sequence numbers. Keep the first valid copy.
                if (!datagram.Span.SequenceEqual(stored.Datagram))
                    _conflicts++;
                else
                    _duplicates++;
            }
            else
            {
                _pending.Add(sequence,
                    new Pending(datagram.ToArray(), packet.Timestamp, leg, monotonicArrival));
                _maximumBuffered = Math.Max(_maximumBuffered, _pending.Count);
            }
            return Drain(monotonicArrival);
        }
    }

    /// <summary>Allow a gap to expire without requiring another arriving RTP packet.</summary>
    public IReadOnlyList<St2022MergedDatagram> Flush(TimeSpan monotonicNow)
    {
        lock (_gate)
        {
            ValidateTime(monotonicNow);
            return Drain(monotonicNow);
        }
    }

    public St2022SoftwareMergeHealth Snapshot(TimeSpan monotonicNow)
    {
        lock (_gate)
        {
            if (monotonicNow < TimeSpan.Zero ||
                _lastObservedTime is { } last && monotonicNow < last)
                throw new ArgumentOutOfRangeException(nameof(monotonicNow));
            var primaryActive = Active(_primaryLastArrival, monotonicNow);
            var secondaryActive = Active(_secondaryLastArrival, monotonicNow);
            var state = _primaryLastArrival is null && _secondaryLastArrival is null
                ? St2022SoftwarePathState.Unknown
                : primaryActive && secondaryActive
                    ? St2022SoftwarePathState.Healthy
                    : primaryActive || secondaryActive
                        ? St2022SoftwarePathState.Degraded
                        : St2022SoftwarePathState.Failed;
            return new St2022SoftwareMergeHealth(
                state, "UNVERIFIED",
                LegSnapshot(_primary, _primaryLastArrival),
                LegSnapshot(_secondary, _secondaryLastArrival),
                _emitted, _selectedPrimary, _selectedSecondary,
                _postMergeLoss, _duplicates, _late, _conflicts, _invalid, _outOfWindow,
                _pending.Count, _pending.Count + _recentCopies.Count, _maximumBuffered);
        }
    }

    private bool Active(TimeSpan? arrival, TimeSpan now) =>
        arrival is { } last && now - last <= _options.LegTimeout;

    private static St2022SoftwareLegHealth LegSnapshot(
        St2110RtpSequenceMonitor monitor, TimeSpan? arrival) =>
        new(monitor.Received, monitor.Missing, monitor.Reordered,
            monitor.DuplicatesOrLate, monitor.JitterTicks, arrival);

    private void ValidateTime(TimeSpan now)
    {
        if (now < TimeSpan.Zero ||
            _lastObservedTime is { } previous && now < previous)
            throw new ArgumentOutOfRangeException(nameof(now),
                "Merge time must be a non-decreasing monotonic observation.");
        _lastObservedTime = now;
    }

    private void ObserveLeg(St2022RtpLeg leg, uint sequence,
        uint timestamp, TimeSpan arrival, bool accepted = true)
    {
        var monitor = leg == St2022RtpLeg.Primary ? _primary : _secondary;
        monitor.Observe(sequence, timestamp, arrival,
            _kind == St2022RtpMediaKind.Video
                ? St2110SoftwareProfile.VideoRtpClockRate
                : St2110SoftwareProfile.AudioSampleRate);
        // Replayed or late traffic contributes sequence diagnostics but cannot
        // refresh live path health after a leg has stopped making progress.
        if (accepted) MarkLegActive(leg, arrival);
    }

    private void MarkLegActive(St2022RtpLeg leg, TimeSpan arrival)
    {
        if (leg == St2022RtpLeg.Primary) _primaryLastArrival = arrival;
        else _secondaryLastArrival = arrival;
    }

    private TimeSpan FirstBufferedArrival()
    {
        var first = TimeSpan.MaxValue;
        foreach (var entry in _pending.Values)
            if (entry.Arrival < first) first = entry.Arrival;
        return first;
    }

    private uint ExtendAudioSequence(ushort sequence)
    {
        if (_highest is not { } highest) return sequence;
        var candidate = (long)(highest & 0xFFFF0000u) + sequence;
        if (candidate - highest > 32767) candidate -= 65536;
        else if (highest - candidate > 32768) candidate += 65536;
        return unchecked((uint)candidate);
    }

    private IReadOnlyList<St2022MergedDatagram> Drain(TimeSpan now)
    {
        var output = new List<St2022MergedDatagram>();
        while (_next is { } next && _pending.Count > 0)
        {
            if (_pending.Remove(next, out var entry))
            {
                output.Add(new St2022MergedDatagram(next, entry.Timestamp,
                    entry.Leg, entry.Datagram));
                _emitted++;
                if (entry.Leg == St2022RtpLeg.Primary) _selectedPrimary++;
                else _selectedSecondary++;
                _recentCopies.Add(next, entry);
                _recentOrder.Enqueue(next);
                if (_recentOrder.Count > _options.ReorderCapacity)
                    _recentCopies.Remove(_recentOrder.Dequeue());
                _next = unchecked(next + 1);
                _gapSince = null;
                continue;
            }

            // For a second gap already buffered before Flush(), use its actual
            // first downstream arrival rather than restarting the holdback now.
            _gapSince ??= FirstBufferedArrival();
            if (now - _gapSince.Value < _options.RepairWindow &&
                _pending.Count < _options.PressureThreshold)
                break;
            // A missing sequence is only considered lost after the bounded
            // software holdback or buffer-pressure fence. Never fabricate PCM.
            _postMergeLoss++;
            _next = unchecked(next + 1);
            // Preserve the original gap deadline for adjacent missing numbers.
        }
        if (_pending.Count == 0) _gapSince = null;
        return output;
    }
}
