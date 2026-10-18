// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.IO.Compression;
using System.Windows.Input;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.RuntimeHost;
using rtaime.Media.Contracts;
using rtaime.Core;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class LiveConsoleRealHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Imported_media_preview_take_scene_and_rundown_follow_use_real_hosts(bool dissolve)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "rtaime-live-" + suffix);
        Directory.CreateDirectory(root);
        var assetPath = Path.Combine(root, "reference.mp4");
        using (var input = File.OpenRead(Path.Combine(RepositoryRoot(), "tests", "TestAssets", "media", "reference-1080p50-h264-aac-1s.mp4.gz")))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var output = File.Create(assetPath)) gzip.CopyTo(output);
        var runtimeEndpoint = "rtaime.live.runtime." + suffix;
        var controlEndpoint = "rtaime.live.control." + suffix;
        using var runtimeStop = new CancellationTokenSource();
        using var controlStop = new CancellationTokenSource();
        var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with {
            ListenEndpoint = controlEndpoint, RuntimeEndpoint = runtimeEndpoint,
            DurabilityRoot = Path.Combine(root, "state"), RuntimeRetryInterval = TimeSpan.FromMilliseconds(25) });
        var runtimeRun = runtime.RunAsync(runtimeStop.Token);
        var controlRun = control.RunAsync(controlStop.Token);
        try
        {
            await Until(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12)));
            await using var owner = new OperatorViewModel(client, runtimeReadiness: new FunctionalReadiness());
            await Execute(owner.SynchronizeCommand, () => owner.IsBusy);
            Assert.True(owner.IsConnected);
            await using var deck = new MediaDeckViewModel(new MediaDeckController(client), () => null, () => null);
            using var pool = new MediaPoolInspectorViewModel(owner, deck);
            var rundown = new RundownViewModel(client, owner, pool);
            var show = new ShowControlViewModel(client, owner);
            using var binding = new LiveConsoleBinding(owner, rundown, show);
            var initialProgram = owner.ProgramSourceId;
            var next = owner.Sources.Single(source => source.Id != initialProgram);
            Assert.True((await client.ImportMediaAssetsAsync([assetPath])).Succeeded);
            var catalog = await client.GetMediaAssetCatalogAsync();
            var asset = Assert.Single(catalog.Assets);
            var opened = await client.OpenMediaDeckAsync(assetPath, new MediaSourceId(Identity.Parse(next.Id)));
            Assert.True(opened.IsLoaded, opened.Failure?.Message);
            await Execute(owner.SynchronizeCommand, () => owner.IsBusy);
            owner.SelectedSource = owner.Sources.Single(source => source.Id == next.Id);
            Assert.Equal(initialProgram, owner.ProgramSourceId);
            await Execute(owner.SetPreviewCommand, () => owner.IsBusy);
            Assert.Equal(next.Id, owner.PreviewSourceId);
            owner.TransitionFrames = 2;
            await Execute(dissolve ? owner.DissolveCommand : owner.CutCommand, () => owner.IsBusy);
            Assert.Equal(next.Id, runtime.Runtime!.CommittedProgramSourceId.ToString());

            owner.SelectedScene = owner.Scenes.First(scene => scene.ProgramSourceId == initialProgram);
            await Execute(owner.ActivateSceneCommand, () => owner.IsBusy);
            Assert.Equal(owner.SelectedScene!.Id, owner.ActiveSceneId);
            Assert.Equal(initialProgram, runtime.Runtime.CommittedProgramSourceId.ToString());

            var rejected = await client.ActivateSceneAsync(Guid.NewGuid().ToString());
            Assert.False(rejected.Accepted);
            Assert.Equal(initialProgram, runtime.Runtime.CommittedProgramSourceId.ToString());

            var stopped = await client.ApplyMediaDeckTransportAsync(new(MediaContractVersion.Current,
                opened.Probe!.AssetId, MediaTransportCommandKind.Stop) { SourceId = new MediaSourceId(Identity.Parse(next.Id)) });
            Assert.Null(stopped.Failure);
            Assert.True(stopped.IsLoaded);
            var item = new RundownMediaItem(RundownItemId.New(), "Prepared clip", asset.AssetId.Value,
                new ProductionSourceId(Identity.Parse(next.Id)), dissolve ? RundownTransition.Dissolve(2) : RundownTransition.Cut);
            var previous = await client.GetRundownSnapshotAsync();
            await client.SaveRundownAsync(new(RundownContractVersion.Current, RundownId.New(), "Live", [item]), previous.StorageVersion);
            await rundown.RefreshAsync();
            await Execute(rundown.PrepareCommand, () => rundown.IsBusy);
            Assert.True(rundown.State == "PREPARED", rundown.State + " · " + rundown.Failure + " · " + rundown.Detail);
            Assert.Contains("NEXT_READY", rundown.Items[0].Readiness);
            Assert.Equal(initialProgram, runtime.Runtime.CommittedProgramSourceId.ToString());
            await Execute(rundown.GoCommand, () => rundown.IsBusy);
            Assert.Equal(next.Id, runtime.Runtime.CommittedProgramSourceId.ToString());
            await UntilAsync(async () => (await client.GetRundownSnapshotAsync()).Execution.State != RundownExecutionState.Executing);

            var hold = new RundownHoldItem(RundownItemId.New(), "Timed hold", 2,
                followAction: RundownFollowAction.AutoGoNextAfterFrames(500));
            var sceneItem = new RundownSceneItem(RundownItemId.New(), "After hold", new SceneId(Identity.Parse(owner.SelectedScene!.Id)));
            previous = await client.GetRundownSnapshotAsync();
            await client.SaveRundownAsync(new(RundownContractVersion.Current, RundownId.New(), "Follow", [hold, sceneItem]), previous.StorageVersion);
            await rundown.RefreshAsync();
            await Execute(rundown.PrepareCommand, () => rundown.IsBusy);
            await Execute(rundown.GoCommand, () => rundown.IsBusy);
            owner.StartAudioMetering();
            await Until(() => rundown.PendingNext == "After hold" && rundown.FollowCountdown.Contains("remaining", StringComparison.Ordinal));
            await Execute(rundown.HoldCommand, () => rundown.IsBusy);
            Assert.Equal("HELD", rundown.State);
            var held = await client.GetRundownSnapshotAsync();
            Assert.False(held.Execution.AutoAdvanceArmed);
            Assert.Null(held.Execution.PendingNextItemId);
            Assert.Equal(next.Id, runtime.Runtime.CommittedProgramSourceId.ToString());
            Assert.Equal("NONE", rundown.PendingNext);

            // An independent connection sees the same held state; observation never replays GO.
            var reconnected = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint));
            await reconnected.SynchronizeAsync();
            var confirmed = await reconnected.GetRundownSnapshotAsync();
            Assert.Equal(held.Execution.Revision, confirmed.Execution.Revision);
            Assert.Equal(RundownExecutionState.Held, confirmed.Execution.State);
        }
        finally
        {
            controlStop.Cancel();
            try { Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(30))); }
            finally {
                runtimeStop.Cancel();
                Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(30)));
                Directory.Delete(root, true);
            }
        }
    }
    private static async Task Execute(ICommand command, Func<bool> busy)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await Until(() => !busy());
    }
    private static Task Until(Func<bool> predicate) => UntilAsync(() => Task.FromResult(predicate()));
    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await predicate()) {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Live console condition did not settle.");
            await Task.Delay(25);
        }
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root was not found.");
    }
    // Functional UI admission only; physical I/O and performance remain unverified.
    private sealed class FunctionalReadiness : IRuntimeReadinessService
    {
        public RuntimeReadinessSnapshot Current { get; } = new(RuntimeReadinessState.Degraded,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new("live.functional.fixture", "Live", ReadinessReasonSeverity.Warning,
                "Functional IPC test; physical output is unqualified.", false)],
            false, RuntimePerformanceVerificationSnapshot.Unverified);
        public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed { add { } remove { } }
        public void Observe(RuntimeReadinessObservation observation) { }
        public void InvalidatePerformance(string detail) { }
    }
}
