// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection;
using rtaime.Runtime;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

/// <summary>Hosted software evidence; storage bus, topology and physical SSD latency remain unverified.</summary>
public sealed class MultiDeckSsdPlaybackQualificationTests
{
    [Fact]
    public async Task Generated_dual_file_load_records_source_bound_single_Program_and_bounded_session_evidence()
    {
        var profile = Environment.GetEnvironmentVariable("RTAIME_SSD_PLAYBACK_PROFILE");
        var externalA = Environment.GetEnvironmentVariable("RTAIME_SSD_MEDIA_A");
        var externalB = Environment.GetEnvironmentVariable("RTAIME_SSD_MEDIA_B");
        if (profile is null && (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled)) return;
        Assert.True(OperatingSystem.IsWindows(), "Real Windows decoding is required.");
        var seconds = profile switch { null or "smoke" => 30, "10m" => 600, "60m" => 3600,
            "8h" => 28800, "24h" => 86400, "72h" => 259200,
            _ => throw new InvalidOperationException("Unknown SSD playback duration.") };
        Assert.Equal(externalA is null, externalB is null);
        using var asset = externalA is null ? ReferenceMediaTestAsset.Generate(ReferenceMediaProfile.Hd50) : null;
        using var other = externalB is null ? LocalMediaTestAsset.ExtractReference1080p50() : null;
        var pathA = externalA ?? asset!.Path;
        var pathB = externalB ?? other!.Path;
        object MediaIdentity(string path)
        {
            using var stream = File.OpenRead(path);
            return new { bytes = stream.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
        }
        var media = new[] { MediaIdentity(pathA), MediaIdentity(pathB) };
        var sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT");
        var buildCommit = Environment.GetEnvironmentVariable("RTAIME_BUILD_COMMIT");
        if (profile is not null)
        {
            foreach (var assembly in new[] { typeof(RuntimeHostProcess).Assembly,
                typeof(ControlHostProcess).Assembly, typeof(OperatorControlClient).Assembly,
                typeof(rtaime.Media.LocalMediaFileProvider).Assembly })
                Assert.Equal(sourceCommit, assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(value => value.Key == "RtaimeSourceRevision").Value);
            Assert.Equal(sourceCommit, buildCommit);
        }
        var observations = new List<object>(128);
        var trend = new BoundedSoakResourceTrend();
        var cpuHistogram = new BoundedSoakHistogram(.1, 1000);
        var observerInterval = new BoundedSoakHistogram(1, 10000);
        var sampledStartLateness = new BoundedSoakHistogram(.1, 10000);
        var sampledPresentationLateness = new BoundedSoakHistogram(.1, 10000);
        var clock = new Stopwatch();
        var startedAt = DateTimeOffset.UtcNow;
        ulong startMissed = 0;
        string? failure = null, cleanupFailure = null;
        object? finalReport = null;
        var handleDiagnostics = new List<object>(4);
        var runtimeEndpoint = $"rtaime.test.dual.runtime.{Guid.NewGuid():N}";
        var controlEndpoint = $"rtaime.test.dual.control.{Guid.NewGuid():N}";
        using var runtimeStop = new CancellationTokenSource();
        using var controlStop = new CancellationTokenSource();
        var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with
        {
            ListenEndpoint = controlEndpoint, RuntimeEndpoint = runtimeEndpoint,
            RequestTimeout = TimeSpan.FromSeconds(10), RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
        });
        var runtimeRun = runtime.RunAsync(runtimeStop.Token);
        var controlRun = control.RunAsync(controlStop.Token);
        try
        {
            var startup = Stopwatch.StartNew();
            while (control.Control?.HasAuthoritativeState != true && startup.Elapsed < TimeSpan.FromSeconds(12))
                await Task.Delay(25);
            Assert.True(control.Control?.HasAuthoritativeState == true);
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
                controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12)));
            var snapshot = await client.SynchronizeAsync();
            await using var a = new MediaDeckController(client);
            await using var b = new MediaDeckController(client);
            var sourceA = new MediaSourceId(Identity.Parse(snapshot.Sources[0].Id));
            var sourceB = new MediaSourceId(Identity.Parse(snapshot.Sources[1].Id));
            var openedA = await a.OpenAsync(pathA, sourceA);
            Assert.True(openedA.IsLoaded, $"Deck A: {openedA.Failure?.Code}: {openedA.Failure?.Message}");
            var openedB = await b.OpenAsync(pathB, sourceB);
            Assert.True(openedB.IsLoaded, $"Deck B: {openedB.Failure?.Code}: {openedB.Failure?.Message}");
            await a.ConfigurePlaybackAsync(true, MediaDeckEndBehavior.Loop);
            await b.ConfigurePlaybackAsync(true, MediaDeckEndBehavior.Loop);
            await a.PlayAsync();
            await b.PlayAsync();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var memoryBefore = process.PrivateMemorySize64;
            var handlesBefore = process.HandleCount;
            var startSequence = runtime.Runtime!.Snapshot.NextSequenceNumber;
            startMissed = runtime.ScheduleStatistics.MissedSlots;
            clock.Start();
            startedAt = DateTimeOffset.UtcNow;
            void CaptureHandles()
            {
                process.Refresh();
                var observation = new { seconds = clock.Elapsed.TotalSeconds,
                    handles = process.HandleCount, privateBytes = process.PrivateMemorySize64,
                    details = LocalPlaybackHandleDiagnostics.Capture() };
                handleDiagnostics.Add(observation);
                if (profile is not null) Console.WriteLine("SSD HANDLE DIAGNOSTIC: " + JsonSerializer.Serialize(observation));
            }
            CaptureHandles();
            var nextHandleDiagnostic = 120;
            var previousElapsed = TimeSpan.Zero;
            var previousCpu = process.TotalProcessorTime;
            var nextRetained = TimeSpan.Zero;
            var nextTake = TimeSpan.Zero;
            var nextCheckpoint = TimeSpan.FromSeconds(60);
            var takes = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (clock.Elapsed >= nextTake)
                {
                    for (var take = 0; take < 2; take++)
                    {
                        var target = takes++ % 2 == 0 ? sourceB : sourceA;
                        Assert.True((await client.SelectPreviewAsync(target.ToString())).Accepted);
                        Assert.True((await client.CutPreviewAsync()).Accepted);
                    }
                    nextTake = clock.Elapsed + TimeSpan.FromSeconds(2);
                }
                Assert.False(runtimeRun.IsCompleted, runtime.Lifecycle.Detail);
                Assert.False(controlRun.IsCompleted, control.Lifecycle.Detail);
                process.Refresh();
                var elapsed = clock.Elapsed;
                if (seconds >= 600 && elapsed.TotalSeconds >= nextHandleDiagnostic && handleDiagnostics.Count < 3)
                {
                    CaptureHandles();
                    nextHandleDiagnostic = 300;
                }
                var cpu = process.TotalProcessorTime;
                if (elapsed > previousElapsed)
                {
                    cpuHistogram.Observe(Math.Clamp(100 * (cpu - previousCpu).TotalSeconds /
                        ((elapsed - previousElapsed).TotalSeconds * Environment.ProcessorCount), 0, 100));
                    observerInterval.Observe((elapsed - previousElapsed).TotalMilliseconds);
                }
                previousCpu = cpu;
                previousElapsed = elapsed;
                trend.Observe(elapsed, process.PrivateMemorySize64, process.HandleCount);
                sampledStartLateness.Observe(Math.Max(0, runtime.ScheduleStatistics.LastStartLateness.TotalMilliseconds));
                sampledPresentationLateness.Observe(Math.Max(0, runtime.ScheduleStatistics.LastPresentationLateness.TotalMilliseconds));
                var resources = runtime.MediaDeck!.Resources;
                var first = runtime.MediaDeck.GetDecodeSnapshot(sourceA)!.Value;
                var second = runtime.MediaDeck.GetDecodeSnapshot(sourceB)!.Value;
                Assert.Equal(2, resources.Reserved);
                Assert.InRange(first.QueuedFrames, 0, 8);
                Assert.InRange(second.QueuedFrames, 0, 8);
                if (elapsed >= nextRetained && observations.Count < 128)
                {
                observations.Add(new
                {
                    Milliseconds = clock.ElapsedMilliseconds, process.PrivateMemorySize64, process.HandleCount,
                    First = first, Second = second, Resources = resources,
                    Sequence = runtime.Runtime.Snapshot.NextSequenceNumber
                });
                nextRetained = elapsed + TimeSpan.FromSeconds(Math.Max(.25, seconds / 120.0));
                }
                if (profile is not null && elapsed >= nextCheckpoint)
                {
                    var checkpointOutput = Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_OUTPUT")!;
                    var checkpoint = Path.Combine(checkpointOutput, "ssd-playback.checkpoint.json");
                    LocalPlaybackEvidenceJson.Write(checkpoint + ".tmp", new
                    {
                        schemaVersion = "1.0", sourceCommit, buildCommit, profile, status = "RUNNING",
                        startedAt, elapsedSeconds = elapsed.TotalSeconds,
                        requestedSeconds = seconds, media, samples = observations,
                        first = runtime.MediaDeck.GetPlaybackMeasurements(sourceA),
                        second = runtime.MediaDeck.GetPlaybackMeasurements(sourceB),
                        schedule = runtime.ScheduleStatistics, resourceWindows = trend.Snapshot(),
                        physicalStatus = "UNVERIFIED"
                    });
                    File.Move(checkpoint + ".tmp", checkpoint, overwrite: true);
                    nextCheckpoint = elapsed + TimeSpan.FromSeconds(60);
                }
                await Task.Delay(250);
            }
            CaptureHandles();
            var finalSequence = runtime.Runtime.Snapshot.NextSequenceNumber;
            process.Refresh();
            // Always retain the actual end state, even if the outer observer was delayed.
            // Replace the last slot at capacity; evidence size never grows with run duration.
            var finalObservation = new
            {
                Milliseconds = clock.ElapsedMilliseconds, process.PrivateMemorySize64, process.HandleCount,
                First = runtime.MediaDeck!.GetDecodeSnapshot(sourceA)!.Value,
                Second = runtime.MediaDeck.GetDecodeSnapshot(sourceB)!.Value,
                Resources = runtime.MediaDeck.Resources,
                Sequence = finalSequence
            };
            if (observations.Count == 128) observations[^1] = finalObservation;
            else observations.Add(finalObservation);
            var source = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT");
            var firstPlayback = runtime.MediaDeck.GetPlaybackMeasurements(sourceA)!.Value;
            var secondPlayback = runtime.MediaDeck.GetPlaybackMeasurements(sourceB)!.Value;
            var report = new
            {
                SourceCommit = source, Class = "HOSTED_SOFTWARE_OBSERVATION", PhysicalQualification = "UNVERIFIED",
                StorageBus = "UNVERIFIED", SeparateDriveTopology = "UNVERIFIED", HighBitrate = "UNVERIFIED",
                DurationMilliseconds = clock.ElapsedMilliseconds, Capacity = 2, Takes = takes,
                StartSequence = startSequence, FinalSequence = finalSequence, runtime.ScheduleStatistics,
                FirstPlayback = firstPlayback, SecondPlayback = secondPlayback,
                PrivateBytesGrowth = process.PrivateMemorySize64 - memoryBefore,
                HandleGrowth = process.HandleCount - handlesBefore,
                Samples = observations
            };
            var output = Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_OUTPUT")!;
            var path = Path.Combine(output, "dual-deck-software.outcome.json");
            LocalPlaybackEvidenceJson.Write(path, report);
            if (profile is null)
                Console.WriteLine("DUAL DECK SOFTWARE OUTCOME: " + File.ReadAllText(path));
            var finalStats = runtime.ScheduleStatistics;
            var missedDelta = finalStats.MissedSlots - startMissed;
            finalReport = new
            {
                schemaVersion = "1.0", qualification = "rtaime-ssd-playback",
                sourceCommit, buildCommit, sdk = Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"),
                profile = profile ?? "smoke", startedAt, elapsedSeconds = clock.Elapsed.TotalSeconds,
                requestedSeconds = seconds, media, capacity = 2, takes,
                startSequence, finalSequence, startMissed, missedDelta, finalStats,
                firstPlayback, secondPlayback,
                deckProbes = new[] { a.Snapshot.Probe, b.Snapshot.Probe },
                decodeA = runtime.MediaDeck.GetDecodeSnapshot(sourceA),
                decodeB = runtime.MediaDeck.GetDecodeSnapshot(sourceB),
                resourceCapacity = 128, samples = observations,
                handleDiagnostics,
                resourceWindows = trend.Snapshot(),
                resourcePlateau = trend.Assess(512L * 1024 * 1024, 64),
                cpu = cpuHistogram.Summary(), observerInterval = observerInterval.Summary(),
                sampledStartLateness = sampledStartLateness.Summary(),
                sampledPresentationLateness = sampledPresentationLateness.Summary(),
                percentileScope = "Read/decode histograms cover all worker calls; scheduler/CPU observations are sampled at observer cadence, not every Program slot.",
                gc = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1),
                    gen2 = GC.CollectionCount(2), allocatedBytes = GC.GetTotalAllocatedBytes() },
                privateGrowth = process.PrivateMemorySize64 - memoryBefore,
                handleGrowth = process.HandleCount - handlesBefore,
                physicalOutputLoss = "UNVERIFIED", gpuUtilization = "UNVERIFIED",
                vram = "UNVERIFIED", physicalAvDrift = "UNVERIFIED",
                fileOnlyReadLatency = "UNVERIFIED", decoderAcceleration = "UNVERIFIED",
                cacheState = "UNVERIFIED", pressure = "UNVERIFIED",
                timingStatus = missedDelta > 0 ? "FAIL" : "UNVERIFIED",
                softwareStatus = "COLLECTED", physicalStatus = "UNVERIFIED"
            };
            Assert.InRange(observations.Count, 1, 128);
            Assert.True(finalSequence > startSequence + 200);
            Assert.Null(runtime.MediaDeck!.GetSnapshot(sourceA).Failure);
            Assert.Null(runtime.MediaDeck.GetSnapshot(sourceB).Failure);
            Assert.InRange(process.PrivateMemorySize64 - memoryBefore, long.MinValue, 512L * 1024 * 1024);
            Assert.InRange(process.HandleCount - handlesBefore, int.MinValue, 64);
            Assert.Matches("^[0-9a-f]{40}$", source!);
            Assert.True(firstPlayback.DecodedFrames > 200);
            Assert.True(secondPlayback.DecodedFrames > 200);
            Assert.Equal(0UL, firstPlayback.ReadFailures);
            Assert.Equal(0UL, secondPlayback.ReadFailures);
            Assert.Equal(0UL, firstPlayback.VideoTimestampRegressions);
            Assert.Equal(0UL, secondPlayback.VideoTimestampRegressions);
            Assert.Equal(0UL, firstPlayback.AudioTimestampDiscontinuities);
            Assert.Equal(0UL, secondPlayback.AudioTimestampDiscontinuities);
            if (seconds >= 28800) Assert.Equal("PASS", trend.Assess(512L * 1024 * 1024, 64));
        }
        catch (Exception exception) { failure = exception.ToString(); throw; }
        finally
        {
            controlStop.Cancel();
            ControlHostExitCode? controlExit = null;
            RuntimeHostExitCode? runtimeExit = null;
            try { controlExit = await controlRun.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception exception) { cleanupFailure = exception.ToString(); }
            runtimeStop.Cancel();
            try { runtimeExit = await runtimeRun.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception exception) { cleanupFailure ??= exception.ToString(); }
            var cleanup = cleanupFailure is null && controlExit == ControlHostExitCode.Success &&
                runtimeExit == RuntimeHostExitCode.Success && runtime.RuntimeDisposed &&
                (runtime.MediaDeck?.Resources.Reserved ?? 0) == 0 &&
                runtime.FinalRuntimeSnapshot?.ActiveGpuSurfaces == 0;
            if (profile is not null)
            {
                var output = Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_OUTPUT")!;
                var evidence = new
                {
                    schemaVersion = "1.0", sourceCommit, buildCommit, profile,
                    capturedAt = DateTimeOffset.UtcNow, media,
                    status = failure is null && cleanup ? "PASS" : "FAIL",
                    failure = failure is null ? null : failure[..Math.Min(failure.Length, 2048)],
                    cleanupFailure = cleanupFailure is null ? null : cleanupFailure[..Math.Min(cleanupFailure.Length, 2048)],
                    cleanup = cleanup ? "PASS" : "FAIL", result = finalReport,
                    lastSamples = finalReport is null ? observations : null, handleDiagnostics
                };
                var evidencePath = Path.Combine(output, "ssd-playback.outcome.json");
                LocalPlaybackEvidenceJson.Write(evidencePath + ".tmp", evidence);
                File.Move(evidencePath + ".tmp", evidencePath, overwrite: true);
                // Compact log projection; the fixed-size detailed evidence is retained as JSON.
                Console.WriteLine("SSD PLAYBACK OUTCOME: " + JsonSerializer.Serialize(new
                { sourceCommit, profile, evidence.status, evidence.cleanup,
                    seconds = clock.Elapsed.TotalSeconds, resources = observations.Count,
                    missedDelta = runtime.ScheduleStatistics.MissedSlots - startMissed,
                    observerInterval = observerInterval.Summary() }));
            }
            Assert.True(cleanup, cleanupFailure ?? "Host cleanup or resource ownership did not complete.");
        }
        Assert.Equal(0, runtime.MediaDeck?.Resources.Reserved ?? 0);
        Assert.Equal(0, runtime.FinalRuntimeSnapshot!.ActiveGpuSurfaces);
    }
}
