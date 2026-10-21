// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;
using Xunit.Abstractions;

namespace rtaime.Tests.Integration;

public sealed class RecordingOperatorWorkflowIntegrationTests
{
	private readonly ITestOutputHelper _output;

	public RecordingOperatorWorkflowIntegrationTests(ITestOutputHelper output) => _output = output;

	[Fact]
	public async Task Operator_records_confirmed_Program_video_audio_and_graphics_to_readable_files_repeatedly()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-ap53-recording", Guid.NewGuid().ToString("N"));
		var destination = Path.Combine(root, "operator");
		Directory.CreateDirectory(root);

		var runtimeEndpoint = $"rtaime.test.ap53.runtime.{Guid.NewGuid():N}";
		var controlEndpoint = $"rtaime.test.ap53.control.{Guid.NewGuid():N}";
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(
			RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint },
			() => new ReferenceRecordingPayloadWriter(root));
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25),
			DurabilityRoot = Path.Combine(root, "control")
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);

		try
		{
			await WaitUntilAsync(() =>
				control.Lifecycle.State == ControlHostProcessState.Ready &&
				control.Control?.HasAuthoritativeState == true);

			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint,
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(5)));
			await client.SynchronizeAsync();

			var graphics = await client.LoadGraphicsOverlayAsync(new OperatorGraphicsAsset(
				"ap53-recording-red.rgba",
				1,
				1,
				new byte[] { 255, 0, 0, 255 }));
			Assert.True(graphics.AssetLoaded);
			graphics = await client.SetGraphicsOverlayAsync(true, 0, 0, 1);
			Assert.True(graphics.Visible);
			var revisionBeforeRecording = client.Snapshot!.Production.Revision;

			var firstPath = await RecordOneAsync(client, runtime, destination, "ap53-program-01");
			var first = ReferenceRecordingPayloadReader.Read(firstPath);
			Assert.NotEmpty(first.Samples);
			Assert.Equal(first.VideoSampleCount, first.AudioSampleCount);
			Assert.True(first.VideoSampleCount >= 1);
			Assert.All(first.Samples, sample => Assert.NotEmpty(sample.AudioPayload));
			Assert.Equal(new byte[] { 255, 0, 0, 255 }, first.Samples[0].VideoPayload.AsSpan(0, 4).ToArray());
			Assert.Equal(VideoFormat.Hd1080p50Rgba8, first.Samples[0].VideoFormat);
			var recordedDuration = TimeSpan.FromSeconds(first.VideoSampleCount / 50.0);
			Assert.InRange(recordedDuration, TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2));

			var completed = client.Snapshot!.Recording;
			Assert.Equal("COMPLETED", completed.State);
			Assert.True(completed.Elapsed + TimeSpan.FromMilliseconds(100) >= recordedDuration);
			Assert.Equal(firstPath, completed.FinalPath);
			Assert.Equal(revisionBeforeRecording, client.Snapshot.Production.Revision);
			Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Runtime!.Snapshot.Runtime.Status);
			Assert.InRange(
				runtime.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers,
				0,
				RuntimeProgramExecutionService.ProgramReadbackBufferCapacity);

			var secondPath = await RecordOneAsync(client, runtime, destination, "ap53-program-02");
			var second = ReferenceRecordingPayloadReader.Read(secondPath);
			Assert.NotEmpty(second.Samples);
			Assert.NotEqual(firstPath, secondPath);
			Assert.True(File.Exists(firstPath));
			Assert.True(File.Exists(secondPath));
			Assert.InRange(
				runtime.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers,
				0,
				RuntimeProgramExecutionService.ProgramReadbackBufferCapacity);
			Assert.Equal(revisionBeforeRecording, client.Snapshot!.Production.Revision);
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}


	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Blocked_recording_STOP_reports_uncertain_completion_and_finalizes_once(bool failFinalize)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-recording-stop", Guid.NewGuid().ToString("N"));
		var destination = Path.Combine(root, "recordings");
		Directory.CreateDirectory(root);
		var writer = new GatedReferenceRecordingWriter(root) { FailFinalize = failFinalize };
		var runtimeEndpoint = $"rtaime.test.stop.runtime.{Guid.NewGuid():N}";
		var controlEndpoint = $"rtaime.test.stop.control.{Guid.NewGuid():N}";
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		using var tracing = new RecordingStopTraceProbe();
		var runtime = new RuntimeHostProcess(
			RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint },
			() => writer);
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25),
			DurabilityRoot = Path.Combine(root, "control")
		});
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);

		try
		{
			await WaitUntilAsync(() =>
				control.Lifecycle.State == ControlHostProcessState.Ready &&
				control.Control?.HasAuthoritativeState == true);
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
			await client.SynchronizeAsync();
			var initialRevision = client.Snapshot!.Production.Revision;
			var graphics = await client.LoadGraphicsOverlayAsync(
				new OperatorGraphicsAsset("stop-graphic.rgba", 1, 1, new byte[] { 255, 0, 0, 255 }));
			Assert.True(graphics.AssetLoaded);
			Assert.True((await client.SetGraphicsOverlayAsync(true, 0, 0, 1)).Visible);

			var started = await client.StartRecordingAsync(destination, "stop-qualified");
			Assert.True(started.Succeeded, started.Failure?.ToString());
			await WaitUntilAsync(() => runtime.Runtime?.Snapshot.Recording.Statistics.Accepted >= 1);
			var sessionId = runtime.Runtime!.Snapshot.Recording.SessionId?.ToString();
			Assert.False(string.IsNullOrWhiteSpace(sessionId));

			var stop = client.StopRecordingAsync().AsTask();
			await writer.FinalizeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			var first = await stop.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(first.Succeeded);
			Assert.Equal("recording.stop.completion_unknown", first.Failure?.Code);
			Assert.Contains(sessionId!, first.Failure!.Value.Message, StringComparison.Ordinal);
			Assert.Equal("FINALIZING", client.Snapshot!.Recording.State);
			Assert.Equal(initialRevision, client.Snapshot.Production.Revision);
			Assert.Equal(1, writer.FinalizeCount);
			Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Runtime.Snapshot.Runtime.Status);

			// The pending finalizer is not allowed to stop the Program cadence.
			var sequence = runtime.Runtime.Snapshot.NextSequenceNumber;
			await WaitUntilAsync(() => runtime.Runtime?.Snapshot.NextSequenceNumber > sequence + 3);
			Assert.False(File.Exists(Path.Combine(destination, "stop-qualified.rtaime-recording")));

			var duplicate = await client.StopRecordingAsync();
			Assert.False(duplicate.Succeeded);
			Assert.Equal("recording.stop.completion_unknown", duplicate.Failure?.Code);
			Assert.Equal(1, writer.FinalizeCount);
			writer.ReleaseFinalize.TrySetResult();

			await WaitUntilAsync(() => runtime.Runtime?.Snapshot.Recording.State ==
				(failFinalize ? RecordingLifecycleState.Failed : RecordingLifecycleState.Completed));
			var terminal = await client.SynchronizeAsync();
			Assert.Equal(failFinalize ? "FAILED" : "COMPLETED", terminal.Recording.State);
			Assert.Equal(initialRevision, terminal.Production.Revision);
			Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Runtime.Snapshot.Runtime.Status);
			Assert.InRange(runtime.Runtime.ProgramReadbackPoolStatistics.ActiveBuffers,
				0, RuntimeProgramExecutionService.ProgramReadbackBufferCapacity);

			var repeated = await client.StopRecordingAsync();
			Assert.Equal(!failFinalize, repeated.Succeeded);
			Assert.Equal(1, writer.FinalizeCount);
			if (failFinalize)
			{
				Assert.Equal("recording.finalize.writer_failure", terminal.Recording.Failure?.Code);
				Assert.False(File.Exists(Path.Combine(destination, "stop-qualified.rtaime-recording")));
			}
			else
			{
				var path = terminal.Recording.FinalPath;
				Assert.False(string.IsNullOrWhiteSpace(path));
				var artifact = ReferenceRecordingPayloadReader.Read(path!);
				Assert.True(artifact.VideoSampleCount > 0);
				Assert.Equal(artifact.VideoSampleCount, artifact.AudioSampleCount);
				Assert.Equal(new byte[] { 255, 0, 0, 255 },
					artifact.Samples[0].VideoPayload.AsSpan(0, 4).ToArray());
			}

			var events = tracing.Snapshot();
			Assert.Contains(events, e => e.Source == "rtaime.RuntimeHost.RecordingStop" &&
				e.SessionId == sessionId && e.Events.Any(stage => stage.Name == "recorder.finalization.unconfirmed"));
			Assert.Contains(events, e => e.Source == "rtaime.ControlHost.RecordingStop" &&
				e.Events.Any(stage => stage.Name == "mutation_gate.acquired") &&
				e.Events.Any(stage => stage.Name == "runtime.response.received"));
			foreach (var entry in events)
			{
				var origin = entry.Events.FirstOrDefault().Timestamp;
				foreach (var stage in entry.Events)
					_output.WriteLine(
						$"{entry.Source} request={entry.RequestId} correlation={entry.CorrelationId} session={entry.SessionId ?? "none"} " +
						$"stage={stage.Name} elapsed={(stage.Timestamp - origin).TotalMilliseconds:F2}ms");
			}
		}
		finally
		{
			writer.ReleaseFinalize.TrySetResult();
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Recording_storage_failure_is_reported_to_Operator_without_losing_Program()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-ap53-recording-failure", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var runtimeEndpoint = $"rtaime.test.ap53.failure.runtime.{Guid.NewGuid():N}";
		var controlEndpoint = $"rtaime.test.ap53.failure.control.{Guid.NewGuid():N}";
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(
			RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint },
			() => new ReferenceRecordingPayloadWriter(root, maximumPayloadBytes: 1));
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25),
			DurabilityRoot = Path.Combine(root, "control")
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);

		try
		{
			await WaitUntilAsync(() =>
				control.Lifecycle.State == ControlHostProcessState.Ready &&
				control.Control?.HasAuthoritativeState == true);

			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(
				controlEndpoint,
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(5)));
			var initial = await client.SynchronizeAsync();
			var revisionBeforeRecording = initial.Production.Revision;

			var started = await client.StartRecordingAsync(root, "ap53-failure");
			Assert.True(started.Succeeded, started.Failure?.ToString());

			await WaitUntilAsync(() =>
				runtime.Runtime?.Snapshot.Recording.State == RecordingLifecycleState.Failed);
			var failed = await client.SynchronizeAsync();

			Assert.Equal("FAILED", failed.Recording.State);
			Assert.NotNull(failed.Recording.Failure);
			Assert.Equal("recording.write.writer_failure", failed.Recording.Failure!.Value.Code);
			Assert.True(failed.Recording.WriterFailures >= 1);
			Assert.Equal(revisionBeforeRecording, failed.Production.Revision);
			Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Runtime!.Snapshot.Runtime.Status);
			Assert.False(File.Exists(Path.Combine(root, "ap53-failure.rtaime-recording")));
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}


	private sealed class GatedReferenceRecordingWriter : IProgramRecordingPayloadWriter, IConfigurableProgramRecordingWriter
	{
		private readonly ReferenceRecordingPayloadWriter _inner;
		private int _finalizeCount;

		public GatedReferenceRecordingWriter(string root) => _inner = new ReferenceRecordingPayloadWriter(root);
		public bool FailFinalize { get; init; }
		public int FinalizeCount => Volatile.Read(ref _finalizeCount);
		public string? FinalPath => _inner.FinalPath;
		public TaskCompletionSource FinalizeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFinalize { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public string ConfigureTarget(string destinationDirectory, string fileName) =>
			_inner.ConfigureTarget(destinationDirectory, fileName);

		public ValueTask OpenAsync(RecordingStartRequest request, CancellationToken cancellationToken) =>
			_inner.OpenAsync(request, cancellationToken);

		public ValueTask WriteAsync(RecordingProgramSample sample, CancellationToken cancellationToken) =>
			_inner.WriteAsync(sample, cancellationToken);

		public void StagePayload(ulong sequenceNumber, IProgramRecordingPayloadLease videoPayload, ReadOnlyMemory<byte> audioPayload) =>
			_inner.StagePayload(sequenceNumber, videoPayload, audioPayload);

		public void DiscardPayload(ulong sequenceNumber) => _inner.DiscardPayload(sequenceNumber);

		public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _finalizeCount);
			FinalizeEntered.TrySetResult();
			await ReleaseFinalize.Task.WaitAsync(cancellationToken);
			if (FailFinalize)
				throw new IOException("Controlled recording finalization failure.");
			await _inner.FinalizeAsync(cancellationToken);
		}

		public ValueTask AbortAsync(CancellationToken cancellationToken) =>
			_inner.AbortAsync(cancellationToken);
	}

	private sealed record RecordedStopTrace(
		string Source,
		string RequestId,
		string CorrelationId,
		string? SessionId,
		IReadOnlyList<ActivityEvent> Events);

	private sealed class RecordingStopTraceProbe : IDisposable
	{
		private readonly object _gate = new();
		private readonly List<RecordedStopTrace> _traces = new();
		private readonly ActivityListener _listener;

		public RecordingStopTraceProbe()
		{
			_listener = new ActivityListener
			{
				ShouldListenTo = source =>
					source.Name is "rtaime.ControlHost.RecordingStop" or "rtaime.RuntimeHost.RecordingStop",
				Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
					ActivitySamplingResult.AllDataAndRecorded,
				ActivityStopped = activity =>
				{
					var recorded = new RecordedStopTrace(
						activity.Source.Name,
						activity.GetTagItem("ipc.request_id")?.ToString() ?? string.Empty,
						activity.GetTagItem("ipc.correlation_id")?.ToString() ?? string.Empty,
						activity.GetTagItem("recording.session_id")?.ToString(),
						activity.Events.ToArray());
					lock (_gate)
						_traces.Add(recorded);
				}
			};
			ActivitySource.AddActivityListener(_listener);
		}

		public IReadOnlyList<RecordedStopTrace> Snapshot()
		{
			lock (_gate)
				return _traces.ToArray();
		}

		public void Dispose() => _listener.Dispose();
	}

	private static async Task<string> RecordOneAsync(
		OperatorControlClient client,
		RuntimeHostProcess runtime,
		string destination,
		string fileName)
	{
		var started = await client.StartRecordingAsync(destination, fileName);
		Assert.True(started.Succeeded, started.Failure?.ToString());
		Assert.Equal("RECORDING", client.Snapshot!.Recording.State);
		Assert.Equal(Path.GetFullPath(destination), client.Snapshot.Recording.Destination);
		Assert.Equal(fileName + ".rtaime-recording", client.Snapshot.Recording.FileName);

		await WaitUntilAsync(() =>
			runtime.Runtime?.Snapshot.Recording.Statistics.Accepted >= 1);

		var stopped = await client.StopRecordingAsync();
		if (!stopped.Succeeded)
			Assert.Equal("recording.stop.completion_unknown", stopped.Failure?.Code);

		// STOP admission is not file finalization: qualify actual terminal Runtime state.
		await WaitUntilAsync(() => runtime.Runtime?.Snapshot.Recording.State is
			RecordingLifecycleState.Completed or RecordingLifecycleState.Failed);
		var final = await client.SynchronizeAsync();
		Assert.Equal("COMPLETED", final.Recording.State);
		Assert.True(final.Recording.Written >= 1);
		Assert.Equal(0UL, client.Snapshot.Recording.Dropped);

		var finalPath = client.Snapshot.Recording.FinalPath;
		Assert.False(string.IsNullOrWhiteSpace(finalPath));
		Assert.True(File.Exists(finalPath));
		return finalPath!;
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition() && DateTime.UtcNow < deadline)
			await Task.Delay(10);
		Assert.True(condition());
	}
}
