// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ReferenceMediaPlaybackBaselineTests
{
    [Fact]
    public async Task Long_reference_plays_through_the_existing_RuntimeHost_cadence_and_retains_bounded_evidence()
    {
        var requestedMinutes = Environment.GetEnvironmentVariable("RTAIME_LOCAL_PLAYBACK_MINUTES");
        if (string.IsNullOrEmpty(requestedMinutes)) return;
        Assert.Contains(requestedMinutes, new[] { "10", "60" });
        Assert.True(OperatingSystem.IsWindows(), "Actual Windows Media Foundation playback is required.");
        Assert.True(ReferenceMediaTestAsset.IsRegressionEnabled, "Long playback requires real reference generation.");
        var minutes = int.Parse(Environment.GetEnvironmentVariable("RTAIME_LOCAL_PLAYBACK_MINUTES")!);
        using var asset = ReferenceMediaTestAsset.Generate(ReferenceMediaProfile.Long(minutes));
        var root = Path.Combine(Path.GetTempPath(), "rtaime-playback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runtimeEndpoint = "rtaime.baseline.runtime." + Guid.NewGuid().ToString("N");
        var controlEndpoint = "rtaime.baseline.control." + Guid.NewGuid().ToString("N");
        using var runtimeStop = new CancellationTokenSource();
        using var controlStop = new CancellationTokenSource();
        var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with {
            ListenEndpoint = controlEndpoint, RuntimeEndpoint = runtimeEndpoint, DurabilityRoot = root });
        var runtimeRun = runtime.RunAsync(runtimeStop.Token);
        var controlRun = control.RunAsync(controlStop.Token);
        var resources = new object[128];
        var sampleCount = 0;
        object? playback = null;
        object? finalTiming = null;
        object? finalSchedule = null;
        object? finalDecode = null;
        object? transport = null;
        object? transportAfterSeeks = null;
        string audioStatus = "UNVERIFIED";
        long? firstPrivate = null, lastPrivate = null;
        int? firstHandles = null, lastHandles = null;
        long? privateGrowth = null;
        int? handleGrowth = null;
        string? failure = null;
        object? runtimePerformance = null;
        double? clientOpenMilliseconds = null;
        var watch = Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await Until(() => control.Lifecycle.State == ControlHostProcessState.Ready, TimeSpan.FromSeconds(30));
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15)));
            var state = await client.SynchronizeAsync();
            var source = new MediaSourceId(Identity.Parse(state.Sources[0].Id));
            await using var deck = new MediaDeckController(client);
            var openStarted = Stopwatch.GetTimestamp();
            var opened = await deck.OpenAsync(asset.Path, source);
            clientOpenMilliseconds = Stopwatch.GetElapsedTime(openStarted).TotalMilliseconds;
            Assert.True(opened.IsLoaded, opened.Failure?.Message);
            Assert.True(opened.Probe!.Duration >= TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            await deck.ConfigurePlaybackAsync(false, MediaDeckEndBehavior.HoldLastFrame);
            await deck.PlayAsync();
            var playbackStarted = Stopwatch.GetTimestamp();
            var nextSample = TimeSpan.Zero;
            using var process = Process.GetCurrentProcess();
            while (Stopwatch.GetElapsedTime(playbackStarted) < TimeSpan.FromMinutes(minutes * 2 + 2))
            {
                Assert.False(runtimeRun.IsCompleted, runtime.Lifecycle.Detail);
                Assert.False(controlRun.IsCompleted, control.Lifecycle.Detail);
                var snapshot = runtime.MediaDeck!.Snapshot;
                Assert.NotEqual(MediaDeckState.Error, snapshot.State);
                var elapsed = Stopwatch.GetElapsedTime(playbackStarted);
                if (elapsed >= nextSample)
                {
                    process.Refresh();
                    var gc = GC.GetGCMemoryInfo();
                    if (elapsed >= TimeSpan.FromMinutes(2))
                    {
                        firstPrivate ??= process.PrivateMemorySize64; firstHandles ??= process.HandleCount;
                        lastPrivate = process.PrivateMemorySize64; lastHandles = process.HandleCount;
                    }
                    Assert.True(sampleCount < resources.Length, "Constant resource evidence capacity exhausted.");
                    resources[sampleCount++] = new { seconds = elapsed.TotalSeconds,
                        decodedFrame = snapshot.Transport?.Position.CurrentFrame,
                        program = runtime.ScheduleStatistics,
                        processHandles = process.HandleCount, privateBytes = process.PrivateMemorySize64,
                        managedBytes = GC.GetTotalMemory(false), heapBytes = gc.HeapSizeBytes,
                        fragmentedBytes = gc.FragmentedBytes,
                        lohBytesAfterLastGc = gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes : (long?)null,
                        allocatedBytes = GC.GetTotalAllocatedBytes(),
                        gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
                        io = ReadIo(process.Handle) };
                    nextSample += TimeSpan.FromSeconds(60);
                }
                if (snapshot.State == MediaDeckState.Ended) break;
                await Task.Delay(250);
            }
            var ended = runtime.MediaDeck!.Snapshot;
            transport = ended;
            playback = runtime.MediaDeck.PlaybackMeasurements;
            finalTiming = runtime.PipelineTiming;
            finalSchedule = runtime.ScheduleStatistics;
            finalDecode = runtime.MediaDeck!.DecodeSnapshot;
            runtimePerformance = runtime.Runtime!.Snapshot.Performance;
            Assert.Equal(MediaDeckState.Ended, ended.State);
            var measurement = runtime.MediaDeck.PlaybackMeasurements!.Value;
            Assert.True(measurement.DecodedFrames >= (ulong)(minutes * 60 * 50 - 4));
            Assert.True(measurement.DecodedAudioSamples > 0);
            audioStatus = measurement.MissingAudioFrames == 0 && measurement.AudioTimestampDiscontinuities == 0 &&
                measurement.MaximumAvTimestampDistanceMilliseconds <= 40 ? "PASS" : "FAIL";
            Assert.Equal("PASS", audioStatus);
            privateGrowth = lastPrivate - firstPrivate; handleGrowth = lastHandles - firstHandles;
            Assert.NotNull(privateGrowth); Assert.NotNull(handleGrowth);
            Assert.InRange(privateGrowth!.Value, long.MinValue, 512L * 1024 * 1024);
            Assert.InRange(handleGrowth!.Value, int.MinValue, 64);
            Assert.Equal(0UL, measurement.ReadFailures);
            Assert.Equal(0UL, measurement.VideoTimestampRegressions);
            Assert.True(runtime.ScheduleStatistics.CommittedBoundaries > 0);
            Assert.True(sampleCount >= minutes - 1);
            // Transport/failure exercises are separate from the uninterrupted-playback metrics.
            await deck.StopAsync();
            await deck.Timeline.SeekToFrameAsync(59);
            Assert.Equal(59, deck.Timeline.State.ConfirmedFrame);
            await deck.PlayAsync();
            await Task.Delay(250);
            await deck.PauseAsync();
            Assert.Equal(MediaDeckState.Paused, deck.Snapshot.State);
            await deck.Timeline.SeekToFrameAsync(61);
            Assert.Equal(61, deck.Timeline.State.ConfirmedFrame);
            await deck.PlayAsync();
            await Task.Delay(250);
            await deck.StopAsync();
            await Until(() => runtime.MediaDeck!.Snapshot.State == MediaDeckState.Ready, TimeSpan.FromSeconds(8));
            await deck.RefreshAsync();
            Assert.Equal(MediaDeckState.Ready, deck.Snapshot.State);
            Assert.Equal(0, deck.Snapshot.Transport!.Position.CurrentFrame);
            transportAfterSeeks = new { stop = deck.Snapshot, observations = runtime.MediaDeck!.PlaybackMeasurements };
            await deck.CloseAsync();
            Assert.True((await deck.OpenAsync(asset.Path, source)).IsLoaded);
            await deck.CloseAsync();
        }
        catch (Exception exception) { failure = exception.ToString(); throw; }
        finally
        {
            controlStop.Cancel();
            runtimeStop.Cancel();
            string? cleanupFailure = null;
            try
            {
                Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(30)));
                Assert.True(runtime.RuntimeDisposed);
            }
            catch (Exception exception) { cleanupFailure = exception.ToString(); }
            var evidence = Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_OUTPUT");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                var diagnostics = playback;
                File.WriteAllText(Path.Combine(evidence, "local-playback-baseline.json"), JsonSerializer.Serialize(new {
                    schemaVersion = "1.0", sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
                    sdk = Environment.GetEnvironmentVariable("RTAIME_BASELINE_SDK"),
                    qualificationClass = "ACTUAL_WINDOWS_RUNTIME_PLAYBACK; HOSTED_STORAGE_NOT_PHYSICAL_SSD_QUALIFICATION",
                    asset = asset.Manifest, assetBytes = new FileInfo(asset.Path).Length,
                    startedAt, elapsedSeconds = watch.Elapsed.TotalSeconds, os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(), decoder = "Windows Media Foundation source reader; installed MFT identity UNVERIFIED",
                    gpu = "Runtime default detected backend; physical adapter/driver UNVERIFIED",
                    storage = "Hosted runner volume; SSD class, cold cache and device latency UNVERIFIED",
                    inputReadLatency = "Native ReadSample CPU wall; file-only bytes and actual disk read latency UNVERIFIED",
                    resources = resources.Take(sampleCount).ToArray(), resourceCapacity = resources.Length,
                    playback, clientOpenMilliseconds, finalTiming, finalSchedule, finalDecode, runtimePerformance, transport, transportAfterSeeks, failure, cleanupFailure,
                    collectionStatus = failure is null && cleanupFailure is null ? "PASS" : "FAIL",
                    audioStatus, privateGrowthAfterTwoMinuteWarmup = privateGrowth, handleGrowthAfterTwoMinuteWarmup = handleGrowth,
                    productionReadiness = "UNVERIFIED; collection success does not waive audio FAIL or physical qualification",
                    audioQualification = "Inspect missing audio, timestamp discontinuities and A/V distance; nonzero findings are not waived as production-ready",
                    physicalStatus = "UNVERIFIED", failureStages = new[] { "probe", "decode", "transport", "scheduler", "GPU", "output" }
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("LOCAL PLAYBACK EVIDENCE: " + Path.Combine(evidence, "local-playback-baseline.json"));
                Console.WriteLine("LOCAL PLAYBACK OBSERVATIONS: " + JsonSerializer.Serialize(playback ?? diagnostics));
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Assert.Null(cleanupFailure);
        }
    }

    [Fact]
    public void Fault_matrix_classifies_missing_corrupt_truncated_locked_and_removed_files()
    {
        if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled) return;
        using var reference = ReferenceMediaTestAsset.Generate(ReferenceMediaProfile.Hd50);
        var root = Path.Combine(Path.GetTempPath(), "rtaime-playback-faults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new rtaime.Media.LocalMediaFileProvider();
        var observations = new List<object>(5);
        void Rejected(string name, string path, string code)
        {
            var started = Stopwatch.GetTimestamp();
            var result = provider.TryOpen(path, MediaSourceId.New());
            Assert.False(result.Succeeded); Assert.Equal(code, result.Failure?.Code);
            observations.Add(new { name, stage = "probe", code, milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
        try
        {
            var path = Path.Combine(root, "fault.mp4");
            Rejected("missing", path, "media.file.not_found");
            File.WriteAllBytes(path, Enumerable.Range(0, 128).Select(value => (byte)value).ToArray());
            Rejected("corrupt", path, "media.file.probe_failed");
            using (var input = File.OpenRead(reference.Path))
            using (var output = File.Create(path)) { var bytes = new byte[64]; Assert.Equal(64, input.Read(bytes)); output.Write(bytes); }
            Rejected("truncated-header", path, "media.file.probe_failed");
            File.Copy(reference.Path, path, true);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Rejected("unreadable-exclusive-lock", path, "media.file.unreadable");
            var open = provider.TryOpen(path, MediaSourceId.New()); Assert.True(open.Succeeded);
            using (var source = open.Source!) Assert.True(source.ReadNext(0).Succeeded);
            File.Delete(path);
            Rejected("removed-before-reload", path, "media.file.not_found");
        }
        finally
        {
            var evidence = Environment.GetEnvironmentVariable("RTAIME_REFERENCE_MEDIA_OUTPUT");
            if (!string.IsNullOrWhiteSpace(evidence))
                File.WriteAllText(Path.Combine(evidence, "local-playback-faults.json"),
                    JsonSerializer.Serialize(new { schemaVersion = "1.0", sourceCommit = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT"),
                        cases = observations, poorStorage = "UNVERIFIED; exclusive sharing denial is not slow-SSD emulation",
                        disappearanceDuringOpenHandle = "UNVERIFIED; Windows sharing may prevent physical removal" }));
            Directory.Delete(root, true);
        }
    }

    private static async Task Until(Func<bool> condition, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < limit) await Task.Delay(25);
        Assert.True(condition());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    private static object ReadIo(IntPtr handle) => GetProcessIoCounters(handle, out var counters)
        ? new { scope = "process aggregate, includes non-media I/O", counters.ReadOperations, counters.ReadBytes }
        : new { scope = "UNVERIFIED", ReadOperations = 0UL, ReadBytes = 0UL };
}
