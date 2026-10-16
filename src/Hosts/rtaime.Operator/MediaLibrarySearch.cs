// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Globalization;
using System.Text;
using rtaime.Media.Contracts;

namespace rtaime.Operator;

public sealed record MediaSearchMetadata(MediaVideoCodec VideoCodec, MediaAudioCodec AudioCodec, VideoFormat Video);

// A bounded predicate over confirmed descriptors; never scans files or sends control requests.
public sealed class MediaLibrarySearch
{
    public const int MaximumTerms = 16;
    private readonly List<Func<MediaPoolItemViewModel, bool>> _terms = [];
    public string? Error { get; private set; }
    public bool Matches(MediaPoolItemViewModel item) => Error is null && _terms.All(test => test(item));
    public static MediaLibrarySearch Parse(string text)
    {
        var result = new MediaLibrarySearch();
        if (text.Length > 256) { result.Error = "Search is limited to 256 characters."; return result; }
        var tokens = new List<string>(); var word = new StringBuilder(); var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (word.Length > 0) { tokens.Add(word.ToString()); word.Clear(); } }
            else word.Append(c);
        }
        if (quoted) { result.Error = "Close the quoted search phrase."; return result; }
        if (word.Length > 0) tokens.Add(word.ToString());
        if (tokens.Count > MaximumTerms) { result.Error = "Use at most 16 search terms."; return result; }
        foreach (var token in tokens)
        {
            var negative = token.StartsWith('-'); var body = negative ? token[1..] : token;
            var colon = body.IndexOf(':');
            if (colon == 1 && body.Length > 2 && (body[2] == (char)92 || body[2] == '/')) colon = -1;
            var field = colon < 0 ? "" : body[..colon].ToLowerInvariant();
            var value = colon < 0 ? body : body[(colon + 1)..];
            if (value.Length == 0) { result.Error = "Search terms require a value."; break; }
            Func<MediaPoolItemViewModel, bool>? predicate = field switch
            {
                "" => i => Contains(i.Name, value) || Contains(i.Detail, value) || Contains(i.Format, value) ||
                    Contains(i.State, value) || Contains(i.ReferenceId, value) || Contains(i.LocalPath, value),
                "name" => i => Contains(i.Name, value),
                "codec" => i => i.SearchMetadata is { } m && (Equal(m.VideoCodec.ToString(), value) || Equal(m.AudioCodec.ToString(), value)),
                "resolution" when Resolution(value, out var width, out var height) =>
                    i => i.SearchMetadata is { } m && m.Video.Width == width && m.Video.Height == height,
                "fps" when Rate(value, out var fps) => i => i.SearchMetadata is { } m &&
                    Math.Abs(m.Video.FrameRate.FramesPerSecond - fps) < 0.0001,
                "ready" when value.ToLowerInvariant() is "true" or "false" => i => i.IsReady == Equal(value, "true"),
                "availability" when value.ToLowerInvariant() is "online" or "offline" or "missing" =>
                    i => Equal(i.AvailabilityLabel, value),
                "type" when value.ToLowerInvariant() is "source" or "clip" or "audio" or "graphics" or "composition" =>
                    i => Equal(i.Kind.ToString(), value),
                _ => null
            };
            if (predicate is null)
            {
                result.Error = field is "tag" or "tags" or "recent" or "used"
                    ? "Tags and last-use metadata are unavailable in the confirmed catalogue."
                    : "Unknown field or invalid value: " + body;
                break;
            }
            var test = predicate;
            // Unknown typed metadata never matches either positive or negated typed filters.
            result._terms.Add(i => (field is not ("codec" or "resolution" or "fps") || i.SearchMetadata is not null) &&
                (negative ? !test(i) : test(i)));
        }
        return result;
    }
    private static bool Contains(string? text, string value) => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Resolution(string value, out uint width, out uint height)
    {
        var parts = value.ToLowerInvariant().Replace('×', 'x').Split('x');
        width = height = 0;
        return parts.Length == 2 && uint.TryParse(parts[0], out width) && uint.TryParse(parts[1], out height) && width > 0 && height > 0;
    }
    private static bool Rate(string value, out double fps)
    {
        if (global::rtaime.Core.FrameRate.TryParse(value, out var rate)) { fps = rate.FramesPerSecond; return true; }
        return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out fps) && double.IsFinite(fps) && fps > 0;
    }
}
