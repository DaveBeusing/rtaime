// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Rtaime.ExternalControl.V1;
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
        var canvas = await grpc.GetOutputCanvasAsync();
        var firstCanvas = canvas.Saved.Canvases[0];
        var canvasSave = await grpc.SaveOutputCanvasAsync(new("1.0", Guid.NewGuid(), canvas.Saved.ConfigurationId, 0,
            [new(OutputCanvasEditKind.UpdateCanvas, firstCanvas.CanvasId, Canvas: firstCanvas with { Name = "Retained canvas" })]));
        Assert.True(canvasSave.Saved);
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
        await pipeTransport.GetSnapshotAsync();
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
        Assert.Equal(SequenceJsonText(canvasSave.Configuration), SequenceJsonText((await restarted.GetOutputCanvasAsync()).Saved));
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

    [Fact]
    public async Task Sequence_edit_batches_commit_move_blade_gap_ripple_and_transition_changes()
    {
        using var asset = LocalMediaTestAsset.ExtractReference1080p50();
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var grpc = fixture.CreateClient();
        var imported = await grpc.ImportMediaAssetsAsync([asset.Path]);
        var media = Assert.Single(imported.Snapshot.Assets);
        var client = new SequenceAuthoringClient(grpc);
        var tracks = new[] { new SequenceTrack(Guid.NewGuid(), "Video", SequenceMediaKind.Video, 0),
            new SequenceTrack(Guid.NewGuid(), "Audio", SequenceMediaKind.Audio, 0, AudioBusId: "program") };
        var clips = tracks.Select(t => new SequenceClip(Guid.NewGuid(), t.TrackId,
            new(Guid.Parse(media.AssetId.ToString()), t.Kind, media.FingerprintSha256), 0, 10000, 0, 10000)).ToArray();
        var sequence = new SequenceDefinition("1.0", Guid.NewGuid(), "Edits", new(50, 1), [], [], []);
        Assert.True((await client.SaveAsync(new("1.0", Guid.NewGuid(), 0, sequence.SequenceId, sequence))).Saved);
        ulong revision = 1;
        async Task<SequenceDefinition> Edit(params SequenceEdit[] edits)
        {
            var result = await client.EditAsync(new("1.0", Guid.NewGuid(), revision, sequence.SequenceId, Edits: edits));
            Assert.True(result.Saved, string.Join(";", result.Issues.Select(i => i.Message)));
            Assert.Equal(++revision, result.AuthoringRevision);
            return result.Sequence!;
        }
        var group = Guid.NewGuid();
        await Edit(new SequenceEdit(SequenceEditKind.AddTrack, tracks[0].TrackId, Track: tracks[0]),
            new(SequenceEditKind.AddTrack, tracks[1].TrackId, Track: tracks[1]),
            new(SequenceEditKind.InsertClip, clips[0].ClipId, Clip: clips[0]),
            new(SequenceEditKind.InsertClip, clips[1].ClipId, Clip: clips[1]),
            new(SequenceEditKind.Link, group, Ids: clips.Select(c => c.ClipId).ToArray()));
        var moved = await Edit(new SequenceEdit(SequenceEditKind.MoveClip, clips[0].ClipId, Position: 10000));
        Assert.All(moved.Clips, c => Assert.Equal(10000L, c.TimelineStart));
        var rights = new[] { Guid.NewGuid(), Guid.NewGuid() }; var rightGroup = Guid.NewGuid();
        var split = await Edit(new SequenceEdit(SequenceEditKind.SplitClip, clips[0].ClipId, Position: 15000, Ids: rights, LinkGroupId: rightGroup));
        Assert.Equal(4, split.Clips.Length);
        Assert.All(clips, c => Assert.Equal(5000L, split.Clips.Single(s => s.ClipId == c.ClipId).Duration));
        var closed = await Edit(new SequenceEdit(SequenceEditKind.CloseGap, Guid.Empty, Position: 0, Duration: 10000));
        Assert.All(closed.Clips.Where(c => c.LinkGroupId == rightGroup), c => Assert.Equal(5000L, c.TimelineStart));
        var ripple = await Edit(new SequenceEdit(SequenceEditKind.RippleDelete, clips[0].ClipId));
        Assert.Equal(2, ripple.Clips.Length);
        Assert.All(ripple.Clips, c => Assert.Equal(0L, c.TimelineStart));
        var unlinked = await Edit(new SequenceEdit(SequenceEditKind.Unlink, rightGroup));
        Assert.All(unlinked.Clips, c => Assert.Null(c.LinkGroupId));
        var video = unlinked.Clips.Single(c => c.Media.Kind == SequenceMediaKind.Video);
        var overlapping = video with { ClipId = Guid.NewGuid(), TimelineStart = 4000 };
        var fade = new SequenceTransition(Guid.NewGuid(), video.ClipId, overlapping.ClipId, SequenceTransitionKind.Dissolve, 4000, 1000);
        await Edit(new SequenceEdit(SequenceEditKind.InsertClip, overlapping.ClipId, Clip: overlapping),
            new(SequenceEditKind.AddTransition, fade.TransitionId, Transition: fade));
        var invalid = await client.EditAsync(new("1.0", Guid.NewGuid(), revision, sequence.SequenceId,
            Edits: [new(SequenceEditKind.DeleteClip, video.ClipId)]));
        Assert.False(invalid.Saved);
        Assert.Contains(invalid.Issues, i => i.Code == "sequence.transition");
        var cleaned = await Edit(new SequenceEdit(SequenceEditKind.DeleteTransition, fade.TransitionId),
            new(SequenceEditKind.DeleteClip, video.ClipId),
            new(SequenceEditKind.UpdateTrack, tracks[0].TrackId, Track: tracks[0] with { Muted = true, Enabled = false, LayerOrder = 2 }));
        Assert.True(cleaned.Tracks.Single(t => t.TrackId == tracks[0].TrackId).Muted);
        await Edit(new SequenceEdit(SequenceEditKind.DeleteClip, overlapping.ClipId), new(SequenceEditKind.DeleteTrack, tracks[0].TrackId));
        Assert.Single((await client.GetAsync(sequence.SequenceId)).Sequence.Tracks);
    }

    [Fact]
    public async Task Sequence_clients_surface_unsupported_legacy_peer_errors_without_fallback()
    {
        // Minimal legacy wire peers deliberately lack sequence handlers.
        var endpoint = "rtaime.sequence.legacy." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        async Task<JsonObject> Read()
        {
            var prefix = new byte[4]; await server.ReadExactlyAsync(prefix, deadline.Token);
            var size = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            Assert.InRange(size, 1U, 1024U * 1024U);
            var bytes = new byte[checked((int)size)]; await server.ReadExactlyAsync(bytes, deadline.Token);
            return JsonNode.Parse(bytes)!.AsObject();
        }
        async Task Write(JsonObject envelope)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
            var prefix = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
            await server.WriteAsync(prefix, deadline.Token); await server.WriteAsync(bytes, deadline.Token);
            await server.FlushAsync(deadline.Token);
        }
        async Task LegacyPipe()
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var hello = await Read(); var host = Guid.NewGuid().ToString();
            hello["messageType"] = "server.hello"; hello["hostInstanceId"] = host; hello["stateVersion"] = 1;
            hello["payload"] = JsonSerializer.SerializeToNode(new {
                protocolVersion = "1.0", role = "ControlHost", hostInstanceId = host, contractVersions = new { control = "1.0" } });
            await Write(hello);
            var request = await Read();
            Assert.Equal("control.sequence.list", request["messageType"]!.GetValue<string>());
            request["messageType"] = "error"; request["hostInstanceId"] = host;
            request["payload"] = JsonSerializer.SerializeToNode(new { code = "ipc.message.unknown", message = "Unknown operation." });
            await Write(request);
        }
        var serving = LegacyPipe();
        var pipe = new SequenceAuthoringClient(new NamedPipeOperatorControlTransport(endpoint));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipe.ListAsync(deadline.Token));
        Assert.Contains("ipc.message.unknown", rejected.Message);
        await serving;

        var root = Path.Combine(Path.GetTempPath(), "rtaime-sequence-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var certificate = CreateCertificate(root, "legacy", client: false);
        var port = FreePort();
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port, listen =>
            {
                listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                listen.UseHttps(certificate.Path, certificate.Password);
            }));
            builder.Services.AddGrpc();
            await using var app = builder.Build();
            app.MapGrpcService<SequenceLegacyGrpcService>();
            await app.StartAsync(deadline.Token);
            try
            {
                await using var grpc = new GrpcOperatorControlTransport(new GrpcOperatorControlOptions {
                    Endpoint = new Uri("https://localhost:" + port), TrustMode = ExternalControlTrustMode.PinnedServerCertificate,
                    TrustedServerCertificateThumbprint = certificate.Thumbprint });
                var client = new SequenceAuthoringClient(grpc);
                var unsupported = await Assert.ThrowsAsync<RpcException>(async () => await client.ListAsync(deadline.Token));
                Assert.Equal(StatusCode.Unimplemented, unsupported.StatusCode);
            }
            finally { await app.StopAsync(deadline.Token); }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    public sealed class SequenceLegacyGrpcService : ExternalControl.ExternalControlBase
    {
        public override Task<ExternalControlReply> Execute(ExternalControlRequest request, ServerCallContext context) =>
            throw new RpcException(new Status(StatusCode.Unimplemented, "Sequence capability is unavailable."));
    }

    [Fact]
    public async Task Sequence_api_rejects_future_null_oversized_and_unresolved_input_without_writes()
    {
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var grpc = fixture.CreateClient();
        var client = new SequenceAuthoringClient(grpc);
        var empty = new SequenceDefinition("1.0", Guid.NewGuid(), "Validation", new(50, 1), [], [], []);
        var track = new SequenceTrack(Guid.NewGuid(), "Video", SequenceMediaKind.Video, 0);
        var missing = new SequenceClip(Guid.NewGuid(), track.TrackId, new(Guid.NewGuid(), SequenceMediaKind.Video, new string('A', 64)), 0, 100, 0, 100);
        foreach (var definition in new[] {
            empty with { Version = "2.0" }, empty with { Tracks = null! },
            empty with { Tracks = Enumerable.Range(0, 65).Select(i => track with { TrackId = Guid.NewGuid() }).ToArray() },
            empty with { Tracks = [track], Clips = [missing] } })
        {
            var result = await client.SaveAsync(new("1.0", Guid.NewGuid(), 0, empty.SequenceId, definition));
            Assert.False(result.Saved);
        }
        Assert.False((await client.SaveAsync(new("2.0", Guid.NewGuid(), 0, empty.SequenceId, empty))).Saved);
        Assert.False((await client.SaveAsync(new("1.0", Guid.NewGuid(), 0, empty.SequenceId))).Saved);
        await Assert.ThrowsAsync<ExternalControlRequestException>(async () =>
            await client.SaveAsync(new("1.0", Guid.NewGuid(), 0, empty.SequenceId, empty with { Name = new string('x', 600000) })));
        Assert.Equal(0UL, (await client.ListAsync()).AuthoringRevision);
        Assert.True((await client.SaveAsync(new("1.0", Guid.NewGuid(), 0, empty.SequenceId, empty))).Saved);
        Assert.False((await client.EditAsync(new("1.0", Guid.NewGuid(), 1, empty.SequenceId, Edits: []))).Saved);
        Assert.Equal(1UL, (await client.ListAsync()).AuthoringRevision);
    }

    private static string SequenceJsonText<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, SequenceJson.Options);
}
