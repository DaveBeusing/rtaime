// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Text.Json;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class RealtimePlayoutEvidenceMatrixTests
{
    private static string Root()
    {
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder is not null)
        {
            if (File.Exists(Path.Combine(folder.FullName, "rtaime.slnx"))) return folder.FullName;
            folder = folder.Parent;
        }
        throw new InvalidOperationException("No source-controlled rtaime solution root.");
    }

    [Fact]
    public void Matrix_has_closed_source_and_rate_profiles_without_hardware_claims()
    {
        var root = Root();
        var path = Path.Combine(root, "qualification", "performance", "realtime-playout-matrix.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var schema = json.RootElement;
        Assert.Equal("1.0", schema.GetProperty("schemaVersion").GetString());
        Assert.Equal("rtaime-realtime-playout-software-matrix",
            schema.GetProperty("qualification").GetString());
        Assert.Equal("10.0.401", schema.GetProperty("sdk").GetString());
        var rates = schema.GetProperty("cadence").EnumerateArray().ToArray();
        Assert.Equal(new[] { "1080p25", "1080p50", "1080p59.94" },
            rates.Select(r => r.GetProperty("format").GetString()).ToArray());
        Assert.Equal("UNVERIFIED", rates[0].GetProperty("evidence").GetString());
        Assert.Equal(40d, rates[0].GetProperty("frameBudgetMilliseconds").GetDouble());
        Assert.Equal(20d, rates[1].GetProperty("frameBudgetMilliseconds").GetDouble());
        Assert.Equal(1001d / 60, rates[2].GetProperty("frameBudgetMilliseconds").GetDouble(), 8);

        var profiles = schema.GetProperty("executionProfiles").EnumerateArray().ToArray();
        Assert.Equal(new[] { "smoke", "reference" },
            profiles.Select(p => p.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { 30, 600 },
            profiles.Select(p => p.GetProperty("ssdDurationSecondsPerFormat").GetInt32()).ToArray());
        Assert.All(profiles, p => Assert.False(p.GetProperty("requireLongSoak").GetBoolean()));

        var scenarios = schema.GetProperty("sourceScenarios").EnumerateArray().ToArray();
        Assert.Equal(8, scenarios.Length);
        Assert.Equal(8, scenarios.Select(s => s.GetProperty("id").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        var paths = scenarios.Select(s => s.TryGetProperty("runner", out var runner)
            ? runner.GetString() : s.GetProperty("test").GetString()).ToArray();
        Assert.All(paths, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        Assert.Contains(scenarios, s => s.GetProperty("id").GetString() == "sequence-two-source");
        Assert.Contains(scenarios, s => s.GetProperty("id").GetString() == "dual-deck-5994");

        var limits = schema.GetProperty("qualificationLimits");
        Assert.True(limits.GetProperty("noApprovedP99Budget").GetBoolean());
        foreach (var field in new[] { "physicalOutput", "gpuDriverVram",
            "storageIoCachePressure", "externalAvSync", "externalGenlock",
            "physicalDeviceLoss", "realContinuousMultitrackSoak", "real25FpsNativeProgram" })
            Assert.Equal("UNVERIFIED", limits.GetProperty(field).GetString());
    }

    [Fact]
    public void Existing_software_producers_and_exact_source_verifiers_remain_the_only_runners()
    {
        var root = Root();
        var runner = File.ReadAllText(Path.Combine(root, "build", "qualification",
            "Invoke-RealtimePlayoutEvidence.ps1"));
        var verifier = File.ReadAllText(Path.Combine(root, "build", "qualification",
            "Test-RealtimePlayoutEvidence.ps1"));
        foreach (var producer in new[] { "Invoke-ManagedAllocationGcQualification.ps1",
            "Invoke-SsdPlaybackQualification.ps1", "Invoke-SustainedMixedLoadQualification.ps1",
            "ReferenceSequenceDistinctSourcesOutputTests", "MonitoringVisualPerformanceQualificationTests" })
            Assert.Contains(producer, runner, StringComparison.Ordinal);
        foreach (var gate in new[] { "Test-ManagedAllocationGcEvidence.ps1",
            "Test-SsdPlaybackEvidence.ps1", "Test-SsdPlaybackEnvironment.ps1",
            "Test-SustainedMixedLoadEvidence.ps1", "Bound-File" })
            Assert.Contains(gate, verifier, StringComparison.Ordinal);
        Assert.Contains("RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS", runner, StringComparison.Ordinal);
        Assert.Contains("Native_decode_worker_attributes_slot_copies_without_releasing_retained_frames",
            runner, StringComparison.Ordinal);
        Assert.Contains("UNVERIFIED_NO_APPROVED_MANAGED_THRESHOLD", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("GC.Collect(", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("NoGCRegion(", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("SOFTWARE_COLLECTION_VERIFIED_NOT_REALTIME_APPROVAL" + " = \"PASS\"",
            runner, StringComparison.Ordinal);
    }
}
