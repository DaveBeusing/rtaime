using System.Diagnostics;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;

namespace rtaime.Tests.Performance;

public sealed class RecordingPerformanceTests
{
    [Fact]
    public async Task Slow_storage_does_not_synchronously_block_program_enqueue_path()
    {
        var writer = new BlockingWriter();
        await using var recorder = new ProgramRecorder(writer, capacity: 8);
        var sink = MediaSinkId.New();
        Assert.True((await recorder.StartAsync(new RecordingStartRequest(
            RecordingContractVersion.Current,
            RecordingSessionId.New(),
            new RecordingOutputDescriptor(RecordingOutputId.New(), sink, "Program")))).Succeeded);

        Assert.True(recorder.TryEnqueue(Frame(0)).Accepted);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        for (ulong sequence = 1; sequence <= 20_000; sequence++)
            _ = recorder.TryEnqueue(Frame(sequence));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"20,000 nonblocking recording enqueue attempts took {stopwatch.Elapsed}.");
        Assert.True(recorder.Snapshot.Statistics.Dropped > 0);
        Assert.Equal(RecordingLifecycleState.Recording, recorder.Snapshot.State);

        writer.Release();
        var stop = await recorder.StopAsync();
        Assert.Equal(RecordingStopStatus.Stopped, stop.Status);
    }

    private static readonly MediaSourceId SourceId =
        new(Identity.Parse("97000000-0000-0000-0000-000000000001"));
    private static readonly SurfaceDescriptor Surface = new(
        new SurfaceId(Identity.Parse("97000000-0000-0000-0000-000000000002")),
        VideoFormat.Hd1080p50Rgba8,
        SurfaceStorageDomain.Shared,
        SurfaceOwnership.SharedLease,
        new SurfaceLifetimeDescriptor(Generation.Initial, Identity.Parse("97000000-0000-0000-0000-000000000003")),
        new OpaqueSurfaceHandle("recording.performance", "shared-surface"));

    private static FrameDescriptor Frame(ulong sequence) => new(
        MediaContractVersion.Current,
        SourceId,
        Surface,
        new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));

    private sealed class BlockingWriter : IProgramRecordingWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask AbortAsync(CancellationToken cancellationToken)
        {
            Release();
            return ValueTask.CompletedTask;
        }

        public void Release() => _release.TrySetResult();
    }
}
