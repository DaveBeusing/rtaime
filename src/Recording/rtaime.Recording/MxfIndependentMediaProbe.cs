// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace rtaime.Recording;

/// <summary>
/// External, independently implemented MXF demux/codec inspection. FFprobe is
/// supplied explicitly and is never discovered or downloaded at runtime.
/// </summary>
internal static class MxfIndependentMediaProbe
{
    internal static async Task ValidateAsync(
        string ffprobePath, string mxfPath, string expectedFrameRate,
        ulong expectedVideoFrames, ulong expectedAudioSampleFrames,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(ffprobePath))
            throw new RecordingOutputUnavailableException("The configured independent FFprobe executable is unavailable.");
        if (expectedVideoFrames == 0 || expectedAudioSampleFrames == 0 ||
            expectedFrameRate is not ("50/1" or "60000/1001"))
            throw new ArgumentException("MXF validation requires a supported rate and nonempty media.");

        var info = new ProcessStartInfo(Path.GetFullPath(ffprobePath))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", Path.GetFullPath(mxfPath) })
            info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new IOException("Independent MXF probe could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var json = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Independent MXF media probe failed: {error[..Math.Min(error.Length, 1024)]}");

        using var root = JsonDocument.Parse(json);
        var format = root.RootElement.GetProperty("format");
        var formatName = Get(format, "format_name");
        if (!formatName.Split(',').Contains("mxf", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Independent probe found unexpected container '{formatName}'.");
        var streams = root.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Where(s => Get(s, "codec_type") == "video").ToArray();
        var audio = streams.Where(s => Get(s, "codec_type") == "audio").ToArray();
        if (streams.Length != 2 || video.Length != 1 || audio.Length != 1)
            throw new InvalidDataException("MXF must contain exactly one video and one stereo audio track.");
        var v = video[0];
        if (Get(v, "codec_name") != "rawvideo" ||
            v.GetProperty("width").GetInt32() != 1920 ||
            v.GetProperty("height").GetInt32() != 1080 ||
            Get(v, "pix_fmt") is not ("uyvy422" or "yuv422p"))
            throw new InvalidDataException("MXF must contain uncompressed 1920x1080 8-bit YUV 422 video.");
        // MXF demuxers may report avg_frame_rate=0/0 for uncompressed
        // frame-wrapped essence while preserving the true stream timebase.
        var averageRate = Get(v, "avg_frame_rate");
        var nominalRate = Get(v, "r_frame_rate");
        var videoTimebase = Get(v, "time_base");
        var timebaseMatches = TryRatio(videoTimebase, out var timeNum, out var timeDen) &&
            TryRatio(expectedFrameRate, out var expectedNum, out var expectedDen) &&
            (decimal)timeDen * expectedDen == (decimal)expectedNum * timeNum;
        if (!EquivalentRatio(averageRate, expectedFrameRate) &&
            !EquivalentRatio(nominalRate, expectedFrameRate) &&
            !timebaseMatches)
            throw new InvalidDataException(
                $"MXF edit rate does not match {expectedFrameRate}: avg={averageRate}, nominal={nominalRate}, timebase={videoTimebase}.");
        if (!ulong.TryParse(Get(v, "nb_read_frames"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var frames) || frames != expectedVideoFrames)
            throw new InvalidDataException("Independent MXF decoded frame count does not match the recording.");

        var a = audio[0];
        if (Get(a, "codec_name") != "pcm_s16le" || Get(a, "sample_rate") != "48000" ||
            a.GetProperty("channels").GetInt32() != 2)
            throw new InvalidDataException("MXF audio must be stereo 48 kHz signed PCM16.");

        // Audio packetization differs across demuxers. The decoded duration
        // expressed in the stream timebase must exactly match sample frames.
        var audioDurationTicks = Get(a, "duration_ts");
        var audioTimebase = Get(a, "time_base");
        if (!long.TryParse(audioDurationTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
            !TryRatio(audioTimebase, out var timebaseNum, out var timebaseDen) ||
            ticks < 0 || (decimal)ticks * timebaseNum * 48000 / timebaseDen != expectedAudioSampleFrames)
            throw new InvalidDataException("Independent MXF audio sample duration does not match recorded samples.");
    }

    private static string Get(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ?
            property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : property.ToString() : "";

    private static bool EquivalentRatio(string candidate, string expected) =>
        TryRatio(candidate, out var cn, out var cd) &&
        TryRatio(expected, out var en, out var ed) &&
        (decimal)cn * ed == (decimal)en * cd;

    private static bool TryRatio(string input, out long numerator, out long denominator)
    {
        numerator = denominator = 0;
        var parts = input.Split('/');
        return parts.Length == 2 &&
            long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator) &&
            long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) &&
            denominator > 0;
    }
}
