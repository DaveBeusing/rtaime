// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Gpu;
using rtaime.Recording;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ReplayClipOwnershipIntegrationTests
{
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task Control_Runtime_timeout_does_not_release_clip_ownership_or_hide_the_terminal_result()
	{
		var materializer = new Materializer();
		await using var fixture = await Fixture.CreateAsync(materializer.RunAsync);
		await using var runtime = new RuntimeProgramExecutionService(MediaSourceId.New(), MediaSourceId.New(), fixture.Format,
			new LocalRecordingManifestWriter(fixture.Root), new ManagedReferenceGpuBackend(), replayService: fixture.Replay);
		var endpoint = "rtaime.replay-ownership." + Guid.NewGuid().ToString("N");
		await using var server = new RuntimeHostIpcServer(endpoint, () => runtime);
		await server.StartAsync();
		var transport = new NamedPipeRuntimeHostTransport(endpoint, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
		await using var management = new SqliteManagementStore(Path.Combine(fixture.Root, "catalog.db"));
		var catalog = new MediaAssetCatalogService(transport, new MediaAssetCatalogPersistenceStore(management));
		var control = new ReplayControlService(transport, catalog);
		try
		{
			await control.SetRangeAsync(Fixture.SelectedRange.In, Fixture.SelectedRange.Out);
			var first = control.CreateClipAsync("first").AsTask();
			await materializer.Started.Task.WaitAsync(Deadline);
			var request = Assert.Single(materializer.Requests);
			await Assert.ThrowsAsync<TimeoutException>(() => first).WaitAsync(Deadline);
			Assert.False(materializer.Token.IsCancellationRequested);
			Assert.Equal(1, materializer.Active);

			await Assert.ThrowsAsync<InvalidOperationException>(() => control.SetRangeAsync(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(440)).AsTask());
			await Assert.ThrowsAsync<InvalidOperationException>(() => control.CreateClipAsync("second").AsTask());
			var pending = await transport.GetReplaySnapshotAsync();
			Assert.Equal((int)ReplayClipState.Finalizing, pending.ClipState);
			Assert.Equal(request.ClipId.ToString(), pending.ActiveClipId);
			Assert.Null(pending.LastClip);
			Assert.Equal(Fixture.SelectedRange.In, pending.MarkIn);
			Assert.Equal(Fixture.SelectedRange.Out, pending.MarkOut);
			Assert.Single(materializer.Requests);
			Assert.Equal(1, materializer.MaximumActive);

			materializer.Release.TrySetResult();
			await WaitUntilAsync(() => fixture.Replay.LastClip is not null);
			var completed = await transport.GetReplaySnapshotAsync();
			Assert.Equal((int)ReplayClipState.Ready, completed.ClipState);
			Assert.Null(completed.ActiveClipId);
			var result = Assert.IsType<RuntimeReplayClipResult>(completed.LastClip);
			Assert.True(result.Succeeded);
			Assert.Equal(request.ClipId.ToString(), result.ClipId);
			Assert.Equal(request.Range.In, result.SourceIn);
			Assert.Equal(request.Range.Out, result.SourceOut);
			Assert.Equal(fixture.Replay.LastClip!.FinalPath, result.FinalPath);
			Assert.Equal(fixture.Replay.LastClip.Sha256, result.Sha256);
			Assert.Null(result.Failure);
			Assert.Equal(request.ClipId.ToString(), pending.ActiveClipId);
			Assert.Null(pending.LastClip); // The earlier observation remains immutable.

			var recovery = await control.GetSnapshotAsync();
			Assert.Equal(ReplayControlClipState.Failed, recovery.ClipState);
			Assert.Equal("replay.clip.catalog_adoption_required", recovery.Failure?.Code);
			Assert.Contains(result.ClipId, recovery.Failure!.Value.Message, StringComparison.Ordinal);
			Assert.Contains(result.FinalPath!, recovery.Failure.Value.Message, StringComparison.Ordinal);
			Assert.Null(control.LastAdoptedClip);
			Assert.Empty((await catalog.GetSnapshotAsync(refreshAvailability: false)).Assets);
			Assert.Single(materializer.Requests);
			Assert.Equal(0, materializer.Active);
		}
		finally
		{
			materializer.Release.TrySetResult();
		}
	}

	[Fact]
	public async Task Every_selection_operation_preserves_the_active_job_and_its_original_range()
	{
		var materializer = new Materializer();
		await using var fixture = await Fixture.CreateAsync(materializer.RunAsync);
		Task<ReplayClipResult>? create = null;
		try
		{
			create = fixture.Replay.CreateClipAsync("owned").AsTask();
			await materializer.Started.Task.WaitAsync(Deadline);
			var active = fixture.Replay.Snapshot;
			Assert.Throws<InvalidOperationException>(() => fixture.Replay.MarkIn());
			Assert.Throws<InvalidOperationException>(() => fixture.Replay.MarkOut());
			Assert.Throws<InvalidOperationException>(() => fixture.Replay.SelectRange(new ReplayRange(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(440))));
			Assert.Throws<InvalidOperationException>(() => fixture.Replay.ClearSelection());
			await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Replay.CreateClipAsync("duplicate").AsTask());
			Assert.Equal(active, fixture.Replay.Snapshot);
			materializer.Release.TrySetResult();
			var result = await create.WaitAsync(Deadline);
			Assert.Equal(active.ActiveClipId, result.ClipId);
			Assert.Equal(Fixture.SelectedRange, result.SourceRange);
			Assert.Equal(result, fixture.Replay.LastClip);
			Assert.Null(fixture.Replay.Snapshot.ActiveClipId);
			fixture.Replay.ClearSelection();
			Assert.Equal(result, fixture.Replay.Snapshot.LastClip);
			Assert.Equal(ReplayClipState.Idle, fixture.Replay.Snapshot.ClipState);
		}
		finally
		{
			materializer.Release.TrySetResult();
			if (create is not null) await create.WaitAsync(Deadline);
		}
	}

	[Theory]
	[InlineData("pre-cancelled")]
	[InlineData("cancelled")]
	[InlineData("synchronous-failure")]
	[InlineData("asynchronous-failure")]
	public async Task Cancellation_and_exceptions_publish_one_terminal_result_and_release_admission(string mode)
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var requests = new List<ReplayClipRequest>();
		var executions = 0;
		ValueTask<ReplayClipResult> Materialize(ReplayClipRequest request, CancellationToken token)
		{
			requests.Add(request);
			if (requests.Count > 1) return ValueTask.FromResult(Success(request));
			if (mode == "synchronous-failure") throw new IOException("Injected materializer startup failure.");
			return new ValueTask<ReplayClipResult>(Task.Run(async () =>
			{
				Interlocked.Increment(ref executions);
				started.TrySetResult();
				await release.Task.WaitAsync(token);
				if (mode == "asynchronous-failure") throw new IOException("Injected materializer completion failure.");
				return Success(request);
			}, token));
		}
		await using var fixture = await Fixture.CreateAsync(Materialize);
		using var cancelled = new CancellationTokenSource();
		if (mode == "pre-cancelled") cancelled.Cancel();
		var first = fixture.Replay.CreateClipAsync("first", cancelled.Token).AsTask();
		try
		{
			if (mode is "cancelled" or "asynchronous-failure")
			{
				await started.Task.WaitAsync(Deadline);
				if (mode == "cancelled") cancelled.Cancel();
				else release.TrySetResult();
			}
			if (mode.Contains("cancelled", StringComparison.Ordinal))
				await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(Deadline);
			else
				await Assert.ThrowsAsync<IOException>(() => first).WaitAsync(Deadline);
			var failed = fixture.Replay.Snapshot;
			Assert.Null(failed.ActiveClipId);
			Assert.Equal(ReplayClipState.Failed, failed.ClipState);
			Assert.Equal(requests[0].ClipId, failed.LastClip!.ClipId);
			Assert.False(failed.LastClip.Succeeded);
			Assert.Equal(mode.Contains("cancelled", StringComparison.Ordinal) ? "replay.clip.cancelled" : "replay.clip.materialization_failed", failed.Failure?.Code);
			if (mode == "pre-cancelled") Assert.Equal(0, executions);

			fixture.Replay.SelectRange(Fixture.SelectedRange);
			var next = await fixture.Replay.CreateClipAsync("after termination");
			Assert.True(next.Succeeded);
			Assert.NotEqual(failed.LastClip.ClipId, next.ClipId);
			Assert.Equal(next, fixture.Replay.LastClip);
			Assert.Null(fixture.Replay.Snapshot.ActiveClipId);
		}
		finally { release.TrySetResult(); }
	}

	[Fact]
	public async Task Repeated_disposal_waits_for_materialization_before_releasing_capture_ownership()
	{
		var materializer = new Materializer();
		await using var fixture = await Fixture.CreateAsync(materializer.RunAsync);
		var create = fixture.Replay.CreateClipAsync("shutdown").AsTask();
		await materializer.Started.Task.WaitAsync(Deadline);
		var firstDispose = fixture.Replay.DisposeAsync().AsTask();
		var secondDispose = fixture.Replay.DisposeAsync().AsTask();
		try
		{
			Assert.False(firstDispose.IsCompleted);
			Assert.False(secondDispose.IsCompleted);
			await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Replay.CreateClipAsync("after shutdown").AsTask());
			materializer.Release.TrySetResult();
			var result = await create.WaitAsync(Deadline);
			await Task.WhenAll(firstDispose, secondDispose).WaitAsync(Deadline);
			Assert.Equal(result, fixture.Replay.Snapshot.LastClip);
			Assert.Null(fixture.Replay.Snapshot.ActiveClipId);
			Assert.Equal(ReplayCaptureState.Disabled, fixture.Replay.Snapshot.CaptureState);
			Assert.Single(materializer.Requests);
		}
		finally { materializer.Release.TrySetResult(); }
	}

	[Fact]
	public async Task Legacy_replay_snapshot_without_job_fields_remains_readable()
	{
		var endpoint = "rtaime.replay-legacy." + Guid.NewGuid().ToString("N");
		using var stop = new CancellationTokenSource(Deadline);
		await using var pipe = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		var host = Identity.New().ToString();
		async Task ServeAsync()
		{
			await pipe.WaitForConnectionAsync(stop.Token);
			using var hello = await ReadAsync(pipe, stop.Token);
			await RespondAsync(pipe, hello, "server.hello", host, new { protocolVersion = "1.0", role = "RuntimeHost", hostInstanceId = host }, stop.Token);
			using var request = await ReadAsync(pipe, stop.Token);
			await RespondAsync(pipe, request, "runtime.replay.snapshot.response", host, new
			{
				captureState = (int)ReplayCaptureState.Capturing, clipState = (int)ReplayClipState.Idle,
				retentionTicks = TimeSpan.FromMinutes(2).Ticks, maximumStorageBytes = 1024,
				segmentDurationTicks = TimeSpan.FromSeconds(2).Ticks, segments = Array.Empty<object>(),
				acceptedSamples = 0UL, droppedSamples = 0UL, finalizedSegments = 0UL,
				evictedSegments = 0UL, discontinuities = 0UL, retainedBytes = 0L
			}, stop.Token);
		}
		var serve = ServeAsync();
		var transport = new NamedPipeRuntimeHostTransport(endpoint, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
		var result = await transport.GetReplaySnapshotAsync();
		await serve;
		Assert.Null(result.ActiveClipId);
		Assert.Null(result.LastClip);
		Assert.Equal((int)ReplayClipState.Idle, result.ClipState);
	}

	private static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken token)
	{
		var prefix = new byte[4];
		await stream.ReadExactlyAsync(prefix, token);
		var bytes = new byte[checked((int)BinaryPrimitives.ReadUInt32BigEndian(prefix))];
		await stream.ReadExactlyAsync(bytes, token);
		return JsonDocument.Parse(bytes);
	}

	private static async Task RespondAsync(Stream stream, JsonDocument request, string type, string host, object payload, CancellationToken token)
	{
		var bytes = JsonSerializer.SerializeToUtf8Bytes(new
		{
			protocolVersion = "1.0", messageType = type,
			requestId = request.RootElement.GetProperty("requestId").GetString(),
			correlationId = request.RootElement.GetProperty("correlationId").GetString(),
			hostInstanceId = host, sentAtUtc = DateTimeOffset.UtcNow, stateVersion = 0UL, sequence = 0UL, payload
		}, JsonOptions);
		var prefix = new byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
		await stream.WriteAsync(prefix, token);
		await stream.WriteAsync(bytes, token);
		await stream.FlushAsync(token);
	}

	private static ReplayClipResult Success(ReplayClipRequest request) => new(request.ClipId,
		Path.Combine(Path.GetTempPath(), "replay-" + request.ClipId + ".mp4"), request.Range, null, new string('A', 64), true, null);

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTimeOffset.UtcNow + Deadline;
		while (!condition())
		{
			if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Replay ownership condition was not reached.");
			await Task.Delay(10);
		}
	}

	private sealed class Materializer
	{
		private int _active;
		public int Active => Volatile.Read(ref _active);
		public int MaximumActive { get; private set; }
		public List<ReplayClipRequest> Requests { get; } = [];
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public CancellationToken Token { get; private set; }
		public async ValueTask<ReplayClipResult> RunAsync(ReplayClipRequest request, CancellationToken token)
		{
			MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref _active));
			Requests.Add(request);
			Token = token;
			Started.TrySetResult();
			try { await Release.Task.WaitAsync(token); return Success(request); }
			finally { Interlocked.Decrement(ref _active); }
		}
	}

	private sealed class Fixture : IAsyncDisposable
	{
		public static ReplayRange SelectedRange { get; } = new(TimeSpan.Zero, TimeSpan.FromMilliseconds(480));
		public string Root { get; } = Path.Combine(Path.GetTempPath(), "rtaime-replay-job-" + Guid.NewGuid().ToString("N"));
		public VideoFormat Format { get; } = VideoFormat.Hd1080p50Rgba8;
		public RuntimeReplayService Replay { get; }
		private readonly GpuProcessingProvider _gpu = new(new ManagedReferenceGpuBackend());
		private readonly GpuFrame _frame;
		private readonly ConcurrentDictionary<ulong, TaskCompletionSource> _written = new();

		private Fixture(Func<ReplayClipRequest, CancellationToken, ValueTask<ReplayClipResult>> materialize)
		{
			_gpu.Start();
			_frame = _gpu.Upload(MediaSourceId.New(), RgbaFrameBuffer.Solid(Format, 1, 2, 3), new FrameTiming(0, 0, new Timebase(1, 50)), Generation.Initial, "replay-job");
			Replay = new RuntimeReplayService(Root, Format,
				new ReplayBufferPolicy(TimeSpan.FromMinutes(1), 1024, TimeSpan.FromMilliseconds(250)),
				() => new Writer(_written), 4, materialize);
		}

		public static async Task<Fixture> CreateAsync(Func<ReplayClipRequest, CancellationToken, ValueTask<ReplayClipResult>> materialize)
		{
			var fixture = new Fixture(materialize);
			try
			{
				for (ulong sequence = 0; sequence < 26; sequence++)
				{
					var written = fixture._written.GetOrAdd(sequence, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
					var video = new FrameDescriptor(MediaContractVersion.Current, fixture._frame.Descriptor.SourceId, fixture._frame.Descriptor.Surface,
						new FrameTiming(sequence, checked((long)sequence), new Timebase(1, 50)));
					var audio = new AudioBufferDescriptor(MediaContractVersion.Current, AudioStreamId.New(), AudioFormat.Stereo48kFloat32, Identity.New(),
						new AudioBufferTiming(sequence * 960, 960, checked((long)sequence * 960), new Timebase(1, 48_000)), new OpaqueAudioHandle("replay-job", sequence.ToString()));
					Assert.True(fixture.Replay.TryCapture(MediaSinkId.New(), video, audio, new Payload(), new byte[7680]).Accepted);
					await written.Task.WaitAsync(Deadline);
				}
				await WaitUntilAsync(() => fixture.Replay.Snapshot.Segments.Count == 2);
				fixture.Replay.SelectRange(SelectedRange);
				return fixture;
			}
			catch { await fixture.DisposeAsync(); throw; }
		}

		public async ValueTask DisposeAsync()
		{
			await Replay.DisposeAsync();
			_frame.Dispose();
			_gpu.Dispose();
			if (Directory.Exists(Root)) Directory.Delete(Root, true);
		}
	}

	private sealed class Payload : IProgramRecordingPayloadLease
	{
		public ReadOnlyMemory<byte> Memory => new byte[16];
		public void Dispose() { }
	}

	private sealed class Writer(ConcurrentDictionary<ulong, TaskCompletionSource> written) : IReplaySegmentWriter
	{
		private IProgramRecordingPayloadLease? _payload;
		public string? FinalPath { get; private set; }
		public string ConfigureTarget(string directory, string name) { FinalPath = Path.Combine(directory, name); return name; }
		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken token) => ValueTask.CompletedTask;
		public void StagePayload(ulong sequence, IProgramRecordingPayloadLease payload, ReadOnlyMemory<byte> audio) => _payload = payload;
		public void DiscardPayload(ulong sequence) => Interlocked.Exchange(ref _payload, null)?.Dispose();
		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken token)
		{
			DiscardPayload(sample.SequenceNumber);
			written.GetOrAdd(sample.SequenceNumber, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
			return ValueTask.CompletedTask;
		}
		public ValueTask FinalizeAsync(CancellationToken token) { File.WriteAllBytes(FinalPath!, new byte[] { 1 }); return ValueTask.CompletedTask; }
		public ValueTask AbortAsync(CancellationToken token) { DiscardPayload(0); return ValueTask.CompletedTask; }
	}
}
