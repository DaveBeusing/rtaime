// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.St2110;

/// <summary>Minimal strict RTP/AVP subset: V2, fixed 12-byte header, no CSRC/extension/padding.</summary>
public readonly record struct St2110RtpPacket(
    byte PayloadType, bool Marker, ushort Sequence, uint Timestamp, uint Ssrc,
    ReadOnlyMemory<byte> Payload);

public static class St2110RtpWire
{
    public const int HeaderBytes = 12;

    public static void WriteHeader(Span<byte> packet, byte payloadType, bool marker,
        ushort sequence, uint timestamp, uint ssrc)
    {
        if (packet.Length < HeaderBytes || payloadType is < 96 or > 127 || ssrc == 0)
            throw new ArgumentException("Invalid bounded RTP reference header.");
        packet[0] = 0x80;
        packet[1] = (byte)(payloadType | (marker ? 0x80 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet[2..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], ssrc);
    }

    public static bool TryParse(ReadOnlyMemory<byte> data, byte payloadType, uint ssrc,
        out St2110RtpPacket parsed)
    {
        parsed = default;
        if (data.Length < HeaderBytes || data.Length > St2110SoftwareProfile.MaximumRtpDatagramBytes ||
            data.Span[0] != 0x80 ||
            (data.Span[1] & 0x7F) != payloadType ||
            BinaryPrimitives.ReadUInt32BigEndian(data.Span[8..]) != ssrc)
            return false;
        parsed = new St2110RtpPacket(
            payloadType, (data.Span[1] & 0x80) != 0,
            BinaryPrimitives.ReadUInt16BigEndian(data.Span[2..]),
            BinaryPrimitives.ReadUInt32BigEndian(data.Span[4..]),
            ssrc, data[HeaderBytes..]);
        return true;
    }
}

/// <summary>
/// RFC 4175 single-scanline-fragment packetizer. One 6-byte line header per packet;
/// the high 16 bits of the 32-bit video packet sequence precede that header.
/// The source is packed Cb-Y0-Cr-Y1, 10 bits per component (5 bytes / 2 pixels).
/// </summary>
public static class St2110VideoRtp
{
    public const int VideoHeaderBytes = St2110RtpWire.HeaderBytes + 2 + 6;

    public static int PacketsPerFrame(St2110SoftwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var maximum = MaximumLinePayload;
        return checked(((profile.PackedLineBytes + maximum - 1) / maximum) * profile.Height);
    }

    private static int MaximumLinePayload =>
        ((St2110SoftwareProfile.MaximumRtpDatagramBytes - VideoHeaderBytes) /
            St2110SoftwareProfile.VideoPixelGroupBytes) *
            St2110SoftwareProfile.VideoPixelGroupBytes;

    public static IEnumerable<byte[]> Packetize(St2110SoftwareProfile profile,
        ReadOnlyMemory<byte> packedFrame, uint rtpTimestamp, uint ssrc,
        uint startingExtendedSequence)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (packedFrame.Length != profile.PackedFrameBytes || ssrc == 0)
            throw new ArgumentException("Frame length or SSRC does not match the negotiated wire tuple.");
        var sequence = startingExtendedSequence;
        for (var line = 0; line < profile.Height; line++)
        {
            var lineStart = checked(line * profile.PackedLineBytes);
            for (var offset = 0; offset < profile.PackedLineBytes;)
            {
                var length = Math.Min(MaximumLinePayload, profile.PackedLineBytes - offset);
                var last = line == profile.Height - 1 && offset + length == profile.PackedLineBytes;
                var packet = new byte[VideoHeaderBytes + length];
                St2110RtpWire.WriteHeader(packet,
                    St2110SoftwareProfile.VideoPayloadType, last,
                    unchecked((ushort)sequence), rtpTimestamp, ssrc);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12), (ushort)(sequence >> 16));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), checked((ushort)length));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16), checked((ushort)line));
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18),
                    checked((ushort)(offset / St2110SoftwareProfile.VideoPixelGroupBytes * 2)));
                packedFrame.Slice(lineStart + offset, length).CopyTo(packet.AsMemory(VideoHeaderBytes));
                yield return packet;
                offset += length;
                sequence = unchecked(sequence + 1);
            }
        }
    }
}

/// <summary>Finite RFC 3550-style sequence and interarrival jitter observations.
/// A gap can be recovered by a reordered packet; no network completeness is inferred.</summary>
public sealed class St2110RtpSequenceMonitor
{
    private const int MaximumOutstanding = 4096;
    private readonly HashSet<uint> _gaps = new();
    private uint? _highest;
    private uint? _lastTimestamp;
    private TimeSpan? _lastArrival;
    private double _jitter;

    public ulong Received { get; private set; }
    public ulong Missing { get; private set; }
    public ulong Reordered { get; private set; }
    public ulong DuplicatesOrLate { get; private set; }
    public double JitterTicks => _jitter;

    public void Observe(uint sequence, uint timestamp, TimeSpan monotonicArrival, int clockRate)
    {
        if (monotonicArrival < TimeSpan.Zero || clockRate is < 1 or > 90000)
            throw new ArgumentOutOfRangeException(nameof(monotonicArrival));
        Received++;
        if (_highest is { } highest)
        {
            var distance = unchecked((int)(sequence - highest));
            if (distance > 0)
            {
                // Keep only a finite reorder window. Older gaps are permanently
                // counted as missing observations, not endlessly retained.
                if (distance <= MaximumOutstanding)
                {
                    for (uint i = 1; i < distance; i++)
                        _gaps.Add(unchecked(highest + i));
                    Missing += (ulong)(distance - 1);
                }
                else
                {
                    _gaps.Clear();
                    Missing += (ulong)(distance - 1);
                }
                _highest = sequence;
            }
            else if (_gaps.Remove(sequence))
            {
                Missing--;
                Reordered++;
            }
            else
            {
                DuplicatesOrLate++;
            }
        }
        else _highest = sequence;

        if (_lastArrival is { } lastArrival && _lastTimestamp is { } lastTimestamp &&
            monotonicArrival >= lastArrival)
        {
            var arrivalTicks = (monotonicArrival - lastArrival).TotalSeconds * clockRate;
            var rtpTicks = unchecked((int)(timestamp - lastTimestamp));
            _jitter += (Math.Abs(arrivalTicks - rtpTicks) - _jitter) / 16.0;
        }
        _lastArrival = monotonicArrival;
        _lastTimestamp = timestamp;
    }
}

public sealed record St2110PackedVideoFrame(uint RtpTimestamp, byte[] PackedYCbCr422TenBit);

/// <summary>
/// Strict, bounded single-frame reassembly. Incomplete, overlapping, stale,
/// duplicate and malformed slices never surface as a complete video frame.
/// </summary>
public sealed class St2110VideoFrameReceiver
{
    private readonly St2110SoftwareProfile _profile;
    private readonly uint _ssrc;
    private readonly bool[] _coverage;
    private readonly St2110RtpSequenceMonitor _sequences = new();
    private byte[]? _frame;
    private uint? _activeTimestamp;
    private uint? _completedTimestamp;
    private int _groups;
    private bool _endMarker;
    private TimeSpan _firstArrival;
    public ulong IncompleteFrames { get; private set; }
    public ulong RejectedPackets { get; private set; }
    public ulong LatePackets { get; private set; }
    public St2110RtpSequenceMonitor SequenceHealth => _sequences;

    public St2110VideoFrameReceiver(St2110SoftwareProfile profile, uint ssrc)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (ssrc == 0) throw new ArgumentOutOfRangeException(nameof(ssrc));
        _ssrc = ssrc;
        _coverage = new bool[checked(profile.Width * profile.Height / 2)];
    }

    public bool TryReceive(ReadOnlyMemory<byte> datagram, TimeSpan monotonicArrival,
        out St2110PackedVideoFrame? completed)
    {
        completed = null;
        if (!St2110RtpWire.TryParse(datagram, St2110SoftwareProfile.VideoPayloadType, _ssrc, out var packet) ||
            datagram.Length < St2110VideoRtp.VideoHeaderBytes ||
            monotonicArrival < TimeSpan.Zero)
            return Reject();

        var payload = packet.Payload.Span;
        var sequence = ((uint)BinaryPrimitives.ReadUInt16BigEndian(payload) << 16) | packet.Sequence;
        var length = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        var line = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        var pixelOffset = BinaryPrimitives.ReadUInt16BigEndian(payload[6..]);
        var groupOffset = pixelOffset / 2;
        if ((line & 0x8000) != 0 || (pixelOffset & 0x8001) != 0 ||
            length == 0 || length % St2110SoftwareProfile.VideoPixelGroupBytes != 0 ||
            payload.Length != length + 8 || line >= _profile.Height ||
            groupOffset + length / 5 > _profile.Width / 2 ||
            packet.Marker && (line != _profile.Height - 1 ||
                (groupOffset + length / 5) != _profile.Width / 2))
            return Reject();

        if (_completedTimestamp == packet.Timestamp)
        {
            LatePackets++;
            return false;
        }
        if (_activeTimestamp != packet.Timestamp)
        {
            if (_activeTimestamp is { } existing &&
                unchecked((int)(packet.Timestamp - existing)) <= 0)
            {
                LatePackets++;
                return false;
            }
            if (_frame is not null && _groups < _coverage.Length)
                IncompleteFrames++;
            _frame = new byte[_profile.PackedFrameBytes];
            Array.Clear(_coverage);
            _groups = 0;
            _endMarker = false;
            _activeTimestamp = packet.Timestamp;
            _firstArrival = monotonicArrival;
        }

        if (monotonicArrival < _firstArrival ||
            monotonicArrival - _firstArrival > TimeSpan.FromMilliseconds(250))
        {
            LatePackets++;
            return false;
        }
        var groupIndex = checked(line * _profile.Width / 2 + groupOffset);
        var groupCount = length / St2110SoftwareProfile.VideoPixelGroupBytes;
        for (var i = 0; i < groupCount; i++)
            if (_coverage[groupIndex + i]) return Reject();

        packet.Payload.Slice(8, length).CopyTo(_frame!.AsMemory(groupIndex * 5, length));
        for (var i = 0; i < groupCount; i++) _coverage[groupIndex + i] = true;
        _groups += groupCount;
        _endMarker |= packet.Marker;
        _sequences.Observe(sequence, packet.Timestamp, monotonicArrival, 90000);
        if (_groups != _coverage.Length || !_endMarker) return false;

        if (_frame is null) return Reject();
        completed = new St2110PackedVideoFrame(packet.Timestamp, _frame);
        _completedTimestamp = packet.Timestamp;
        _frame = null;
        return true;
    }

    private bool Reject()
    {
        RejectedPackets++;
        return false;
    }
}
