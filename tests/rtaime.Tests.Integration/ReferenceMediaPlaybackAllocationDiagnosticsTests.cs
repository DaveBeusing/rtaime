// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ReferenceMediaPlaybackAllocationDiagnosticsTests
{
    [Fact]
    public async Task Short_allocation_probe_collects_sampled_types_without_qualifying_long_playback()
    {
        if (Environment.GetEnvironmentVariable("RTAIME_LOCAL_ALLOCATION_DIAGNOSTICS") != "1") return;
        Assert.True(OperatingSystem.IsWindows());
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var root = Path.Combine(Path.GetTempPath(), "rtaime-allocation-" + Guid.NewGuid().ToString("N"));
        var runtimeEndpoint = "rtaime.allocation.runtime." + Guid.NewGuid().ToString("N");
        var controlEndpoint = "rtaime.allocation.control." + Guid.NewGuid().ToString("N");
        using var runtimeStop = new CancellationTokenSource();
        using var controlStop = new CancellationTokenSource();
        using var allocations = new LocalPlaybackAllocationListener();
        var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with {
            ListenEndpoint = controlEndpoint, RuntimeEndpoint = runtimeEndpoint, DurabilityRoot = root });
        var runtimeRun = runtime.RunAsync(runtimeStop.Token);
        var controlRun = control.RunAsync(controlStop.Token);
        try
        {
            var deadline = Environment.TickCount64 + 30_000;
            while (control.Lifecycle.State != ControlHostProcessState.Ready)
            {
                Assert.True(Environment.TickCount64 < deadline);
                await Task.Delay(25);
            }
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15)));
            var state = await client.SynchronizeAsync();
            await using var deck = new MediaDeckController(client);
            var source = new MediaSourceId(Identity.Parse(state.Sources[0].Id));
            Assert.True((await deck.OpenAsync(asset.Path, source)).IsLoaded);
            await deck.ConfigurePlaybackAsync(false, MediaDeckEndBehavior.Loop);
            if (deck.Snapshot.Transport?.State != MediaTransportState.Playing) await deck.PlayAsync();
            await Task.Delay(TimeSpan.FromSeconds(60));
            Assert.False(runtimeRun.IsCompleted);
            Assert.NotEqual(MediaDeckState.Error, runtime.MediaDeck!.Snapshot.State);
            Console.WriteLine("SAMPLED LOH TYPE AND SIZE DIAGNOSTIC: " + JsonSerializer.Serialize(allocations.Snapshot()));
            Console.WriteLine("DIAGNOSTIC ONLY: short loop playback is not ten-minute or physical SSD qualification.");
        }
        finally
        {
            controlStop.Cancel();
            runtimeStop.Cancel();
            Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.True(runtime.RuntimeDisposed);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
