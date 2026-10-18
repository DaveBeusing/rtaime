// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.RuntimeHost;
using rtaime.Media.Contracts;
using rtaime.Core;
using rtaime.Operator;
using Xunit;
namespace rtaime.Tests.Operator;

public sealed class AudioMixerRealHostTests
{
    [Fact]
    public async Task Mixer_drafts_cross_real_hosts_and_change_Runtime_stereo_samples_only_after_apply()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "rtaime-mixer-" + suffix);
        using var runtimeStop = new CancellationTokenSource();
        using var controlStop = new CancellationTokenSource();
        var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = "rtaime.mixer.runtime." + suffix });
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with {
            ListenEndpoint = "rtaime.mixer.control." + suffix, RuntimeEndpoint = "rtaime.mixer.runtime." + suffix,
            DurabilityRoot = root, RuntimeRetryInterval = TimeSpan.FromMilliseconds(25) });
        var runtimeRun = runtime.RunAsync(runtimeStop.Token);
        var controlRun = control.RunAsync(controlStop.Token);
        try
        {
            await Until(() => Task.FromResult(control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true));
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport("rtaime.mixer.control." + suffix));
            await using var owner = new OperatorViewModel(client);
            owner.SynchronizeCommand.Execute(null);
            await Until(async () => {
                if (!owner.IsBusy) owner.SynchronizeCommand.Execute(null);
                await Task.Delay(20);
                return owner.IsConnected && !owner.IsBusy && owner.AudioMixer.CanEdit;
            }, () => owner.GlobalReadinessSummary + " · " + owner.LastError);
            var source = owner.AudioMixer.Sources[0];
            var id = new MediaSourceId(Identity.Parse(source.SourceId));
            var samples = new float[1920];
            for (var i = 0; i < samples.Length; i += 2) { samples[i] = .4f; samples[i + 1] = -.2f; }
            runtime.Runtime!.SetExternalAudioInput(id, samples);
            source.FollowRoutedSource = false;
            source.Gain = .5;
            var baseline = client.Snapshot!.AudioProduction!.Configuration.Revision;
            Assert.Equal(baseline, runtime.Runtime.Snapshot.AudioProduction!.Revision);
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError + " · " + owner.GlobalReadinessSummary);
            await Until(async () => {
                var snapshot = await client.SynchronizeAsync();
                return Math.Abs(snapshot.AudioProduction!.Buses.Single(b => b.BusId == "program").LeftPeak - .2) < .0001;
            });
            Assert.Equal(.1, client.Snapshot!.AudioProduction!.Buses.Single(b => b.BusId == "program").RightPeak, 4);
            source.ToggleMuteCommand.Execute(null);
            Assert.False(client.Snapshot.AudioProduction.Configuration.GetSource(id).Muted);
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError + " · " + owner.GlobalReadinessSummary);
            await Until(async () => (await client.SynchronizeAsync()).AudioProduction!.Buses.Single(b => b.BusId == "program").LeftPeak == 0);
            source.ToggleMuteCommand.Execute(null);
            var route = source.Assignments.Single(a => a.BusId == "program");
            route.ToggleCommand.Execute(null);
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError + " · " + owner.GlobalReadinessSummary);
            Assert.Empty(client.Snapshot!.AudioProduction!.Configuration.GetSource(id).BusAssignments);
            route.ToggleCommand.Execute(null); source.UnityDraft();
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError + " · " + owner.GlobalReadinessSummary);
            await Until(async () => Math.Abs((await client.SynchronizeAsync()).AudioProduction!.Buses.Single(b => b.BusId == "program").LeftPeak - .4) < .0001);
            Assert.Equal(1, client.Snapshot!.AudioProduction!.Configuration.GetSource(id).Gain);
            // A different real writer advances the revision. The retained draft must reject and remain editable.
            var competing = new OperatorControlClient(new NamedPipeOperatorControlTransport("rtaime.mixer.control." + suffix));
            var current = (await competing.SynchronizeAsync()).AudioProduction!.Configuration;
            await competing.SetAudioProductionAsync(new(current.Revision + 1, current.Buses, current.Sources, current.Crossfade, current.Ducking, current.ClipStrategy));
            source.Gain = .25;
            Assert.False(await owner.AudioMixer.ApplySourceAsync(source));
            Assert.Equal("REJECTED", source.DraftState); Assert.Equal(.25, source.Gain);
        }
        finally
        {
            controlStop.Cancel(); Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(10)));
            runtimeStop.Cancel(); Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(10)));
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static async Task Until(Func<Task<bool>> condition, Func<string>? detail = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Mixer host condition did not settle: " + detail?.Invoke());
            await Task.Delay(20);
        }
    }
}
