using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.VirtualMedia;
using rtaime.Recording;
using rtaime.Runtime;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RecordingRuntimeIntegrationTests
{
    [Fact]
    public async Task Committed_program_output_is_recorded_through_runtime_host_bridge()
    {
        var sourceA = new MediaSourceId(Identity.Parse("96000000-0000-0000-0000-000000000001"));
        var sourceB = new MediaSourceId(Identity.Parse("96000000-0000-0000-0000-000000000002"));
        var sink = new MediaSinkId(Identity.Parse("96000000-0000-0000-0000-000000000003"));
        var provider = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
        var runtime = CommitProgram(sourceA, sink, provider);
        var writer = new CollectingWriter();
        await using var recorder = new ProgramRecorder(writer, capacity: 4);
        var output = new RecordingOutputDescriptor(RecordingOutputId.New(), sink, "Program");
        Assert.True((await recorder.StartAsync(new RecordingStartRequest(
            RecordingContractVersion.Current,
            RecordingSessionId.New(),
            output))).Succeeded);

        var bridge = new RuntimeRecordingBridge(recorder);
        var frame = provider.SourceA.GenerateFrame(0);
        var enqueue = bridge.TryRecordCommittedProgram(runtime.ActiveExecution!, frame);

        Assert.True(enqueue.Accepted);
        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);
        Assert.Single(writer.Samples);
        Assert.Equal(output.OutputId, writer.Samples[0].OutputId);
        Assert.Equal(sourceA, writer.Samples[0].Video.SourceId);
        Assert.Equal(0UL, writer.Samples[0].SequenceNumber);
    }

    [Fact]
    public async Task Runtime_bridge_rejects_frame_that_is_not_the_committed_program_source()
    {
        var sourceA = MediaSourceId.New();
        var sourceB = MediaSourceId.New();
        var sink = MediaSinkId.New();
        var provider = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
        var runtime = CommitProgram(sourceA, sink, provider);
        var writer = new CollectingWriter();
        await using var recorder = new ProgramRecorder(writer);
        Assert.True((await recorder.StartAsync(new RecordingStartRequest(
            RecordingContractVersion.Current,
            RecordingSessionId.New(),
            new RecordingOutputDescriptor(RecordingOutputId.New(), sink, "Program")))).Succeeded);

        var bridge = new RuntimeRecordingBridge(recorder);
        var rejected = bridge.TryRecordCommittedProgram(runtime.ActiveExecution!, provider.SourceB.GenerateFrame(0));

        Assert.Equal(RecordingEnqueueStatus.Rejected, rejected.Status);
        Assert.True(rejected.Failure.HasValue);
        Assert.Equal("recording.runtime.source_mismatch", rejected.Failure.Value.Code);
        Assert.Empty(writer.Samples);
        await recorder.StopAsync();
    }

    private static TransactionalRuntime CommitProgram(
        MediaSourceId source,
        MediaSinkId sink,
        VirtualMediaReferenceProvider provider)
    {
        var capability = provider.Descriptor.Capabilities.Single(item => item.Kind == "media.route");
        var prepared = new PreparedExecutionContract(
            RuntimeContractVersion.Current,
            PreparedExecutionId.New(),
            new AuthoritySnapshotReference(Identity.New(), new Revision(1)),
            Generation.Initial,
            new[]
            {
                new PreparedExecutionBinding(
                    Identity.New(),
                    capability.CapabilityId,
                    provider.Descriptor.Resources[0],
                    source,
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
        return runtime;
    }

    private sealed class CollectingWriter : IProgramRecordingWriter
    {
        public List<RecordingProgramSample> Samples { get; } = new();

        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
        {
            Samples.Add(sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
