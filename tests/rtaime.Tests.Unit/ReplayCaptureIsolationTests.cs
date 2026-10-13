// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class ReplayCaptureIsolationTests
{
    [Fact]
    public async Task Every_stop_waits_for_the_same_worker_and_cancellation_does_not_abandon_drain()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-replay-drain-" + Guid.NewGuid().ToString("N"));
        var writer = new Writer();
        var capture = new ReplayCaptureEngine(root, () => writer, queueCapacity: 1);
        var lease = new Lease();
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        using var frame = gpu.Upload(MediaSourceId.New(),
            RgbaFrameBuffer.Solid(new VideoFormat(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive), 1, 2, 3),
            new FrameTiming(0, 0, new Timebase(1, 50)), Generation.Initial, "replay-drain");
        var audio = new AudioBufferDescriptor(MediaContractVersion.Current, AudioStreamId.New(),
            AudioFormat.Stereo48kFloat32, Identity.New(),
            new AudioBufferTiming(0, 960, 0, new Timebase(1, 50)), new OpaqueAudioHandle("replay-drain", "0"));
        Task? first = null;
        Task? second = null;
        try
        {
            Assert.True(capture.TryEnqueue(MediaSinkId.New(), frame.Descriptor, audio, lease, new byte[7680]).Accepted);
            await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first = capture.StopAsync().AsTask();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.StopAsync(cancelled.Token).AsTask());
            second = capture.StopAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(0, lease.Disposals);
            writer.Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ReplayCaptureState.Disabled, capture.Snapshot.CaptureState);
            Assert.Equal(1, lease.Disposals);
            Assert.Equal(1, writer.Finalizations);
        }
        finally
        {
            writer.Release.TrySetResult();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(5));
            await capture.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Failed_writer_open_aborts_partial_resources_and_returns_the_sample_lease()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-replay-open-" + Guid.NewGuid().ToString("N"));
        var writer = new Writer { FailOpen = true };
        var capture = new ReplayCaptureEngine(root, () => writer);
        var lease = new Lease();
        using var gpu = new GpuProcessingProvider(new ManagedReferenceGpuBackend());
        gpu.Start();
        using var frame = gpu.Upload(MediaSourceId.New(),
            RgbaFrameBuffer.Solid(new VideoFormat(2, 1, FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive), 1, 2, 3),
            new FrameTiming(0, 0, new Timebase(1, 50)), Generation.Initial, "replay-open");
        var audio = new AudioBufferDescriptor(MediaContractVersion.Current, AudioStreamId.New(),
            AudioFormat.Stereo48kFloat32, Identity.New(),
            new AudioBufferTiming(0, 960, 0, new Timebase(1, 50)), new OpaqueAudioHandle("replay-open", "0"));
        try
        {
            Assert.True(capture.TryEnqueue(MediaSinkId.New(), frame.Descriptor, audio, lease, new byte[7680]).Accepted);
            await capture.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, writer.Aborts);
            Assert.Equal(1, lease.Disposals);
            Assert.Equal(1UL, capture.Snapshot.Statistics.DroppedSamples);
        }
        finally
        {
            await capture.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Lease : IProgramRecordingPayloadLease
    {
        public int Disposals { get; private set; }
        public ReadOnlyMemory<byte> Memory => new byte[] { 1, 2, 3, 255 };
        public void Dispose() => Disposals++;
    }

    private sealed class Writer : IReplaySegmentWriter
    {
        private IProgramRecordingPayloadLease? _payload;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailOpen { get; init; }
        public int Aborts { get; private set; }
        public int Finalizations { get; private set; }
        public string? FinalPath { get; private set; }
        public string ConfigureTarget(string directory, string name)
        {
            FinalPath = Path.Combine(directory, name);
            return name;
        }
        public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken token)
        {
            if (FailOpen) throw new IOException("Partial writer initialization failed.");
            return ValueTask.CompletedTask;
        }
        public void StagePayload(ulong sequence, IProgramRecordingPayloadLease payload, ReadOnlyMemory<byte> audio) => _payload = payload;
        public void DiscardPayload(ulong sequence) => Interlocked.Exchange(ref _payload, null)?.Dispose();
        public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken token)
        {
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            finally { DiscardPayload(sample.SequenceNumber); }
        }
        public ValueTask FinalizeAsync(CancellationToken token)
        {
            Finalizations++;
            File.WriteAllBytes(FinalPath!, new byte[] { 1 });
            return ValueTask.CompletedTask;
        }
        public ValueTask AbortAsync(CancellationToken token)
        {
            Aborts++;
            DiscardPayload(0);
            return ValueTask.CompletedTask;
        }
    }
}
