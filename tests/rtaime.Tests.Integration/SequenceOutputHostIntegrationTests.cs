// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Diagnostics;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using Xunit;

namespace rtaime.Tests.Integration;

/// <summary>
/// Source-bound native Preview and committed Program Take through genuine
/// Operator->ControlHost->RuntimeHost process/IPC boundaries. The media pixel
/// and PCM mixer fidelity is separately qualified with distinct native
/// source fixtures; this test owns the authoritative transport/recovery path.
/// </summary>
public sealed partial class ExternalControlIntegrationTests
{
    [Fact]
    public async Task Real_sequence_Preview_Take_and_explicit_recovery_preserve_single_Control_Program_commit()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var mediaFile = LocalMediaTestAsset.ExtractReference1080p50();
        await using var fixture = await ExternalFixture.StartAsync(ExternalControlRole.Operator);
        await using var grpc = fixture.CreateClient();

        var original = await grpc.GetSnapshotAsync();
        var sourceB = new ProductionSourceId(Identity.Parse(original.Sources[1].Id));
        await CommitSequencePreparationAuthorityAsync(grpc);
        var previewCommitted = await grpc.GetSnapshotAsync();
        Assert.Equal(sourceB, previewCommitted.Production.Routing.PreviewSourceId);
        Assert.NotEqual(previewCommitted.Production.Routing.PreviewSourceId,
            previewCommitted.Production.Routing.ProgramSourceId);

        var imported = await grpc.ImportMediaAssetsAsync([mediaFile.Path]);
        Assert.True(imported.Succeeded);
        var seedSequence = PreparedVideoSequence(Assert.Single(imported.Snapshot.Assets));
        // The repository reference media is one second long. Reserve a
        // forty-frame authored window so real native preroll, Preview
        // observation and the explicit Take can run on ordinary CI runners
        // without timing out an artificially four-frame sequence.
        var authoredClip = Assert.Single(seedSequence.Clips);
        var sequence = seedSequence with
        {
            Clips = [authoredClip with { SourceOut = 40 * 4800, Duration = 40 * 4800 }]
        };
        var authoring = new SequenceAuthoringClient(grpc);
        var saved = await authoring.SaveAsync(new(
            SequenceContract.Version, Guid.NewGuid(), 0,
            sequence.SequenceId, sequence));
        Assert.True(saved.Saved);

        var preparation = new SequencePreparationClient(grpc);
        var context = await preparation.ContextAsync();
        var staged = await preparation.PrepareAsync(new(
            SequencePreparationContract.Version, sequence.SequenceId,
            saved.AuthoringRevision, context.RuntimeHostInstanceId));
        Assert.Equal("PREPARED", staged.State);

        var reference = new SequencePreparationReference(staged.Version,
            staged.SequenceId, staged.AuthoringRevision, staged.RuntimeHostInstanceId,
            staged.PreparedExecutionId, staged.Generation);
        var shuttle = new SequenceShuttleClient(grpc);
        var stopped = await shuttle.GetAsync(reference);
        Assert.Equal("STOPPED", stopped.State);

        var command = new SequenceOutputCommand(SequenceOutputContract.Version,
            reference, sourceB.ToString(), previewCommitted.Production.Revision.Value,
            Guid.NewGuid());
        var localOutput = new SequenceOutputClient(
            new NamedPipeOperatorControlTransport(fixture.ControlEndpoint));
        var grpcOutput = new SequenceOutputClient(grpc);
        var activation = await localOutput.ActivatePreviewAsync(command);
        Assert.Equal(sourceB.ToString(), activation.PreviewSourceId);

        // Source identity, Runtime lifetime and production revision are
        // independently checked by the real ControlHost gRPC boundary.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await grpcOutput.ActivatePreviewAsync(command with
            {
                Preparation = reference with { RuntimeHostInstanceId = Guid.NewGuid().ToString("D") },
                TransactionId = Guid.NewGuid()
            }));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await grpcOutput.TakeAsync(command with
            {
                ExpectedProductionRevision = command.ExpectedProductionRevision + 1,
                TransactionId = Guid.NewGuid()
            }));
        // PREPARED and preview activation are not sufficient. A Take before
        // any actual Preview frame must leave the committed Program unchanged.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await grpcOutput.TakeAsync(command));
        var beforeRealPreview = await grpc.GetSnapshotAsync();
        Assert.Equal(original.Production.Routing.ProgramSourceId,
            beforeRealPreview.Production.Routing.ProgramSourceId);
        Assert.Equal(previewCommitted.Production.Revision,
            beforeRealPreview.Production.Revision);

        // Arm the exact Preview route before consuming the finite one-second
        // reference clip. This still requires genuine PLAYING native frames
        // and a completed Preview sink observation before any Program Take.
        var playing = await shuttle.ApplyAsync(new(SequenceShuttleContract.Version,
            reference, stopped.StateRevision, SequenceShuttleAction.Play));
        Assert.Equal("PLAYING", playing.State);

        static async Task<SequenceOutputObservation> AwaitOutputAsync(
            SequenceOutputClient client, SequencePreparationReference current,
            string expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var clock = Stopwatch.StartNew();
            SequenceOutputObservation? last = null;
            while (!timeout.IsCancellationRequested)
            {
                var status = await client.GetAsync(current, timeout.Token);
                last = status;
                if (status.OutputState == expected) return status;
                Assert.DoesNotContain(status.OutputState, new[] { "FAILED", "STALE" });
                if (clock.Elapsed > TimeSpan.FromSeconds(19))
                    break;
                await Task.Delay(60, timeout.Token);
            }
            throw new TimeoutException(
                "No matching confirmed native Preview/Program output was observed. " +
                $"Expected={expected}; lastState={last?.OutputState ?? "missing"}; " +
                $"failure={last?.FailureCode ?? "none"}; " +
                $"sourceFrames={last?.PublishedSourceFrames}; sinkFrame={last?.LastProgramSlot}; " +
                $"latestSourceSlot={last?.LastSourceSlot}.");
        }

        var preview = await AwaitOutputAsync(grpcOutput, reference, "PREVIEW_READY");
        Assert.True(preview.PublishedSourceFrames > 0);
        Assert.Null(preview.LastProgramSlot);
        Assert.Equal(previewCommitted.Production.Revision.Value,
            (await grpc.GetSnapshotAsync()).Production.Revision.Value);

        var take = await grpcOutput.TakeAsync(command);
        Assert.Contains(take.OutputState, new[] { "TAKE_COMMITTED", "PROGRAM_CONFIRMED" });
        var confirmed = await AwaitOutputAsync(localOutput, reference, "PROGRAM_CONFIRMED");
        Assert.NotNull(confirmed.LastProgramSlot);
        Assert.True(confirmed.PublishedSourceFrames > 0);

        var committed = await grpc.GetSnapshotAsync();
        Assert.Equal(sourceB, committed.Production.Routing.ProgramSourceId);
        Assert.Equal(previewCommitted.Production.Revision.Value + 1,
            committed.Production.Revision.Value);

        // Recover the same exact already committed successor without
        // invoking another Program mutation, even after a lost reply.
        var recovered = await grpcOutput.RecoverAsync(command);
        Assert.Contains(recovered.OutputState,
            new[] { "TAKE_COMMITTED", "PROGRAM_CONFIRMED" });
        Assert.Equal(committed.Production.Revision.Value,
            (await grpc.GetSnapshotAsync()).Production.Revision.Value);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await grpcOutput.TakeAsync(command));
        Assert.Equal(committed.Production.Revision.Value,
            (await grpc.GetSnapshotAsync()).Production.Revision.Value);

        var canceled = await preparation.CancelAsync(reference);
        Assert.Equal("CANCELED", canceled.State);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await localOutput.GetAsync(reference));
        Assert.Equal(committed.Production.Revision.Value,
            (await grpc.GetSnapshotAsync()).Production.Revision.Value);
    }
}
