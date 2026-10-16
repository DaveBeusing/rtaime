// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Media;

/// <summary>Constant-space cumulative observations; no per-frame log or sample history.</summary>
public readonly record struct LocalMediaReadMeasurements(
    ulong Calls, double TotalMilliseconds, double MaximumMilliseconds)
{
    public LocalMediaReadMeasurements Observe(double milliseconds) => new(
        Calls == ulong.MaxValue ? Calls : Calls + 1,
        Math.Min(double.MaxValue, TotalMilliseconds + milliseconds),
        Math.Max(MaximumMilliseconds, milliseconds));
}

public readonly record struct LocalMediaPlaybackMeasurements(
    double OpenMilliseconds, double? FirstFrameMilliseconds,
    LocalMediaReadMeasurements Decode, LocalMediaReadMeasurements Seek,
    LocalMediaReadMeasurements NativeVideoRead, LocalMediaReadMeasurements NativeAudioRead,
    ulong DecodedFrames, ulong DecodedAudioSamples, ulong MissingAudioFrames,
    ulong VideoTimestampRegressions, ulong AudioTimestampDiscontinuities,
    double MaximumAvTimestampDistanceMilliseconds, ulong ReadRetries, ulong ReadFailures);
