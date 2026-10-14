// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Runtime;

/// <summary>Qualification-only whole-run histogram. No raw observations are retained.</summary>
public sealed class BoundedSoakHistogram
{
    private readonly ulong[] _bins;
    private readonly double _width;
    private double _minimum = double.PositiveInfinity;
    private double _maximum;
    private ulong _count;

    public BoundedSoakHistogram(double binWidth, int finiteBins)
    {
        if (!double.IsFinite(binWidth) || binWidth <= 0) throw new ArgumentOutOfRangeException(nameof(binWidth));
        if (finiteBins is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(finiteBins));
        if (!double.IsFinite(binWidth * finiteBins)) throw new ArgumentOutOfRangeException(nameof(binWidth));
        _width = binWidth;
        _bins = new ulong[finiteBins + 1];
    }

    public ulong Count => _count;
    public int Capacity => _bins.Length;
    public double BinWidth => _width;
    public double? Minimum => _count == 0 ? null : _minimum;
    public double? Maximum => _count == 0 ? null : _maximum;

    public void Observe(double value)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (_count == ulong.MaxValue) throw new InvalidOperationException("Histogram observation count exhausted.");
        var index = value == 0 ? 0 : (int)Math.Min(_bins.Length - 1, Math.Ceiling(value / _width) - 1);
        _bins[index]++;
        _count++;
        _minimum = Math.Min(_minimum, value);
        _maximum = Math.Max(_maximum, value);
    }

    /// <summary>Nearest-rank quantile upper bound; overflow uses the actual maximum.</summary>
    public double? PercentileUpperBound(double rank)
    {
        if (!double.IsFinite(rank) || rank <= 0 || rank > 1) throw new ArgumentOutOfRangeException(nameof(rank));
        if (_count == 0) return null;
        var target = Math.Max(1UL, checked((ulong)Math.Ceiling(_count * rank)));
        ulong cumulative = 0;
        for (var index = 0; index < _bins.Length; index++)
        {
            cumulative += _bins[index];
            if (cumulative >= target)
                return index == _bins.Length - 1 ? _maximum : Math.Min(_maximum, (index + 1) * _width);
        }
        throw new InvalidOperationException("Histogram counts do not reconcile.");
    }

    public object Summary() => new
    {
        count = _count, capacity = Capacity, binWidth = _width,
        percentileMethod = "WHOLE_RUN_NEAREST_RANK_UPPER_BIN_BOUND",
        p50 = PercentileUpperBound(.50), p95 = PercentileUpperBound(.95),
        p99 = PercentileUpperBound(.99), minimum = Minimum, maximum = Maximum,
        overflowSamples = _bins[^1]
    };
}

public readonly record struct SoakResourceWindow(
    long Index, ulong Samples, double MeanPrivateBytes, double MeanHandles,
    long MaximumPrivateBytes, int MaximumHandles);

/// <summary>Fixed hourly windows for qualification resource trend evidence, outside Program execution.</summary>
public sealed class BoundedSoakResourceTrend
{
    public const int Capacity = 96;
    private readonly long[] _indices = Enumerable.Repeat(-1L, Capacity).ToArray();
    private readonly ulong[] _counts = new ulong[Capacity];
    private readonly double[] _memorySums = new double[Capacity];
    private readonly double[] _handleSums = new double[Capacity];
    private readonly long[] _memoryMaxima = new long[Capacity];
    private readonly int[] _handleMaxima = new int[Capacity];
    private long _lastWindow = -1;
    private TimeSpan _lastElapsed;
    public ulong Samples { get; private set; }

    public void Observe(TimeSpan elapsed, long privateBytes, int handles)
    {
        if (elapsed < TimeSpan.Zero || elapsed < _lastElapsed) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (privateBytes < 0 || handles < 0) throw new ArgumentOutOfRangeException(nameof(privateBytes));
        var index = elapsed.Ticks / TimeSpan.TicksPerHour;
        var slot = (int)(index % Capacity);
        if (_indices[slot] != index)
        {
            _indices[slot] = index;
            _counts[slot] = 0;
            _memorySums[slot] = _handleSums[slot] = 0;
            _memoryMaxima[slot] = 0;
            _handleMaxima[slot] = 0;
        }
        _counts[slot]++;
        _memorySums[slot] += privateBytes;
        _handleSums[slot] += handles;
        _memoryMaxima[slot] = Math.Max(_memoryMaxima[slot], privateBytes);
        _handleMaxima[slot] = Math.Max(_handleMaxima[slot], handles);
        _lastWindow = index;
        _lastElapsed = elapsed;
        Samples++;
    }

    public SoakResourceWindow[] Snapshot() => Enumerable.Range(0, Capacity)
        .Where(slot => _counts[slot] > 0).OrderBy(slot => _indices[slot])
        .Select(slot => new SoakResourceWindow(_indices[slot], _counts[slot],
            _memorySums[slot] / _counts[slot], _handleSums[slot] / _counts[slot],
            _memoryMaxima[slot], _handleMaxima[slot])).ToArray();

    public string Assess(double maximumMemoryGrowthBytes, double maximumHandleGrowth)
    {
        if (!double.IsFinite(maximumMemoryGrowthBytes) || maximumMemoryGrowthBytes < 0 ||
            !double.IsFinite(maximumHandleGrowth) || maximumHandleGrowth < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMemoryGrowthBytes));
        // Exclude first-hour warm-up and the still-open final window.
        var windows = Snapshot().Where(window => window.Index > 0 && window.Index < _lastWindow).ToArray();
        if (windows.Length < 3 || windows.Any(window => window.Samples < 2)) return "UNVERIFIED";
        for (var index = 1; index < windows.Length; index++)
            if (windows[index].Index != windows[index - 1].Index + 1) return "UNVERIFIED";
        var first = windows[0];
        var memoryGrowth = windows.Max(window => window.MeanPrivateBytes) - first.MeanPrivateBytes;
        var handleGrowth = windows.Max(window => window.MeanHandles) - first.MeanHandles;
        return memoryGrowth <= maximumMemoryGrowthBytes && handleGrowth <= maximumHandleGrowth ? "PASS" : "FAIL";
    }
}
