// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Media.Contracts;
using Xunit;

namespace rtaime.Tests.Integration;

/// <summary>
/// Shared native-media authoring/analysis/preparation/transport qualification.
/// A real sequence Program Take must be tested separately when that output
/// capability exists; transport state must not impersonate delivered pixels.
/// </summary>
public sealed partial class ExternalControlIntegrationTests
{
    [Fact]
    public async Task Operator_experience_import_analyze_multitrack_save_prepare_shuttle_preserves_Program_authority()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var mediaFile = LocalMediaTestAsset.ExtractReference1080p50();
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var remote = fixture.CreateClient();
        var local = new OperatorControlClient(
            new NamedPipeOperatorControlTransport(fixture.ControlEndpoint));
        var before = await remote.GetSnapshotAsync();

        // The one actual imported asset is the source of every downstream
        // identity. Frame analysis is read-only and uses Runtime admission.
        var imported = await remote.ImportMediaAssetsAsync([mediaFile.Path]);
        Assert.True(imported.Succeeded);
        var asset = Assert.Single(imported.Snapshot.Assets);
        Assert.Equal(MediaAssetAvailability.Online, asset.Availability);

        var analysisRequest = new MediaAnalysisRequest(MediaAnalysisContract.Version,
            asset.AssetId.ToString(), "integrated-operator-review-" + Guid.NewGuid().ToString("N"),
            1, MediaAnalysisKind.Frame, TimeSpan.FromMilliseconds(200).Ticks, 0,
            160, 90, 0, 0);
        var admittedAnalysis = await local.BeginMediaAnalysisAsync(analysisRequest);
        Assert.Equal(analysisRequest.AssetId, admittedAnalysis.AssetId);
        Assert.Equal(analysisRequest.Generation, admittedAnalysis.Generation);
        var observationRef = new MediaAnalysisJobRequest(admittedAnalysis.Version,
            admittedAnalysis.JobId, admittedAnalysis.AssetId, admittedAnalysis.ConsumerId,
            admittedAnalysis.Generation, admittedAnalysis.RuntimeEpoch);
        MediaAnalysisObservation? analyzed = null;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (!deadline.IsCancellationRequested)
            {
                var current = await local.GetMediaAnalysisAsync(observationRef, deadline.Token);
                current.Validate();
                Assert.Equal(observationRef.JobId, current.JobId);
                Assert.Equal(observationRef.RuntimeEpoch, current.RuntimeEpoch);
                if (current.State is not (MediaAnalysisState.Pending or MediaAnalysisState.Running))
                {
                    analyzed = current;
                    break;
                }
                await Task.Delay(75, deadline.Token);
            }
        }
        Assert.NotNull(analyzed);
        Assert.Equal(MediaAnalysisState.Complete, analyzed.State);
        Assert.InRange(analyzed.ActualTicks!.Value,
            analysisRequest.PositionTicks, asset.Duration.Ticks);
        Assert.Equal(160 * 90 * 4, analyzed.RgbaPixels!.Length);
        Assert.Equal(before.Production, (await remote.GetSnapshotAsync()).Production);

        // Two different source offsets each share one decoder between their
        // linked video/audio clips. Two owners are required; neither is
        // claimed to be a committed Preview or Program output.
        const long frameTicks = 4800;
        var tracks = new[]
        {
            new SequenceTrack(Guid.NewGuid(), "V1", SequenceMediaKind.Video, 0),
            new SequenceTrack(Guid.NewGuid(), "V2", SequenceMediaKind.Video, 1),
            new SequenceTrack(Guid.NewGuid(), "A1", SequenceMediaKind.Audio, 2, AudioBusId: "program"),
            new SequenceTrack(Guid.NewGuid(), "A2", SequenceMediaKind.Audio, 3, AudioBusId: "program")
        };
        var mediaId = Guid.Parse(asset.AssetId.ToString());
        var firstPair = Guid.NewGuid();
        var secondPair = Guid.NewGuid();
        var clips = tracks.Select((track, i) =>
        {
            var start = i is 1 or 3 ? frameTicks : 0L;
            var group = i is 0 or 2 ? firstPair : secondPair;
            return new SequenceClip(Guid.NewGuid(), track.TrackId,
                new SequenceMediaReference(mediaId, track.Kind, asset.FingerprintSha256),
                start, start + 3 * frameTicks, 0, 3 * frameTicks,
                LinkGroupId: group);
        }).ToArray();
        var sequence = new SequenceDefinition(SequenceContract.Version, Guid.NewGuid(),
            "Integrated source-bound A/V", new SequenceRate(50, 1), tracks, clips, []);
        var authoring = new SequenceAuthoringClient(remote);
        var saved = await authoring.SaveAsync(new(SequenceContract.Version,
            Guid.NewGuid(), 0, sequence.SequenceId, sequence));
        Assert.True(saved.Saved, string.Join("; ", saved.Issues.Select(i => i.Message)));
        Assert.Equal(4, (await authoring.GetAsync(sequence.SequenceId)).Sequence.Clips.Length);

        var native = new SequencePreparationClient(remote);
        var context = await native.ContextAsync();
        Assert.Equal(saved.AuthoringRevision, context.AuthoringRevision);
        var staged = await native.PrepareAsync(new(SequencePreparationContract.Version,
            sequence.SequenceId, saved.AuthoringRevision, context.RuntimeHostInstanceId));
        Assert.Equal("PREPARED", staged.State);
        Assert.Equal(2, staged.DecoderOwners);
        var owner = new SequencePreparationReference(staged.Version,
            staged.SequenceId, staged.AuthoringRevision, staged.RuntimeHostInstanceId,
            staged.PreparedExecutionId, staged.Generation);

        var shuttle = new SequenceShuttleClient(remote);
        var stopped = await shuttle.GetAsync(owner);
        Assert.Equal("STOPPED", stopped.State);
        Assert.True(stopped.NativePrepared);
        Assert.Equal("UNAVAILABLE", stopped.OutputState);
        var cued = await shuttle.ApplyAsync(new(SequenceShuttleContract.Version,
            owner, stopped.StateRevision, SequenceShuttleAction.Cue, PositionTicks: frameTicks));
        Assert.Equal("PAUSED", cued.State);
        Assert.True(cued.NativePrepared);
        var playing = await shuttle.ApplyAsync(new(SequenceShuttleContract.Version,
            owner, cued.StateRevision, SequenceShuttleAction.Play));
        Assert.Equal("PLAYING", playing.State);
        Assert.Equal("UNAVAILABLE", playing.OutputState);
        Assert.Equal(before.Production, (await remote.GetSnapshotAsync()).Production);
        Assert.Equal("UNAVAILABLE", (await authoring.GetAsync(sequence.SequenceId)).PlaybackState);

        // A subsequent authored revision must revoke the native owner
        // instead of implicitly replacing the execution or taking Program.
        var edited = await authoring.SaveAsync(new(SequenceContract.Version,
            Guid.NewGuid(), saved.AuthoringRevision, sequence.SequenceId,
            sequence with { Name = "Revised after native preroll" }));
        Assert.True(edited.Saved);
        await Assert.ThrowsAnyAsync<Exception>(async () => await shuttle.GetAsync(owner));
        Assert.Equal(before.Production, (await remote.GetSnapshotAsync()).Production);
        Assert.Equal(edited.AuthoringRevision, (await authoring.ListAsync()).AuthoringRevision);
    }
}
