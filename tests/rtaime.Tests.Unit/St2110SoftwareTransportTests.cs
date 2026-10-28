// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using System.Net;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Provider.St2110;

namespace rtaime.Tests.Unit;

public sealed class St2110SoftwareTransportTests
{
    private static St2110SoftwareEndpoint Endpoint() => new(
        IPAddress.Loopback, 5004, 5006, 111, 222);

    [Theory]
    [InlineData(25, 1)]
    [InlineData(50, 1)]
    [InlineData(60000, 1001)]
    public void Canonical_SDP_round_trips_exact_wire_tuple_and_rejects_unsafe_attributes(
        long numerator, long denominator)
    {
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(numerator, denominator));
        var sdp = St2110SoftwareSdp.Generate(profile, Endpoint());
        Assert.True(St2110SoftwareSdp.TryParse(sdp, out var parsedProfile, out var endpoint));
        Assert.Equal(profile, parsedProfile);
        Assert.Equal(Endpoint(), endpoint);
        Assert.True(sdp.Length < St2110SoftwareSdp.MaximumSdpBytes);
        Assert.False(St2110SoftwareSdp.TryParse(sdp.Replace("L24/48000/2", "L16/48000/2"),
            out _, out _));
        Assert.False(St2110SoftwareSdp.TryParse(sdp + "a=recvonly\r\n", out _, out _));
        Assert.False(St2110SoftwareSdp.TryParse(sdp.Replace("127.0.0.1", "192.0.2.1"),
            out _, out _));
        Assert.False(St2110SoftwareSdp.TryParse(sdp.Replace("exactframerate=", "exactframerate=invalid"),
            out _, out _));
        Assert.False(St2110SoftwareSdp.TryParse(new string('X', 4097), out _, out _));
    }

    [Fact]
    public void Software_reference_fails_closed_on_non_loopback_and_unknown_formats()
    {
        Assert.Throws<NotSupportedException>(() => new St2110SoftwareEndpoint(
            IPAddress.Parse("192.0.2.1"), 5004, 5006, 111, 222));
        Assert.Throws<ArgumentOutOfRangeException>(() => new St2110SoftwareEndpoint(
            IPAddress.Loopback, 5005, 5007, 111, 222));
        Assert.Throws<ArgumentException>(() => new St2110SoftwareEndpoint(
            IPAddress.Loopback, 5004, 5006, 111, 111));
        Assert.Throws<NotSupportedException>(() => new St2110SoftwareProfile(
            1920, 1080, new FrameRate(24, 1)));
        Assert.Throws<NotSupportedException>(() => new St2110SoftwareProfile(
            3840, 2160, new FrameRate(50, 1)));
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(50, 1));
        St2110TransportAdmission.RequireSoftwareReference(profile, Endpoint(), null);
        Assert.Equal(0u, St2110TransportAdmission.VideoTimestamp(profile, 0));
        Assert.Equal(1800u, St2110TransportAdmission.VideoTimestamp(profile, 1));
        Assert.Equal(960u, St2110TransportAdmission.AudioTimestamp(profile, 1));
        Assert.Equal(1799u, St2110TransportAdmission.VideoTimestamp(
            profile, 1, uint.MaxValue));
    }

    [Fact]
    public void Wire_video_packets_use_RFC4175_header_line_pgroup_and_reassemble_out_of_order()
    {
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(50, 1));
        var source = Enumerable.Range(0, profile.PackedFrameBytes)
            .Select(i => (byte)i).ToArray();
        var packets = St2110VideoRtp.Packetize(profile, source, 12345, 111, 65535).ToArray();
        Assert.Equal(2, packets.Length);
        Assert.All(packets, p => Assert.InRange(p.Length, 1, St2110SoftwareProfile.MaximumRtpDatagramBytes));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(packets[0].AsSpan(2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(packets[1].AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packets[1].AsSpan(12)));
        Assert.Equal(0x80, packets[0][0]);
        Assert.False((packets[0][1] & 0x80) != 0);
        Assert.True((packets[1][1] & 0x80) != 0);
        Assert.Equal(20, BinaryPrimitives.ReadUInt16BigEndian(packets[0].AsSpan(14)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packets[1].AsSpan(16)));

        var receiver = new St2110VideoFrameReceiver(profile, 111);
        Assert.False(receiver.TryReceive(packets[1], TimeSpan.FromMilliseconds(1), out _));
        Assert.True(receiver.TryReceive(packets[0], TimeSpan.FromMilliseconds(2), out var received));
        Assert.Equal(source, received!.PackedYCbCr422TenBit);
        Assert.Equal(12345u, received.RtpTimestamp);
        Assert.Equal(0ul, receiver.SequenceHealth.Reordered);
        Assert.Equal(0ul, receiver.SequenceHealth.Missing);
        Assert.False(receiver.TryReceive(packets[0], TimeSpan.FromMilliseconds(3), out _));
        Assert.Equal(1ul, receiver.LatePackets);
    }

    [Fact]
    public void Malformed_RTP_video_datagrams_are_rejected_without_unbounded_reassembly()
    {
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(25, 1));
        var source = new byte[profile.PackedFrameBytes];
        var packets = St2110VideoRtp.Packetize(profile, source, 0, 111, 0).ToArray();
        var receiver = new St2110VideoFrameReceiver(profile, 111);
        var corruptedVersion = packets[0].ToArray();
        corruptedVersion[0] = 0x90;
        Assert.False(receiver.TryReceive(corruptedVersion, TimeSpan.Zero, out _));
        var wrongSsrc = packets[0].ToArray();
        wrongSsrc[11] = 0x55;
        Assert.False(receiver.TryReceive(wrongSsrc, TimeSpan.Zero, out _));
        var wrongLine = packets[0].ToArray();
        wrongLine[16] = 0x7F;
        Assert.False(receiver.TryReceive(wrongLine, TimeSpan.Zero, out _));
        Assert.False(receiver.TryReceive(new byte[16], TimeSpan.Zero, out _));
        Assert.True(receiver.RejectedPackets >= 4);
        Assert.False(receiver.TryReceive(packets[0], TimeSpan.FromMilliseconds(1), out _));
        var next = St2110VideoRtp.Packetize(profile, source, 3600, 111, 2).First();
        Assert.False(receiver.TryReceive(next, TimeSpan.FromMilliseconds(2), out _));
        Assert.Equal(1ul, receiver.IncompleteFrames);
    }

    [Theory]
    [InlineData(50, 1, 960, 20)]
    [InlineData(60000, 1001, 800, 16)]
    public void Pcm_L24_packetization_has_exact_sample_cadence(
        long num, long den, int expectedSamples, int expectedPackets)
    {
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(num, den));
        var pcm = new St2110PcmRtpPacketizer(222, uint.MaxValue);
        var bytes = new byte[expectedSamples * 8];
        for (var frame = 0; frame < expectedSamples; frame++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(frame * 8), 1.0f);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(frame * 8 + 4), -1.0f);
        }
        var result = pcm.Packetize(bytes, 0);
        Assert.Equal(expectedPackets, result.Count);
        Assert.Equal(expectedSamples % 48, pcm.BufferedSampleFrames);
        Assert.Equal(expectedSamples, pcm.NextExpectedSample);
        var receiver = new St2110PcmRtpReceiver(222);
        Assert.True(receiver.TryReceive(result[0], TimeSpan.Zero, out var first));
        Assert.Equal(uint.MaxValue, first!.RtpTimestamp);
        Assert.Equal(new byte[] { 0x7F, 0xFF, 0xFF, 0x80, 0x00, 0x01 },
            first.InterleavedBigEndianL24[..6]);
        Assert.False(receiver.TryReceive(result[0], TimeSpan.FromMilliseconds(1), out _));
        Assert.Equal(1ul, receiver.LatePackets);
        Assert.Equal(expectedPackets * 48, result.Sum(p => (p.Length - 12) / 6));
        Assert.Equal(48u, unchecked((uint)(St2110TransportAdmission.AudioTimestamp(profile, 0, uint.MaxValue) + 49)));
    }

    [Fact]
    public void Fractional_video_frame_rate_preserves_pcm_residue_and_timestamp_wrap()
    {
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(60000, 1001));
        var writer = new St2110PcmRtpPacketizer(222);
        var start = 0L;
        var packetCount = 0;
        for (ulong frame = 0; frame < 60; frame++)
        {
            var next = checked((long)((Int128)(frame + 1) * 1001 * 48000 / 60000));
            var samples = checked((int)(next - start));
            packetCount += writer.Packetize(new byte[samples * 8], start).Count;
            start = next;
        }
        Assert.Equal(48048L, start);
        Assert.Equal(1001, packetCount);
        Assert.Equal(0, writer.BufferedSampleFrames);
        Assert.Equal(90090u, St2110TransportAdmission.VideoTimestamp(profile, 60));
        Assert.Equal(48048u, St2110TransportAdmission.AudioTimestamp(profile, 60));
        Assert.Throws<InvalidOperationException>(() =>
            writer.Packetize(new byte[800 * 8], 0));
    }

    [Fact]
    public void Rtp_gap_reorder_and_wrap_are_observed_without_false_permanent_loss()
    {
        var monitor = new St2110RtpSequenceMonitor();
        monitor.Observe(uint.MaxValue - 1, 90000, TimeSpan.Zero, 90000);
        monitor.Observe(0, 90001, TimeSpan.FromMilliseconds(1), 90000);
        Assert.Equal(1ul, monitor.Missing);
        monitor.Observe(uint.MaxValue, 90000, TimeSpan.FromMilliseconds(2), 90000);
        Assert.Equal(0ul, monitor.Missing);
        Assert.Equal(1ul, monitor.Reordered);
        monitor.Observe(uint.MaxValue, 90000, TimeSpan.FromMilliseconds(3), 90000);
        Assert.Equal(1ul, monitor.DuplicatesOrLate);
        Assert.True(double.IsFinite(monitor.JitterTicks));
    }

    [Fact]
    public void Rgba_conversion_is_bounded_to_explicit_packed_byte_length()
    {
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(50, 1));
        var rgba = Enumerable.Repeat((byte)128, 8 * 2 * 4).ToArray();
        var packed = St2110RgbaReferenceConversion.ToPackedYcbcr422TenBit(profile, rgba);
        Assert.Equal(profile.PackedFrameBytes, packed.Length);
        Assert.Throws<ArgumentException>(() =>
            St2110RgbaReferenceConversion.ToPackedYcbcr422TenBit(profile, new byte[5]));
    }
}
