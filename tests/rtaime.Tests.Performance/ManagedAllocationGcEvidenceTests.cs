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

    [Fact]
    public async Task Qualification_profile_and_PowerShell_sources_parse_on_the_Windows_CI_runner()
    {
        if (!OperatingSystem.IsWindows()) return;
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "rtaime.slnx")))
            folder = folder.Parent;
        Assert.NotNull(folder);
        var root = folder.FullName;
        var profileFile = Path.Combine(root, "qualification", "performance", "managed-allocation-gc.json");
        using var descriptor = JsonDocument.Parse(File.ReadAllText(profileFile));
        var data = descriptor.RootElement;
        Assert.Equal("1.0", data.GetProperty("schemaVersion").GetString());
        Assert.Equal("10.0.401", data.GetProperty("sdk").GetString());
        Assert.Equal(3, data.GetProperty("warmupBoundaries").GetInt32());
        Assert.Equal(12, data.GetProperty("measuredBoundaries").GetInt32());
        Assert.Equal(8, data.GetProperty("casesPerFormat").GetInt32());
        Assert.Equal(2, data.GetProperty("formats").GetArrayLength());
        Assert.Equal("UNVERIFIED",
            data.GetProperty("sourceEvidence").GetProperty("physicalOutput").GetString());
        Assert.Equal("UNVERIFIED",
            data.GetProperty("sourceEvidence").GetProperty("hardwareGpu").GetString());

        var scripts = new[]
        {
            "Invoke-ManagedAllocationGcQualification.ps1",
            "Test-ManagedAllocationGcEvidence.ps1",
            "Test-ManagedAllocationGcVerifierRegressions.ps1"
        }.Select(file => Path.Combine(root, "build", "qualification", file)).ToArray();
        Assert.All(scripts, script => Assert.True(File.Exists(script), script));
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var powershell = "$files = @(" + string.Join(",", scripts.Select(Quote)) + "); " +
            "foreach ($file in $files) { $tokens = $null; $parseErrors = $null; " +
            "[System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$parseErrors) | Out-Null; " +
            "if ($parseErrors.Count -gt 0) { $parseErrors | ForEach-Object { Write-Error $_.Message }; exit 1 } }; exit 0";
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(powershell);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The PowerShell parser process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var diagnostics = (await stdout) + (await stderr);
        Assert.Equal(0, process.ExitCode);
        Assert.True(diagnostics.Length < 8192, "PowerShell syntax qualification emitted unbounded diagnostics.");
    }

    private static string RequiredSha(string name)
    {
        var sha = Environment.GetEnvironmentVariable(name);
        if (sha is null || sha.Length != 40 || sha.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Exact source and build SHA are required.");
        return sha.ToLowerInvariant();
    }
}
