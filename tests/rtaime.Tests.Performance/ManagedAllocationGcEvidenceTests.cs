// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace rtaime.Tests.Performance;

/// <summary>
/// Opt-in, isolated producer process. Reuses the existing real Runtime Program
/// boundary measurement instead of measuring a second synthetic rendering path.
/// Nothing is sampled or serialized from inside ProcessNextBoundary.
/// </summary>
public sealed class ManagedAllocationGcEvidenceTests
{
    [Fact]
    [Trait("Qualification", "ManagedAllocationGc")]
    public async Task Capture_existing_Program_producer_for_exact_source_profile()
    {
        var output = Environment.GetEnvironmentVariable("RTAIME_MANAGED_GC_PRODUCER_DIR");
        if (string.IsNullOrWhiteSpace(output)) return;

        Assert.True(OperatingSystem.IsWindows() && Environment.Is64BitProcess,
            "Program allocation qualification requires an isolated Windows x64 testhost.");

        var source = RequiredSha("RTAIME_SOURCE_COMMIT");
        Assert.Equal(source, RequiredSha("RTAIME_BUILD_COMMIT"));
        Assert.Equal("10.0.401", Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"));
        Assert.Equal(Path.GetFullPath(output),
            Path.GetFullPath(Environment.GetEnvironmentVariable("RTAIME_PIPELINE_BASELINE_EVIDENCE")!));

        Directory.CreateDirectory(output);
        foreach (var file in new[] { "program-load-False.json", "program-load-True.json", "producer-process.json" })
            Assert.False(File.Exists(Path.Combine(output, file)), "Qualification must never reuse existing producer artifacts.");

        var started = DateTimeOffset.UtcNow;
        // Both native frame rates and all eight existing consumer masks execute
        // through RuntimeProgramExecutionService.ProcessNextBoundary. Its own
        // assertions qualify frame identities, queue/lease cleanup and telemetry.
        var producer = new ProgramPipelineMeasurementBaselineTests();
        await producer.Program_service_load_matrix_reports_consumers_separately_from_physical_output(false);
        await producer.Program_service_load_matrix_reports_consumers_separately_from_physical_output(true);

        foreach (var file in new[] { "program-load-False.json", "program-load-True.json" })
            Assert.True(File.Exists(Path.Combine(output, file)), "The existing Program producer did not emit its rate report.");

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        var metadata = new
        {
            schemaVersion = "1.0",
            qualification = "rtaime-managed-allocation-gc-software",
            producer = "ProgramPipelineMeasurementBaselineTests",
            sourceCommit = source,
            buildCommit = source,
            sdk = "10.0.401",
            startedAtUtc = started,
            completedAtUtc = DateTimeOffset.UtcNow,
            testhostProcessId = Environment.ProcessId,
            currentThreadId = Environment.CurrentManagedThreadId,
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            machineName = Environment.MachineName,
            cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "UNAVAILABLE",
            logicalProcessors = Environment.ProcessorCount,
            processPrivateBytesAfterSessions = process.PrivateMemorySize64,
            processHandlesAfterSessions = process.HandleCount,
            lastGcHeapBytes = gc.HeapSizeBytes,
            lastGcFragmentedBytes = gc.FragmentedBytes,
            lastGcLargeObjectHeapBytes = gc.GenerationInfo.Length > 3
                ? gc.GenerationInfo[3].SizeAfterBytes : (long?)null,
            lastGcPinnedObjectHeapBytes = gc.GenerationInfo.Length > 4
                ? gc.GenerationInfo[4].SizeAfterBytes : (long?)null,
            gcEventSamples = "UNVERIFIED",
            observer = "NONINVASIVE_GC_COUNTER_SNAPSHOTS",
            physicalDevice = "UNVERIFIED"
        };
        File.WriteAllText(Path.Combine(output, "producer-process.json"),
            JsonSerializer.Serialize(metadata) + Environment.NewLine);
    }

    private static string RequiredSha(string name)
    {
        var sha = Environment.GetEnvironmentVariable(name);
        if (sha is null || sha.Length != 40 || sha.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Exact source and build SHA are required.");
        return sha.ToLowerInvariant();
    }
}
