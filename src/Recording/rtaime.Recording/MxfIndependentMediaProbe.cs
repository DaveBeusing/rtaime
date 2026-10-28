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
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        string json;
        string error;
        try
        {
            var outputRead = ReadBoundedOutputAsync(process.StandardOutput, 256 * 1024, budget.Token);
            var errorRead = ReadBoundedOutputAsync(process.StandardError, 16 * 1024, budget.Token);
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            json = await outputRead.ConfigureAwait(false);
            error = await errorRead.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryStopProcess(process);
            throw new TimeoutException("Independent MXF probe exceeded its two-minute wall-clock budget.");
        }
        catch
        {
            TryStopProcess(process);
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Independent MXF media probe failed: {error[..Math.Min(error.Length, 1024)]}");

        using var root = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
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
        // Contradictory depth and field-order metadata cannot be treated as
        // independently verified progressive 8-bit essence.
        var fieldOrder = Get(v, "field_order");
        if (fieldOrder is not ("" or "unknown" or "progressive"))
            throw new InvalidDataException("MXF video field order is not admitted progressive.");
        var bitDepth = Get(v, "bits_per_raw_sample");
        if (bitDepth is not ("" or "N/A" or "0" or "8"))
            throw new InvalidDataException("MXF source bit depth contradicts 8-bit UYVY.");
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

        var layout = Get(a, "channel_layout");
        if (layout is not ("" or "unknown" or "stereo" or "2 channels"))
            throw new InvalidDataException("MXF declared audio channel layout is not stereo.");
        // Audio packetization differs across demuxers. The decoded duration
        // expressed in the stream timebase must exactly match sample frames.
        var audioDurationTicks = Get(a, "duration_ts");
        var audioTimebase = Get(a, "time_base");
        if (!long.TryParse(audioDurationTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
            !TryRatio(audioTimebase, out var timebaseNum, out var timebaseDen) ||
            ticks < 0 || (decimal)ticks * timebaseNum * 48000 / timebaseDen != expectedAudioSampleFrames)
            throw new InvalidDataException("Independent MXF audio sample duration does not match recorded samples.");
    }

    private static async Task<string> ReadBoundedOutputAsync(
        StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return text.ToString();
            if (read > maximumCharacters - text.Length)
                throw new InvalidDataException("Independent MXF probe output exceeded its bounded evidence limit.");
            text.Append(buffer, 0, read);
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
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
