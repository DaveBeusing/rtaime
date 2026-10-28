// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Text.Json;
using rtaime.Media.Contracts;

namespace rtaime.Recording;

// The observations below are from one independently inspected, finalized MXF.
// They do NOT prove that a matching decoder can ingest the file in rtaime,
// and are not a substitute for a content SHA-256 identity.
public sealed record MxfValidatedInterchangeEvidence(
    string FrameRate,
    string VideoTimebase,
    string AudioTimebase,
    ulong VideoFrames,
    ulong AudioSampleFrames,
    string? ReportedTimecode,
    string? ReportedAudioLanguage,
    string? ReportedColorTransfer,
    string? ReportedColorPrimaries,
    string? ReportedAudioLayout)
{
    internal static MxfValidatedInterchangeEvidence FromValidatedStreams(
        JsonElement video, JsonElement audio, string frameRate,
        ulong frames, ulong sampleFrames)
    {
        static string? Property(JsonElement node, string property)
        {
            if (!node.TryGetProperty(property, out var value) ||
                value.ValueKind != JsonValueKind.String)
                return null;
            var s = value.GetString();
            return string.IsNullOrWhiteSpace(s) || s.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                ? null : s;
        }
        static string? Tag(JsonElement stream, string name) =>
            stream.TryGetProperty("tags", out var tags) &&
            tags.ValueKind == JsonValueKind.Object
                ? Property(tags, name) : null;

        var timecode = Tag(video, "timecode");
        var language = Tag(audio, "language");
        var transfer = Property(video, "color_transfer");
        var primaries = Property(video, "color_primaries");
        var layout = Property(audio, "channel_layout");
        foreach (var value in new[] { timecode, language, transfer, primaries, layout })
            if (value is { Length: > 128 } || value?.Any(char.IsControl) == true)
                throw new InvalidDataException("Independent MXF metadata exceeds a bounded field or contains controls.");

        return new MxfValidatedInterchangeEvidence(
            frameRate, video.GetProperty("time_base").GetString() ?? "",
            audio.GetProperty("time_base").GetString() ?? "",
            frames, sampleFrames, timecode, language, transfer, primaries, layout);
    }

    // This is an EBUCore-oriented rtaime JSON *projection*, not an EBUCore XML
    // assertion. The caller must first SHA-256 hash the exact, stable MXF file.
    public MediaInterchangeMetadata ToInterchangeMetadata(
        string fileSha256, DateTimeOffset observedAtUtc)
    {
        var fields = new List<MediaInterchangeField>();
        void Add(string path, string value, string source = "Independent MXF FFprobe")
            => fields.Add(new MediaInterchangeField(
                path, value, MediaInterchangeSource.ProviderProbe, source, observedAtUtc));
        Add("content.sha256", fileSha256);
        Add("video.0.codec", "rawvideo/uyvy422");
        Add("video.0.width", "1920");
        Add("video.0.height", "1080");
        Add("video.0.frameRate", FrameRate);
        Add("audio.0.codec", "pcm_s16le");
        Add("audio.0.sampleRate", "48000");
        Add("audio.0.channelCount", "2");

        if (ReportedColorTransfer is { } transfer)
            Add("video.0.transfer", transfer);
        if (ReportedColorPrimaries is { } primaries)
            Add("video.0.primaries", primaries);
        if (ReportedAudioLanguage is { } lang &&
            System.Text.RegularExpressions.Regex.IsMatch(lang,
                @"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            Add("audio.0.language", lang);
        if (ReportedTimecode is { } timecode)
        {
            Add("timecode.start", timecode, "MXF reported timecode; source preservation UNVERIFIED");
            Add("timecode.dropFrame", timecode.Contains(';') ? "true" : "false",
                "MXF reported timecode; source preservation UNVERIFIED");
        }

        var unmapped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mxf.video.timebase"] = VideoTimebase,
            ["mxf.audio.timebase"] = AudioTimebase,
            ["mxf.audio.samples"] = AudioSampleFrames.ToString(CultureInfo.InvariantCulture),
            ["mxf.video.frames"] = VideoFrames.ToString(CultureInfo.InvariantCulture),
            ["mxf.audio.trackTopology"] = "1 track, 2 channels; order UNVERIFIED",
            ["mxf.video.bitDepth"] = "8",
            ["mxf.audio.bitDepth"] = "16"
        };
        if (ReportedAudioLanguage is { } raw && !fields.Any(f => f.Path == "audio.0.language"))
            unmapped["mxf.audio.language.raw"] = raw;

        var result = new MediaInterchangeMetadata(
            MediaInterchangeMetadata.CurrentFormat, fileSha256,
            fields.ToArray(), unmapped);
        result.Validate();
        _ = MediaInterchangeJson.Export(result);
        return result;
    }
}
