// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using rtaime.Runtime;

namespace rtaime.Tests.Integration;

public sealed class SustainedMixedLoadQualificationTests
{
    [Fact]
    public async Task Staged_mixed_load_executes_only_when_explicitly_enabled()
    {
        var profileName = Environment.GetEnvironmentVariable("RTAIME_MIXED_SOAK_PROFILE");
        if (string.IsNullOrWhiteSpace(profileName)) return;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Mixed-load playback and host qualification requires Windows.");
        var source = Require("RTAIME_MIXED_SOAK_SOURCE");
        var build = Require("RTAIME_MIXED_SOAK_BUILD");
        if (source.Length != 40 || source.Any(c => !Uri.IsHexDigit(c)) || source != build ||
            Require("RTAIME_MIXED_SOAK_SDK") != "10.0.401")
            throw new InvalidOperationException("Mixed-load evidence requires exact matching source/build and pinned SDK.");
        var descriptorPath = Require("RTAIME_MIXED_SOAK_DESCRIPTOR");
        var outputPath = Require("RTAIME_MIXED_SOAK_EVIDENCE");
        var descriptorBytes = File.ReadAllBytes(descriptorPath);
        var descriptorHash = Convert.ToHexString(SHA256.HashData(descriptorBytes)).ToLowerInvariant();
        using var descriptor = JsonDocument.Parse(descriptorBytes);
        var configuration = descriptor.RootElement;
        if (configuration.GetProperty("schemaVersion").GetString() != "1.0" ||
            configuration.GetProperty("lane").GetString() != "MANAGED_STAGED_SINGLE_TESTHOST")
            throw new InvalidOperationException("Unsupported mixed-load workload descriptor.");
        var profile = configuration.GetProperty("profiles").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == profileName);
        var requiredSeconds = profile.GetProperty("minimumElapsedSeconds").GetInt32();
        var expectedSeconds = profileName switch { "smoke" => 0, "8h" => 28800, "24h" => 86400, "72h" => 259200,
            _ => throw new InvalidOperationException("Unknown mixed-load profile.") };
        if (requiredSeconds != expectedSeconds) throw new InvalidOperationException("Soak duration differs from its named profile.");
        var minimumCycles = profile.GetProperty("minimumCycles").GetInt32();
        if (minimumCycles < 2) throw new InvalidOperationException("Mixed-load requires repeated cycles.");
        var resourcePolicy = configuration.GetProperty("resourcePolicy");
        var maximumPrivateGrowth = resourcePolicy.GetProperty("maximumPrivateGrowthBytes").GetInt64();
        var maximumHandleGrowth = resourcePolicy.GetProperty("maximumHandleGrowth").GetInt32();
        var faultInterval = configuration.GetProperty("faultSchedule").GetProperty("everyCycles").GetInt32();
        if (faultInterval is < 1 or > 100) throw new InvalidOperationException("Invalid bounded fault schedule.");
        var latency = new BoundedSoakHistogram(.01, 10000);
        var resources = new BoundedSoakResourceTrend();
        using var process = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        var startedAtUtc = DateTimeOffset.UtcNow;
        ulong cycles = 0, frames = 0, schedulerMisses = 0, monitorDrops = 0;
        ulong recordingDrops = 0, replayDrops = 0, ndiDrops = 0, srtDrops = 0;
        ulong deadlineMisses = 0, recoveries = 0;
        var maximumReadbackBuffers = 0;
        var maximumMonitoringDepth = 0;
        long maximumPrivateBytes = 0, maximumWorkingSetBytes = 0;
        var maximumHandles = 0;
        var stages = new ulong[5];
        var observedFormats = new HashSet<string>();
        string completion = "RUNNING";
        string? failure = null;

        void Observe(bool fractional, object value, IReadOnlyList<double> durations)
        {
            var report = JsonSerializer.SerializeToElement(value,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var format = fractional ? "1080p59.94" : "1080p50";
            observedFormats.Add(format);
            var budget = fractional ? 1000 * 1001.0 / 60000 : 20;
            foreach (var sample in durations)
            {
                latency.Observe(sample);
                if (sample > budget) deadlineMisses++;
            }
            frames += report.GetProperty("virtualOutputAccepted").GetUInt64();
            schedulerMisses += report.GetProperty("missedSchedulerSlots").GetUInt64();
            monitorDrops += report.GetProperty("monitorDrops").GetUInt64();
            recordingDrops += report.GetProperty("recording").GetProperty("dropped").GetUInt64();
            replayDrops += report.GetProperty("replayDroppedSamples").GetUInt64();
            ndiDrops += report.GetProperty("ndi").GetProperty("droppedSamples").GetUInt64();
            srtDrops += report.GetProperty("srt").GetProperty("droppedSamples").GetUInt64();
            maximumReadbackBuffers = Math.Max(maximumReadbackBuffers, report.GetProperty("maximumReadbackBuffers").GetInt32());
            maximumMonitoringDepth = Math.Max(maximumMonitoringDepth, report.GetProperty("maximumMonitoringDepth").GetInt32());
            Assert.Equal(0, report.GetProperty("audioDriftSamples").GetInt32());
        }

        void Checkpoint()
        {
            process.Refresh();
            resources.Observe(clock.Elapsed, process.PrivateMemorySize64, process.HandleCount);
            maximumPrivateBytes = Math.Max(maximumPrivateBytes, process.PrivateMemorySize64);
            maximumWorkingSetBytes = Math.Max(maximumWorkingSetBytes, process.WorkingSet64);
            maximumHandles = Math.Max(maximumHandles, process.HandleCount);
            var report = new
            {
                schemaVersion = "1.0", qualification = "rtaime-staged-mixed-load",
                profile = profileName, completion, failure, sourceCommit = source, buildCommit = build,
                descriptorSha256 = descriptorHash, startedAtUtc, capturedAtUtc = DateTimeOffset.UtcNow,
                elapsedSeconds = clock.Elapsed.TotalSeconds, requiredSeconds, cycles, minimumCycles,
                runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local",
                runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "local",
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                cpuCount = Environment.ProcessorCount, sdk = "10.0.401",
                formats = observedFormats.Order().ToArray(), stages,
                scope = "STAGED_MANAGED_SESSIONS_IN_ONE_TESTHOST",
                latency = latency.Summary(), frames, deadlineMisses, schedulerMisses,
                perConsumerDrops = new { recordingDrops, replayDrops, ndiDrops, srtDrops, monitorDrops },
                softwareAudioDriftSamples = 0, maximumReadbackBuffers, maximumMonitoringDepth, recoveries,
                resourceWindows = resources.Snapshot(),
                resourcePlateau = resources.Assess(maximumPrivateGrowth, maximumHandleGrowth),
                resourcePolicy = new { maximumPrivateGrowth, maximumHandleGrowth },
                maximumPrivateBytes, maximumWorkingSetBytes, maximumHandles,
                processCpuSeconds = process.TotalProcessorTime.TotalSeconds,
                cleanup = "ASSERTED_AFTER_EACH_MIXED_SESSION",
                physicalGpu = "UNVERIFIED", gpuUtilization = "UNVERIFIED", vram = "UNVERIFIED",
                driver = "UNVERIFIED", nativeNetworkTransport = "UNVERIFIED",
                physicalAvSynchronization = "UNVERIFIED", continuousSingleProduction = "UNVERIFIED",
                longSoak = profileName == "smoke" ? "UNVERIFIED" : completion
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            // One atomically replaced bounded summary, never one file per frame/cycle.
            var temporary = outputPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Move(temporary, outputPath, overwrite: true);
        }

        try
        {
            Checkpoint();
            while (clock.Elapsed.TotalSeconds < requiredSeconds || cycles < (ulong)minimumCycles)
            {
                var mixed = new RuntimeConsumerIsolationTests();
                await mixed.RunMixedAsync(false, (e, d) => Observe(false, e, d));
                await mixed.RunMixedAsync(true, (e, d) => Observe(true, e, d));
                stages[0]++;
                new LocalMediaRuntimeIntegrationTests().Reference_mp4_sustains_multiple_playback_cycles_without_decode_failure();
                new LocalMediaRuntimeIntegrationTests().Reference_mp4_normalizes_to_requested_runtime_format();
                stages[1]++;
                var cues = new ShowControlCoordinatorIntegrationTests();
                await cues.Go_executes_actions_in_order_and_rearms_between_cues();
                await cues.Template_dry_run_and_commit_preserve_Program_and_create_new_authored_identities();
                stages[2]++;
                await new AdvancedAudioQualificationIntegrationTests().Full_authoritative_configuration_crosses_Client_Control_and_Runtime();
                stages[3]++;
                if (cycles % (ulong)faultInterval == 0)
                {
                    var faults = new ProcessSupervisionRecoveryTests();
                    await faults.Killed_RuntimeHost_is_restarted_and_Control_reapplies_same_authority_revision();
                    await faults.Killed_AIHost_is_restarted_with_a_new_process_identity();
                    await new ControlTransportFaultQualificationTests().Lost_TAKE_requires_snapshot_without_replay_before_or_after_commit(true);
                    recoveries += 3;
                    stages[4]++;
                }
                cycles++;
                Checkpoint();
                if (resources.Assess(maximumPrivateGrowth, maximumHandleGrowth) == "FAIL")
                    throw new InvalidOperationException("Resource growth exceeded the declared post-warmup plateau budgets.");
            }
            Assert.Equal(2, observedFormats.Count);
            Assert.All(stages, count => Assert.True(count > 0));
            Assert.True(latency.Count > 0);
            if (profileName != "smoke")
                Assert.Equal("PASS", resources.Assess(maximumPrivateGrowth, maximumHandleGrowth));
            completion = profileName == "smoke" ? "SHORT_SOFTWARE_PASS" : "STAGED_SOFTWARE_SOAK_PASS";
        }
        catch (Exception exception)
        {
            completion = "FAILED";
            failure = exception.ToString();
            throw;
        }
        finally { Checkpoint(); }
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
        throw new InvalidOperationException("Required qualification environment: " + name);
}
