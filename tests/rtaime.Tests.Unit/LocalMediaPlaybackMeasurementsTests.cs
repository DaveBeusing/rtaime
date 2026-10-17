// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media;

namespace rtaime.Tests.Unit;

public sealed class LocalMediaPlaybackMeasurementsTests
{
    [Fact]
    public void Read_statistics_keep_full_run_totals_and_maximum_without_retaining_samples()
    {
        var stats = new LocalMediaReadMeasurements();
        stats = stats.Observe(1).Observe(20).Observe(2);
        Assert.Equal(3UL, stats.Calls);
        Assert.Equal(23, stats.TotalMilliseconds);
        Assert.Equal(20, stats.MaximumMilliseconds);
        Assert.Equal(0UL, default(LocalMediaPlaybackMeasurements).DecodedFrames);
    }

    [Fact]
    public void Long_read_observation_has_zero_steady_state_managed_allocation_and_saturates_counts()
    {
        var stats = new LocalMediaReadMeasurements(ulong.MaxValue, double.MaxValue, 1);
        for (var index = 0; index < 100; index++) stats = stats.Observe(2);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100_000; index++) stats = stats.Observe(2);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(ulong.MaxValue, stats.Calls);
        Assert.Equal(double.MaxValue, stats.TotalMilliseconds);
        Assert.Equal(2, stats.MaximumMilliseconds);
    }

    [Fact]
    public void Whole_run_quantiles_retain_exact_counts_and_upper_bounds_including_overflow()
    {
        var stats = new LocalMediaReadMeasurements();
        for (var index = 0; index < 100; index++)
            stats = stats.Observe(index < 50 ? .1 : index < 95 ? 1 : index < 99 ? 10 : 9000);
        Assert.Equal(100UL, stats.Latency.Count);
        Assert.Equal(17, stats.Latency.Capacity);
        Assert.Equal(.125, stats.Latency.P50);
        Assert.Equal(1, stats.Latency.P95);
        Assert.Equal(16, stats.Latency.P99);
        Assert.Equal(9000, stats.MaximumMilliseconds);
        Assert.Null(default(LocalMediaReadMeasurements).Latency.P50);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Nonfinite_or_negative_latency_is_rejected(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalMediaReadMeasurements().Observe(value));

    [Fact]
    public void Single_sample_quantile_uses_actual_maximum_and_small_rank_is_not_rounded_up_twice()
    {
        var stats = new LocalMediaReadMeasurements().Observe(0).Observe(.1).Observe(.2);
        Assert.Equal(.125, stats.Latency.P50);
        Assert.Equal(.2, stats.Latency.P95);
        Assert.Equal(0, new LocalMediaReadMeasurements().Observe(0).Latency.P99);
    }
}
