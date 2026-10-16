// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Runtime.InteropServices;
using rtaime.Media;
using rtaime.Media.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Integration;

public sealed class LocalMediaDecoderSafetyTests
{
    [Fact]
    public void Empty_callbacks_and_stream_ticks_are_not_EOF_but_have_a_finite_progress_budget()
    {
        var progress = new LocalMediaSampleProgress();
        for (var index = 0; index < LocalMediaSampleProgress.MaximumEmptyReads - 1; index++)
        {
            progress.BeforeRead(CancellationToken.None);
            Assert.Equal(LocalMediaSampleDisposition.Gap, progress.Observe(0x100, index, false, null));
        }
        Assert.Equal(LocalMediaSampleDisposition.Frame, progress.Observe(0, 200, true, null));
        Assert.Equal(LocalMediaSampleDisposition.Gap, progress.Observe(0, 0, false, null));
        Assert.Equal(LocalMediaSampleDisposition.Ended, progress.Observe(2, 0, false, null));
        var stalled = new LocalMediaSampleProgress();
        for (var index = 1; index < LocalMediaSampleProgress.MaximumEmptyReads; index++)
            stalled.Observe(0, index, false, null);
        Assert.Equal("media.file.no_progress", Assert.Throws<LocalMediaDecodeException>(() =>
            stalled.Observe(0, 0, false, null)).Code);
        // An EOS flag accompanying a final owned sample must not discard that sample.
        Assert.Equal(LocalMediaSampleDisposition.Frame, progress.Observe(2, 201, true, null));
    }

    [Theory]
    [InlineData(1u, "media.file.decoder_lost")]
    [InlineData(4u, "media.file.decoded_type_changed")]
    [InlineData(16u, "media.file.decoded_type_changed")]
    [InlineData(32u, "media.file.decoded_type_changed")]
    [InlineData(512u, "media.file.decoded_type_changed")]
    [InlineData(8u, "media.file.stream_flags_invalid")]
    public void Unsupported_reader_state_fails_before_payload_access(uint flags, string code)
    {
        Assert.Equal(code, Assert.Throws<LocalMediaDecodeException>(() =>
            new LocalMediaSampleProgress().Observe(flags, 0, true, null)).Code);
    }

    [Fact]
    public void Seek_scanning_requires_advancing_timestamps_and_has_an_absolute_read_budget()
    {
        var progress = new LocalMediaSampleProgress();
        Assert.Equal(LocalMediaSampleDisposition.Gap, progress.Observe(0, -20, true, 100));
        Assert.Equal(LocalMediaSampleDisposition.Gap, progress.Observe(0, 0, true, 100));
        Assert.Equal("media.file.timestamp_discontinuity", Assert.Throws<LocalMediaDecodeException>(() =>
            progress.Observe(0, 0, true, 100)).Code);
        var scan = new LocalMediaSampleProgress();
        for (var index = 0; index < LocalMediaSampleProgress.MaximumReads; index++)
        {
            scan.BeforeRead(CancellationToken.None);
            scan.Observe(0, index, true, long.MaxValue);
        }
        Assert.Equal("media.file.no_progress", Assert.Throws<LocalMediaDecodeException>(() =>
            scan.BeforeRead(CancellationToken.None)).Code);
        Assert.Equal(LocalMediaSampleDisposition.Frame, new LocalMediaSampleProgress().Observe(0, 100, true, 100));
    }

    [Fact]
    public void Cancellation_and_operation_deadline_cover_all_reads_in_one_frame()
    {
        var clock = new ControlledClock();
        var progress = new LocalMediaSampleProgress(clock);
        Assert.Equal(TimeSpan.FromSeconds(30), progress.BeforeRead(CancellationToken.None));
        clock.Seconds = 29;
        Assert.Equal(TimeSpan.FromSeconds(1), progress.BeforeRead(CancellationToken.None));
        clock.Seconds = 30;
        Assert.Equal("media.file.read_timeout", Assert.Throws<LocalMediaDecodeException>(() =>
            progress.BeforeRead(CancellationToken.None)).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => progress.BeforeRead(cancelled.Token));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(192004u)]
    [InlineData(uint.MaxValue)]
    public void Invalid_PCM_extent_is_rejected_before_managed_allocation(uint length)
    {
        Assert.Equal("media.file.audio_payload_invalid", Assert.Throws<LocalMediaDecodeException>(() =>
            LocalMediaSampleProgress.ValidatePcmLength(length)).Code);
        Assert.Equal(192000, LocalMediaSampleProgress.ValidatePcmLength(192000));
        Assert.Equal(4, LocalMediaSampleProgress.ValidatePcmLength(4));
    }

    [Fact]
    public void Terminal_failure_never_retries_or_seeks_the_damaged_reader()
    {
        using var decoder = new FaultDecoder();
        using var source = new LocalMediaFileSource("controlled.mp4", decoder.Probe, decoder);
        Assert.Equal("media.file.no_progress", source.ReadNext(0).Failure?.Code);
        Assert.Equal("media.file.no_progress", source.ReadNext(1).Failure?.Code);
        Assert.Equal("media.file.no_progress", source.SeekToFrame(0).Failure?.Code);
        Assert.Equal(1, decoder.Reads);
        Assert.Equal(0, decoder.Seeks);
        Assert.Equal(0UL, source.Measurements.ReadRetries);
        Assert.Equal(1UL, source.Measurements.ReadFailures);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(-12)]
    public void Production_RGB32_copy_preserves_pitch_orientation_channels_and_opaque_alpha(int pitch)
    {
        if (!OperatingSystem.IsWindows()) return;
        var native = Marshal.AllocHGlobal(24);
        try
        {
            var data = Enumerable.Repeat((byte)0xEE, 24).ToArray();
            byte[] top = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] bottom = [9, 10, 11, 12, 13, 14, 15, 16];
            top.CopyTo(data, pitch > 0 ? 0 : 12);
            bottom.CopyTo(data, pitch > 0 ? 12 : 0);
            Marshal.Copy(data, 0, native, data.Length);
            var destination = new byte[16];
            WindowsMediaFoundationLocalMediaDecoder.CopyRgb32Rows(
                IntPtr.Add(native, pitch > 0 ? 0 : 12), pitch, 2, 2, destination);
            WindowsMediaFoundationLocalMediaDecoder.SwizzleRgb32(destination);
            Assert.Equal(new byte[] { 3, 2, 1, 255, 7, 6, 5, 255, 11, 10, 9, 255, 15, 14, 13, 255 }, destination);
            Assert.Throws<InvalidDataException>(() => {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                WindowsMediaFoundationLocalMediaDecoder.CopyRgb32Rows(native, 4, 2, 2, destination);
            });
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    private sealed class ControlledClock : TimeProvider
    {
        public long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    private sealed class FaultDecoder : ILocalMediaDecoder
    {
        public int Reads { get; private set; }
        public int Seeks { get; private set; }
        public LocalMediaProbe Probe { get; } = new(MediaContractVersion.Current, MediaAssetId.New(), MediaSourceId.New(),
            "controlled.mp4", MediaContainerFormat.Mp4, MediaVideoCodec.H264, MediaAudioCodec.None,
            VideoFormat.Hd1080p50Rgba8, AudioFormat.Stereo48kFloat32, TimeSpan.FromSeconds(1));
        public bool TryReadNext(ulong sequence, out LocalMediaDecodedFrame? frame)
        {
            frame = null;
            Reads++;
            throw new LocalMediaDecodeException("media.file.no_progress", "Injected stalled reader.");
        }
        public LocalMediaSeekResult SeekToFrame(long frame) { Seeks++; return LocalMediaSeekResult.Positioned(frame); }
        public void Dispose() { }
    }
}
