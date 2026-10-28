// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Provider.Contracts;
using rtaime.Provider.St2110;

namespace rtaime.Tests.Integration;

public sealed class St2022SoftwareMergeIntegrationTests
{
    [Fact]
    public void Merged_RTP_copies_feed_existing_single_leg_video_and_audio_decoders()
    {
        var profile = new St2110SoftwareProfile(8, 4, new FrameRate(50, 1));
        var packed = Enumerable.Range(0, profile.PackedFrameBytes)
            .Select(index => (byte)index).ToArray();
        var wireVideo = St2110VideoRtp.Packetize(profile, packed, 90000, 111, 65535).ToArray();
        Assert.Equal(4, wireVideo.Length);

        var videoMerger = new St2022SoftwarePacketMerger(
            St2022RtpMediaKind.Video, 111, new St2022SoftwareMergeOptions(
                16, 8, TimeSpan.FromMilliseconds(5)));
        var videoReceiver = new St2110VideoFrameReceiver(profile, 111);
        St2110PackedVideoFrame? finalFrame = null;
        var videoSequences = new List<uint>();

        void DeliverVideo(St2022RtpLeg leg, byte[] packet, int milliseconds)
        {
            var arrival = TimeSpan.FromMilliseconds(milliseconds);
            foreach (var merged in videoMerger.Observe(leg, packet, arrival))
            {
                videoSequences.Add(merged.ExtendedSequence);
                if (videoReceiver.TryReceive(merged.Datagram, arrival, out var frame))
                    finalFrame = frame;
            }
        }

        DeliverVideo(St2022RtpLeg.Primary, wireVideo[0], 0);
        DeliverVideo(St2022RtpLeg.Primary, wireVideo[2], 1);
        DeliverVideo(St2022RtpLeg.Secondary, wireVideo[1], 2);
        DeliverVideo(St2022RtpLeg.Secondary, wireVideo[3], 3);
        Assert.Equal(new uint[] { 65535, 65536, 65537, 65538 }, videoSequences);
        Assert.NotNull(finalFrame);
        Assert.Equal(packed, finalFrame.PackedYCbCr422TenBit);
        Assert.Equal(90000u, finalFrame.RtpTimestamp);
        Assert.Equal(0ul, videoReceiver.IncompleteFrames);
        Assert.Equal(0ul, videoMerger.Snapshot(TimeSpan.FromMilliseconds(3)).PostMergeObservedLoss);

        var packetizer = new St2110PcmRtpPacketizer(222, 48000, 65535);
        var wirePcm = packetizer.Packetize(new byte[48 * 3 * 8], 0).ToArray();
        Assert.Equal(3, wirePcm.Length);
        var audioMerger = new St2022SoftwarePacketMerger(
            St2022RtpMediaKind.Audio, 222, new St2022SoftwareMergeOptions(
                16, 8, TimeSpan.FromMilliseconds(5)));
        var audioReceiver = new St2110PcmRtpReceiver(222);
        var audioTimestamps = new List<uint>();

        void DeliverAudio(St2022RtpLeg leg, byte[] packet, int milliseconds)
        {
            var arrival = TimeSpan.FromMilliseconds(milliseconds);
            foreach (var merged in audioMerger.Observe(leg, packet, arrival))
            {
                Assert.True(audioReceiver.TryReceive(merged.Datagram, arrival, out var pcm));
                Assert.NotNull(pcm);
                audioTimestamps.Add(pcm.RtpTimestamp);
            }
        }

        DeliverAudio(St2022RtpLeg.Primary, wirePcm[0], 0);
        DeliverAudio(St2022RtpLeg.Primary, wirePcm[2], 1);
        DeliverAudio(St2022RtpLeg.Secondary, wirePcm[1], 2);
        Assert.Equal(new uint[] { 48000, 48048, 48096 }, audioTimestamps);
        Assert.Equal(0ul, audioMerger.Snapshot(TimeSpan.FromMilliseconds(2)).PostMergeObservedLoss);
        Assert.Equal("UNVERIFIED",
            audioMerger.Snapshot(TimeSpan.FromMilliseconds(2)).HardwareQualification);
    }
}
