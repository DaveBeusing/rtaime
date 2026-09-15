using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;

namespace rtaime.Tests.Failure;

public sealed class RecordingFailureIsolationTests
{
    [Fact]
    public async Task Recording_writer_failure_does_not_change_committed_runtime_or_stop_program_frames()
    {
        var sourceA = new MediaSourceId(Identity.Parse("95000000-0000-0000-0000-000000000001"));
        var sourceB = new MediaSourceId(Identity.Parse("95000000-0000-0000-0000-000000000002"));
        var sink = new MediaSinkId(Identity.Parse("95000000-0000-0000-0000-000000000003"));
        var provider = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
        var routeCapability = provider.Descriptor.Capabilities.Single(capability => capability.Kind == "media.route");
        var resource = provider.Descriptor.Resources[0];
        var prepared = new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(1)),
            Generation.Initial,
            new[]
            {
                new PreparedExecutionBinding(
                    Identity.New(),
                    routeCapability.CapabilityId,
                    resource,
                    sourceA,
                    sink)
            });

        var runtime = new TransactionalRuntime(new InMemoryRuntimeResourceReservationManager());
        var prepare = runtime.Prepare(prepared);
        Assert.Equal(RuntimePrepareStatus.Prepared, prepare.Status);
        var commit = runtime.Commit(new RuntimeCommitRequest(
            RuntimeContractVersion.Current,
            prepared.PreparedExecutionId,
            prepare.ReservationId!.Value,
            Revision.Initial));
        Assert.Equal(RuntimeCommitStatus.Committed, commit.Status);
        var committedBeforeFailure = runtime.ActiveExecution!;

        await using var recorder = new ProgramRecorder(new FailingWriteWriter());
        var start = await recorder.StartAsync(new RecordingStartRequest(
            RecordingContractVersion.Current,
            RecordingSessionId.New(),
            new RecordingOutputDescriptor(RecordingOutputId.New(), sink, "Program")));
        Assert.True(start.Succeeded);

        var frame0 = provider.SourceA.GenerateFrame(0);
        Assert.True(recorder.TryEnqueue(frame0).Accepted);
        await WaitForAsync(() => recorder.Snapshot.State == RecordingLifecycleState.Failed);

        Assert.Same(committedBeforeFailure, runtime.ActiveExecution);
        Assert.Equal(committedBeforeFailure.ExecutionRevision, runtime.State.ExecutionRevision);
        Assert.Equal("recording.write.writer_failure", recorder.Snapshot.Failure!.Code);

        var frame1 = provider.SourceA.GenerateFrame(1);
        Assert.Equal(1UL, frame1.Timing.SequenceNumber);
        Assert.Equal(sourceA, frame1.SourceId);
        Assert.Same(committedBeforeFailure, runtime.ActiveExecution);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException("Timed out waiting for recorder failure state.");
    }

    private sealed class FailingWriteWriter : IProgramRecordingWriter
    {
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("Simulated storage failure."));

        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
