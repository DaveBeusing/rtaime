// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Buffers.Binary;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.RuntimeHost;
using rtaime.Media.Contracts;
using rtaime.Core;
using rtaime.Operator;
using rtaime.Recording;
using Xunit;
namespace rtaime.Tests.Operator;

public sealed class AudioMixerRealHostTests
{
    [Fact]
    public async Task Mixer_drafts_cross_real_hosts_and_change_Runtime_stereo_samples_only_after_apply()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "rtaime-mixer-" + suffix);
        using var controlStop = new CancellationTokenSource();
        var options = RuntimeHostProcessOptions.Default;
        // Real Runtime engine and IPC; explicit boundaries isolate DSP/authoring correctness from scheduler qualification.
        await using var runtime = new V1RuntimeHostService(options.SourceAId, options.SourceBId, options.Format, new NullRecordingWriter());
        await using var runtimeIpc = new RuntimeHostIpcServer("rtaime.mixer.runtime." + suffix, () => runtime);
        await runtimeIpc.StartAsync();
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with {
            ListenEndpoint = "rtaime.mixer.control." + suffix, RuntimeEndpoint = runtimeIpc.Endpoint,
            DurabilityRoot = root, RuntimeRetryInterval = TimeSpan.FromMilliseconds(25) });
        var controlRun = control.RunAsync(controlStop.Token);
        try
        {
            await Until(() => Task.FromResult(control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true));
            using (var initialBoundary = runtime.ProcessNextBoundary()) { }
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport("rtaime.mixer.control." + suffix));
            await using var owner = new OperatorViewModel(client);
            owner.SynchronizeCommand.Execute(null);
            await Until(() => Task.FromResult(owner.IsConnected && !owner.IsBusy));
            Assert.True(owner.AudioMixer.CanEdit, owner.GlobalReadinessSummary + " · " + owner.RuntimeStatus);
            var source = owner.AudioMixer.Sources[0];
            var id = new MediaSourceId(Identity.Parse(source.SourceId));
            var samples = new float[1920];
            for (var i = 0; i < samples.Length; i += 2) { samples[i] = .4f; samples[i + 1] = -.2f; }
            runtime.SetExternalAudioInput(id, samples);
            source.FollowRoutedSource = false; source.Gain = .5;
            var baseline = client.Snapshot!.AudioProduction!.Configuration.Revision;
            Assert.Equal(baseline, runtime.Snapshot.AudioProduction!.Revision);
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError);
            using (var gained = runtime.ProcessNextBoundary())
            {
                Assert.True(gained.Audio.Emitted);
                Assert.Equal(.2, gained.Audio.LeftPeakLevel, 4); Assert.Equal(.1, gained.Audio.RightPeakLevel, 4);
                Assert.Equal(.2f, BinaryPrimitives.ReadSingleLittleEndian(gained.ProgramAudioPayload), 4);
                Assert.Equal(-.1f, BinaryPrimitives.ReadSingleLittleEndian(gained.ProgramAudioPayload.AsSpan(4)), 4);
            }
            source.ToggleMuteCommand.Execute(null);
            Assert.False(client.Snapshot.AudioProduction.Configuration.GetSource(id).Muted);
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError);
            using (var muted = runtime.ProcessNextBoundary()) Assert.Equal(0, muted.Audio.LeftPeakLevel);
            source.ToggleMuteCommand.Execute(null);
            var route = source.Assignments.Single(a => a.BusId == "program");
            route.ToggleCommand.Execute(null);
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError);
            Assert.Empty(client.Snapshot!.AudioProduction!.Configuration.GetSource(id).BusAssignments);
            using (var unrouted = runtime.ProcessNextBoundary()) Assert.Equal(0, unrouted.Audio.LeftPeakLevel);
            route.ToggleCommand.Execute(null); source.UnityDraft();
            Assert.True(await owner.AudioMixer.ApplySourceAsync(source), owner.LastError);
            using (var unity = runtime.ProcessNextBoundary()) Assert.Equal(.4, unity.Audio.LeftPeakLevel, 4);
            Assert.Equal(1, client.Snapshot!.AudioProduction!.Configuration.GetSource(id).Gain);
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
            await runtimeIpc.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Mixer host startup did not settle.");
            await Task.Delay(20);
        }
    }
    private sealed class NullRecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
