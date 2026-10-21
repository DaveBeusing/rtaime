// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Runtime.CompilerServices;
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
        const int warmup = 100_000;
        const int iterations = 100_000;
        LocalMediaReadMeasurements result = default;
        long observerBytes = -1;
        long workloadBytes = -1;
        int beforeThread = -1;
        int afterThread = -1;
        Exception? workerError = null;

        // The observer and workload must stay on one dedicated thread. Both
        // the thread startup and tiered-JIT warmup precede the measured window.
        var worker = new Thread(() =>
        {
            try
            {
                var stats = new LocalMediaReadMeasurements(ulong.MaxValue, double.MaxValue, 1);
                stats = ObserveConstant(stats, warmup);
                beforeThread = Environment.CurrentManagedThreadId;

                var observerStart = GC.GetAllocatedBytesForCurrentThread();
                observerBytes = GC.GetAllocatedBytesForCurrentThread() - observerStart;

                var start = GC.GetAllocatedBytesForCurrentThread();
                stats = ObserveConstant(stats, iterations);
                workloadBytes = GC.GetAllocatedBytesForCurrentThread() - start;

                afterThread = Environment.CurrentManagedThreadId;
                result = stats;
            }
            catch (Exception error)
            {
                workerError = error;
            }
        }) { IsBackground = true, Name = "LocalMediaReadAllocationUnit" };

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "Local media read allocation worker did not finish.");
        if (workerError is not null)
            throw new InvalidOperationException("Local media read allocation worker failed.", workerError);

        Assert.Equal(beforeThread, afterThread);
        Assert.True(beforeThread > 0);
        Assert.Equal(0L, observerBytes);
        Assert.True(workloadBytes == 0,
            $"100,000 warmed Observe calls allocated {workloadBytes} managed bytes on thread {beforeThread} " +
            $"(observer control {observerBytes} bytes, warmup {warmup}, process {Environment.ProcessId}).");
        Assert.Equal(ulong.MaxValue, result.Calls);
        Assert.Equal(double.MaxValue, result.TotalMilliseconds);
        Assert.Equal(2, result.MaximumMilliseconds);
        Assert.Equal((ulong)(warmup + iterations), result.Latency.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalMediaReadMeasurements ObserveConstant(LocalMediaReadMeasurements stats, int iterations)
    {
        for (var index = 0; index < iterations; index++) stats = stats.Observe(2);
        return stats;
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
    [InlineData(double.NegativeInfinity)]
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

    [Fact]
    public void Extreme_finite_samples_saturate_totals_and_retain_overflow_quantiles()
    {
        var stats = new LocalMediaReadMeasurements().Observe(.1)
            .Observe(double.MaxValue).Observe(double.MaxValue);
        Assert.Equal(3UL, stats.Calls);
        Assert.Equal(double.MaxValue, stats.TotalMilliseconds);
        Assert.Equal(double.MaxValue, stats.MaximumMilliseconds);
        Assert.Equal(double.MaxValue, stats.Latency.P95);
        Assert.Equal(double.MaxValue, stats.Latency.P99);
    }

    [Fact]
    public void Inline_histogram_retains_value_equality_and_hash_semantics()
    {
        var first = new LocalMediaReadMeasurements().Observe(1).Observe(2).Observe(3);
        var same = new LocalMediaReadMeasurements().Observe(3).Observe(1).Observe(2);
        var different = new LocalMediaReadMeasurements().Observe(.5).Observe(2.5).Observe(3);
        Assert.Equal(first, same);
        Assert.True(first == same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, different);
        Assert.Equal(default(LocalMediaReadMeasurements), new LocalMediaReadMeasurements());
    }

    [Fact]
    public void Immutable_histogram_snapshots_support_parallel_readers()
    {
        var snapshot = new LocalMediaReadMeasurements();
        for (var index = 0; index < 1024; index++)
            snapshot = snapshot.Observe(index % 4 == 0 ? 9000 : index % 4 == 1 ? .1 : 2);

        var expected = snapshot.Latency;
        var observed = new LocalMediaLatencySummary[32];
        Parallel.For(0, observed.Length, index => observed[index] = snapshot.Latency);
        Assert.All(observed, item => Assert.Equal(expected, item));
        Assert.Equal(1024UL, snapshot.Calls);
    }
}
