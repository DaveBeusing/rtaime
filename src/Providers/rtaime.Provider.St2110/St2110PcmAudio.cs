// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.St2110;

/// <summary>
/// ST 2110-30 Level A software reference: L24 stereo at 48 kHz,
/// exactly 48 sample frames per RTP packet (1 millisecond).
/// Receives the existing Runtime interleaved Float32 48 kHz PCM window.
/// </summary>
public sealed class St2110PcmRtpPacketizer
{
    public const int PcmBytesPerSampleFrame = 6;
    public const int AudioPayloadBytes = St2110SoftwareProfile.AudioPacketSamples * PcmBytesPerSampleFrame;
    private readonly byte[] _pending = new byte[AudioPayloadBytes];
    private readonly uint _ssrc;
    private readonly uint _epoch;
    private ushort _sequence;
    private long _nextExpectedSample = -1;
    private long _packetStartSample;
    private int _pendingFrames;

    public St2110PcmRtpPacketizer(uint ssrc, uint epoch = 0, ushort firstSequence = 0)
    {
        if (ssrc == 0) throw new ArgumentOutOfRangeException(nameof(ssrc));
        _ssrc = ssrc;
        _epoch = epoch;
        _sequence = firstSequence;
    }

    public int BufferedSampleFrames => _pendingFrames;
    public long NextExpectedSample => _nextExpectedSample;

    public IReadOnlyList<byte[]> Packetize(ReadOnlyMemory<byte> interleavedFloat32,
        long firstSampleFrame)
    {
        if (firstSampleFrame < 0 || interleavedFloat32.Length == 0 ||
            interleavedFloat32.Length % 8 != 0)
            throw new ArgumentException("PCM window must be non-empty interleaved 48 kHz stereo Float32.");
        if (_nextExpectedSample >= 0 && firstSampleFrame != _nextExpectedSample)
            throw new InvalidOperationException("PCM time discontinuity requires a new RTP session/epoch.");

        var numberOfFrames = interleavedFloat32.Length / 8;
        var next = checked(firstSampleFrame + numberOfFrames);
        var output = new List<byte[]>((numberOfFrames + _pendingFrames) / 48 + 1);
        var span = interleavedFloat32.Span;
        for (var frame = 0; frame < numberOfFrames; frame++)
        {
            var index = frame * 8;
            var left = BinaryPrimitives.ReadSingleLittleEndian(span[index..]);
            var right = BinaryPrimitives.ReadSingleLittleEndian(span[(index + 4)..]);
            if (!float.IsFinite(left) || !float.IsFinite(right))
                throw new ArgumentException("PCM contains a nonfinite sample; no packet is published.");
            if (_pendingFrames == 0)
                _packetStartSample = firstSampleFrame + frame;
            WriteL24(left, _pending.AsSpan(_pendingFrames * 6));
            WriteL24(right, _pending.AsSpan(_pendingFrames * 6 + 3));
            _pendingFrames++;
            if (_pendingFrames != St2110SoftwareProfile.AudioPacketSamples) continue;

            var datagram = new byte[St2110RtpWire.HeaderBytes + AudioPayloadBytes];
            St2110RtpWire.WriteHeader(datagram,
                St2110SoftwareProfile.AudioPayloadType, false, _sequence++,
                unchecked(_epoch + (uint)_packetStartSample), _ssrc);
            _pending.CopyTo(datagram, St2110RtpWire.HeaderBytes);
            output.Add(datagram);
            _pendingFrames = 0;
        }
        _nextExpectedSample = next;
        return output;
    }

    private static void WriteL24(float sample, Span<byte> target)
    {
        // Exactly signed big-endian PCM24; do not silently wrap clipped values.
        var scaled = (int)Math.Round(Math.Clamp((double)sample, -1, 1) * 8_388_607.0,
            MidpointRounding.AwayFromZero);
        target[0] = unchecked((byte)(scaled >> 16));
        target[1] = unchecked((byte)(scaled >> 8));
        target[2] = unchecked((byte)scaled);
    }
}

public sealed record St2110PcmPacket(uint RtpTimestamp, byte[] InterleavedBigEndianL24);

/// <summary>
/// Receive-side strict RTP payload validation and observable gaps/reordering.
/// Deinterleaving or resampling must be an explicit downstream media adapter.
/// </summary>
public sealed class St2110PcmRtpReceiver
{
    private readonly uint _ssrc;
    private readonly St2110RtpSequenceMonitor _sequence = new();
    private uint? _mostRecentTimestamp;
    public ulong RejectedPackets { get; private set; }
    public ulong LatePackets { get; private set; }
    public St2110RtpSequenceMonitor SequenceHealth => _sequence;

    public St2110PcmRtpReceiver(uint ssrc)
    {
        if (ssrc == 0) throw new ArgumentOutOfRangeException(nameof(ssrc));
        _ssrc = ssrc;
    }

    public bool TryReceive(ReadOnlyMemory<byte> datagram, TimeSpan monotonicArrival,
        out St2110PcmPacket? packet)
    {
        packet = null;
        if (!St2110RtpWire.TryParse(datagram, St2110SoftwareProfile.AudioPayloadType, _ssrc, out var rtp) ||
            rtp.Marker || rtp.Payload.Length != St2110PcmRtpPacketizer.AudioPayloadBytes ||
            monotonicArrival < TimeSpan.Zero)
        {
            RejectedPackets++;
            return false;
        }
        if (_mostRecentTimestamp is { } previous &&
            unchecked((int)(rtp.Timestamp - previous)) <= 0)
        {
            LatePackets++;
            _sequence.Observe(rtp.Sequence, rtp.Timestamp, monotonicArrival,
                St2110SoftwareProfile.AudioSampleRate);
            return false;
        }
        _mostRecentTimestamp = rtp.Timestamp;
        _sequence.Observe(rtp.Sequence, rtp.Timestamp, monotonicArrival,
            St2110SoftwareProfile.AudioSampleRate);
        packet = new St2110PcmPacket(rtp.Timestamp, rtp.Payload.ToArray());
        return true;
    }
}

/// <summary>Software RGBA8 to limited-range BT.709 YCbCr422 10-bit pgroups.
/// A physical colour-accurate broadcast claim requires independent colour tests.</summary>
public static class St2110RgbaReferenceConversion
{
    public static byte[] ToPackedYcbcr422TenBit(St2110SoftwareProfile profile,
        ReadOnlyMemory<byte> rgba)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (rgba.Length != checked(profile.Width * profile.Height * 4))
            throw new ArgumentException("RGBA8 frame size differs from negotiated video profile.");
        var result = new byte[profile.PackedFrameBytes];
        var source = rgba.Span;
        for (var px = 0; px < profile.Width * profile.Height; px += 2)
        {
            var p0 = px * 4;
            var p1 = (px + 1) * 4;
            var y0 = Luma(source[p0], source[p0 + 1], source[p0 + 2]);
            var y1 = Luma(source[p1], source[p1 + 1], source[p1 + 2]);
            var blue = ((double)source[p0 + 2] + source[p1 + 2]) / 2;
            var red = ((double)source[p0] + source[p1]) / 2;
            var yAverage = (y0 + y1) / 2;
            var cb = ClampTenBit(512 + (blue - yAverage) * 896 / (1.8556 * 255), 64, 960);
            var cr = ClampTenBit(512 + (red - yAverage) * 896 / (1.5748 * 255), 64, 960);
            var yl0 = ClampTenBit(64 + y0 * 876 / 255, 64, 940);
            var yl1 = ClampTenBit(64 + y1 * 876 / 255, 64, 940);
            var dst = px / 2 * 5;
            result[dst] = (byte)(cb >> 2);
            result[dst + 1] = (byte)((cb & 3) << 6 | yl0 >> 4);
            result[dst + 2] = (byte)((yl0 & 15) << 4 | cr >> 6);
            result[dst + 3] = (byte)((cr & 63) << 2 | yl1 >> 8);
            result[dst + 4] = (byte)yl1;
        }
        return result;
    }

    private static double Luma(byte r, byte g, byte b) =>
        0.2126 * r + 0.7152 * g + 0.0722 * b;

    private static int ClampTenBit(double sample, int min, int max) =>
        Math.Clamp((int)Math.Round(sample, MidpointRounding.AwayFromZero), min, max);
}
