// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Recording;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class RecordingOperatorWorkflowIntegrationTests
{
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
			var initial = await client.SynchronizeAsync();
			var revisionBeforeRecording = initial.Production.Revision;

			var graphics = await client.LoadGraphicsOverlayAsync(new OperatorGraphicsAsset(
				"ap53-recording-red.rgba",
				1,
				1,
				new byte[] { 255, 0, 0, 255 }));
			Assert.True(graphics.AssetLoaded);
			graphics = await client.SetGraphicsOverlayAsync(true, 0, 0, 1);
			Assert.True(graphics.Visible);

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
			Assert.Equal(0, runtime.Runtime.Snapshot.ActiveGpuSurfaces);

			var secondPath = await RecordOneAsync(client, runtime, destination, "ap53-program-02");
			var second = ReferenceRecordingPayloadReader.Read(secondPath);
			Assert.NotEmpty(second.Samples);
			Assert.NotEqual(firstPath, secondPath);
			Assert.True(File.Exists(firstPath));
			Assert.True(File.Exists(secondPath));
			Assert.Equal(0, runtime.Runtime.Snapshot.ActiveGpuSurfaces);
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
		Assert.True(stopped.Succeeded, stopped.Failure?.ToString());
		Assert.Equal("COMPLETED", client.Snapshot!.Recording.State);
		Assert.True(client.Snapshot.Recording.Written >= 1);
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
