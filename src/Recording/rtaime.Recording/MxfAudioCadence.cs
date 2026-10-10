// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Recording;

/// <summary>
/// Rational Program frame-to-audio sample-frame mapping for 48 kHz PCM.
/// Each boundary is computed from the origin, preventing cumulative rounding drift.
/// </summary>
internal static class MxfAudioCadence
{
    internal const uint SampleRate = 48_000;

    internal static long SampleBoundary(long videoFrameIndex, uint editRateNumerator, uint editRateDenominator)
    {
        if (videoFrameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(videoFrameIndex));
        if (!((editRateNumerator == 50 && editRateDenominator == 1) ||
              (editRateNumerator == 60_000 && editRateDenominator == 1_001)))
            throw new ArgumentOutOfRangeException(nameof(editRateNumerator), "Only 1080p50 and 1080p60000/1001 are supported.");
        var numerator = checked((decimal)videoFrameIndex * SampleRate * editRateDenominator);
        return checked((long)decimal.Floor(numerator / editRateNumerator));
    }

    internal static int SamplesForFrame(long frameIndex, uint editRateNumerator, uint editRateDenominator)
    {
        var start = SampleBoundary(frameIndex, editRateNumerator, editRateDenominator);
        var end = SampleBoundary(checked(frameIndex + 1), editRateNumerator, editRateDenominator);
        return checked((int)(end - start));
    }
}
