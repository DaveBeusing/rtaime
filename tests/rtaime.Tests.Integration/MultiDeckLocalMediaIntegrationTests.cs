// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class MultiDeckLocalMediaIntegrationTests
{
    [Fact]
    public void Same_asset_has_independent_source_transport_payloads_and_tally()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [a, b]);
        var openedA = decks.Open(new(MediaContractVersion.Current, a, asset.Path), Prepared(decks, a, 0));
        var openedB = decks.Open(new(MediaContractVersion.Current, b, asset.Path), Prepared(decks, b, 1));
        Assert.True(openedA.Probe is not null, openedA.Failure?.Message);
        Assert.True(openedB.Probe is not null, openedB.Failure?.Message);
        Assert.Equal(openedA.Probe!.AssetId, openedB.Probe!.AssetId);
        Assert.Equal(2, decks.Resources.Reserved);
        Assert.Throws<InvalidOperationException>(() => decks.ApplyTransport(
            new(MediaContractVersion.Current, openedA.Probe.AssetId, MediaTransportCommandKind.Play)));
        Command(decks, a, MediaTransportCommandKind.Play);
        Command(decks, b, MediaTransportCommandKind.Seek, 12);
        Command(decks, b, MediaTransportCommandKind.Play);
        using var frameA = Next(decks, a);
        using var frameB = Next(decks, b);
        Assert.Equal(a, frameA.Video!.SourceId);
        Assert.Equal(b, frameB.Video!.SourceId);
        Assert.Equal(0, frameA.Transport.Position.CurrentFrame);
        Assert.Equal(12, frameB.Transport.Position.CurrentFrame);
        Assert.False(frameA.AudioPayload.IsEmpty);
        Assert.False(frameB.AudioPayload.IsEmpty);
        Assert.True(frameB.Video.Timing.PresentationTimestamp > frameA.Video.Timing.PresentationTimestamp);
        decks.ObserveProgramSource(a);
        Assert.True(decks.GetSnapshot(a).Transport!.IsOnProgram);
        Assert.False(decks.GetSnapshot(b).Transport!.IsOnProgram);
        decks.ObserveProgramSource(b);
        Assert.False(decks.GetSnapshot(a).Transport!.IsOnProgram);
        Assert.True(decks.GetSnapshot(b).Transport!.IsOnProgram);
        Command(decks, a, MediaTransportCommandKind.Stop);
        Assert.Equal(0, decks.GetSnapshot(a).Transport!.Position.CurrentFrame);
        Assert.Equal(12, decks.GetSnapshot(b).Transport!.Position.CurrentFrame);
        var replacement = decks.Open(new(MediaContractVersion.Current, b, asset.Path), Prepared(decks, b, 1));
        Assert.Equal("runtime.media_deck.source_active", replacement.Failure?.Code);
        Assert.Equal(12, decks.GetSnapshot(b).Transport!.Position.CurrentFrame);
        decks.Close(a);
        Assert.Equal(1, decks.Resources.Reserved);
        Assert.Equal(b, decks.GetSnapshot(b).SourceId);
        decks.Dispose();
        Assert.Equal(0, decks.Resources.Reserved);
    }

    [Fact]
    public void Configured_capacity_rejects_active_eviction_and_evicts_only_inactive_prepared_deck()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 1, [a, b]);
        Assert.NotNull(decks.Open(new(MediaContractVersion.Current, a, asset.Path), Prepared(decks, a, 0)).Probe);
        decks.ObserveProgramSource(a);
        var rejected = decks.Open(new(MediaContractVersion.Current, b, asset.Path), Prepared(decks, b, 1));
        Assert.Equal("runtime.media_deck.capacity", rejected.Failure?.Code);
        Assert.Equal(new LocalMediaDeckResources(1, 1, 1, 0), decks.Resources);
        decks.ObserveProgramSource(null);
        Command(decks, a, MediaTransportCommandKind.Pause);
        var accepted = decks.Open(new(MediaContractVersion.Current, b, asset.Path), Prepared(decks, b, 1));
        Assert.NotNull(accepted.Probe);
        Assert.Equal(MediaDeckState.Unloaded, decks.GetSnapshot(a).State);
        Assert.Equal(new LocalMediaDeckResources(1, 1, 1, 1), decks.Resources);
        var probe = decks.Probe(asset.Path, MediaAssetId.New());
        Assert.False(probe.Succeeded);
        Assert.Equal(2, decks.Resources.Rejected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Decoder_capacity_is_bounded_to_approved_range(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, capacity));

    [Fact]
    public void Independent_loop_seek_and_stop_fence_previous_payload_generation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [a, b]);
        var first = decks.Open(new(MediaContractVersion.Current, a, asset.Path), Prepared(decks, a, 0));
        var second = decks.Open(new(MediaContractVersion.Current, b, asset.Path), Prepared(decks, b, 1));
        Assert.NotNull(first.Probe);
        Assert.NotNull(second.Probe);
        decks.ApplyTransport(new(MediaContractVersion.Current, first.Probe!.AssetId,
            MediaTransportCommandKind.ConfigurePlayback, autoPlayOnProgram: true,
            endBehavior: MediaDeckEndBehavior.Loop, inPointFrame: 5, outPointFrame: 7) { SourceId = a });
        Command(decks, b, MediaTransportCommandKind.Seek, 20);
        Command(decks, b, MediaTransportCommandKind.Play);
        decks.ObserveProgramSource(a);
        foreach (var expected in new long[] { 5, 6, 7, 5, 6, 7, 5 })
        {
            using var frame = Next(decks, a);
            Assert.Equal(expected, frame.Transport.Position.CurrentFrame);
            Assert.Equal(a, frame.Video!.SourceId);
        }
        using (var frame = Next(decks, b))
            Assert.Equal(20, frame.Transport.Position.CurrentFrame);
        Command(decks, a, MediaTransportCommandKind.Stop);
        Command(decks, a, MediaTransportCommandKind.Seek, 30);
        Command(decks, a, MediaTransportCommandKind.ConfigurePlayback, configure: true);
        Command(decks, a, MediaTransportCommandKind.Play);
        using var sought = Next(decks, a);
        Assert.Equal(30, sought.Transport.Position.CurrentFrame);
        Assert.Equal(20, decks.GetSnapshot(b).Transport!.Position.CurrentFrame);
    }

    private static void Command(LocalMediaDeckRuntimeService decks, MediaSourceId source,
        MediaTransportCommandKind kind, long? frame = null, bool configure = false)
    {
        var probe = decks.GetSnapshot(source).Probe!;
        var command = configure
            ? new MediaTransportCommand(MediaContractVersion.Current, probe.AssetId, kind,
                autoPlayOnProgram: true, endBehavior: MediaDeckEndBehavior.HoldLastFrame)
            : new MediaTransportCommand(MediaContractVersion.Current, probe.AssetId, kind, frame);
        var result = decks.ApplyTransport(command with { SourceId = source });
        Assert.True(result.Succeeded, result.Failure?.Message);
    }
    private static LocalMediaRuntimeBoundaryResult Next(LocalMediaDeckRuntimeService decks, MediaSourceId source)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(6))
        {
            var snapshot = decks.ProcessBoundary(source, out var frame);
            Assert.NotEqual(MediaDeckState.Error, snapshot.State);
            if (frame is { Succeeded: true }) return frame;
            frame?.Dispose();
            Thread.Sleep(1);
        }
        throw new TimeoutException("Deck did not admit its next prepared frame.");
    }
    private static PreparedExecutionContract Prepared(LocalMediaDeckRuntimeService decks, MediaSourceId source, int route)
    {
        var provider = decks.ProviderDescriptor;
        var capability = provider.Capabilities.Single(c => c.Kind == "media.route");
        var resource = provider.Resources.Where(r => r.Kind == "media.route").ElementAt(route);
        return new(RuntimeContractVersion.Current, PreparedExecutionId.New(),
            new AuthoritySnapshotReference(source.Value, new Revision(1)), new Generation(1),
            [new PreparedExecutionBinding(source.Value, capability.CapabilityId, resource, source, null)]);
    }
}
