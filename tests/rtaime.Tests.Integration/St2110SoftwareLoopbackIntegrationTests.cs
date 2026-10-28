// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Net;
using System.Net.Sockets;
using rtaime.Core;
using rtaime.Provider.Contracts;
using rtaime.Provider.St2110;

namespace rtaime.Tests.Integration;

public sealed class St2110SoftwareLoopbackIntegrationTests
{
    [Fact]
    public async Task Real_loopback_UDP_sender_and_receiver_exchange_video_and_pcm_without_external_network()
    {
        var profile = new St2110SoftwareProfile(8, 2, new FrameRate(50, 1));
        St2110SoftwareLoopbackReceiver? receiver = null;
        St2110SoftwareEndpoint? endpoint = null;
        for (var port = 23000; port < 23100 && receiver is null; port += 4)
        {
            try
            {
                endpoint = new St2110SoftwareEndpoint(IPAddress.Loopback,
                    port, port + 2, 2211, 2212);
                receiver = new St2110SoftwareLoopbackReceiver(profile, endpoint);
            }
            catch (SocketException) { }
        }
        Assert.NotNull(receiver);
        Assert.NotNull(endpoint);
        await using var openedReceiver = receiver;
        await using var sender = new St2110LoopbackUdpSender(endpoint);

        var video = Enumerable.Range(0, profile.PackedFrameBytes)
            .Select(i => (byte)i).ToArray();
        var packets = St2110VideoRtp.Packetize(profile, video,
            90000, endpoint.VideoSsrc, 65535).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var datagram in packets)
            Assert.Equal(datagram.Length,
                await sender.SendVideoAsync(datagram, deadline.Token));

        var received = await openedReceiver.Frames.ReadAsync(deadline.Token);
        Assert.Equal(video, received.PackedYCbCr422TenBit);
        Assert.Equal(90000u, received.RtpTimestamp);

        var pcm = new St2110PcmRtpPacketizer(endpoint.AudioSsrc, 48000);
        var audioPacket = Assert.Single(pcm.Packetize(new byte[48 * 8], 0));
        Assert.Equal(audioPacket.Length,
            await sender.SendAudioAsync(audioPacket, deadline.Token));
        var audio = await openedReceiver.AudioPackets.ReadAsync(deadline.Token);
        Assert.Equal(48000u, audio.RtpTimestamp);
        Assert.Equal(St2110PcmRtpPacketizer.AudioPayloadBytes,
            audio.InterleavedBigEndianL24.Length);
        Assert.Equal((ulong)packets.Length, openedReceiver.Health.VideoPackets);
        Assert.Equal(1ul, openedReceiver.Health.AudioPackets);
        Assert.Equal(0ul, openedReceiver.Health.VideoMissing);
        Assert.Equal(0ul, openedReceiver.Health.AudioMissing);
    }
}
