// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Globalization;
using System.Net;
using rtaime.Core;
using rtaime.Provider.Contracts;

namespace rtaime.Provider.St2110;

/// <summary>
/// Deliberately narrow, canonical SDP profile for loopback ST 2110 reference
/// streams. Never signals a physical PTP lock, multicast capability, NMOS
/// registration, ST 2022-7 redundancy or physical traffic-shaping compliance.
/// </summary>
public static class St2110SoftwareSdp
{
    public const int MaximumSdpBytes = 4096;

    public static string Generate(St2110SoftwareProfile profile, St2110SoftwareEndpoint endpoint)
    {
        St2110TransportAdmission.RequireSoftwareReference(profile, endpoint, null);
        var rate = profile.FrameRate.Denominator == 1
            ? profile.FrameRate.Numerator.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture,
                $"{profile.FrameRate.Numerator}/{profile.FrameRate.Denominator}");
        var lines = new[]
        {
            "v=0",
            $"o=- 1 1 IN IP4 {endpoint.Address}",
            "s=rtaime ST 2110 software reference (unqualified loopback)",
            "t=0 0",
            "a=recvonly",
            $"m=video {endpoint.VideoPort} RTP/AVP 96",
            $"c=IN IP4 {endpoint.Address}",
            "a=rtpmap:96 raw/90000",
            $"a=fmtp:96 sampling=YCbCr-4:2:2;width={profile.Width};height={profile.Height};exactframerate={rate};depth=10;colorimetry=BT709;TCS=SDR;PM=2110GPM;TP=2110TPN",
            $"a=ssrc:{endpoint.VideoSsrc} cname:software-video",
            "a=mid:video",
            $"m=audio {endpoint.AudioPort} RTP/AVP 97",
            $"c=IN IP4 {endpoint.Address}",
            "a=rtpmap:97 L24/48000/2",
            "a=ptime:1",
            "a=fmtp:97 channel-order=SMPTE2110.(ST)",
            $"a=ssrc:{endpoint.AudioSsrc} cname:software-audio",
            "a=mid:audio"
        };
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>Parse only the canonical, bounded software profile.
    /// Unknown/duplicated attributes and non-loopback routes fail closed.</summary>
    public static bool TryParse(string? sdp, out St2110SoftwareProfile? profile,
        out St2110SoftwareEndpoint? endpoint)
    {
        profile = null;
        endpoint = null;
        if (string.IsNullOrEmpty(sdp) || sdp.Length > MaximumSdpBytes ||
            sdp.Any(c => c == '\0' || c == '\n' && !sdp.Contains("\r\n", StringComparison.Ordinal)))
            return false;
        try
        {
            var lines = sdp.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != 18 || lines[0] != "v=0" || lines[3] != "t=0 0" ||
                lines[4] != "a=recvonly" ||
                !lines[5].StartsWith("m=video ", StringComparison.Ordinal) ||
                !lines[11].StartsWith("m=audio ", StringComparison.Ordinal))
                return false;
            var origin = lines[1].Split(' ');
            if (origin.Length != 6 || origin[0] != "o=-" || origin[1] != "1" ||
                origin[2] != "1" || origin[3] != "IN" || origin[4] != "IP4" ||
                !IPAddress.TryParse(origin[5], out var address))
                return false;

            var videoMedia = lines[5].Split(' ');
            var audioMedia = lines[11].Split(' ');
            if (videoMedia.Length != 4 || videoMedia[0] != "m=video" ||
                videoMedia[2] != "RTP/AVP" || videoMedia[3] != "96" ||
                audioMedia.Length != 4 || audioMedia[0] != "m=audio" ||
                audioMedia[2] != "RTP/AVP" || audioMedia[3] != "97" ||
                !int.TryParse(videoMedia[1], NumberStyles.None, CultureInfo.InvariantCulture, out var videoPort) ||
                !int.TryParse(audioMedia[1], NumberStyles.None, CultureInfo.InvariantCulture, out var audioPort))
                return false;

            var videoSsrc = ParseSsrc(lines[9], " cname:software-video");
            var audioSsrc = ParseSsrc(lines[16], " cname:software-audio");
            if (!videoSsrc.HasValue || !audioSsrc.HasValue) return false;

            var fmtp = lines[8].Split(';');
            if (fmtp.Length != 9 ||
                fmtp[0] != "a=fmtp:96 sampling=YCbCr-4:2:2" ||
                fmtp[4] != "depth=10" || fmtp[5] != "colorimetry=BT709" ||
                fmtp[6] != "TCS=SDR" || fmtp[7] != "PM=2110GPM" ||
                fmtp[8] != "TP=2110TPN" ||
                !TryField(fmtp[1], "width=", out var width) ||
                !TryField(fmtp[2], "height=", out var height) ||
                !fmtp[3].StartsWith("exactframerate=", StringComparison.Ordinal))
                return false;
            var frameRate = fmtp[3]["exactframerate=".Length..] switch
            {
                "25" => new FrameRate(25, 1),
                "50" => new FrameRate(50, 1),
                "60000/1001" => new FrameRate(60000, 1001),
                _ => throw new FormatException("Unsupported frame rate.")
            };

            var parsedProfile = new St2110SoftwareProfile(width, height, frameRate);
            var parsedEndpoint = new St2110SoftwareEndpoint(address, videoPort,
                audioPort, videoSsrc.Value, audioSsrc.Value);
            // Canonical reconstruction is an exact whitelist, including c=,
            // payload mappings, ptime, SSRCs and absence of unsafe attributes.
            if (!string.Equals(Generate(parsedProfile, parsedEndpoint), sdp,
                StringComparison.Ordinal))
                return false;
            profile = parsedProfile;
            endpoint = parsedEndpoint;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or
            OverflowException or NotSupportedException)
        {
            return false;
        }
    }

    private static uint? ParseSsrc(string line, string suffix)
    {
        if (!line.StartsWith("a=ssrc:", StringComparison.Ordinal) ||
            !line.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var number = line["a=ssrc:".Length..^suffix.Length];
        return uint.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;
    }

    private static bool TryField(string field, string prefix, out int value)
    {
        value = default;
        return field.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(field[prefix.Length..], NumberStyles.None,
                CultureInfo.InvariantCulture, out value);
    }
}
