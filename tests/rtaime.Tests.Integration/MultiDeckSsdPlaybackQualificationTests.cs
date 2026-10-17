// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Text.Json;
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
        if (!OperatingSystem.IsWindows() || !ReferenceMediaTestAsset.IsRegressionEnabled) return;
        using var asset = ReferenceMediaTestAsset.Generate(ReferenceMediaProfile.Hd50);
        using var other = LocalMediaTestAsset.ExtractReference1080p50();
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
            Assert.True((await a.OpenAsync(asset.Path, sourceA)).IsLoaded);
            Assert.True((await b.OpenAsync(other.Path, sourceB)).IsLoaded);
            await a.ConfigurePlaybackAsync(true, MediaDeckEndBehavior.Loop);
            await b.ConfigurePlaybackAsync(true, MediaDeckEndBehavior.Loop);
            await a.PlayAsync();
            await b.PlayAsync();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var memoryBefore = process.PrivateMemorySize64;
            var handlesBefore = process.HandleCount;
            var startSequence = runtime.Runtime!.Snapshot.NextSequenceNumber;
            var observations = new List<object>(128);
            var clock = Stopwatch.StartNew();
            var takes = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (observations.Count % 8 == 0)
                {
                    for (var take = 0; take < 2; take++)
                    {
                        var target = takes++ % 2 == 0 ? sourceB : sourceA;
                        Assert.True((await client.SelectPreviewAsync(target.ToString())).Accepted);
                        Assert.True((await client.CutPreviewAsync()).Accepted);
                    }
                }
                process.Refresh();
                var resources = runtime.MediaDeck!.Resources;
                var first = runtime.MediaDeck.GetDecodeSnapshot(sourceA)!.Value;
                var second = runtime.MediaDeck.GetDecodeSnapshot(sourceB)!.Value;
                Assert.Equal(2, resources.Reserved);
                Assert.InRange(first.QueuedFrames, 0, 8);
                Assert.InRange(second.QueuedFrames, 0, 8);
                observations.Add(new
                {
                    Milliseconds = clock.ElapsedMilliseconds, process.PrivateMemorySize64, process.HandleCount,
                    First = first, Second = second, Resources = resources,
                    Sequence = runtime.Runtime.Snapshot.NextSequenceNumber
                });
                await Task.Delay(250);
            }
            Assert.InRange(observations.Count, 1, 128);
            var finalSequence = runtime.Runtime.Snapshot.NextSequenceNumber;
            Assert.True(finalSequence > startSequence + 200);
            Assert.Null(runtime.MediaDeck!.GetSnapshot(sourceA).Failure);
            Assert.Null(runtime.MediaDeck.GetSnapshot(sourceB).Failure);
            process.Refresh();
            Assert.InRange(process.PrivateMemorySize64 - memoryBefore, long.MinValue, 512L * 1024 * 1024);
            Assert.InRange(process.HandleCount - handlesBefore, int.MinValue, 64);
            var source = Environment.GetEnvironmentVariable("RTAIME_SOURCE_COMMIT");
            Assert.Matches("^[0-9a-f]{40}$", source!);
            var firstPlayback = runtime.MediaDeck.GetPlaybackMeasurements(sourceA)!.Value;
            var secondPlayback = runtime.MediaDeck.GetPlaybackMeasurements(sourceB)!.Value;
            Assert.True(firstPlayback.DecodedFrames > 200);
            Assert.True(secondPlayback.DecodedFrames > 200);
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
            File.WriteAllText(path, JsonSerializer.Serialize(report));
            Console.WriteLine("DUAL DECK SOFTWARE OUTCOME: " + File.ReadAllText(path));
        }
        finally
        {
            controlStop.Cancel();
            var controlExit = await controlRun;
            runtimeStop.Cancel();
            var runtimeExit = await runtimeRun;
            Assert.Equal(ControlHostExitCode.Success, controlExit);
            Assert.Equal(RuntimeHostExitCode.Success, runtimeExit);
        }
        Assert.Equal(0, runtime.MediaDeck?.Resources.Reserved ?? 0);
        Assert.Equal(0, runtime.FinalRuntimeSnapshot!.ActiveGpuSurfaces);
    }
}
