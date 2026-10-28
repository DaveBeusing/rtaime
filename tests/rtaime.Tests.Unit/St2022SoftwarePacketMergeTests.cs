// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using rtaime.Provider.Contracts;
using rtaime.Provider.St2110;

namespace rtaime.Tests.Unit;

public sealed class St2022SoftwarePacketMergeTests
{
    private static St2022SoftwareMergeOptions Options(int capacity = 32,
        int threshold = 16, int windowMs = 5, int timeoutMs = 20) =>
        new(capacity, threshold, TimeSpan.FromMilliseconds(windowMs),
            TimeSpan.FromMilliseconds(timeoutMs));

    private static byte[] Video(uint sequence, uint timestamp = 90000, uint ssrc = 111)
    {
        var bytes = new byte[St2110VideoRtp.VideoHeaderBytes + 5];
        St2110RtpWire.WriteHeader(bytes, St2110SoftwareProfile.VideoPayloadType,
            false, unchecked((ushort)sequence), timestamp, ssrc);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), (ushort)(sequence >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14), 5);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18), 0);
        bytes[^1] = (byte)sequence;
        return bytes;
    }

    private static byte[] Audio(ushort sequence, uint timestamp = 48000, uint ssrc = 222)
    {
        var bytes = new byte[St2110RtpWire.HeaderBytes + St2110PcmRtpPacketizer.AudioPayloadBytes];
        St2110RtpWire.WriteHeader(bytes, St2110SoftwareProfile.AudioPayloadType,
            false, sequence, timestamp, ssrc);
        bytes[^1] = (byte)sequence;
        return bytes;
    }

    [Fact]
    public void Independent_leg_loss_selects_missing_copies_without_post_merge_gaps()
    {
        var merge = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Video, 111, Options());
        var emitted = new List<St2022MergedDatagram>();
        emitted.AddRange(merge.Observe(St2022RtpLeg.Primary, Video(100), TimeSpan.Zero));
        emitted.AddRange(merge.Observe(St2022RtpLeg.Primary, Video(102), TimeSpan.FromMilliseconds(1)));
        emitted.AddRange(merge.Observe(St2022RtpLeg.Secondary, Video(101), TimeSpan.FromMilliseconds(2)));
        emitted.AddRange(merge.Observe(St2022RtpLeg.Secondary, Video(103), TimeSpan.FromMilliseconds(3)));
        emitted.AddRange(merge.Observe(St2022RtpLeg.Primary, Video(105), TimeSpan.FromMilliseconds(4)));
        emitted.AddRange(merge.Observe(St2022RtpLeg.Secondary, Video(104), TimeSpan.FromMilliseconds(5)));

        Assert.Equal(new uint[] { 100, 101, 102, 103, 104, 105 },
            emitted.Select(p => p.ExtendedSequence));
        Assert.All(emitted, p => Assert.Equal(90000u, p.Timestamp));
        Assert.All(emitted, p => Assert.Equal(Video(p.ExtendedSequence), p.Datagram));
        var health = merge.Snapshot(TimeSpan.FromMilliseconds(5));
        Assert.Equal("UNVERIFIED", health.HardwareQualification);
        Assert.Equal(St2022SoftwarePathState.Healthy, health.ObservedPathState);
        Assert.Equal(0ul, health.PostMergeObservedLoss);
        Assert.Equal(3ul, health.SelectedPrimary);
        Assert.Equal(3ul, health.SelectedSecondary);
        Assert.Equal(3ul, health.Primary.OutstandingGaps);
        Assert.Equal(1ul, health.Secondary.OutstandingGaps);
        Assert.Equal(0, health.BufferedPackets);
    }

    [Fact]
    public void Common_mode_loss_expires_by_holdback_and_late_repair_is_not_replayed()
    {
        var merge = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Video, 111, Options());
        Assert.Single(merge.Observe(St2022RtpLeg.Primary, Video(10), TimeSpan.Zero));
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, Video(12), TimeSpan.FromMilliseconds(1)));
        Assert.Empty(merge.Flush(TimeSpan.FromMilliseconds(4)));
        var released = Assert.Single(merge.Flush(TimeSpan.FromMilliseconds(6)));
        Assert.Equal(12u, released.ExtendedSequence);
        Assert.Equal(1ul, merge.Snapshot(TimeSpan.FromMilliseconds(6)).PostMergeObservedLoss);
        Assert.Empty(merge.Observe(St2022RtpLeg.Secondary, Video(11), TimeSpan.FromMilliseconds(7)));
        Assert.Equal(1ul, merge.Snapshot(TimeSpan.FromMilliseconds(7)).Late);
    }

    [Fact]
    public void Both_stream_sequence_spaces_wrap_without_changing_rtp_timestamps()
    {
        var video = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Video, 111, Options());
        var videoOut = new List<St2022MergedDatagram>();
        var videoSeq = new[] { uint.MaxValue - 1, uint.MaxValue, 0u, 1u };
        for (var i = 0; i < videoSeq.Length; i++)
            videoOut.AddRange(video.Observe(i % 2 == 0
                ? St2022RtpLeg.Primary : St2022RtpLeg.Secondary,
                Video(videoSeq[i], uint.MaxValue), TimeSpan.FromMilliseconds(i)));
        Assert.Equal(videoSeq, videoOut.Select(p => p.ExtendedSequence));
        Assert.All(videoOut, p => Assert.Equal(uint.MaxValue, p.Timestamp));

        var audio = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Audio, 222, Options());
        var audioOut = new List<St2022MergedDatagram>();
        var audioSeq = new ushort[] { 65534, 65535, 0, 1 };
        for (var i = 0; i < audioSeq.Length; i++)
            audioOut.AddRange(audio.Observe(i % 2 == 0
                ? St2022RtpLeg.Primary : St2022RtpLeg.Secondary,
                Audio(audioSeq[i], unchecked(uint.MaxValue + (uint)(i * 48))),
                TimeSpan.FromMilliseconds(i)));
        Assert.Equal(new uint[] { 65534, 65535, 65536, 65537 },
            audioOut.Select(p => p.ExtendedSequence));
        Assert.Equal(new uint[] { uint.MaxValue, 47, 95, 143 },
            audioOut.Select(p => p.Timestamp));
        Assert.Equal(0ul, audio.Snapshot(TimeSpan.FromMilliseconds(3)).PostMergeObservedLoss);
    }

    [Fact]
    public void Duplicate_and_conflicting_copies_never_override_selected_packet()
    {
        var merge = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Video, 111, Options());
        Assert.Single(merge.Observe(St2022RtpLeg.Primary, Video(10), TimeSpan.Zero));
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, Video(12), TimeSpan.FromMilliseconds(1)));
        Assert.Empty(merge.Observe(St2022RtpLeg.Secondary, Video(12), TimeSpan.FromMilliseconds(2)));
        var conflictingPending = Video(12, 91000);
        Assert.Empty(merge.Observe(St2022RtpLeg.Secondary,
            conflictingPending, TimeSpan.FromMilliseconds(3)));
        Assert.Empty(merge.Observe(St2022RtpLeg.Secondary, Video(10), TimeSpan.FromMilliseconds(4)));
        Assert.Empty(merge.Observe(St2022RtpLeg.Secondary,
            Video(10, 91000), TimeSpan.FromMilliseconds(5)));
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary,
            Video(11, ssrc: 999), TimeSpan.FromMilliseconds(6)));
        var badHeader = Video(11);
        badHeader[0] = 0x90;
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, badHeader, TimeSpan.FromMilliseconds(7)));
        var emitted = Assert.Single(merge.Flush(TimeSpan.FromMilliseconds(8)));
        Assert.Equal(Video(12), emitted.Datagram);
        var health = merge.Snapshot(TimeSpan.FromMilliseconds(8));
        Assert.Equal(1ul, health.Duplicates);
        Assert.Equal(1ul, health.Late);
        Assert.Equal(2ul, health.ConflictingCopies);
        Assert.Equal(2ul, health.InvalidPackets);
        Assert.Equal(1ul, health.PostMergeObservedLoss);
    }

    [Fact]
    public void Finite_window_rejects_jump_and_stores_at_most_two_bounded_packet_windows()
    {
        var merge = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Video, 111,
            Options(4, 4, 250));
        Assert.Single(merge.Observe(St2022RtpLeg.Primary, Video(0), TimeSpan.Zero));
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, Video(2), TimeSpan.FromMilliseconds(1)));
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, Video(3), TimeSpan.FromMilliseconds(2)));
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, Video(4), TimeSpan.FromMilliseconds(3)));
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, Video(5), TimeSpan.FromMilliseconds(4)));
        var before = merge.Snapshot(TimeSpan.FromMilliseconds(4));
        Assert.Equal(1ul, before.OutOfWindow);
        Assert.Equal(3, before.BufferedPackets);
        Assert.InRange(before.RetainedDatagrams, 1, 8);
        var after = merge.Flush(TimeSpan.FromMilliseconds(252));
        Assert.Equal(new uint[] { 2, 3, 4 }, after.Select(p => p.ExtendedSequence));
        Assert.Equal(1ul, merge.Snapshot(TimeSpan.FromMilliseconds(252)).PostMergeObservedLoss);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            merge.Flush(TimeSpan.FromMilliseconds(251)));
    }

    [Fact]
    public void Software_observed_states_distinguish_unknown_one_leg_both_legs_and_blackout()
    {
        var merge = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Video, 111,
            Options(timeoutMs: 4));
        Assert.Equal(St2022SoftwarePathState.Unknown,
            merge.Snapshot(TimeSpan.Zero).ObservedPathState);
        Assert.Single(merge.Observe(St2022RtpLeg.Primary, Video(50), TimeSpan.Zero));
        Assert.Equal(St2022SoftwarePathState.Degraded,
            merge.Snapshot(TimeSpan.Zero).ObservedPathState);
        Assert.Empty(merge.Observe(St2022RtpLeg.Secondary, Video(50),
            TimeSpan.FromMilliseconds(1)));
        Assert.Equal(St2022SoftwarePathState.Healthy,
            merge.Snapshot(TimeSpan.FromMilliseconds(1)).ObservedPathState);
        Assert.Equal(St2022SoftwarePathState.Degraded,
            merge.Snapshot(TimeSpan.FromMilliseconds(5)).ObservedPathState);
        Assert.Equal(St2022SoftwarePathState.Failed,
            merge.Snapshot(TimeSpan.FromMilliseconds(6)).ObservedPathState);
        Assert.Single(merge.Observe(St2022RtpLeg.Secondary, Video(51),
            TimeSpan.FromMilliseconds(7)));
        Assert.Equal(St2022SoftwarePathState.Degraded,
            merge.Snapshot(TimeSpan.FromMilliseconds(7)).ObservedPathState);
        Assert.Single(merge.Observe(St2022RtpLeg.Primary, Video(52),
            TimeSpan.FromMilliseconds(8)));
        Assert.Equal(St2022SoftwarePathState.Healthy,
            merge.Snapshot(TimeSpan.FromMilliseconds(8)).ObservedPathState);
        Assert.Equal("UNVERIFIED", merge.Snapshot(TimeSpan.FromMilliseconds(8)).HardwareQualification);
    }

    [Fact]
    public void Software_impairment_patterns_do_not_miscount_single_leg_loss_as_combined_loss()
    {
        var merge = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Audio, 222,
            Options(256, 128, 5));
        var emitted = new List<St2022MergedDatagram>();
        for (ushort sequence = 0; sequence < 100; sequence++)
        {
            var now = TimeSpan.FromMilliseconds(sequence);
            // Independent missing, blackout and duplicates: 0, 35, 70
            // are lost on both legs, while all other numbers reach one.
            if (sequence % 7 != 0)
                emitted.AddRange(merge.Observe(St2022RtpLeg.Primary,
                    Audio(sequence, (uint)sequence * 48), now));
            if (sequence % 5 != 0)
                emitted.AddRange(merge.Observe(St2022RtpLeg.Secondary,
                    Audio(sequence, (uint)sequence * 48), now));
        }
        emitted.AddRange(merge.Flush(TimeSpan.FromMilliseconds(110)));
        var expected = Enumerable.Range(1, 99)
            .Where(n => n % 35 != 0).Select(n => (uint)n).ToArray();
        Assert.Equal(expected, emitted.Select(p => p.ExtendedSequence));
        var health = merge.Snapshot(TimeSpan.FromMilliseconds(110));
        Assert.Equal(2ul, health.PostMergeObservedLoss);
        Assert.True(health.Primary.OutstandingGaps > 0);
        Assert.True(health.Secondary.OutstandingGaps > 0);
        Assert.InRange(health.MaximumBufferedPackets, 1, 256);
        Assert.InRange(health.RetainedDatagrams, 0, 512);
    }

    [Fact]
    public void Invalid_configuration_and_wrong_payload_type_cannot_become_a_protection_path()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new St2022SoftwareMergeOptions(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new St2022SoftwareMergeOptions(4, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new St2022SoftwareMergeOptions(
            repairWindow: TimeSpan.FromMilliseconds(300)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new St2022SoftwareMergeOptions(
            legTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new St2022SoftwarePacketMerger(St2022RtpMediaKind.Video, 0));
        var merge = new St2022SoftwarePacketMerger(St2022RtpMediaKind.Audio, 222);
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, Video(0), TimeSpan.Zero));
        var wrongMarker = Audio(0);
        wrongMarker[1] |= 0x80;
        Assert.Empty(merge.Observe(St2022RtpLeg.Primary, wrongMarker,
            TimeSpan.FromMilliseconds(1)));
        Assert.Equal(2ul, merge.Snapshot(TimeSpan.FromMilliseconds(1)).InvalidPackets);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            merge.Observe(St2022RtpLeg.Primary, Audio(0), TimeSpan.FromMilliseconds(-1)));
    }
}
