// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.CompilerServices;

namespace rtaime.Media;

/// <summary>Constant-space cumulative observations; no per-frame log or sample history.</summary>
[InlineArray(17)]
internal struct LocalMediaLatencyBins { private ulong _first; }

public readonly record struct LocalMediaLatencySummary(
    ulong Count, string PercentileMethod, int Capacity,
    double? P50, double? P95, double? P99);

public readonly record struct LocalMediaReadMeasurements(
    ulong Calls, double TotalMilliseconds, double MaximumMilliseconds)
{
    private readonly LocalMediaLatencyBins _bins;
    private readonly ulong _observed;

    private LocalMediaReadMeasurements(ulong calls, double total, double maximum,
        LocalMediaLatencyBins bins, ulong observed) : this(calls, total, maximum)
    { _bins = bins; _observed = observed; }

    // Fixed 0.125-ms exponential bins through 4096 ms plus overflow. Value storage
    // avoids per-read allocation and belongs to the decoder worker, never Program.
    public LocalMediaReadMeasurements Observe(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var bins = _bins;
        var index = 0;
        var upper = .125;
        while (index < 16 && milliseconds > upper) { index++; upper *= 2; }
        var observed = _observed;
        if (observed != ulong.MaxValue) { bins[index]++; observed++; }
        return new(Calls == ulong.MaxValue ? Calls : Calls + 1,
            Math.Min(double.MaxValue, TotalMilliseconds + milliseconds),
            Math.Max(MaximumMilliseconds, milliseconds), bins, observed);
    }

    private double? Quantile(double rank)
    {
        if (_observed == 0) return null;
        // Clamp the floating-point rank conversion at the UInt64 boundary.
        var target = Math.Max(1UL, (ulong)Math.Ceiling((decimal)_observed * (decimal)rank));
        ulong cumulative = 0;
        for (var index = 0; index < 17; index++)
        {
            cumulative += _bins[index];
            if (cumulative >= target)
                return index == 16 ? MaximumMilliseconds :
                    Math.Min(MaximumMilliseconds, .125 * Math.Pow(2, index));
        }
        throw new InvalidOperationException("Read latency histogram does not reconcile.");
    }

    public bool Equals(LocalMediaReadMeasurements other)
    {
        if (Calls != other.Calls || TotalMilliseconds != other.TotalMilliseconds ||
            MaximumMilliseconds != other.MaximumMilliseconds || _observed != other._observed) return false;
        for (var index = 0; index < 17; index++)
            if (_bins[index] != other._bins[index]) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Calls); hash.Add(TotalMilliseconds); hash.Add(MaximumMilliseconds); hash.Add(_observed);
        for (var index = 0; index < 17; index++) hash.Add(_bins[index]);
        return hash.ToHashCode();
    }

    public LocalMediaLatencySummary Latency => new(_observed,
        "WHOLE_RUN_NEAREST_RANK_EXPONENTIAL_UPPER_BOUND", 17,
        Quantile(.50), Quantile(.95), Quantile(.99));
}

public readonly record struct LocalMediaPlaybackMeasurements(
    double OpenMilliseconds, double? FirstFrameMilliseconds,
    LocalMediaReadMeasurements Decode, LocalMediaReadMeasurements Seek,
    LocalMediaReadMeasurements NativeVideoRead, LocalMediaReadMeasurements NativeAudioRead,
    ulong DecodedFrames, ulong DecodedAudioSamples, ulong MissingAudioFrames,
    ulong VideoTimestampRegressions, ulong AudioTimestampDiscontinuities,
    double MaximumAvTimestampDistanceMilliseconds, ulong ReadRetries, ulong ReadFailures)
{
    public long? FirstVideoTimestamp { get; init; }
    public long? LastVideoTimestamp { get; init; }
    public double VideoSecondsPerTick { get; init; }
    public ulong VideoForwardGapCount { get; init; }
    public double MaximumVideoIntervalMilliseconds { get; init; }
}
