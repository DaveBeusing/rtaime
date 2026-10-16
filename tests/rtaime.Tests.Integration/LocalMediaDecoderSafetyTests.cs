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
            var contiguous = new byte[16];
            WindowsMediaFoundationLocalMediaDecoder.CopyContiguousRgb32Rows(native, 24, 24, 2, 2, pitch, contiguous);
            WindowsMediaFoundationLocalMediaDecoder.SwizzleRgb32(contiguous);
            Assert.Equal(destination, contiguous);
            Assert.Throws<LocalMediaDecodeException>(() => {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                WindowsMediaFoundationLocalMediaDecoder.CopyContiguousRgb32Rows(native, 19, 24, 2, 2, pitch, contiguous);
            });
            Assert.Equal(new byte[] { 3, 2, 1, 255, 7, 6, 5, 255, 11, 10, 9, 255, 15, 14, 13, 255 }, destination);
            Assert.Throws<InvalidDataException>(() => {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                WindowsMediaFoundationLocalMediaDecoder.CopyRgb32Rows(native, 4, 2, 2, destination);
            });
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    [Fact]
    public void Repeated_native_open_seek_close_has_bounded_resources_and_records_allocation_and_read_P99()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var provider = new LocalMediaFileProvider();
        using var process = Process.GetCurrentProcess();
        var timings = new List<double>(96);
        var handles = new List<int>(24);
        var privateBytes = new List<long>(24);
        long allocatedAtStart = 0;
        for (var cycle = 0; cycle < 32; cycle++)
        {
            if (cycle == 8) allocatedAtStart = GC.GetTotalAllocatedBytes();
            var opened = provider.TryOpen(asset.Path, MediaSourceId.New());
            Assert.True(opened.Succeeded, opened.Failure?.Message);
            using (var source = opened.Source!)
            {
                for (var frame = 0; frame < 3; frame++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var read = source.ReadNext((ulong)frame);
                    Assert.True(read.Succeeded, read.Failure?.Code + ": " + read.Failure?.Message);
                    if (cycle >= 8) timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                Assert.True(source.SeekToFrame(20).Succeeded);
                Assert.True(source.ReadNext(3).Succeeded);
            }
            if (cycle >= 8)
            {
                process.Refresh();
                handles.Add(process.HandleCount);
                privateBytes.Add(process.PrivateMemorySize64);
            }
        }
        timings.Sort();
        var handleGrowth = handles.Max() - handles[0];
        var privateGrowth = privateBytes.Max() - privateBytes[0];
        Console.WriteLine("Native lifecycle: cycles=32 warmup=8 reads=128 seek=32; allocationBytes=" +
            (GC.GetTotalAllocatedBytes() - allocatedAtStart) + "; readP99Milliseconds=" +
            timings[(int)Math.Ceiling(timings.Count * .99) - 1] +
            "; postWarmupHandleGrowth=" + handleGrowth + "; postWarmupPrivateGrowth=" + privateGrowth);
        // Existing hosted playback ceilings; no forced collection or physical-SLA claim.
        Assert.InRange(handleGrowth, 0, 64);
        Assert.InRange(privateGrowth, 0, 512L * 1024 * 1024);
    }

    [Fact]
    public void Production_full_frame_copy_profile_reuses_storage_without_per_frame_allocations()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int width = 1920, height = 1080;
        var pixels = new byte[width * height * 4];
        var elapsed = new double[64];
        var native = Marshal.AllocHGlobal(pixels.Length);
        try
        {
            Array.Fill(pixels, (byte)17);
            Marshal.Copy(pixels, 0, native, pixels.Length);
            for (var index = 0; index < 4; index++)
            {
                WindowsMediaFoundationLocalMediaDecoder.CopyRgb32Rows(native, width * 4, width, height, pixels);
                WindowsMediaFoundationLocalMediaDecoder.SwizzleRgb32(pixels);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < elapsed.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                WindowsMediaFoundationLocalMediaDecoder.CopyRgb32Rows(native, width * 4, width, height, pixels);
                WindowsMediaFoundationLocalMediaDecoder.SwizzleRgb32(pixels);
                elapsed[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Array.Sort(elapsed);
            Console.WriteLine("Production RGB32 copy/swizzle: samples=64 size=1920x1080; allocatedBytes=" +
                allocated + "; P99Milliseconds=" + elapsed[^1]);
            Assert.Equal(17, pixels[0]);
            Assert.Equal(255, pixels[3]);
            Assert.InRange(allocated, 0, 4096);
        }
        finally { Marshal.FreeHGlobal(native); }
    }

    [Theory]
    [InlineData(0x80070002u, "media.file.storage_failure")]
    [InlineData(0x80070015u, "media.file.storage_failure")]
    [InlineData(0x80070020u, "media.file.storage_failure")]
    [InlineData(0x80070070u, "media.file.storage_failure")]
    [InlineData(0x8007045Du, "media.file.storage_failure")]
    [InlineData(0x80070057u, "media.file.decoder_lost")]
    [InlineData(0x80004005u, "media.file.decoder_lost")]
    public void Native_error_codes_distinguish_known_storage_faults_from_decoder_faults(uint status, string code)
    {
        Assert.Equal(code, LocalMediaSampleProgress.NativeFailureCode(unchecked((int)status)));
    }

    [Fact]
    public void A_type_notification_requires_explicit_layout_qualification_before_sample_or_gap_admission()
    {
        Assert.Equal(LocalMediaSampleDisposition.Frame,
            new LocalMediaSampleProgress().Observe(0x30, 0, true, null, normalizedTypeQualified: true));
        Assert.Equal(LocalMediaSampleDisposition.Gap,
            new LocalMediaSampleProgress().Observe(0x20, 0, false, null, normalizedTypeQualified: true));
        Assert.Equal("media.file.decoded_type_changed", Assert.Throws<LocalMediaDecodeException>(() =>
            new LocalMediaSampleProgress().Observe(0x20, 0, true, null, normalizedTypeQualified: false)).Code);
        Assert.Equal("media.file.decoder_lost", Assert.Throws<LocalMediaDecodeException>(() =>
            new LocalMediaSampleProgress().Observe(0x21, 0, true, null, normalizedTypeQualified: true)).Code);
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
