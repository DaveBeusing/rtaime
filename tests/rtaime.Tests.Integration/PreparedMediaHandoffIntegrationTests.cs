// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using System.Security.Cryptography;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class PreparedMediaHandoffIntegrationTests
{
    [Theory]
    [InlineData("seek")]
    [InlineData("close")]
    [InlineData("cancel")]
    [InlineData("replaced-ticket")]
    [InlineData("wrong-epoch")]
    public void Changed_resident_evidence_never_enters_the_authorized_commit_callback(string change)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New(); var b = MediaSourceId.New();
        using var deck = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [a, b]);
        deck.ObserveProgramSource(a);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        var opened = deck.Preload(new(MediaContractVersion.Current, b, asset.Path), Prepared(deck, b), hash);
        Assert.Equal(MediaDeckState.Ready, opened.State);
        var candidate = deck.PrepareLoadedNext(b, opened.Probe!.AssetId);
        Assert.Equal(1, deck.Resources.Reserved);
        Assert.Equal(8, deck.GetDecodeSnapshot(b)!.Value.Capacity);
        switch (change)
        {
            case "seek":
                deck.ApplyTransport(new(MediaContractVersion.Current, opened.Probe.AssetId, MediaTransportCommandKind.Seek, 4) { SourceId = b });
                break;
            case "close": deck.Close(b); break;
            case "cancel": deck.CancelPreparation(candidate.TicketId); break;
            case "replaced-ticket": candidate = candidate with { TicketId = Guid.NewGuid().ToString("N") }; break;
            case "wrong-epoch": candidate = candidate with { RuntimeEpoch = "old-runtime" }; break;
        }
        var commits = 0;
        Assert.Throws<InvalidOperationException>(() =>
            deck.WithPreparedNext(candidate, true, () => ++commits, _ => true));
        Assert.Equal(0, commits);
    }

    [Fact]
    public void Preroll_retains_exact_identity_IN_OUT_and_single_use_without_freeing_its_owner()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New(); var b = MediaSourceId.New();
        using var deck = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [a, b]);
        deck.ObserveProgramSource(a);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        var opened = deck.Preload(new(MediaContractVersion.Current, b, asset.Path), Prepared(deck, b), hash);
        Assert.Equal(MediaDeckState.Ready, opened.State);
        Assert.True(deck.ApplyTransport(new(MediaContractVersion.Current, opened.Probe!.AssetId,
            MediaTransportCommandKind.ConfigurePlayback, autoPlayOnProgram: true,
            endBehavior: MediaDeckEndBehavior.HoldLastFrame, inPointFrame: 5, outPointFrame: 30) { SourceId = b }).Succeeded);
        var candidate = deck.PrepareLoadedNext(b, opened.Probe.AssetId);
        Assert.Equal(5, candidate.PositionFrame); Assert.Equal(30, candidate.EndFrame);
        Assert.Equal(hash, candidate.FingerprintSha256); Assert.Equal(b.ToString(), candidate.SourceId);
        Assert.True(deck.GetDecodeSnapshot(b)!.Value.QueuedFrames >= 3);
        Assert.Throws<IOException>(() => { using var write = new FileStream(asset.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        Assert.True(deck.WithPreparedNext(candidate, false, () => true, value => value));
        Assert.True(deck.WithPreparedNext(candidate, true, () => true, value => value));
        Assert.Throws<InvalidOperationException>(() => deck.WithPreparedNext(candidate, true, () => true, value => value));
        Assert.Equal(1, deck.Resources.Reserved);
        deck.Close(b);
        Assert.Equal(0, deck.Resources.Reserved);
    }

    [Fact]
    public void Monotonic_expiry_blocks_take_even_if_the_UTC_clock_moves_backwards()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New(); var b = MediaSourceId.New();
        using var deck = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [a, b]);
        var clock = new DeadlineClock();
        deck.PreparationClock = clock;
        deck.ObserveProgramSource(a);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(asset.Path)));
        var opened = deck.Preload(new(MediaContractVersion.Current, b, asset.Path), Prepared(deck, b), hash);
        Assert.Equal(MediaDeckState.Ready, opened.State);
        var candidate = deck.PrepareLoadedNext(b, opened.Probe!.AssetId);
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(31));
        Assert.True(clock.GetUtcNow() < candidate.ExpiresAtUtc);
        var committed = false;
        Assert.Throws<InvalidOperationException>(() => deck.WithPreparedNext(candidate, true,
            () => committed = true, value => value));
        Assert.False(committed);
        Assert.Equal(1, deck.Resources.Reserved);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        public void AdvanceMonotonic(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    [Fact]
    public void Wrong_hash_and_Program_target_are_rejected_without_a_decoder_reservation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New(); var b = MediaSourceId.New();
        using var deck = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [a, b]);
        deck.ObserveProgramSource(a);
        var onProgram = deck.Preload(new(MediaContractVersion.Current, a, asset.Path), Prepared(deck, a), new string('0', 64));
        Assert.Equal("runtime.media_preload.source_on_program", onProgram.Failure?.Code);
        var changed = deck.Preload(new(MediaContractVersion.Current, b, asset.Path), Prepared(deck, b), new string('0', 64));
        Assert.Equal("runtime.media_preload.identity_changed", changed.Failure?.Code);
        Assert.Equal(0, deck.Resources.Reserved);
        Assert.Equal(MediaDeckState.Unloaded, deck.GetSnapshot(a).State);
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "seek")]
    [InlineData(false, "catalogue")]
    [InlineData(false, "authority")]
    [InlineData(false, "hold")]
    public async Task Existing_rundown_PREPARE_stages_B_while_A_plays_then_GO_uses_guarded_CUT_or_DISSOLVE(bool dissolve, string invalidate)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var root = Path.Combine(Path.GetTempPath(), "rtaime-prepared-media-" + Guid.NewGuid().ToString("N"));
        var runtimeEndpoint = "rtaime.test.preload.runtime." + Guid.NewGuid().ToString("N");
        var controlEndpoint = "rtaime.test.preload.control." + Guid.NewGuid().ToString("N");
        using var runtimeStop = new CancellationTokenSource(); using var controlStop = new CancellationTokenSource();
        var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
        var control = new ControlHostProcess(ControlHostProcessOptions.Default with
        {
            ListenEndpoint = controlEndpoint, RuntimeEndpoint = runtimeEndpoint, DurabilityRoot = root,
            RuntimeRetryInterval = TimeSpan.FromMilliseconds(25), RequestTimeout = TimeSpan.FromSeconds(10)
        });
        var runtimeRun = runtime.RunAsync(runtimeStop.Token); var controlRun = control.RunAsync(controlStop.Token);
        try
        {
            await Until(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);
            var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12)));
            var initial = await client.SynchronizeAsync();
            var a = new MediaSourceId(initial.Production.Routing.ProgramSourceId.Value);
            var b = new MediaSourceId(Identity.Parse(initial.Sources.Single(source => source.Id != a.ToString()).Id));
            var imported = await client.ImportMediaAssetsAsync([asset.Path]);
            Assert.True(imported.Succeeded, imported.Items.FirstOrDefault()?.Failure?.Message);
            var catalogue = await client.GetMediaAssetCatalogAsync();
            var id = catalogue.Assets.Single().AssetId;
            var openedA = await client.OpenMediaDeckAsync(asset.Path, a);
            Assert.True(openedA.IsLoaded, openedA.Failure?.Message);
            await client.ApplyMediaDeckTransportAsync(new(MediaContractVersion.Current, openedA.Probe!.AssetId,
                MediaTransportCommandKind.ConfigurePlayback, autoPlayOnProgram: true,
                endBehavior: MediaDeckEndBehavior.Loop) { SourceId = a });
            await client.ApplyMediaDeckTransportAsync(new(MediaContractVersion.Current, openedA.Probe.AssetId,
                MediaTransportCommandKind.Play) { SourceId = a });
            var item = new RundownMediaItem(RundownItemId.New(), "Next B", id.Value,
                new ProductionSourceId(b.Value), new RundownTransition(dissolve ? RundownTransitionKind.Dissolve : RundownTransitionKind.Cut,
                    dissolve ? 6u : null));
            var rundown = new RundownDefinition(RundownContractVersion.Current, RundownId.New(), "Resident next", [item]);
            var old = await client.GetRundownSnapshotAsync();
            await client.SaveRundownAsync(rundown, old.StorageVersion);
            var prepared = await client.PrepareRundownItemAsync(item.ItemId);
            Assert.Equal(RundownExecutionState.Prepared, prepared.Execution.State);
            Assert.Equal("NEXT_READY", prepared.Execution.PreparedMediaState);
            Assert.Equal(b.ToString(), prepared.Execution.PreparedMediaSourceId);
            Assert.Equal(a, runtime.Runtime!.CommittedProgramSourceId);
            Assert.Equal(2, runtime.MediaDeck!.Resources.Reserved);
            var generation = runtime.MediaDeck.GetDecodeSnapshot(b)!.Value.Generation;
            if (invalidate == "hold")
            {
                var held = await client.HoldRundownAsync();
                Assert.Equal(RundownExecutionState.Held, held.Execution.State);
                Assert.Null(held.Execution.PreparedMediaState);
                var resynced = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint,
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(12)));
                await resynced.SynchronizeAsync();
                Assert.Equal(RundownExecutionState.Held, (await resynced.GetRundownSnapshotAsync()).Execution.State);
                Assert.Equal(a, runtime.Runtime.CommittedProgramSourceId);
                return;
            }
            if (invalidate == "seek")
                await client.ApplyMediaDeckTransportAsync(new(MediaContractVersion.Current, id, MediaTransportCommandKind.Seek, 4) { SourceId = b });
            if (invalidate == "catalogue") Assert.True((await client.RemoveMediaAssetAsync(id)).Succeeded);
            if (invalidate == "authority") Assert.True((await client.SelectPreviewAsync(b.ToString())).Accepted);
            var revision = control.Control!.State.Revision;
            var go = await client.GoRundownAsync();
            if (invalidate != "none")
            {
                Assert.Equal(RundownExecutionState.Failed, go.Execution.State);
                Assert.Equal(a, runtime.Runtime.CommittedProgramSourceId);
                Assert.Equal(revision, control.Control.State.Revision);
                return;
            }
            Assert.NotEqual(RundownExecutionState.Failed, go.Execution.State);
            Assert.Equal(b, runtime.Runtime.CommittedProgramSourceId);
            Assert.Equal(generation, runtime.MediaDeck.GetDecodeSnapshot(b)!.Value.Generation);
            Assert.Equal(2, runtime.MediaDeck.Resources.Reserved);
            Assert.Equal(revision.Value + 2, control.Control!.State.Revision.Value);
            var sequence = runtime.Runtime.Snapshot.NextSequenceNumber;
            await Until(() => runtime.Runtime.Snapshot.NextSequenceNumber > sequence + 3);
            var deckB = await client.GetMediaDeckSnapshotAsync(b);
            Assert.True(deckB.Transport!.IsOnProgram);
            Assert.True(deckB.Transport.Position.CurrentFrame > 0);
            var playback = runtime.MediaDeck.GetPlaybackMeasurements(b)!.Value;
            Assert.True(playback.DecodedAudioSamples > 0);
            Assert.Equal(0UL, playback.AudioTimestampDiscontinuities);
            Assert.Equal(0UL, playback.ReadFailures);
        }
        finally
        {
            controlStop.Cancel();
            try { Assert.Equal(ControlHostExitCode.Success, await controlRun.WaitAsync(TimeSpan.FromSeconds(30))); }
            finally
            {
                runtimeStop.Cancel();
                Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(30)));
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }

    private static PreparedExecutionContract Prepared(LocalMediaDeckRuntimeService deck, MediaSourceId source)
    {
        var capability = deck.ProviderDescriptor.Capabilities.Single(c => c.Kind == "media.route");
        var resource = deck.ProviderDescriptor.Resources.First(r => r.Kind == "media.route");
        return new(RuntimeContractVersion.Current, PreparedExecutionId.New(),
            new AuthoritySnapshotReference(source.Value, new Revision(1)), new Generation(1),
            [new PreparedExecutionBinding(source.Value, capability.CapabilityId, resource, source, null)]);
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
        Assert.True(condition());
    }
}
