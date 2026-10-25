// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.Concurrent;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Gpu;
using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class ReplayCaptureContinuityTests
{
	[Theory]
	[InlineData(2, false)]
	[InlineData(12, false)]
	[InlineData(26, false)]
	[InlineData(2, true)]
	[InlineData(14, true)]
	[InlineData(30, true)]
	public async Task Queue_drops_split_at_the_actual_missing_sample_without_poisoning_queued_history(int backlog, bool fractionalRate)
	{
		await using var fixture = new Fixture(backlog, fractionalRate);
		Assert.True(fixture.Enqueue(0).Accepted);
		await fixture.Writers.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
		for (ulong sequence = 1; sequence <= (ulong)backlog; sequence++)
			Assert.True(fixture.Enqueue(sequence).Accepted);
		var missing = (ulong)backlog + 1;
		Assert.Equal(ReplayEnqueueStatus.Dropped, fixture.Enqueue(missing).Status);

		fixture.Writers.Release.TrySetResult();
		await fixture.Writers.Written((ulong)backlog).WaitAsync(TimeSpan.FromSeconds(5));
		var last = missing + 17;
		for (var sequence = missing + 1; sequence <= last; sequence++)
		{
			Assert.True(fixture.Enqueue(sequence).Accepted);
			await fixture.Writers.Written(sequence).WaitAsync(TimeSpan.FromSeconds(5));
		}
		await fixture.Capture.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		var snapshot = fixture.Capture.Snapshot;
		Assert.Null(snapshot.Failure);
		Assert.Equal(1UL, snapshot.Statistics.DroppedSamples);
		Assert.All(snapshot.Segments, segment => Assert.False(segment.FirstProgramSequence <= missing && segment.LastProgramSequence >= missing));
		var before = snapshot.Segments.Where(segment => segment.LastProgramSequence < missing).ToArray();
		var after = snapshot.Segments.Where(segment => segment.FirstProgramSequence > missing).ToArray();
		Assert.NotEmpty(before);
		Assert.NotEmpty(after);
		Assert.All(before, segment => Assert.False(segment.DiscontinuityBefore));
		Assert.True(after[0].DiscontinuityBefore);
		Assert.All(after.Skip(1), segment => Assert.False(segment.DiscontinuityBefore));
		Assert.Equal(fixture.TimeAt(missing), before[^1].End);
		Assert.Equal(fixture.TimeAt(missing + 1), after[0].Start);
		Assert.Equal(fixture.TimeAt(last + 1), after[^1].End);

		AssertRetained(fixture.Capture.Store, new ReplayRange(TimeSpan.Zero, before[^1].End));
		AssertRetained(fixture.Capture.Store, new ReplayRange(after[0].Start, after[^1].End));
		AssertMissing(fixture.Capture.Store, new ReplayRange(TimeSpan.Zero, after[^1].End));
		var gapMiddle = TimeSpan.FromTicks((before[^1].End.Ticks + after[0].Start.Ticks) / 2);
		AssertMissing(fixture.Capture.Store, new ReplayRange(gapMiddle, after[^1].End));
		AssertMissing(fixture.Capture.Store, new ReplayRange(TimeSpan.Zero, gapMiddle));
		Assert.All(fixture.Leases, lease => Assert.Equal(1, lease.Disposals));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Sequence_or_timestamp_gap_is_detected_even_without_a_local_queue_drop(bool timestampGap)
	{
		await using var fixture = new Fixture(2, false);
		fixture.Writers.Release.TrySetResult();
		Assert.True(fixture.Enqueue(0).Accepted);
		await fixture.Writers.Written(0).WaitAsync(TimeSpan.FromSeconds(5));
		var next = timestampGap ? 1UL : 2UL;
		Assert.True(fixture.Enqueue(next, timestampGap ? 2L : 1L).Accepted);
		await fixture.Capture.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		var segments = fixture.Capture.Snapshot.Segments;
		Assert.Equal(2, segments.Count);
		Assert.False(segments[0].DiscontinuityBefore);
		Assert.True(segments[1].DiscontinuityBefore);
		Assert.Equal(0UL, fixture.Capture.Snapshot.Statistics.DroppedSamples);
		Assert.Equal(1UL, fixture.Capture.Snapshot.Statistics.Discontinuities);
		AssertMissing(fixture.Capture.Store, new ReplayRange(segments[0].Start, segments[1].End));
		AssertRetained(fixture.Capture.Store, new ReplayRange(segments[1].Start, segments[1].End));
	}

	private static void AssertRetained(RollingReplaySegmentStore store, ReplayRange range)
	{
		Assert.True(store.ContainsRange(range));
		using var pin = store.PinRange(range);
		Assert.NotEmpty(pin.Segments);
	}

	private static void AssertMissing(RollingReplaySegmentStore store, ReplayRange range)
	{
		Assert.False(store.ContainsRange(range));
		Assert.Throws<InvalidOperationException>(() => store.PinRange(range));
	}

	private sealed class Fixture : IAsyncDisposable
	{
		private readonly string _root = Path.Combine(Path.GetTempPath(), "rtaime-replay-continuity-" + Guid.NewGuid().ToString("N"));
		private readonly GpuProcessingProvider _gpu = new(new ManagedReferenceGpuBackend());
		private readonly GpuFrame _surface;
		private readonly VideoFormat _format;
		private readonly MediaSinkId _sink = MediaSinkId.New();
		public WriterGroup Writers { get; } = new();
		public List<Lease> Leases { get; } = [];
		public ReplayCaptureEngine Capture { get; }

		public Fixture(int queueCapacity, bool fractionalRate)
		{
			_format = new VideoFormat(2, 1, fractionalRate ? FrameRate.Fps59_94 : FrameRate.Fps50, PixelFormat.Rgba8, ScanMode.Progressive);
			_gpu.Start();
			_surface = _gpu.Upload(MediaSourceId.New(), RgbaFrameBuffer.Solid(_format, 1, 2, 3),
				new FrameTiming(0, 0, new Timebase(_format.FrameRate.Denominator, _format.FrameRate.Numerator)), Generation.Initial, "replay-continuity");
			Capture = new ReplayCaptureEngine(_root, () => new Writer(Writers),
				new ReplayBufferPolicy(TimeSpan.FromMinutes(1), 1024, TimeSpan.FromMilliseconds(250)), queueCapacity);
		}

		public TimeSpan TimeAt(ulong sequence) => TimeSpan.FromTicks((long)decimal.Round(
			(decimal)sequence * _format.FrameRate.Denominator * TimeSpan.TicksPerSecond / _format.FrameRate.Numerator,
			0, MidpointRounding.AwayFromZero));

		public ReplayEnqueueResult Enqueue(ulong sequence, long? timestamp = null)
		{
			var timing = new FrameTiming(sequence, timestamp ?? checked((long)sequence), new Timebase(_format.FrameRate.Denominator, _format.FrameRate.Numerator));
			var video = new FrameDescriptor(MediaContractVersion.Current, _surface.Descriptor.SourceId, _surface.Descriptor.Surface, timing);
			var audio = new AudioBufferDescriptor(MediaContractVersion.Current, AudioStreamId.New(), AudioFormat.Stereo48kFloat32,
				Identity.New(), new AudioBufferTiming(sequence * 960, 960, checked((long)sequence * 960), new Timebase(1, 48_000)), new OpaqueAudioHandle("replay-test", sequence.ToString()));
			var lease = new Lease();
			Leases.Add(lease);
			return Capture.TryEnqueue(_sink, video, audio, lease, new byte[7680]);
		}

		public async ValueTask DisposeAsync()
		{
			Writers.Release.TrySetResult();
			await Capture.DisposeAsync();
			_surface.Dispose();
			_gpu.Dispose();
			if (Directory.Exists(_root)) Directory.Delete(_root, true);
		}
	}

	private sealed class Lease : IProgramRecordingPayloadLease
	{
		private int _disposals;
		public int Disposals => Volatile.Read(ref _disposals);
		public ReadOnlyMemory<byte> Memory => new byte[] { 1, 2, 3, 255 };
		public void Dispose() => Interlocked.Increment(ref _disposals);
	}

	private sealed class WriterGroup
	{
		private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _written = new();
		public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private TaskCompletionSource Completion(ulong sequence) => _written.GetOrAdd(sequence, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
		public Task Written(ulong sequence) => Completion(sequence).Task;
		public void Complete(ulong sequence) => Completion(sequence).TrySetResult();
	}

	private sealed class Writer(WriterGroup group) : IReplaySegmentWriter
	{
		private IProgramRecordingPayloadLease? _payload;
		public string? FinalPath { get; private set; }
		public string ConfigureTarget(string directory, string name) { FinalPath = Path.Combine(directory, name); return name; }
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken token) => ValueTask.CompletedTask;
		public void StagePayload(ulong sequence, IProgramRecordingPayloadLease payload, ReadOnlyMemory<byte> audio) => _payload = payload;
		public void DiscardPayload(ulong sequence) => Interlocked.Exchange(ref _payload, null)?.Dispose();
		public async ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken token)
		{
			try
			{
				if (sample.SequenceNumber == 0)
				{
					group.FirstWrite.TrySetResult();
					await group.Release.Task.WaitAsync(token);
				}
			}
			finally { DiscardPayload(sample.SequenceNumber); }
			group.Complete(sample.SequenceNumber);
		}
		public ValueTask FinalizeAsync(CancellationToken token) { File.WriteAllBytes(FinalPath!, new byte[] { 1 }); return ValueTask.CompletedTask; }
		public ValueTask AbortAsync(CancellationToken token) { DiscardPayload(0); return ValueTask.CompletedTask; }
	}
}
