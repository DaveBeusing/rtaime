using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class RecordingFoundationTests
{
    [Fact]
    public async Task Start_and_stop_drain_samples_and_finalize_in_order()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer, capacity: 4);
        var request = Request();

        var start = await recorder.StartAsync(request);
        Assert.True(start.Succeeded);
        Assert.Equal(RecordingLifecycleState.Recording, recorder.Snapshot.State);

        Assert.Equal(RecordingEnqueueStatus.Accepted, recorder.TryEnqueue(Frame(0)).Status);
        Assert.Equal(RecordingEnqueueStatus.Accepted, recorder.TryEnqueue(Frame(1)).Status);

        var stop = await recorder.StopAsync();

        Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
        Assert.Equal(RecordingLifecycleState.Completed, recorder.Snapshot.State);
        Assert.Equal(new ulong[] { 0, 1 }, writer.Samples.Select(sample => sample.SequenceNumber));
        Assert.Equal(2UL, recorder.Snapshot.Statistics.Written);
        Assert.Equal(1, writer.FinalizeCount);
        Assert.Contains(recorder.Observations, observation => observation.Code == "recording.finalized");
    }

    [Fact]
    public async Task Repeated_recording_sessions_reopen_writer_with_new_output_identity()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer, capacity: 2);

        var first = Request();
        Assert.True((await recorder.StartAsync(first)).Succeeded);
        Assert.True(recorder.TryEnqueue(Frame(0)).Accepted);
        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);

        var second = Request();
        Assert.NotEqual(first.Output.OutputId, second.Output.OutputId);
        Assert.True((await recorder.StartAsync(second)).Succeeded);
        Assert.True(recorder.TryEnqueue(Frame(0)).Accepted);
        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);

        Assert.Equal(2, writer.OpenCount);
        Assert.Equal(2, writer.FinalizeCount);
        Assert.Equal(second.Output.OutputId, recorder.Snapshot.Output!.OutputId);
    }

    [Fact]
    public async Task Invalid_start_while_recording_is_rejected_without_replacing_active_session()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer);
        var first = Request();
        var second = Request();

        Assert.True((await recorder.StartAsync(first)).Succeeded);
        var rejected = await recorder.StartAsync(second);

        Assert.Equal(RecordingStartStatus.Rejected, rejected.Status);
        Assert.Equal("recording.start.invalid_state", rejected.Failure!.Code);
        Assert.Equal(first.SessionId, recorder.Snapshot.SessionId);
        Assert.Equal(first.Output.OutputId, recorder.Snapshot.Output!.OutputId);
        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);
    }

    [Fact]
    public async Task Output_unavailable_fails_start_without_entering_recording_state()
    {
        var writer = new TestWriter
        {
            OpenException = new RecordingOutputUnavailableException("Disk target unavailable.")
        };
        await using var recorder = new ProgramRecorder(writer);

        var result = await recorder.StartAsync(Request());

        Assert.Equal(RecordingStartStatus.Failed, result.Status);
        Assert.Equal("recording.output.unavailable", result.Failure!.Code);
        Assert.Equal(RecordingLifecycleState.Failed, recorder.Snapshot.State);
        Assert.Equal(1UL, recorder.Snapshot.Statistics.WriterFailures);
    }

    [Fact]
    public async Task Runtime_shutdown_drains_and_finalizes_active_recording()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer, capacity: 4);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);
        Assert.True(recorder.TryEnqueue(Frame(0)).Accepted);
        Assert.True(recorder.TryEnqueue(Frame(1)).Accepted);

        var result = await recorder.ShutdownAsync();

        Assert.Equal(RecordingStopStatus.Stopped, result.Status);
        Assert.Equal(RecordingLifecycleState.Completed, recorder.Snapshot.State);
        Assert.Equal(2UL, recorder.Snapshot.Statistics.Written);
        Assert.Contains(recorder.Observations, observation => observation.Code == "recording.shutdown");
    }

    [Fact]
    public async Task Non_monotonic_sequence_is_rejected_fail_closed()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);

        Assert.True(recorder.TryEnqueue(Frame(5)).Accepted);
        var duplicate = recorder.TryEnqueue(Frame(5));

        Assert.Equal(RecordingEnqueueStatus.Rejected, duplicate.Status);
        Assert.Equal("recording.sequence.non_monotonic", duplicate.Failure!.Code);
        Assert.Equal(1UL, recorder.Snapshot.Statistics.Rejected);
        await recorder.StopAsync();
    }

    private static RecordingStartRequest Request() => new(
        RecordingContractVersion.Current,
        RecordingSessionId.New(),
        new RecordingOutputDescriptor(RecordingOutputId.New(), MediaSinkId.New(), "Program"));

    private static FrameDescriptor Frame(ulong sequence)
    {
        var surface = new SurfaceDescriptor(
            SurfaceId.New(),
            VideoFormat.Hd1080p50Rgba8,
            SurfaceStorageDomain.Shared,
            SurfaceOwnership.SharedLease,
            new SurfaceLifetimeDescriptor(new Generation(sequence), Identity.New()),
            new OpaqueSurfaceHandle("test.recording", $"surface-{sequence}"));
        return new FrameDescriptor(
            MediaContractVersion.Current,
            MediaSourceId.New(),
            surface,
            new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));
    }

    private sealed class TestWriter : IProgramRecordingWriter
    {
        public Exception? OpenException { get; init; }
        public List<RecordingProgramSample> Samples { get; } = new();
        public int OpenCount { get; private set; }
        public int FinalizeCount { get; private set; }

        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            if (OpenException is not null)
                throw OpenException;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Samples.Add(sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbortAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
