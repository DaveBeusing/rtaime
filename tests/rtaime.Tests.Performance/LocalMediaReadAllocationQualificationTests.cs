// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using rtaime.Media;
using Xunit.Abstractions;

namespace rtaime.Tests.Performance;

public sealed class LocalMediaReadAllocationQualificationTests
{
    private const int WarmupObservations = 100_000;
    private const int ObservationsPerWindow = 100_000;
    private const int Windows = 3;

    private readonly ITestOutputHelper _output;

    public LocalMediaReadAllocationQualificationTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalMediaReadAllocation_has_zero_workload_bytes_after_warmup_in_both_sample_orders(bool reverseOrder)
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var observerBytes = new long[Windows];
        var workloadBytes = new long[Windows];
        LocalMediaReadMeasurements final = default;
        int workerThreadId = -1;
        int endingThreadId = -1;
        Exception? workerError = null;

        // Warmup, measurement and the unchanged value-based histogram all run
        // on one dedicated thread. No runner, logging or results formatting is
        // performed between either pair of allocation-counter reads.
        var worker = new Thread(() =>
        {
            try
            {
                var measurements = new LocalMediaReadMeasurements();
                measurements = ObserveBatch(measurements, WarmupObservations, reverseOrder);
                workerThreadId = Environment.CurrentManagedThreadId;

                for (var window = 0; window < Windows; window++)
                {
                    var controlStart = GC.GetAllocatedBytesForCurrentThread();
                    observerBytes[window] = GC.GetAllocatedBytesForCurrentThread() - controlStart;

                    var start = GC.GetAllocatedBytesForCurrentThread();
                    measurements = ObserveBatch(measurements, ObservationsPerWindow,
                        reverseOrder ^ (window % 2 != 0));
                    workloadBytes[window] = GC.GetAllocatedBytesForCurrentThread() - start;
                }

                endingThreadId = Environment.CurrentManagedThreadId;
                final = measurements;
            }
            catch (Exception error)
            {
                workerError = error;
            }
        }) { IsBackground = true, Name = "LocalMediaReadAllocationQualification" };

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "Local media read allocation worker did not finish.");
        if (workerError is not null)
            throw new InvalidOperationException("Local media read allocation worker failed.", workerError);

        var assemblyIdentity = typeof(LocalMediaReadMeasurements).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "UNVERIFIED";
        var source = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "UNVERIFIED";
        var processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "UNVERIFIED";
        var context = $"order={(reverseOrder ? "reverse" : "forward")}; windows={Windows}; " +
            $"warmup={WarmupObservations}; observationsPerWindow={ObservationsPerWindow}; " +
            $"processId={Environment.ProcessId}; callerThread={callerThreadId}; " +
            $"workerThread={workerThreadId}; endingThread={endingThreadId}; " +
            $"CPU={processor}; architecture={RuntimeInformation.ProcessArchitecture}; " +
            $"processorCount={Environment.ProcessorCount}; OS={RuntimeInformation.OSDescription}; " +
            $"runtime={RuntimeInformation.FrameworkDescription}; " +
            $"assemblyVersion={assemblyIdentity}; githubSHA={source} (PR may be merge SHA); " +
            "SDK=check global.json and runner Setup .NET/Qualify SDK logs";
        for (var window = 0; window < Windows; window++)
        {
            _output.WriteLine($"window={window} observerBytes={observerBytes[window]} " +
                $"workloadBytes={workloadBytes[window]}; {context}");
        }

        Assert.True(workerThreadId > 0 && workerThreadId == endingThreadId &&
            workerThreadId != callerThreadId, $"Thread confinement failed: {context}");
        for (var window = 0; window < Windows; window++)
        {
            Assert.True(observerBytes[window] == 0,
                $"Allocation observer control was not empty in window {window}: " +
                $"{observerBytes[window]} bytes; {context}");
            Assert.True(workloadBytes[window] == 0,
                $"Observe allocated {workloadBytes[window]} bytes in window {window}, " +
                $"with {observerBytes[window]} observer-control bytes; {context}");
        }

        Assert.Equal((ulong)(WarmupObservations + Windows * ObservationsPerWindow), final.Calls);
        Assert.Equal(final.Calls, final.Latency.Count);
        Assert.Equal(17, final.Latency.Capacity);
        Assert.Equal(.125, final.Latency.P50);
        Assert.Equal(9000, final.Latency.P95);
        Assert.Equal(9000, final.Latency.P99);
        Assert.Equal(9000, final.MaximumMilliseconds);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LocalMediaReadMeasurements ObserveBatch(
        LocalMediaReadMeasurements measurements, int observations, bool reversed)
    {
        for (var index = 0; index < observations; index++)
        {
            var slot = reversed ? 3 - (index & 3) : index & 3;
            measurements = measurements.Observe(slot switch
            {
                0 => 0d,
                1 => .1d,
                2 => 1d,
                _ => 9000d,
            });
        }

        return measurements;
    }
}
