// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Text;
using rtaime.Media;
using rtaime.Media.Contracts;

namespace rtaime.Tests.Integration;

public sealed class LocalMediaContainerProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Probe_checks_brand_codec_timing_and_version_without_a_native_decoder(bool mov)
    {
        WithFile(Movie(mov: mov), path => {
            ILocalMediaProbe probe = mov ? new QuickTimeLocalMediaProbe() : new Mp4LocalMediaProbe();
            Assert.Equal(1, probe.Version);
            var result = probe.Read(path);
            Assert.Equal(mov ? MediaContainerFormat.QuickTimeMov : MediaContainerFormat.Mp4, result.Container);
            Assert.Equal("avc1", result.SampleEntry);
            Assert.Equal(MediaVideoCodec.H264, result.VideoCodec);
            Assert.Equal(100, result.Profile); Assert.Equal(8, result.BitDepth); Assert.Equal(1, result.ChromaFormat);
            Assert.Equal(50, result.FrameRateNumerator); Assert.Equal(1, result.FrameRateDenominator);
            Assert.Equal(TimeSpan.FromSeconds(6), result.Duration);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Renaming_a_container_does_not_change_its_identity(bool mov)
    {
        WithFile(Movie(mov: mov), path => {
            ILocalMediaProbe wrong = mov ? new Mp4LocalMediaProbe() : new QuickTimeLocalMediaProbe();
            Assert.Equal("media.file.container_mismatch", Assert.Throws<LocalMediaProbeException>(() => wrong.Read(path)).Code);
        });
    }

    [Theory]
    [InlineData("external", "media.file.external_reference_unsupported")]
    [InlineData("rotation", "media.file.transform_unsupported")]
    [InlineData("pasp", "media.file.pixel_aspect_unsupported")]
    [InlineData("hdr", "media.file.color_unsupported")]
    [InlineData("edit", "media.file.edit_list_unsupported")]
    [InlineData("prores", "media.file.video_codec_unsupported")]
    [InlineData("high10", "media.file.profile_unsupported")]
    public void Unsupported_conversion_and_timeline_metadata_is_explicit(string mutation, string code)
    {
        WithFile(Movie(mutation: mutation), path =>
            Assert.Equal(code, Assert.Throws<LocalMediaProbeException>(() => new Mp4LocalMediaProbe().Read(path)).Code));
    }

    [Fact]
    public void Vfr_timing_is_retained_as_a_rational_average_and_sdr_is_explicit()
    {
        WithFile(Movie(mutation: "vfr"), path => {
            var result = new Mp4LocalMediaProbe().Read(path);
            Assert.True(result.VariableFrameRate);
            Assert.Equal(100, result.FrameRateNumerator); Assert.Equal(3, result.FrameRateDenominator);
            Assert.Equal("nclx:1/1/1", result.ColorDescription);
        });
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("zero-timescale")]
    [InlineData("timing-count")]
    [InlineData("descriptions")]
    [InlineData("duplicate-video")]
    [InlineData("avc-length")]
    [InlineData("composition-count")]
    public void Malformed_container_metadata_fails_without_native_open(string mutation)
    {
        WithFile(Movie(mutation: mutation), path =>
            Assert.Throws<InvalidDataException>(() => new Mp4LocalMediaProbe().Read(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void B_frame_composition_offsets_extend_presentation_without_widening_edit_limits(bool mov)
    {
        WithFile(Movie(mov: mov, mutation: "composition"), path => {
            ILocalMediaProbe probe = mov ? new QuickTimeLocalMediaProbe() : new Mp4LocalMediaProbe();
            Assert.Equal(TimeSpan.FromSeconds(6), probe.Read(path).Duration);
        });
    }

    [Fact]
    public void Native_coded_padding_requires_an_exact_zero_origin_display_aperture()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(WindowsMediaFoundationLocalMediaDecoder.NativeGeometryMatches(1920, 1080, 1920, 1088, []));
        var aperture = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(aperture.AsSpan(8), 1920);
        BinaryPrimitives.WriteUInt32LittleEndian(aperture.AsSpan(12), 1080);
        Assert.True(WindowsMediaFoundationLocalMediaDecoder.NativeGeometryMatches(1920, 1080, 1920, 1088, aperture));
        aperture[0] = 1;
        Assert.False(WindowsMediaFoundationLocalMediaDecoder.NativeGeometryMatches(1920, 1080, 1920, 1088, aperture));
        Assert.False(WindowsMediaFoundationLocalMediaDecoder.NativeGeometryMatches(1920, 1080, 1900, 1088, []));
    }

    [Fact]
    public void Moov_allocation_and_top_level_scan_have_explicit_bounds()
    {
        var huge = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(huge, 34 * 1024 * 1024);
        Encoding.ASCII.GetBytes("moov").CopyTo(huge, 4);
        var prefix = Box("ftyp", Join(Encoding.ASCII.GetBytes("isom"), U32(0)));
        WithFile(Join(prefix, huge), path => {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(prefix.Length + 34 * 1024 * 1024);
            Assert.Throws<InvalidDataException>(() => new Mp4LocalMediaProbe().Read(path));
        });
        WithFile(Join(Enumerable.Repeat(Box("free", []), 4097).ToArray()), path =>
            Assert.Throws<InvalidDataException>(() => new Mp4LocalMediaProbe().Read(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_checked_in_reference_passes_probe_before_native_open(bool mismatched)
    {
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        if (mismatched)
            Assert.Equal("media.file.container_mismatch", Assert.Throws<LocalMediaProbeException>(() =>
                new QuickTimeLocalMediaProbe().Read(asset.Path)).Code);
        else
        {
            var metadata = new Mp4LocalMediaProbe().Read(asset.Path);
            Assert.Equal(MediaVideoCodec.H264, metadata.VideoCodec);
            Assert.Equal("mp4a", metadata.AudioSampleEntry);
        }
    }

    private static byte[] Movie(bool mov = false, string mutation = "")
    {
        var mvhd = new byte[100]; U32(1000).CopyTo(mvhd, 12); U32(6000).CopyTo(mvhd, 16);
        var tkhd = new byte[84];
        U32(mutation == "rotation" ? 0u : 65536u).CopyTo(tkhd, 40);
        U32(65536).CopyTo(tkhd, 56); U32(0x40000000).CopyTo(tkhd, 72);
        var mdhd = new byte[24]; U32(mutation == "zero-timescale" ? 0u : 1000u).CopyTo(mdhd, 12);
        U32(6000).CopyTo(mdhd, 16);
        var hdlr = new byte[12]; Encoding.ASCII.GetBytes("vide").CopyTo(hdlr, 8);
        var visual = new byte[78]; visual[7] = 1; visual[24] = 7; visual[25] = 128; visual[26] = 4; visual[27] = 56;
        byte[] avcc = [1, mutation == "high10" ? (byte)110 : (byte)100, 0, 40, 255, 225, 0,
            mutation == "avc-length" ? (byte)255 : (byte)1, 103, 1, 0, 1, 104];
        var pasp = Box("pasp", Join(U32(mutation == "pasp" ? 2u : 1u), U32(1)));
        byte[] color = [.. Encoding.ASCII.GetBytes("nclx"), 0, 1, 0,
            mutation == "hdr" ? (byte)16 : (byte)1, 0, 1, 0];
        var entry = Box(mutation == "prores" ? "apch" : "avc1", Join(visual, Box("avcC", avcc), pasp, Box("colr", color)));
        var stsd = Box("stsd", Join(U32(0), U32(mutation == "descriptions" ? 2u : 1u), entry));
        var stts = Box("stts", mutation == "vfr"
            ? Join(U32(0), U32(2), U32(100), U32(20), U32(100), U32(40))
            : Join(U32(0), U32(mutation == "timing-count" ? uint.MaxValue : 1), U32(300), U32(20)));
        var edit = Box("edts", Box("elst", Join(U32(0), U32(1), U32(6000),
            U32(mutation == "edit" ? uint.MaxValue : mutation.StartsWith("composition", StringComparison.Ordinal) ? 40u : 0u), U32(65536))));
        var track = Box("trak", Join(Box("tkhd", tkhd), edit, Box("mdia", Join(Box("mdhd", mdhd),
            Box("hdlr", hdlr), Box("minf", Join(Box("dinf", Box("dref", Join(U32(0), U32(1),
                Box("url ", U32(mutation == "external" ? 0u : 1u))))), Box("stbl", Join(stsd, stts,
                    mutation.StartsWith("composition", StringComparison.Ordinal)
                        ? Box("ctts", Join(U32(0), U32(1), U32(mutation == "composition-count" ? 299u : 300u), U32(40))) : []))))))));
        var bytes = Join(Box("ftyp", Join(Encoding.ASCII.GetBytes(mov ? "qt  " : "isom"), U32(0))),
            Box("moov", Join(Box("mvhd", mvhd), track, mutation == "duplicate-video" ? track : [])));
        return mutation == "truncated" ? bytes[..^1] : bytes;
    }

    private static byte[] Box(string name, byte[] payload) => Join(U32(checked((uint)payload.Length + 8)),
        Encoding.ASCII.GetBytes(name), payload);
    private static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    private static void WithFile(byte[] bytes, Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rtaime-probe-{Guid.NewGuid():N}.mp4");
        try { File.WriteAllBytes(path, bytes); action(path); }
        finally { File.Delete(path); }
    }
}
