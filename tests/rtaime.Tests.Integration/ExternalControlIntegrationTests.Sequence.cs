// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using Grpc.Core;
using rtaime.Client;
using rtaime.Control;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Media.Contracts;
namespace rtaime.Tests.Integration;

public sealed partial class ExternalControlIntegrationTests
{
    [Theory]
    [InlineData(25, 1)]
    [InlineData(50, 1)]
    [InlineData(60000, 1001)]
    public async Task Sequence_real_asset_authoring_roundtrips_both_transports_and_restart(int numerator, int denominator)
    {
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var grpc = fixture.CreateClient();
        var before = await grpc.GetSnapshotAsync();
        var imported = await grpc.ImportMediaAssetsAsync([asset.Path]);
        Assert.True(imported.Succeeded);
        var media = Assert.Single(imported.Snapshot.Assets);
        var pipeTransport = new NamedPipeOperatorControlTransport(fixture.ControlEndpoint);
        var pipe = new SequenceAuthoringClient(pipeTransport);
        var client = new SequenceAuthoringClient(grpc);
        Assert.Equal(0UL, (await client.ListAsync()).AuthoringRevision);
        var tracks = Enumerable.Range(0, 4).Select(i => new SequenceTrack(Guid.NewGuid(), "Track " + i,
            i < 2 ? SequenceMediaKind.Video : SequenceMediaKind.Audio, i, AudioBusId: i < 2 ? null : "program")).ToArray();
        var group = Guid.NewGuid();
        var frame = SequenceTime.FromFrames(1, new(numerator, denominator));
        var sequence = new SequenceDefinition("1.0", Guid.NewGuid(), "Real AV", new(numerator, denominator), tracks,
            tracks.Select((t, i) => new SequenceClip(Guid.NewGuid(), t.TrackId,
                new(Guid.Parse(media.AssetId.ToString()), t.Kind, media.FingerprintSha256),
                0, frame * 4, 0, frame * 4, LinkGroupId: i is 0 or 2 ? group : null)).ToArray(), []);
        var save = new SequenceMutationRequest("1.0", Guid.NewGuid(), 0, sequence.SequenceId, sequence);
        var result = await client.SaveAsync(save);
        Assert.True(result.Saved, string.Join(";", result.Issues.Select(i => i.Message)));
        Assert.Equal(1UL, result.AuthoringRevision);
        var snapshot = await pipe.GetAsync(sequence.SequenceId);
        Assert.True(snapshot.Validation.IsValid);
        Assert.Null(snapshot.ActiveExecutionRevision);
        Assert.Equal("UNAVAILABLE", snapshot.PlaybackState);
        Assert.Equal(SequenceJsonText(sequence), SequenceJsonText(snapshot.Sequence));
        var edit = new SequenceMutationRequest("1.0", Guid.NewGuid(), 1, sequence.SequenceId,
            Edits: [new(SequenceEditKind.TrimClip, sequence.Clips[0].ClipId, SourceIn: frame, SourceOut: frame * 3)]);
        var trim = await pipe.EditAsync(edit);
        Assert.True(trim.Saved);
        Assert.All(trim.Sequence!.Clips.Where(c => c.LinkGroupId == group), c => {
            Assert.Equal(frame, c.SourceIn); Assert.Equal(frame, c.TimelineStart); Assert.Equal(frame * 2, c.Duration);
        });
        var conflict = await client.EditAsync(edit with { TransactionId = Guid.NewGuid() });
        Assert.False(conflict.Saved);
        Assert.Contains(conflict.Issues, i => i.Code == "sequence.revision_conflict");
        var replay = await client.EditAsync(edit);
        Assert.True(replay.Replayed);
        Assert.Equal(2UL, replay.AuthoringRevision);
        Assert.False((await client.SaveAsync(save with { Definition = sequence with { Name = "Reuse" } })).Saved);
        var invalid = await client.EditAsync(edit with { TransactionId = Guid.NewGuid(), ExpectedRevision = 2,
            Edits = [new(SequenceEditKind.DeleteTrack, tracks[0].TrackId)] });
        Assert.False(invalid.Saved);
        Assert.Equal(2UL, (await client.ListAsync()).AuthoringRevision);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pipe.SaveAsync(save, cancelled.Token));
        }
        await fixture.RestartControlAsync();
        await using var restarted = fixture.CreateClient();
        var recovered = new SequenceAuthoringClient(restarted);
        Assert.True((await recovered.EditAsync(edit)).Replayed);
        var undo = await recovered.UndoAsync(new("1.0", Guid.NewGuid(), 2, Guid.Empty));
        Assert.True(undo.Saved);
        Assert.Equal(3UL, undo.AuthoringRevision);
        Assert.Equal(SequenceJsonText(sequence), SequenceJsonText((await pipe.GetAsync(sequence.SequenceId)).Sequence));
        var redo = await pipe.RedoAsync(new("1.0", Guid.NewGuid(), 3, Guid.Empty));
        Assert.True(redo.Saved);
        Assert.Equal(4UL, redo.AuthoringRevision);
        Assert.Equal(SequenceJsonText(trim.Sequence), SequenceJsonText((await recovered.GetAsync(sequence.SequenceId)).Sequence));
        Assert.Equal(before.Production, (await restarted.GetSnapshotAsync()).Production);
        Assert.Equal(MediaDeckState.Unloaded, (await restarted.GetMediaDeckSnapshotAsync()).State);
        var removed = await restarted.RemoveMediaAssetAsync(media.AssetId);
        Assert.True(removed.Succeeded);
        var missing = await pipe.GetAsync(sequence.SequenceId);
        Assert.False(missing.Validation.IsValid);
        Assert.Contains(missing.Validation.Issues, i => i.Code == "sequence.asset_reference");
        Assert.Equal(sequence.Clips[0].Media.AssetId, missing.Sequence.Clips[0].Media.AssetId);
    }

    [Fact]
    public async Task Sequence_observer_can_discover_and_list_but_cannot_mutate()
    {
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Observer);
        await using var grpc = fixture.CreateClient();
        var discovered = await grpc.DiscoverAsync();
        Assert.Contains("control.sequence.list", discovered.Capabilities);
        Assert.DoesNotContain(discovered.Capabilities, c => c.StartsWith("control.sequence.play"));
        var client = new SequenceAuthoringClient(grpc);
        Assert.Empty((await client.ListAsync()).Sequences);
        foreach (var operation in new Func<SequenceMutationRequest, CancellationToken, ValueTask<SequenceMutationResult>>[] {
            client.SaveAsync, client.EditAsync, client.UndoAsync, client.RedoAsync })
        {
            var denied = await Assert.ThrowsAsync<RpcException>(async () =>
                await operation(new("1.0", Guid.NewGuid(), 0, Guid.Empty), default));
            Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        }
    }

    private static string SequenceJsonText<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, SequenceJson.Options);
}
