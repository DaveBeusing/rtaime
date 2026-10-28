// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using rtaime.Core;

namespace rtaime.Media.Contracts;

// A deliberately bounded EBUCore 1.10 interchange projection, not an assertion
// that this application can read or emit arbitrary EBUCore XML documents.
public enum MediaInterchangeSource
{
    ProviderProbe = 1,
    Embedded = 2,
    InterchangeImport = 3,
    User = 4
}

public sealed record MediaInterchangeField(
    string Path,
    string Value,
    MediaInterchangeSource Source,
    string Evidence,
    DateTimeOffset ObservedAtUtc);

public sealed record MediaInterchangeMetadata(
    string Format,
    string FingerprintSha256,
    MediaInterchangeField[] Fields,
    Dictionary<string, string>? Unmapped = null)
{
    public const string CurrentFormat = "rtaime.media-interchange.ebucore-1.10.v1";
    public const int MaximumJsonBytes = 2048;
    public const int MaximumFields = 24;

    private static readonly HashSet<string> ScalarPaths = new(StringComparer.Ordinal)
    {
        "title", "description", "language", "rights", "timecode.start",
        "timecode.dropFrame", "content.sha256"
    };
    private static readonly HashSet<string> VideoProperties = new(StringComparer.Ordinal)
    {
        "codec", "width", "height", "frameRate", "timeScale", "transfer", "primaries", "hdr"
    };
    private static readonly HashSet<string> AudioProperties = new(StringComparer.Ordinal)
    {
        "codec", "language", "channelRole", "channelCount", "sampleRate"
    };
    private static readonly HashSet<string> CaptionProperties = new(StringComparer.Ordinal)
    {
        "language", "format", "kind"
    };

    public void Validate(string? expectedFingerprint = null)
    {
        if (!string.Equals(Format, CurrentFormat, StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported media interchange projection version.");
        if (string.IsNullOrEmpty(FingerprintSha256) || FingerprintSha256.Length != 64 ||
            FingerprintSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Media interchange requires a SHA-256 content fingerprint.");
        if (expectedFingerprint is not null &&
            !string.Equals(expectedFingerprint, FingerprintSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Interchange metadata is bound to different media content.");
        if (Fields is null || Fields.Length > MaximumFields || Fields.Any(field => field is null))
            throw new InvalidDataException("Interchange metadata field count is invalid.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (!seen.Add(field.Path) || !IsAllowedPath(field.Path))
                throw new InvalidDataException("Duplicate or unsupported interchange metadata field.");
            if (!Enum.IsDefined(field.Source) || field.Value is null ||
                field.Value.Length > 512 || field.Evidence is null ||
                field.Evidence.Length is < 1 or > 128 ||
                field.ObservedAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Invalid interchange field value or source provenance.");
            if (field.Path == "content.sha256" &&
                !string.Equals(field.Value, FingerprintSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Interchange content hash differs from the catalogued source.");
            if (field.Path.EndsWith(".frameRate", StringComparison.Ordinal) &&
                !FrameRate.TryParse(field.Value, out _))
                throw new InvalidDataException("Video frame rate must be an exact positive rational.");
            if (field.Path.EndsWith(".timeScale", StringComparison.Ordinal) &&
                (!long.TryParse(field.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) || scale <= 0))
                throw new InvalidDataException("Video time scale must be a positive integer.");
            if ((field.Path.EndsWith(".channelCount", StringComparison.Ordinal) ||
                 field.Path.EndsWith(".sampleRate", StringComparison.Ordinal) ||
                 field.Path.EndsWith(".width", StringComparison.Ordinal) ||
                 field.Path.EndsWith(".height", StringComparison.Ordinal)) &&
                (!uint.TryParse(field.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count == 0))
                throw new InvalidDataException("Track dimension/count must be a positive integer.");
            if ((field.Path == "language" || field.Path.EndsWith(".language", StringComparison.Ordinal)) &&
                !Regex.IsMatch(field.Value, @"\A[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Language must use a valid BCP-47-shaped tag.");
        }

        if (Unmapped is not null)
        {
            if (Unmapped.Count > 8)
                throw new InvalidDataException("Too many unmapped interchange fields.");
            foreach (var (key, value) in Unmapped)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 64 ||
                    value is null || value.Length > 256)
                    throw new InvalidDataException("Unmapped metadata key/value exceeds its bound.");
            }
        }

        ValidateTimecode();
    }

    private static bool IsAllowedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 64)
            return false;
        if (ScalarPaths.Contains(path))
            return true;
        var parts = path.Split('.');
        if (parts.Length != 3 || parts[1].Length != 1 ||
            parts[1][0] is < '0' or > '7')
            return false;
        return parts[0] switch
        {
            "video" => VideoProperties.Contains(parts[2]),
            "audio" => AudioProperties.Contains(parts[2]),
            "caption" => CaptionProperties.Contains(parts[2]),
            _ => false
        };
    }

    private void ValidateTimecode()
    {
        var start = Fields.FirstOrDefault(field => field.Path == "timecode.start")?.Value;
        var drop = Fields.FirstOrDefault(field => field.Path == "timecode.dropFrame")?.Value;
        if (start is null && drop is null) return;
        if (start is null || (drop is not "true" and not "false"))
            throw new InvalidDataException("Start timecode requires an explicit drop-frame flag.");

        var match = Regex.Match(start,
            @"\A(?<hh>[01][0-9]|2[0-3]):(?<mm>[0-5][0-9]):(?<ss>[0-5][0-9])(?<sep>[:;])(?<ff>[0-9]{2})\z",
            RegexOptions.CultureInvariant);
        if (!match.Success || (match.Groups["sep"].Value == ";") != (drop == "true"))
            throw new InvalidDataException("Start timecode and DF/NDF separator disagree.");

        var rateText = Fields.FirstOrDefault(field => field.Path == "video.0.frameRate")?.Value;
        if (rateText is null || !FrameRate.TryParse(rateText, out var rate))
            throw new InvalidDataException("Start timecode requires an exact video frame rate.");
        var nominal = (int)Math.Round(rate.FramesPerSecond, MidpointRounding.AwayFromZero);
        var frame = int.Parse(match.Groups["ff"].Value, CultureInfo.InvariantCulture);
        if (nominal is < 1 or > 99 || frame >= nominal)
            throw new InvalidDataException("Timecode frame exceeds the nominal frame rate.");
        if (drop == "true")
        {
            var dropCount = rate.Numerator == 30000 && rate.Denominator == 1001 ? 2
                : rate.Numerator == 60000 && rate.Denominator == 1001 ? 4 : 0;
            if (dropCount == 0)
                throw new InvalidDataException("Drop-frame timecode is not supported at this frame rate.");
            var minute = int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture);
            if (minute % 10 != 0 && match.Groups["ss"].Value == "00" && frame < dropCount)
                throw new InvalidDataException("Drop-frame timecode specifies a skipped label.");
        }
    }

    public MediaInterchangeMetadata Merge(MediaInterchangeMetadata incoming)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(incoming);
        incoming.Validate(FingerprintSha256);
        var merged = Fields.ToDictionary(field => field.Path, StringComparer.Ordinal);
        foreach (var next in incoming.Fields)
        {
            if (merged.TryGetValue(next.Path, out var previous))
            {
                if (previous.Value == next.Value) continue;
                if ((int)next.Source < (int)previous.Source)
                    throw new InvalidOperationException(
                        $"Interchange metadata conflict at '{next.Path}': a lower-authority source cannot overwrite the existing value.");
            }
            merged[next.Path] = next;
        }

        var unmapped = Unmapped is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(Unmapped, StringComparer.Ordinal);
        if (incoming.Unmapped is not null)
            foreach (var (key, value) in incoming.Unmapped)
            {
                if (unmapped.TryGetValue(key, out var previous) && previous != value)
                    throw new InvalidOperationException($"Unmapped interchange metadata conflict at '{key}'.");
                unmapped[key] = value;
            }

        var result = this with
        {
            Fields = merged.Values.OrderBy(field => field.Path, StringComparer.Ordinal).ToArray(),
            Unmapped = unmapped.Count == 0 ? null : unmapped
        };
        result.Validate();
        return result;
    }

    public static MediaInterchangeMetadata FromProbe(MediaAssetDescriptor asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var observed = DateTimeOffset.UtcNow;
        MediaInterchangeField Item(string path, string value) =>
            new(path, value, MediaInterchangeSource.ProviderProbe, "Runtime media probe", observed);
        var fields = new List<MediaInterchangeField>
        {
            Item("content.sha256", asset.FingerprintSha256)
        };
        if (asset.VideoCodec != MediaVideoCodec.None)
            fields.AddRange(
            [
                Item("video.0.codec", asset.VideoCodec.ToString()),
                Item("video.0.width", asset.VideoFormat.Width.ToString(CultureInfo.InvariantCulture)),
                Item("video.0.height", asset.VideoFormat.Height.ToString(CultureInfo.InvariantCulture)),
                Item("video.0.frameRate", asset.VideoFormat.FrameRate.ToString())
            ]);
        if (asset.AudioCodec != MediaAudioCodec.None)
            fields.AddRange(
            [
                Item("audio.0.codec", asset.AudioCodec.ToString()),
                Item("audio.0.sampleRate", asset.AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture)),
                Item("audio.0.channelCount", asset.AudioFormat.ChannelCount.ToString(CultureInfo.InvariantCulture))
            ]);
        var result = new MediaInterchangeMetadata(CurrentFormat, asset.FingerprintSha256, fields.ToArray());
        result.Validate();
        return result;
    }
}

// Explicit JSON adapter for the documented rtaime EBUCore 1.10 mapping profile.
// It is intentionally not a general EBUCore XML/XSD reader or writer.
public static class MediaInterchangeJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };

    public static string Export(MediaInterchangeMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        var json = JsonSerializer.Serialize(metadata, Options);
        if (Encoding.UTF8.GetByteCount(json) > MediaInterchangeMetadata.MaximumJsonBytes)
            throw new InvalidDataException("Interchange metadata exceeds its bounded JSON document size.");
        return json;
    }

    public static MediaInterchangeMetadata Import(string json, string? expectedFingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MediaInterchangeMetadata.MaximumJsonBytes)
            throw new InvalidDataException("Interchange JSON is empty or exceeds its bounded size.");
        var metadata = JsonSerializer.Deserialize<MediaInterchangeMetadata>(json, Options)
            ?? throw new InvalidDataException("Interchange JSON document is missing.");
        metadata.Validate(expectedFingerprint);
        return metadata;
    }
}
