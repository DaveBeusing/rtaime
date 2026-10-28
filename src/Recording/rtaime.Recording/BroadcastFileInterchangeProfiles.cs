// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

// A descriptive, provider-evidenced exchange inventory. It does not authorize
// a decoder, recorder, or change any existing RecordingProfileId.
public enum BroadcastFileInterchangeStatus
{
    NotSupported = 1,
    Unavailable = 2,
    Implemented = 3,
    SoftwareQualified = 4,
    ConditionalProbe = 5
}

public sealed record BroadcastFileInterchangeProfile(
    string Id,
    string Reference,
    string Container,
    string VideoEssence,
    uint Width,
    uint Height,
    FrameRate FrameRate,
    string PixelEncoding,
    int VideoBitDepth,
    string Transfer,
    string AudioEssence,
    int AudioBitDepth,
    uint AudioSampleRate,
    int AudioTrackCount,
    int AudioChannelsPerTrack,
    string AudioChannelOrder,
    string Timecode,
    string Language,
    string? RecordingProfileId,
    BroadcastFileInterchangeStatus RecordStatus,
    BroadcastFileInterchangeStatus IngestStatus,
    bool IndependentReopenRequired,
    string Reason)
{
    // A catalogue descriptor or known tuple is not a successful file roundtrip.
    // Only source-bound output validation can establish a real reopened file.
    public bool InterchangeFileQualified => false;
    public bool MeetsEbuR154 => false;
}

public static class BroadcastFileInterchangeProfiles
{
    private const string CurrentProfileScope = "rtaime existing recording provider; not EBU R 154 v2.0";
    private const string R154Scope = "EBU R 154 v2.0 (June 2023) guidance; NOT an admitted rtaime output";
    private const string Unknown = "UNVERIFIED";

    public static IReadOnlyList<BroadcastFileInterchangeProfile> Snapshot(RecordingProfileCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var rows = new List<BroadcastFileInterchangeProfile>(18);
        foreach (var (rate, format, rateId) in new[]
        {
            (FrameRate.Fps50, VideoFormat.Hd1080p50Rgba8, "50"),
            (FrameRate.Fps59_94, VideoFormat.Hd1080p59_94Rgba8, "60000-1001")
        })
        {
            rows.Add(Existing(
                "mp4-h264-aac-" + rateId, "MP4", "H.264/AVC Main",
                "8-bit 4:2:0", 8, Unknown, "AAC-LC", 0,
                "Stereo 48 kHz; physical channel order not verified",
                ProfessionalRecordingFormats.Mp4H264AacProfileId,
                ProfessionalRecordingFormats.WindowsMediaFoundationProviderId,
                rate, format, catalog, BroadcastFileInterchangeStatus.ConditionalProbe));
            rows.Add(Existing(
                "mov-2vuy-pcm-" + rateId, "QuickTime MOV", "Uncompressed 2vuy",
                "8-bit 4:2:2", 8, "BT.709 SDR (nclc 1/1/1)", "PCM S16LE", 16,
                "Stereo 48 kHz; physical channel order not verified",
                ProfessionalRecordingFormats.Mov2VuyPcmProfileId,
                ProfessionalRecordingFormats.ManagedQuickTimeProviderId,
                rate, format, catalog, BroadcastFileInterchangeStatus.NotSupported));
            rows.Add(Existing(
                "mxf-op1a-uyvy-pcm-" + rateId, "MXF OP1a", "Uncompressed UYVY",
                "8-bit 4:2:2", 8, Unknown, "PCM S16LE", 16,
                "One MXF Sound Track containing two stereo channels; channel ordering unverified",
                ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId,
                ProfessionalRecordingFormats.ManagedMxfOp1aProviderId,
                rate, format, catalog, BroadcastFileInterchangeStatus.NotSupported));
        }

        foreach (var (rate, id) in new[]
        {
            (new FrameRate(25, 1), "25"),
            (FrameRate.Fps50, "50"),
            (FrameRate.Fps59_94, "60000-1001")
        })
        {
            rows.Add(Unsupported(
                "ebu-r154-xavc-mxf-class300-" + id,
                "MXF OP1a (SMPTE RDD 32)",
                "XAVC 4K Intra Class 300",
                rate, "8+ independent mono PCM24 tracks, EBU R 123:8a Option A",
                8, 1, "Not mapped; requires evidence for every channel",
                "No admitted XAVC Class 300 UHD/HLG encoder or MXF input decoder."));
            rows.Add(Unsupported(
                "ebu-r154-imf-app6-" + id,
                "IMF (SMPTE ST 2067)",
                "AVC application #6",
                rate, "IMF audio virtual tracks, explicit multichannel labels",
                0, 0, "No IMF CPL/AssetMap/PKL or virtual-track parser",
                "No IMF package/CPL parser, AVC application #6 encoder or ingest provider."));
            rows.Add(Unsupported(
                "ebu-r154-imf-dpp-prores-" + id,
                "IMF (SMPTE ST 2067)",
                "ProRes application DPP",
                rate, "IMF audio virtual tracks, explicit multichannel labels",
                0, 0, "No IMF CPL/AssetMap/PKL or virtual-track parser",
                "No licensed/admitted ProRes application DPP encoder or IMF ingest provider."));
        }
        return Array.AsReadOnly(rows.ToArray());
    }

    private static BroadcastFileInterchangeProfile Existing(
        string id, string container, string essence, string pixel, int depth,
        string transfer, string audio, int audioDepth, string order,
        RecordingProfileId recorder, RecordingWriterProviderId provider,
        FrameRate rate, VideoFormat format, RecordingProfileCatalog catalog,
        BroadcastFileInterchangeStatus input)
    {
        var status = BroadcastFileInterchangeStatus.NotSupported;
        string reason;
        if (!catalog.TryGet(recorder, out var actual))
        {
            reason = $"Required recording profile '{recorder}' is not registered.";
        }
        else if (actual.ProviderId != provider ||
                 !actual.SupportedInputVideoFormats.Contains(format) ||
                 actual.RequiredInputAudioFormat != AudioFormat.Stereo48kFloat32 ||
                 !TupleMatches(actual, recorder))
        {
            reason = "Recording provider/essence tuple differs from the exact supported catalog definition.";
        }
        else if (!actual.Available)
        {
            status = BroadcastFileInterchangeStatus.Unavailable;
            reason = actual.UnavailableReason ?? "Writer provider unavailable.";
        }
        else
        {
            status = actual.EvidenceState switch
            {
                RecordingProfileEvidenceState.Qualified => BroadcastFileInterchangeStatus.SoftwareQualified,
                RecordingProfileEvidenceState.Implemented => BroadcastFileInterchangeStatus.Implemented,
                _ => BroadcastFileInterchangeStatus.Unavailable
            };
            reason = status == BroadcastFileInterchangeStatus.Unavailable
                ? "Recorder advertised availability without qualifying evidence."
                : "Conditional recorder capability only; every finalized file requires its independent probe. " +
                  "No source timecode, language, rights or channel-allocation roundtrip is claimed.";
        }

        return new BroadcastFileInterchangeProfile(
            id, CurrentProfileScope, container, essence,
            1920, 1080, rate, pixel, depth, transfer, audio, audioDepth, 48000,
            1, 2, order, "UNMAPPED — no source timecode preservation evidence",
            "UNMAPPED — no per-track language metadata", recorder.Value,
            status, input, true, reason);
    }

    private static bool TupleMatches(RecordingProfileDescriptor profile, RecordingProfileId recorder)
    {
        if (recorder == ProfessionalRecordingFormats.Mp4H264AacProfileId)
            return profile.FileExtension == ".mp4" &&
                   profile.Container == "ISO Base Media File Format (MP4)" &&
                   profile.VideoCodec == "H.264/AVC" &&
                   profile.VideoProfile == "Main" &&
                   profile.AudioCodec == "AAC-LC";
        if (recorder == ProfessionalRecordingFormats.Mov2VuyPcmProfileId)
            return profile.FileExtension == ".mov" &&
                   profile.Container == "QuickTime Movie (MOV)" &&
                   profile.VideoCodec == "Uncompressed YUV 4:2:2" &&
                   profile.VideoProfile == "2vuy 8-bit" &&
                   profile.AudioCodec == "PCM S16LE";
        if (recorder == ProfessionalRecordingFormats.MxfOp1aUncompressedPcmProfileId)
            return profile.FileExtension == ".mxf" &&
                   profile.Container == "Material eXchange Format OP1a" &&
                   profile.VideoCodec == "Uncompressed YUV 4:2:2" &&
                   profile.VideoProfile == "8-bit UYVY 4:2:2 progressive" &&
                   profile.AudioCodec == "PCM S16LE 48 kHz Stereo";
        return false;
    }

    private static BroadcastFileInterchangeProfile Unsupported(
        string id, string container, string essence, FrameRate rate, string order,
        int tracks, int channelsPerTrack, string channelDetails, string reason) =>
        new(id, R154Scope, container, essence, 3840, 2160, rate, "10-bit 4:2:2",
            10, "ITU-R BT.2100 HLG / BT.2020", "48 kHz PCM, 24-bit", 24, 48000,
            tracks, channelsPerTrack,
            order + " — " + channelDetails, "UNMAPPED", "UNMAPPED", null,
            BroadcastFileInterchangeStatus.NotSupported,
            BroadcastFileInterchangeStatus.NotSupported, false, reason);
}
