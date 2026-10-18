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
        Assert.Equal(2, decks.Resources.Reserved); // The caller still owns frameA's old pool.
        Assert.Equal(1, decks.ResourceSnapshot.RetainedPoolOwners);
        frameA.Dispose();
        Assert.Equal(1, decks.Resources.Reserved);
        Assert.Equal(b, decks.GetSnapshot(b).SourceId);
        decks.Dispose();
        Assert.Equal(1, decks.Resources.Reserved); // frameB keeps its pool charged beyond shutdown.
        frameB.Dispose();
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
        using (var before = Next(decks, a))
            Assert.Equal(a, before.Video!.SourceId);
        var programProbe = decks.GetSnapshot(a).Probe;
        // Capacity denial wins before trying to open an unrelated missing path.
        var missing = Path.Combine(Path.GetTempPath(), $"rtaime-capacity-{Guid.NewGuid():N}.mp4");
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var rejected = decks.Open(new(MediaContractVersion.Current, b, missing), Prepared(decks, b, 1));
            Assert.Equal("runtime.media_deck.capacity", rejected.Failure?.Code);
            Assert.Equal(new LocalMediaDeckResources(1, 1, attempt, 0), decks.Resources);
            Assert.Equal(programProbe, decks.GetSnapshot(a).Probe);
            Assert.True(decks.GetSnapshot(a).Transport!.IsOnProgram);
            Assert.Equal(MediaDeckState.Unloaded, decks.GetSnapshot(b).State);
        }
        using (var after = Next(decks, a))
        {
            Assert.Equal(a, after.Video!.SourceId);
            Assert.True(after.Transport.Position.CurrentFrame > 0);
            Assert.False(after.AudioPayload.IsEmpty);
        }
        decks.ObserveProgramSource(null);
        Command(decks, a, MediaTransportCommandKind.Pause);
        var accepted = decks.Open(new(MediaContractVersion.Current, b, asset.Path), Prepared(decks, b, 1));
        Assert.NotNull(accepted.Probe);
        Assert.Equal(MediaDeckState.Unloaded, decks.GetSnapshot(a).State);
        Assert.Equal(new LocalMediaDeckResources(1, 1, 8, 1), decks.Resources);
        var probe = decks.Probe(asset.Path, MediaAssetId.New());
        Assert.False(probe.Succeeded);
        Assert.Equal(9, decks.Resources.Rejected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(int.MaxValue)]
    public void Decoder_capacity_is_bounded_to_approved_range(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, capacity));

    [Theory]
    [InlineData(50, 1)]
    [InlineData(60_000, 1_001)]
    public void Independent_loop_seek_and_stop_fence_previous_payload_generation(long numerator, long denominator)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        var format = new VideoFormat(1920, 1080, new FrameRate(numerator, denominator), PixelFormat.Rgba8, ScanMode.Progressive);
        using var decks = new LocalMediaDeckRuntimeService(format, 2, [a, b]);
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
            Assert.False(frame.AudioPayload.IsEmpty);
            Assert.Equal(frame.Video.Timing.PresentationTimestamp, frame.Audio!.Timing.PresentationTimestamp);
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

    [Theory]
    [InlineData(50, 1)]
    [InlineData(60_000, 1_001)]
    public void Native_deck_reopens_after_missing_and_truncated_copy_without_disturbing_other_owner(long numerator, long denominator)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(asset.Path));
        var copy = Path.Combine(Path.GetTempPath(), $"rtaime-deck-fault-{Guid.NewGuid():N}.mp4");
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        var format = new VideoFormat(1920, 1080, new FrameRate(numerator, denominator), PixelFormat.Rgba8, ScanMode.Progressive);
        using var decks = new LocalMediaDeckRuntimeService(format, 2, [a, b]);
        try
        {
            Assert.NotNull(decks.Open(new(MediaContractVersion.Current, b, asset.Path), Prepared(decks, b, 1)).Probe);
            Command(decks, b, MediaTransportCommandKind.Play);
            decks.ObserveProgramSource(b);
            long previousB = -1;
            for (var cycle = 0; cycle < 4; cycle++)
            {
                Assert.NotNull(decks.Open(new(MediaContractVersion.Current, a, asset.Path), Prepared(decks, a, 0)).Probe);
                Command(decks, a, MediaTransportCommandKind.Play);
                using (var frame = Next(decks, a))
                    Assert.Equal(0, frame.Transport.Position.CurrentFrame);
                Command(decks, a, MediaTransportCommandKind.Stop);
                Assert.Equal(0, decks.GetSnapshot(a).Transport!.Position.CurrentFrame);
                decks.Close(a);
                Assert.Equal(1, decks.Resources.Reserved);
                var missing = decks.Open(new(MediaContractVersion.Current, a, copy), Prepared(decks, a, 0));
                Assert.Equal(MediaDeckState.Error, missing.State);
                Assert.Equal("media.file.not_found", missing.Failure?.Code);
                decks.Close(a);
                // Only a private, closed copy is truncated; the original input is retained.
                File.Copy(asset.Path, copy);
                using (var stream = new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.None))
                    stream.SetLength(16);
                var truncated = decks.Open(new(MediaContractVersion.Current, a, copy), Prepared(decks, a, 0));
                Assert.Equal(MediaDeckState.Error, truncated.State);
                Assert.NotNull(truncated.Failure);
                Assert.Null(truncated.Probe);
                decks.Close(a);
                File.Delete(copy);
                Assert.Equal(1, decks.Resources.Reserved);
                using var other = Next(decks, b);
                Assert.Equal(b, other.Video!.SourceId);
                Assert.True(other.Transport.Position.CurrentFrame > previousB);
                previousB = other.Transport.Position.CurrentFrame;
                Assert.Null(decks.GetSnapshot(b).Failure);
                Assert.True(decks.GetSnapshot(b).Transport!.IsOnProgram);
            }
            Assert.NotNull(decks.Open(new(MediaContractVersion.Current, a, asset.Path), Prepared(decks, a, 0)).Probe);
            Command(decks, a, MediaTransportCommandKind.Play);
            using (var recovered = Next(decks, a))
                Assert.Equal(a, recovered.Video!.SourceId);
            decks.Dispose();
            Assert.Equal(0, decks.Resources.Reserved);
            Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(asset.Path)));
        }
        finally { if (File.Exists(copy)) File.Delete(copy); }
    }


    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Production_source_allowlist_cannot_expand_or_shrink_the_AB_identity_contract(int count)
    {
        var sources = Enumerable.Range(0, count).Select(_ => MediaSourceId.New()).ToArray();
        Assert.Throws<ArgumentException>(() =>
            new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, sources));
    }

    [Fact]
    public void Production_source_allowlist_requires_distinct_identities()
    {
        var source = MediaSourceId.New();
        Assert.Throws<ArgumentException>(() =>
            new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [source, source]));
    }

    [Fact]
    public void Caller_mutation_cannot_replace_an_authorized_source_after_construction()
    {
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        var unauthorized = MediaSourceId.New();
        var sources = new[] { a, b };
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, sources);
        sources[0] = unauthorized;
        var missing = Path.Combine(Path.GetTempPath(), $"rtaime-source-{Guid.NewGuid():N}.mp4");
        var denied = decks.Open(new(MediaContractVersion.Current, unauthorized, missing), Prepared(decks, unauthorized, 0));
        Assert.Equal("runtime.media_deck.source_invalid", denied.Failure?.Code);
        Assert.Equal(0, decks.Resources.Reserved);
        var authorized = decks.Open(new(MediaContractVersion.Current, a, missing), Prepared(decks, a, 0));
        Assert.Equal("media.file.not_found", authorized.Failure?.Code);
        Assert.Equal(1, decks.Resources.Reserved);
        Assert.Equal(MediaDeckState.Unloaded, decks.Close(a).State);
        Assert.Equal(0, decks.Resources.Reserved);
    }

    [Fact]
    public void Closing_failed_entries_releases_capacity_without_authorizing_a_third_source()
    {
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        var third = MediaSourceId.New();
        using var decks = new LocalMediaDeckRuntimeService();
        var missing = Path.Combine(Path.GetTempPath(), $"rtaime-reservation-{Guid.NewGuid():N}.mp4");
        foreach (var source in new[] { a, b })
        {
            var opened = decks.Open(new(MediaContractVersion.Current, source, missing), Prepared(decks, source, 0));
            Assert.Equal("media.file.not_found", opened.Failure?.Code);
        }
        Assert.Equal(2, decks.Resources.Reserved);
        var first = decks.GetSnapshot(a);
        var second = decks.GetSnapshot(b);
        var denied = decks.Open(new(MediaContractVersion.Current, third, missing), Prepared(decks, third, 0));
        Assert.Equal("runtime.media_deck.source_invalid", denied.Failure?.Code);
        Assert.Equal(first, decks.GetSnapshot(a));
        Assert.Equal(second, decks.GetSnapshot(b));
        Assert.Equal(2, decks.Resources.Reserved);
        Assert.Equal(MediaDeckState.Unloaded, decks.Close(a).State);
        Assert.Equal(1, decks.Resources.Reserved);
        denied = decks.Open(new(MediaContractVersion.Current, third, missing), Prepared(decks, third, 0));
        Assert.Equal("runtime.media_deck.source_invalid", denied.Failure?.Code);
        Assert.Equal(1, decks.Resources.Reserved);
        Assert.Equal(second, decks.GetSnapshot(b));
        Assert.Equal(MediaDeckState.Unloaded, decks.Close(b).State);
        Assert.Equal(0, decks.Resources.Reserved);
    }

    [Theory]
    [InlineData(1, LocalMediaCapacityProfile.Conservative)]
    [InlineData(2, LocalMediaCapacityProfile.Conservative)]
    [InlineData(4, LocalMediaCapacityProfile.DevelopmentFour)]
    public void Productive_open_close_and_retained_pool_use_one_ledger(int capacity, LocalMediaCapacityProfile profile)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        var sources = Enumerable.Range(0, Math.Max(2, capacity)).Select(_ => MediaSourceId.New()).ToArray();
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, capacity, sources, profile);
        for (var index = 0; index < capacity; index++)
        {
            var execution = Prepared(decks, sources[index], index);
            var opened = decks.Open(new(MediaContractVersion.Current, sources[index], asset.Path), execution);
            Assert.NotNull(opened.Probe);
            Assert.Equal(index + 1, decks.ResourceSnapshot.ReservedOwners);
        }
        Command(decks, sources[0], MediaTransportCommandKind.Play);
        using var held = Next(decks, sources[0]);
        Command(decks, sources[0], MediaTransportCommandKind.Stop);
        var bytes = decks.ResourceSnapshot.KnownManagedBytes;
        Assert.Equal(MediaDeckState.Unloaded, decks.Close(sources[0]).State);
        Assert.Equal(capacity, decks.ResourceSnapshot.ReservedOwners);
        Assert.Equal(bytes, decks.ResourceSnapshot.KnownManagedBytes);
        Assert.Equal(1, decks.ResourceSnapshot.RetainedPoolOwners);
        var denied = decks.Open(new(MediaContractVersion.Current, sources[0], asset.Path), Prepared(decks, sources[0], 0));
        Assert.Equal("runtime.media_resources.capacity", denied.Failure?.Code);
        held.Dispose();
        Assert.Equal(capacity - 1, decks.ResourceSnapshot.ReservedOwners);
        Assert.NotNull(decks.Open(new(MediaContractVersion.Current, sources[0], asset.Path), Prepared(decks, sources[0], 0)).Probe);
        decks.Dispose();
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        Assert.Equal(0L, decks.ResourceSnapshot.KnownManagedBytes);
    }

    [Fact]
    public void Probe_cannot_open_a_native_owner_beside_a_resident_when_storage_pressure_is_unknown()
    {
        var a = MediaSourceId.New();
        var b = MediaSourceId.New();
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 2, [a, b]);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
        Assert.Equal("media.file.not_found", decks.Open(new(MediaContractVersion.Current, a, missing), Prepared(decks, a, 0)).Failure?.Code);
        var probe = decks.Probe(missing, MediaAssetId.New());
        Assert.Equal("runtime.media_resources.pressure_unknown", probe.Failure?.Code);
        Assert.Equal(1, decks.ResourceSnapshot.ReservedOwners);
        decks.Close(a);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        Assert.Equal("media.file.not_found", decks.Probe(missing, MediaAssetId.New()).Failure?.Code);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    [Fact]
    public void Development_source_identity_registry_is_finite_and_independent_of_concurrent_decoder_capacity()
    {
        using var decks = new LocalMediaDeckRuntimeService(VideoFormat.Hd1080p50Rgba8, 4, null,
            LocalMediaCapacityProfile.DevelopmentFour);
        var sources = Enumerable.Range(0, 9).Select(_ => MediaSourceId.New()).ToArray();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
        foreach (var source in sources.Take(8))
        {
            Assert.Equal("media.file.not_found", decks.Open(new(MediaContractVersion.Current, source, missing), Prepared(decks, source, 0)).Failure?.Code);
            decks.Close(source);
            Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        }
        Assert.Equal("runtime.media_deck.source_invalid",
            decks.Open(new(MediaContractVersion.Current, sources[8], missing), Prepared(decks, sources[8], 0)).Failure?.Code);
        Assert.Equal("media.file.not_found",
            decks.Open(new(MediaContractVersion.Current, sources[0], missing), Prepared(decks, sources[0], 0)).Failure?.Code);
        decks.Close(sources[0]);
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
