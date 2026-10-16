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
}
