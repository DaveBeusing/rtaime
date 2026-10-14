// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Runtime;

namespace rtaime.Tests.Unit;

public sealed class BoundedSoakEvidenceTests
{
    [Fact]
    public void Whole_run_quantiles_keep_early_outliers_after_millions_of_observations()
    {
        var histogram = new BoundedSoakHistogram(.01, 10000);
        for (var index = 0; index < 40000; index++) histogram.Observe(80);
        for (var index = 0; index < 960000; index++) histogram.Observe(1.005);
        Assert.Equal(1000000UL, histogram.Count);
        Assert.Equal(10001, histogram.Capacity);
        Assert.Equal(1.01, histogram.PercentileUpperBound(.95)!.Value, 8);
        Assert.Equal(80, histogram.PercentileUpperBound(.99));
        Assert.Equal(80, histogram.Maximum);
    }

    [Fact]
    public void Overflow_is_conservative_and_empty_evidence_cannot_claim_a_quantile()
    {
        var histogram = new BoundedSoakHistogram(1, 10);
        Assert.Null(histogram.PercentileUpperBound(.95));
        histogram.Observe(0);
        histogram.Observe(10);
        histogram.Observe(100000);
        Assert.Equal(100000, histogram.PercentileUpperBound(.99));
        Assert.Equal(0, histogram.Minimum);
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Observe(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.Observe(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.PercentileUpperBound(0));
    }

    [Fact]
    public void Qualification_histogram_writer_allocates_nothing_after_warmup()
    {
        var histogram = new BoundedSoakHistogram(.01, 10000);
        for (var index = 0; index < 16384; index++) histogram.Observe(index % 300 / 10.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100000; index++) histogram.Observe(index % 300 / 10.0);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resource_windows_detect_growth_without_retaining_each_sample(bool leak)
    {
        var trend = new BoundedSoakResourceTrend();
        for (var hour = 0; hour < 73; hour++)
        {
            for (var sample = 0; sample < 60; sample++)
                trend.Observe(TimeSpan.FromHours(hour) + TimeSpan.FromSeconds(sample),
                    1000000 + (leak ? hour * 100000L : 0), 40 + (leak ? hour : 0));
        }
        Assert.Equal(4380UL, trend.Samples);
        Assert.Equal(73, trend.Snapshot().Length);
        Assert.Equal(leak ? "FAIL" : "PASS", trend.Assess(200000, 4));
    }

    [Fact]
    public void Missing_or_short_resource_evidence_remains_unverified_and_retention_is_fixed()
    {
        var shortRun = new BoundedSoakResourceTrend();
        shortRun.Observe(TimeSpan.Zero, 10, 2);
        Assert.Equal("UNVERIFIED", shortRun.Assess(100, 10));
        var gap = new BoundedSoakResourceTrend();
        foreach (var hour in new[] { 1, 2, 4, 5 })
        {
            gap.Observe(TimeSpan.FromHours(hour), 10, 2);
            gap.Observe(TimeSpan.FromHours(hour) + TimeSpan.FromMinutes(1), 10, 2);
        }
        Assert.Equal("UNVERIFIED", gap.Assess(100, 10));
        var bounded = new BoundedSoakResourceTrend();
        for (var hour = 0; hour < 200; hour++) bounded.Observe(TimeSpan.FromHours(hour), 10, 2);
        Assert.Equal(BoundedSoakResourceTrend.Capacity, bounded.Snapshot().Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => bounded.Observe(TimeSpan.Zero, 10, 2));
    }
}
