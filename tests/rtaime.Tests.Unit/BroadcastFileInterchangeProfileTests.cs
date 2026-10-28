// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class BroadcastFileInterchangeProfileTests
{
    private static RecordingProfileCatalog Catalog(
        RecordingProfileDescriptor mxf) =>
        new([
            ProfessionalRecordingFormats.Mp4H264Aac,
            ProfessionalRecordingFormats.Mov2VuyPcm,
            mxf
        ], ProfessionalRecordingFormats.Mp4H264AacProfileId);

    [Fact]
    public void Unprovisioned_MXF_is_visible_as_unavailable_not_as_an_ingest_decoder()
    {
        var rows = BroadcastFileInterchangeProfiles.Snapshot(
            Catalog(ProfessionalRecordingFormats.MxfOp1aUncompressedPcm));
        Assert.Equal(15, rows.Count);
        var mxf = Assert.Single(rows, row => row.Id == "mxf-op1a-uyvy-pcm-50");
        Assert.Equal(BroadcastFileInterchangeStatus.Unavailable, mxf.RecordStatus);
        Assert.Equal(BroadcastFileInterchangeStatus.NotSupported, mxf.IngestStatus);
        Assert.Equal(1920u, mxf.Width);
        Assert.Equal(1080u, mxf.Height);
        Assert.Equal(new FrameRate(50, 1), mxf.FrameRate);
        Assert.Equal("8-bit 4:2:2", mxf.PixelEncoding);
        Assert.Equal(8, mxf.VideoBitDepth);
        Assert.Equal("PCM S16LE", mxf.AudioEssence);
        Assert.Equal(16, mxf.AudioBitDepth);
        Assert.Equal(1, mxf.AudioTrackCount);
        Assert.Equal(2, mxf.AudioChannelsPerTrack);
        Assert.Contains("order unverified", mxf.AudioChannelOrder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNMAPPED", mxf.Timecode, StringComparison.Ordinal);
        Assert.Contains("UNMAPPED", mxf.Language, StringComparison.Ordinal);
        Assert.False(mxf.InterchangeFileQualified);
        Assert.False(mxf.MeetsEbuR154);
    }

    [Fact]
    public void Provisioned_MXF_is_only_an_implemented_writer_until_exact_file_is_qualified()
    {
        var rows = BroadcastFileInterchangeProfiles.Snapshot(
            Catalog(ProfessionalRecordingFormats.CreateMxfOp1aUncompressedPcmDescriptor(true)));
        var mxf = Assert.Single(rows, row => row.Id == "mxf-op1a-uyvy-pcm-60000-1001");
        Assert.Equal(BroadcastFileInterchangeStatus.Implemented, mxf.RecordStatus);
        Assert.Equal(BroadcastFileInterchangeStatus.NotSupported, mxf.IngestStatus);
        Assert.Equal(new FrameRate(60000, 1001), mxf.FrameRate);
        Assert.True(mxf.IndependentReopenRequired);
        Assert.False(mxf.InterchangeFileQualified);
        Assert.False(mxf.MeetsEbuR154);
    }

    [Fact]
    public void Actual_provider_catalog_is_the_authority_not_a_static_support_flag()
    {
        var noMxf = new RecordingProfileCatalog(
            [ProfessionalRecordingFormats.Mov2VuyPcm],
            ProfessionalRecordingFormats.Mov2VuyPcmProfileId);
        var row = BroadcastFileInterchangeProfiles.Snapshot(noMxf)
            .Single(item => item.Id == "mxf-op1a-uyvy-pcm-50");
        Assert.Equal(BroadcastFileInterchangeStatus.NotSupported, row.RecordStatus);
        Assert.Contains("not registered", row.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BroadcastFileInterchangeStatus.NotSupported, row.IngestStatus);

        var available = ProfessionalRecordingFormats.CreateMxfOp1aUncompressedPcmDescriptor(true);
        var mismatched = new RecordingProfileDescriptor(
            available.ProfileId, available.DisplayName, available.Container, ".mxf",
            "H.264/AVC", available.VideoProfile, available.VideoLevel, available.AudioCodec,
            available.SupportedInputVideoFormats, available.RequiredInputAudioFormat,
            available.VideoBitRate, available.AudioBitRate, available.AccelerationClass,
            available.ProviderId, true, null, available.EvidenceState, available.Evidence);
        var result = BroadcastFileInterchangeProfiles.Snapshot(Catalog(mismatched))
            .Single(item => item.Id == "mxf-op1a-uyvy-pcm-50");
        Assert.Equal(BroadcastFileInterchangeStatus.NotSupported, result.RecordStatus);
        Assert.Contains("differs", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_MP4_input_is_conditional_and_Mov_output_does_not_imply_Mov_2vuy_input()
    {
        var rows = BroadcastFileInterchangeProfiles.Snapshot(
            Catalog(ProfessionalRecordingFormats.MxfOp1aUncompressedPcm));
        Assert.Equal(BroadcastFileInterchangeStatus.ConditionalProbe,
            rows.Single(row => row.Id == "mp4-h264-aac-50").IngestStatus);
        Assert.Equal(BroadcastFileInterchangeStatus.NotSupported,
            rows.Single(row => row.Id == "mov-2vuy-pcm-50").IngestStatus);
    }

    [Fact]
    public void R154_UHD_HLG_XAVC_and_IMF_profiles_remain_not_supported()
    {
        var rows = BroadcastFileInterchangeProfiles.Snapshot(
            Catalog(ProfessionalRecordingFormats.MxfOp1aUncompressedPcm));
        var r154 = rows.Where(row => row.Reference.StartsWith("EBU R 154", StringComparison.Ordinal)).ToArray();
        Assert.Equal(9, r154.Length);
        foreach (var row in r154)
        {
            Assert.Equal(3840u, row.Width);
            Assert.Equal(2160u, row.Height);
            Assert.Equal(10, row.VideoBitDepth);
            Assert.Equal("10-bit 4:2:2", row.PixelEncoding);
            Assert.Contains("HLG", row.Transfer, StringComparison.Ordinal);
            Assert.Equal(24, row.AudioBitDepth);
            Assert.Equal(48000u, row.AudioSampleRate);
            Assert.Null(row.RecordingProfileId);
            Assert.Equal(BroadcastFileInterchangeStatus.NotSupported, row.RecordStatus);
            Assert.Equal(BroadcastFileInterchangeStatus.NotSupported, row.IngestStatus);
            Assert.False(row.MeetsEbuR154);
        }
        Assert.Equal(new[] { "25/1", "50/1", "60000/1001" },
            r154.Where(row => row.Id.StartsWith("ebu-r154-xavc", StringComparison.Ordinal))
                .Select(row => row.FrameRate.ToString()).ToArray());
        Assert.All(r154.Where(row => row.Id.StartsWith("ebu-r154-xavc", StringComparison.Ordinal)),
            row =>
            {
                Assert.Equal(8, row.AudioTrackCount);
                Assert.Equal(1, row.AudioChannelsPerTrack);
                Assert.Contains("EBU R 123:8a", row.AudioChannelOrder, StringComparison.Ordinal);
            });
    }
}
