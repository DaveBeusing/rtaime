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
    public async Task Stop_waits_for_admitted_payload_staging_before_finalizing()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer, capacity: 4);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var staged = false;
        var enqueue = Task.Run(() => recorder.TryEnqueue(Frame(0), stagePayload: () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Payload stage was not released.");
            staged = true;
        }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var stop = Task.Run(async () => await recorder.StopAsync());
        try
        {
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        Assert.True((await enqueue).Accepted);
        Assert.Equal(RecordingStopStatus.Stopped, (await stop).Status);
        Assert.True(staged);
        Assert.Single(writer.Samples);
        Assert.Equal(1, writer.FinalizeCount);
    }

    [Fact]
    public async Task Rejected_enqueue_does_not_stage_payload_after_stop()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);
        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);

        var stagingCalls = 0;
        var enqueue = recorder.TryEnqueue(Frame(1), stagePayload: () => stagingCalls++);

        Assert.False(enqueue.Accepted);
        Assert.Equal(0, stagingCalls);
        Assert.Empty(writer.Samples);
    }

    [Fact]
    public async Task Failed_payload_stage_is_rejected_without_enqueuing()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);

        var enqueue = recorder.TryEnqueue(Frame(0), stagePayload: () => throw new IOException("Staging failed."));

        Assert.False(enqueue.Accepted);
        Assert.Equal("recording.payload.stage_failed", enqueue.Failure?.Code);
        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);
        Assert.Empty(writer.Samples);
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
        Assert.True(rejected.Failure.HasValue);
        Assert.Equal("recording.start.invalid_state", rejected.Failure.Value.Code);
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
        Assert.True(result.Failure.HasValue);
        Assert.Equal("recording.output.unavailable", result.Failure.Value.Code);
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
        Assert.True(duplicate.Failure.HasValue);
        Assert.Equal("recording.sequence.non_monotonic", duplicate.Failure.Value.Code);
        Assert.Equal(1UL, recorder.Snapshot.Statistics.Rejected);
        await recorder.StopAsync();
    }


    [Fact]
    public async Task Observation_history_remains_bounded_during_repeated_rejections()
    {
        var writer = new TestWriter();
        await using var recorder = new ProgramRecorder(writer);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);
        Assert.True(recorder.TryEnqueue(Frame(5)).Accepted);

        for (var index = 0; index < ProgramRecorder.RetainedObservationCapacity + 64; index++)
            Assert.Equal(RecordingEnqueueStatus.Rejected, recorder.TryEnqueue(Frame(5)).Status);

        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);

        var observations = recorder.Observations;
        Assert.Equal(ProgramRecorder.RetainedObservationCapacity, observations.Count);
        Assert.True(recorder.OverwrittenObservationCount > 0);
        Assert.Equal("recording.finalized", observations[^1].Code);
    }

    [Fact]
    public async Task Stalled_recording_writer_keeps_depth_bounded_and_resets_high_water_on_new_session()
    {
        var writer = new BlockingWriter();
        await using var recorder = new ProgramRecorder(writer, capacity: 2);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);
        try
        {
            Assert.True(recorder.TryEnqueue(Frame(1)).Accepted);
            await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(recorder.TryEnqueue(Frame(2)).Accepted);
            Assert.True(recorder.TryEnqueue(Frame(3)).Accepted);
            for (ulong sequence = 4; sequence <= 10_003; sequence++)
                Assert.Equal(RecordingEnqueueStatus.Dropped, recorder.TryEnqueue(Frame(sequence)).Status);
            var statistics = recorder.Snapshot.Statistics;
            Assert.Equal(2, statistics.QueueDepth);
            Assert.Equal(2, statistics.MaximumQueueDepth);
            Assert.Equal(2, statistics.QueueCapacity);
            Assert.True(statistics.Backpressured);
            Assert.Equal(10_000UL, statistics.Dropped);
        }
        finally
        {
            writer.Release.TrySetResult();
        }
        Assert.Equal(RecordingStopStatus.Stopped, (await recorder.StopAsync()).Status);
        Assert.Equal(0, recorder.Snapshot.Statistics.QueueDepth);
        Assert.False(recorder.Snapshot.Statistics.Backpressured);
        Assert.Equal(3UL, recorder.Snapshot.Statistics.Written);
        Assert.True((await recorder.StartAsync(Request())).Succeeded);
        Assert.Equal(0, recorder.Snapshot.Statistics.MaximumQueueDepth);
        await recorder.StopAsync();
    }

    private sealed class BlockingWriter : IProgramRecordingWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask AbortAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
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
