// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;
using System.Security.Cryptography;

namespace rtaime.Tests.Integration;

/// <summary>
/// Explicit external qualification: set RTAIME_MXF_QUALIFICATION=1, supply
/// RTAIME_RAW2BMX and RTAIME_FFPROBE to run genuine native OP1a interchange.
/// Without those inputs, the release evidence must not claim qualification.
/// </summary>
public sealed class BmxOp1aRecordingIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recording_produces_independently_verified_op1a(bool fractionalRate)
    {
        if (Environment.GetEnvironmentVariable("RTAIME_MXF_QUALIFICATION") != "1")
            return;
        var bmx = Environment.GetEnvironmentVariable("RTAIME_RAW2BMX");
        var ffprobe = Environment.GetEnvironmentVariable("RTAIME_FFPROBE");
        Assert.True(File.Exists(bmx), "Qualification requires a provisioned raw2bmx executable.");
        Assert.True(File.Exists(ffprobe), "Qualification requires an independent FFprobe executable.");

        var root = Path.Combine(Path.GetTempPath(), "rtaime-mxf-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var writer = new BmxOp1aRecordingWriter(root, bmx!, ffprobe!);
            Assert.Equal("program.mxf", writer.ConfigureTarget(root, "program"));
            var id = RecordingOutputId.New();
            await writer.OpenAsync(new RecordingStartRequest(
                RecordingContractVersion.Current, RecordingSessionId.New(),
                new RecordingOutputDescriptor(id, MediaSinkId.New(), "Program"),
                ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId), CancellationToken.None);

            var format = fractionalRate ? VideoFormat.Hd1080p59_94Rgba8 : VideoFormat.Hd1080p50Rgba8;
            var videoPayload = new byte[1920 * 1080 * 4];
            for (ulong frame = 0; frame < 2; frame++)
            {
                var surface = new SurfaceDescriptor(
                    SurfaceId.New(), format, SurfaceStorageDomain.Shared,
                    SurfaceOwnership.SharedLease,
                    new SurfaceLifetimeDescriptor(new Generation(frame), Identity.New()),
                    new OpaqueSurfaceHandle("test.mxf", $"frame-{frame}"));
                var video = new FrameDescriptor(
                    MediaContractVersion.Current, MediaSourceId.New(), surface,
                    new FrameTiming(frame, (long)frame,
                        new Timebase(format.FrameRate.Denominator, format.FrameRate.Numerator)));
                var audioStart = AudioPosition(frame, format.FrameRate);
                var audioEnd = AudioPosition(frame + 1, format.FrameRate);
                var audioCount = checked((uint)(audioEnd - audioStart));
                var audio = new AudioBufferDescriptor(
                    MediaContractVersion.Current, AudioStreamId.New(),
                    AudioFormat.Stereo48kFloat32, Identity.New(),
                    new AudioBufferTiming(audioStart, audioCount, (long)audioStart,
                        new Timebase(1, 48000)),
                    new OpaqueAudioHandle("test.mxf.audio", $"audio-{frame}"));
                var sample = new RecordingProgramSample(RecordingContractVersion.Current, id, video, audio);
                writer.StagePayload(frame, new Lease(videoPayload),
                    new byte[checked((int)audioCount * 2 * sizeof(float))]);
                await writer.WriteAsync(sample, CancellationToken.None);
            }
            await writer.FinalizeAsync(CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(root, "program.mxf")));
            Assert.False(File.Exists(Path.Combine(root, "program.partial.mxf")));
            Assert.False(File.Exists(Path.Combine(root, "program.mxf.lock")));
            var finalFile = Path.Combine(root, "program.mxf");
            var evidence = await MxfIndependentMediaProbe.InspectValidatedAsync(
                ffprobe!, finalFile, format.FrameRate.ToString(), 2,
                AudioPositionCeiling(2, format.FrameRate), CancellationToken.None);
            await using var published = File.OpenRead(finalFile);
            var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(published));
            var interchange = evidence.ToInterchangeMetadata(fingerprint, DateTimeOffset.UtcNow);
            Assert.Equal(fingerprint, interchange.FingerprintSha256);
            Assert.Contains(interchange.Fields, field =>
                field.Path == "video.0.frameRate" &&
                field.Value == format.FrameRate.ToString());
            Assert.Contains(interchange.Fields, field =>
                field.Path == "audio.0.channelCount" && field.Value == "2");
            Assert.DoesNotContain(interchange.Fields, field =>
                field.Path == "video.0.hdr" || field.Path == "audio.0.channelRole");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ulong AudioPosition(ulong index, FrameRate rate) =>
        checked((ulong)(((UInt128)index * 48000U * (ulong)rate.Denominator) /
                         (ulong)rate.Numerator));

    // The MXF writer pads at most one terminal PCM sample frame at fractional
    // rates before muxing, so independent reopen must verify the published
    // sample count (ceil), not the last unpadded input boundary (floor).
    private static ulong AudioPositionCeiling(ulong index, FrameRate rate)
    {
        var numerator = checked((UInt128)index * 48000U * (ulong)rate.Denominator);
        var denominator = (ulong)rate.Numerator;
        return checked((ulong)((numerator + denominator - 1) / denominator));
    }

    private sealed class Lease(ReadOnlyMemory<byte> memory) : IProgramRecordingPayloadLease
    {
        private bool _disposed;
        public ReadOnlyMemory<byte> Memory => _disposed ? throw new ObjectDisposedException(nameof(Lease)) : memory;
        public void Dispose() => _disposed = true;
    }
}
