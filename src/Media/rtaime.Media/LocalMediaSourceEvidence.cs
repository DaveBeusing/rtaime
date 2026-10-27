// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Media;

// Bounded metadata/negotiation description for source-bound preflight status.
// Container claims, native codec result and normalized output are deliberately
// distinct; this is not selected-MFT or end-to-end decoder certification.
internal static class LocalMediaSourceEvidence
{
    internal static string Describe(LocalMediaStreamMetadata source, LocalMediaProbe decoded)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(decoded);
        var rate = $"{source.FrameRateNumerator}/{source.FrameRateDenominator}";
        var frameTicks = source.MinimumFrameDeltaTicks == source.MaximumFrameDeltaTicks
            ? $"{source.MinimumFrameDeltaTicks}" : $"{source.MinimumFrameDeltaTicks}..{source.MaximumFrameDeltaTicks}";
        var audio = source.AudioSampleEntry.Length == 0
            ? "no declared audio track"
            : $"entry {source.AudioSampleEntry}, " +
              $"{source.AudioChannels?.ToString() ?? "?"}ch, " +
              $"{source.AudioSampleRateHz?.ToString() ?? "?"}Hz, " +
              $"{source.AudioSampleSizeBits?.ToString() ?? "?"}-bit declared";
        return $"SOURCE {source.Container}/{source.SampleEntry} {source.VideoCodec?.ToString() ?? "UNKNOWN"} " +
               $"profile {source.Profile?.ToString() ?? "?"} {source.BitDepth?.ToString() ?? "?"}-bit " +
               $"chroma {source.ChromaFormat?.ToString() ?? "?"}, " +
               $"{source.Width}x{source.Height} @ {rate} avg, " +
               $"timebase 1/{source.VideoTimeScale}, stts {source.VideoSampleCount} samples " +
               $"delta {frameTicks} ticks ({(source.VariableFrameRate ? "VFR" : "CFR")}); " +
               $"AUDIO {audio}, negotiated {decoded.AudioCodec}; " +
               $"OUTPUT {decoded.VideoFormat}, PCM {decoded.AudioFormat}; selected MFT UNVERIFIED";
    }
}
