// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Security.Cryptography;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;
using Xunit;

namespace rtaime.Tests.Integration;

public sealed class SequenceNativeProgramOutputIntegrationTests
{
    [Fact]
    public async Task Two_native_video_and_two_audio_tracks_reach_committed_program_pixel_and_pcm_sink()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var media = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(media.Path)));
        var format = VideoFormat.Hd1080p50Rgba8;
        var sourceA = ProductionSourceId.New();
        var sourceB = ProductionSourceId.New();
        var mediaB = new MediaSourceId(sourceB.Value);
        await using var runtime = new RuntimeProgramExecutionService(
            new MediaSourceId(sourceA.Value), mediaB, format, new NoopRecordingWriter());
        await using var journal = new BoundedProductionJournal(64);
        var spec = new ProductionSpecification(ControlContractVersion.Current,
            ProductionId.New(), "Native Sequence Program Output",
            [new ProductionSourceSpecification(sourceA, "A"),
             new ProductionSourceSpecification(sourceB, "Preview sequence")],
            new ProductionRoutingState(sourceA, sourceA),
            initialOutputRoles:
            [ProductionOutputRoleState.Program(sourceA),
             ProductionOutputRoleState.Aux(sourceB)]);
        var control = new ControlHostService(spec, runtime.ProviderDescriptors, journal);
        Confirm(control, runtime, control.Initialize());
        Confirm(control, runtime, control.SelectPreview(new SelectPreviewCommand(
            new ControlCommandMetadata(ControlContractVersion.Current, CommandId.New(),
                control.State.ProductionId, control.State.Revision), sourceB)));
        Assert.True(runtime.IsCommittedSequencePreviewSource(mediaB));

        using var decks = new LocalMediaDeckRuntimeService(format, 2);
        var provider = decks.ProviderDescriptor;
        var route = provider.Capabilities.Single(cap => cap.Kind == "media.route");
        var resources = provider.Resources.Where(resource => resource.Kind == "media.route").ToArray();
        var owners = new[]
        {
            new SequenceMediaOwnerRequest(MediaSourceId.New(), MediaAssetId.New(), media.Path, hash, 0, 0),
            new SequenceMediaOwnerRequest(MediaSourceId.New(), MediaAssetId.New(), media.Path, hash, 0, 0)
        };
        var authority = control.State;
        var staged = new PreparedExecutionContract(RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(authority.ProductionId.Value, authority.Revision),
            new Generation(1),
            owners.Select((o, i) => new PreparedExecutionBinding(o.SourceId.Value,
                route.CapabilityId, resources[i], o.SourceId, null)).ToArray());
        var sequenceId = Guid.NewGuid();
        var map = SequenceRuntimePositionMap.Prepare(sequenceId, 7, format.FrameRate,
            20 * 4800, runtime.NextSequenceOutputSlot, 1);
        SequenceExecutionClip Clip(int owner, SequenceMediaKind kind, int order) =>
            new(Guid.NewGuid(), Guid.Parse(owners[owner].AssetId.ToString()), hash,
                kind, owners[owner].SourceId.ToString(), order,
                kind == SequenceMediaKind.Audio ? "program" : null,
                0, 0, null, 0, 0, false);
        var snapshot = new SequenceExecutionSnapshot(
            SequenceExecutionSnapshotContract.Version, sequenceId, 7,
            new(50, 1), 20 * 4800,
            [new SequenceExecutionSlice(0, 20 * 4800,
            [
                Clip(0, SequenceMediaKind.Video, 0),
                Clip(1, SequenceMediaKind.Video, 1),
                Clip(0, SequenceMediaKind.Audio, 2),
                Clip(1, SequenceMediaKind.Audio, 3)
            ])]);
        var prepared = decks.PrepareSequenceMedia(staged, map, owners, executionSnapshot: snapshot);
        Assert.True(prepared.Prepared, prepared.Failure?.Message);
        Assert.Equal(2, prepared.DecoderOwners);

        var host = Guid.NewGuid().ToString("D");
        var reference = new SequencePreparationReference(
            SequencePreparationContract.Version, sequenceId, 7, host,
            staged.PreparedExecutionId.ToString(), 1);
        decks.BindSequencePreviewOutput(reference, mediaB,
            authority.ProductionId.Value, authority.Revision, runtime);
        var playing = decks.ApplySequenceShuttle(
            new(SequenceShuttleContract.Version, reference, 0, SequenceShuttleAction.Play),
            host, authority.ProductionId.Value, authority.Revision,
            () => runtime.NextSequenceOutputSlot);
        Assert.Equal("PLAYING", playing.State);

        var preview = decks.PresentSequenceBoundary(runtime, runtime.NextSequenceOutputSlot);
        Assert.True(preview.Published, preview.FailureCode);
        Assert.Equal("UNAVAILABLE", decks.GetSequenceOutput(reference, host).OutputState);
        using (var first = runtime.ProcessNextBoundary())
        {
            decks.ObserveSequenceProgramBoundary(first);
            Assert.Equal(new MediaSourceId(sourceA.Value), first.CommittedProgramSourceId);
        }
        var beforeTake = decks.GetSequenceOutput(reference, host);
        Assert.Equal("PREVIEW_READY", beforeTake.OutputState);
        Assert.Equal(1UL, beforeTake.PublishedSourceFrames);

        // A generic Program CUT cannot adopt these native owners without
        // the separate optimistic sequence Take reservation.
        var generic = control.CutProgram(new CutProgramCommand(
            new ControlCommandMetadata(ControlContractVersion.Current,
                CommandId.New(), control.State.ProductionId, control.State.Revision), sourceB));
        Assert.True(generic.Accepted);
        var genericExecution = Assert.IsType<ControlHostExecutionPackage>(generic.Execution);
        Assert.Throws<InvalidOperationException>(() => decks.RequireArmedSequenceTake(
            genericExecution.PreparedExecution, genericExecution.ProgramTransition));
        control.RejectRuntimeCommit(genericExecution.PreparedExecution.PreparedExecutionId,
            new Failure("runtime.sequence.take_not_armed", "Unarmed native source must not change Program."));
        Assert.Equal(new MediaSourceId(sourceA.Value), runtime.CommittedProgramSourceId);

        var previous = control.State.Revision;
        var takeCommand = new SequenceOutputCommand(
            SequenceOutputContract.Version, reference, mediaB.ToString(),
            previous.Value, Guid.NewGuid());
        var armed = decks.ArmSequenceTake(takeCommand,
            authority.ProductionId.Value, previous, runtime, host);
        Assert.Equal("TAKE_PENDING", armed.OutputState);
        var cut = control.CutProgram(new CutProgramCommand(
            new ControlCommandMetadata(ControlContractVersion.Current, CommandId.New(),
                control.State.ProductionId, previous), sourceB));
        Assert.True(cut.Accepted, cut.Failure?.Message);
        var preparedCut = Assert.IsType<ControlHostExecutionPackage>(cut.Execution);
        Assert.True(decks.IsBoundSequenceProgramTarget(mediaB));
        Assert.Equal(takeCommand, decks.RequireArmedSequenceTake(
            preparedCut.PreparedExecution, preparedCut.ProgramTransition));
        var unapprovedCommitAttempts = 0;
        Assert.Throws<InvalidOperationException>(() =>
            decks.CommitArmedSequenceTake(
                takeCommand with { TransactionId = Guid.NewGuid() },
                preparedCut.PreparedExecution,
                () =>
                {
                    unapprovedCommitAttempts++;
                    return runtime.ApplyArmedSequenceTake(preparedCut.PreparedExecution,
                        preparedCut.ProgramSinkId, preparedCut.ProgramTransition,
                        () => throw new InvalidOperationException(
                            "Unapproved native transaction reached Program."));
                }));
        Assert.Equal(0, unapprovedCommitAttempts);
        Assert.Equal(new MediaSourceId(sourceA.Value), runtime.CommittedProgramSourceId);

        // A completed Preview-era boundary may be observed only after TAKE.
        // Its old Program frame must neither confirm nor fail the new sink.
        var inFlightPreview = decks.PresentSequenceBoundary(runtime, runtime.NextSequenceOutputSlot);
        Assert.True(inFlightPreview.Published, inFlightPreview.FailureCode);
        using (var delayedPreview = runtime.ProcessNextBoundary())
        {
            Assert.Equal(new MediaSourceId(sourceA.Value), delayedPreview.CommittedProgramSourceId);
            Assert.Equal(takeCommand, decks.RequireArmedSequenceTake(
                preparedCut.PreparedExecution, preparedCut.ProgramTransition));

            var committed = decks.CommitArmedSequenceTake(takeCommand, preparedCut.PreparedExecution,
                () => runtime.ApplyArmedSequenceTake(preparedCut.PreparedExecution,
                    preparedCut.ProgramSinkId, preparedCut.ProgramTransition,
                    () => decks.AuthorizeSequenceProgram(reference, mediaB,
                        authority.ProductionId.Value, previous,
                        preparedCut.PreparedExecution.AuthoritySnapshot.Revision, runtime)));
            Assert.True(committed.Committed, committed.Commit?.Failure?.Message ??
                committed.Prepare.Failure?.Message);
            Assert.True(control.ConfirmRuntimeCommit(
                preparedCut.PreparedExecution.PreparedExecutionId, committed.Commit!).Committed);
            decks.ObserveSequenceProgramBoundary(delayedPreview);
            var awaitingRealProgram = decks.GetSequenceOutput(reference, host);
            Assert.Equal("TAKE_COMMITTED", awaitingRealProgram.OutputState);
            Assert.Null(awaitingRealProgram.LastProgramSlot);
            Assert.Equal(previous.Value + 1, awaitingRealProgram.ProductionRevision);
        }

        var program = decks.PresentSequenceBoundary(runtime, runtime.NextSequenceOutputSlot);
        Assert.True(program.Published, program.FailureCode);
        using (var taken = runtime.ProcessNextBoundary())
        {
            decks.ObserveSequenceProgramBoundary(taken);
            Assert.Equal(mediaB, taken.CommittedProgramSourceId);
            Assert.Equal(mediaB, taken.ProgramFrame.SourceId);
            Assert.True(taken.ProgramPixels.Length > 0);
            Assert.Equal((byte)255, taken.ProgramPixels.Span[3]);
            Assert.NotEmpty(taken.ProgramAudioPayload);
            Assert.Equal(960 * 2 * sizeof(float), taken.ProgramAudioPayload.Length);
        }
        var observed = decks.GetSequenceOutput(reference, host);
        Assert.Equal("PROGRAM_CONFIRMED", observed.OutputState);
        Assert.Equal(2UL, observed.PublishedSourceFrames);
        Assert.NotNull(observed.LastProgramSlot);
        Assert.Equal(control.State.Revision.Value, observed.ProductionRevision);
        var committedRevision = control.State.Revision;
        // A lost promotion response may only reconcile the same native
        // owner/committed successor. It must not increment production again.
        decks.AuthorizeSequenceProgram(reference, mediaB, authority.ProductionId.Value,
            previous, committedRevision, runtime);
        Assert.Equal(committedRevision, control.State.Revision);
        Assert.Throws<InvalidOperationException>(() => decks.ArmSequenceTake(
            takeCommand with { TransactionId = Guid.NewGuid() },
            authority.ProductionId.Value, committedRevision, runtime, host));

        // A substituted same-slot result from a different Runtime lifetime
        // cannot prove this Program sink. Once failed, fresh native ingress
        // must not silently restore a previous PROGRAM_CONFIRMED state.
        await using (var foreignRuntime = new RuntimeProgramExecutionService(
            new MediaSourceId(sourceA.Value), mediaB, format, new NoopRecordingWriter()))
        {
            await using var foreignJournal = new BoundedProductionJournal(64);
            var foreignControl = new ControlHostService(
                spec, foreignRuntime.ProviderDescriptors, foreignJournal);
            Confirm(foreignControl, foreignRuntime, foreignControl.Initialize());
            var inFlightSlot = runtime.NextSequenceOutputSlot;
            while (foreignRuntime.NextSequenceOutputSlot < inFlightSlot)
            {
                using var discarded = foreignRuntime.ProcessNextBoundary();
            }
            var inFlight = decks.PresentSequenceBoundary(runtime, inFlightSlot);
            Assert.True(inFlight.Published, inFlight.FailureCode);
            using (var foreignBoundary = foreignRuntime.ProcessNextBoundary())
            {
                Assert.Equal(inFlightSlot, foreignBoundary.SequenceNumber);
                Assert.NotEqual(mediaB, foreignBoundary.CommittedProgramSourceId);
                decks.ObserveSequenceProgramBoundary(foreignBoundary);
            }
            var failedSink = decks.GetSequenceOutput(reference, host);
            Assert.Equal("FAILED", failedSink.OutputState);
            Assert.Equal("runtime.sequence.program_sink_unconfirmed", failedSink.FailureCode);
            var suppressed = decks.PresentSequenceBoundary(
                runtime, runtime.NextSequenceOutputSlot);
            Assert.False(suppressed.Published);
            Assert.Equal("FAILED", suppressed.State);
            Assert.Equal(failedSink.FailureCode, suppressed.FailureCode);
            using var degraded = runtime.ProcessNextBoundary();
            Assert.Equal(mediaB, degraded.CommittedProgramSourceId);
            Assert.Equal((byte)0, degraded.ProgramPixels.Span[0]);
            Assert.Equal((byte)0, degraded.ProgramPixels.Span[1]);
            Assert.Equal((byte)0, degraded.ProgramPixels.Span[2]);
            // Even a later same-slot callback from the legitimate Runtime
            // cannot revive a sink that has already failed closed.
            decks.ObserveSequenceProgramBoundary(degraded);
            Assert.Equal("FAILED", decks.GetSequenceOutput(reference, host).OutputState);
        }

        // A subsequent ordinary committed Preview mutation must not keep
        // publishing the old sequence on Program with an outdated authority
        // revision. It is invalidated and Program falls back to black.
        var reselect = control.SelectPreview(new SelectPreviewCommand(
            new ControlCommandMetadata(ControlContractVersion.Current, CommandId.New(),
                control.State.ProductionId, control.State.Revision), sourceA));
        Confirm(control, runtime, reselect);
        Assert.Equal(mediaB, runtime.CommittedProgramSourceId);
        var staleOutput = decks.PresentSequenceBoundary(
            runtime, runtime.NextSequenceOutputSlot);
        Assert.False(staleOutput.Published);
        Assert.Equal("STALE", staleOutput.State);
        Assert.Equal("runtime.sequence.route_changed", staleOutput.FailureCode);
        Assert.Equal("STALE", decks.GetSequenceOutput(reference, host).OutputState);

        var cancellation = decks.CancelPreparedSequence(reference, host);
        Assert.Equal("CANCELED", cancellation.State);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
        using (var afterCancellation = runtime.ProcessNextBoundary())
        {
            Assert.Equal(mediaB, afterCancellation.CommittedProgramSourceId);
            var lostPixels = afterCancellation.ProgramPixels.Span;
            Assert.Equal((byte)0, lostPixels[0]);
            Assert.Equal((byte)0, lostPixels[1]);
            Assert.Equal((byte)0, lostPixels[2]);
            Assert.Equal((byte)255, lostPixels[3]);
        }
        Assert.Equal(0, runtime.ProgramReadbackPoolStatistics.ActiveBuffers);
    }

    [Theory]
    [InlineData("seek")]
    [InlineData("stop")]
    [InlineData("cancel")]
    public async Task Armed_sequence_take_is_revoked_by_native_transport_change_before_commit(string change)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var media = LocalMediaTestAsset.ExtractReference1080p50();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(media.Path)));
        var format = VideoFormat.Hd1080p50Rgba8;
        var sourceA = ProductionSourceId.New();
        var sourceB = ProductionSourceId.New();
        var mediaB = new MediaSourceId(sourceB.Value);
        await using var runtime = new RuntimeProgramExecutionService(
            new MediaSourceId(sourceA.Value), mediaB, format, new NoopRecordingWriter());
        await using var journal = new BoundedProductionJournal(64);
        var spec = new ProductionSpecification(ControlContractVersion.Current,
            ProductionId.New(), "Native sequence Take mutation fence",
            [new ProductionSourceSpecification(sourceA, "A"),
             new ProductionSourceSpecification(sourceB, "B")],
            new ProductionRoutingState(sourceA, sourceA),
            initialOutputRoles:
            [ProductionOutputRoleState.Program(sourceA), ProductionOutputRoleState.Aux(sourceB)]);
        var control = new ControlHostService(spec, runtime.ProviderDescriptors, journal);
        Confirm(control, runtime, control.Initialize());
        Confirm(control, runtime, control.SelectPreview(new SelectPreviewCommand(
            new ControlCommandMetadata(ControlContractVersion.Current, CommandId.New(),
                control.State.ProductionId, control.State.Revision), sourceB)));

        using var decks = new LocalMediaDeckRuntimeService(format, 2);
        var provider = decks.ProviderDescriptor;
        var route = provider.Capabilities.Single(cap => cap.Kind == "media.route");
        var resource = provider.Resources.First(r => r.Kind == "media.route");
        var owner = new SequenceMediaOwnerRequest(
            MediaSourceId.New(), MediaAssetId.New(), media.Path, hash, 0, 0);
        var authority = control.State;
        var staged = new PreparedExecutionContract(RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(authority.ProductionId.Value, authority.Revision),
            new Generation(1),
            [new PreparedExecutionBinding(owner.SourceId.Value, route.CapabilityId,
                resource, owner.SourceId, null)]);
        var sequenceId = Guid.NewGuid();
        const long duration = 20 * 4800;
        var map = SequenceRuntimePositionMap.Prepare(
            sequenceId, 7, format.FrameRate, duration, runtime.NextSequenceOutputSlot, 1);
        SequenceExecutionClip Clip(SequenceMediaKind kind, int order) =>
            new(Guid.NewGuid(), Guid.Parse(owner.AssetId.ToString()), hash,
                kind, owner.SourceId.ToString(), order,
                kind == SequenceMediaKind.Audio ? "program" : null,
                0, 0, null, 0, 0, false);
        var snapshot = new SequenceExecutionSnapshot(
            SequenceExecutionSnapshotContract.Version, sequenceId, 7,
            new(50, 1), duration,
            [new SequenceExecutionSlice(0, duration,
                [Clip(SequenceMediaKind.Video, 0), Clip(SequenceMediaKind.Audio, 1)])]);
        var preparation = decks.PrepareSequenceMedia(staged, map, [owner],
            executionSnapshot: snapshot);
        Assert.True(preparation.Prepared, preparation.Failure?.Message);

        var epoch = Guid.NewGuid().ToString("D");
        var reference = new SequencePreparationReference(
            SequencePreparationContract.Version, sequenceId, 7, epoch,
            staged.PreparedExecutionId.ToString(), 1);
        decks.BindSequencePreviewOutput(reference, mediaB,
            authority.ProductionId.Value, authority.Revision, runtime);
        var playing = decks.ApplySequenceShuttle(
            new(SequenceShuttleContract.Version, reference, 0, SequenceShuttleAction.Play),
            epoch, authority.ProductionId.Value, authority.Revision,
            () => runtime.NextSequenceOutputSlot);
        Assert.Equal("PLAYING", playing.State);
        var first = decks.PresentSequenceBoundary(runtime, runtime.NextSequenceOutputSlot);
        Assert.True(first.Published, first.FailureCode);
        using (var boundary = runtime.ProcessNextBoundary())
            decks.ObserveSequenceProgramBoundary(boundary);
        Assert.Equal("PREVIEW_READY", decks.GetSequenceOutput(reference, epoch).OutputState);

        var command = new SequenceOutputCommand(SequenceOutputContract.Version,
            reference, mediaB.ToString(), authority.Revision.Value, Guid.NewGuid());
        Assert.Equal("TAKE_PENDING", decks.ArmSequenceTake(command,
            authority.ProductionId.Value, authority.Revision, runtime, epoch).OutputState);

        if (change == "cancel")
        {
            Assert.Equal("CANCELED", decks.CancelPreparedSequence(reference, epoch).State);
        }
        else
        {
            var action = change == "seek"
                ? SequenceShuttleAction.Seek : SequenceShuttleAction.Stop;
            var request = new SequenceShuttleRequest(
                SequenceShuttleContract.Version, reference, playing.StateRevision, action,
                change == "seek" ? 5 * 4800L : null);
            _ = decks.ApplySequenceShuttle(request, epoch,
                authority.ProductionId.Value, authority.Revision,
                () => runtime.NextSequenceOutputSlot);
            var invalidated = decks.GetSequenceOutput(reference, epoch);
            Assert.Equal("UNAVAILABLE", invalidated.OutputState);
            Assert.Null(invalidated.LastProgramSlot);
            Assert.ThrowsAny<InvalidOperationException>(() => decks.ArmSequenceTake(
                command with { TransactionId = Guid.NewGuid() },
                authority.ProductionId.Value, authority.Revision, runtime, epoch));
        }

        var cut = control.CutProgram(new CutProgramCommand(
            new ControlCommandMetadata(ControlContractVersion.Current, CommandId.New(),
                control.State.ProductionId, control.State.Revision), sourceB));
        Assert.True(cut.Accepted, cut.Failure?.Message);
        var package = Assert.IsType<ControlHostExecutionPackage>(cut.Execution);
        Assert.ThrowsAny<InvalidOperationException>(() =>
            decks.RequireArmedSequenceTake(package.PreparedExecution, package.ProgramTransition));
        control.RejectRuntimeCommit(package.PreparedExecution.PreparedExecutionId,
            new Failure("runtime.sequence.take_not_armed",
                "Transport changed after the last confirmed Preview frame."));
        Assert.Equal(new MediaSourceId(sourceA.Value), runtime.CommittedProgramSourceId);
        if (change != "cancel")
            Assert.Equal("CANCELED", decks.CancelPreparedSequence(reference, epoch).State);
        Assert.Equal(0, decks.ResourceSnapshot.ReservedOwners);
    }

    private static void Confirm(ControlHostService control, RuntimeProgramExecutionService runtime,
        ControlHostOperationResult staged)
    {
        Assert.True(staged.Accepted, staged.Failure?.Message);
        var package = Assert.IsType<ControlHostExecutionPackage>(staged.Execution);
        var applied = runtime.ApplyExecution(package.PreparedExecution,
            package.ProgramSinkId, package.ProgramTransition);
        Assert.True(applied.Committed, applied.Commit?.Failure?.Message ?? applied.Prepare.Failure?.Message);
        var confirmed = control.ConfirmRuntimeCommit(
            package.PreparedExecution.PreparedExecutionId, applied.Commit!);
        Assert.True(confirmed.Committed, confirmed.Failure?.Message);
    }

    private sealed class NoopRecordingWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(RecordingProgramSample sample,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
