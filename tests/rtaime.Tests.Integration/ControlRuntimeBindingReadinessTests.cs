// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ControlRuntimeBindingReadinessTests
{
	[Theory]
	[InlineData("transport-failure")]
	[InlineData("timeout")]
	[InlineData("lost-reply")]
	public async Task Failed_initial_commit_can_retry_without_stranding_pending_authority(string fault)
	{
		await WithRuntimeAsync(async (options, transport, control, run) =>
		{
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready, run);
			Assert.True(transport.ApplyCalls >= 2);
			Assert.False(control.Control!.HasPendingExecution);
			Assert.True(control.Control.HasAuthoritativeState);
			var snapshot = await transport.GetSnapshotAsync();
			Assert.Equal(control.Control.State.Revision, snapshot.AuthorityRevision);
			Assert.Equal(control.Control.State.ProductionId.Value, snapshot.AuthorityStateId);
		}, fault);
	}

	[Fact]
	public async Task Failed_audio_restore_keeps_host_unqualified_until_restore_succeeds()
	{
		await WithRuntimeAsync(async (options, transport, control, run) =>
		{
			await transport.AudioRetryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(control.Control!.HasAuthoritativeState);
			var revision = control.Control.State.Revision;
			Assert.Equal(ControlHostProcessState.Degraded, control.Lifecycle.State);
			Assert.NotEqual(ControlHostHealthState.Healthy, control.Lifecycle.Health);
			transport.AllowAudioRestore.TrySetResult();
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready, run);
			Assert.Equal(revision, control.Control.State.Revision);
			Assert.Equal(1, transport.ApplyCalls);
			Assert.True(transport.AudioCalls >= 2);
		}, "audio-restore");
	}

	[Fact]
	public async Task Cancellation_during_initial_commit_clears_owned_pending_execution_and_stops_cleanly()
	{
		await WithRuntimeAsync(async (options, transport, control, run) =>
		{
			await transport.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.True(control.Control!.HasPendingExecution);
		}, "cancel", cancelAfterBody: true);
	}

	private static async Task WithRuntimeAsync(
		Func<ControlHostProcessOptions, FaultTransport, ControlHostProcess, Task<ControlHostExitCode>, Task> body,
		string fault,
		bool cancelAfterBody = false)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-binding-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var runtimeEndpoint = $"rtaime.test.binding.runtime.{Guid.NewGuid():N}";
		var options = ControlHostProcessOptions.Default with
		{
			ListenEndpoint = $"rtaime.test.binding.control.{Guid.NewGuid():N}",
			RuntimeEndpoint = runtimeEndpoint,
			DurabilityRoot = root,
			ConnectTimeout = TimeSpan.FromMilliseconds(100),
			RequestTimeout = TimeSpan.FromMilliseconds(500),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		};
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		using var log = HostLog.Open("ControlHost", new[] { $"--log-root={root}", "--log-session-id=binding-tests" }, publishEnvironment: false);
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var transport = new FaultTransport(new NamedPipeRuntimeHostTransport(runtimeEndpoint, options.ConnectTimeout, options.RequestTimeout), fault);
		var control = new ControlHostProcess(options, () => transport, log);
		var run = control.RunAsync(controlStop.Token);
		try
		{
			await body(options, transport, control, run);
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await run.WaitAsync(TimeSpan.FromSeconds(10)));
			if (cancelAfterBody) Assert.False(control.Control!.HasPendingExecution);
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun.WaitAsync(TimeSpan.FromSeconds(10)));
			log.Flush();
			log.Dispose();
			// Read the closed log before fixture cleanup so Windows sharing cannot mask the regression.
			var records = File.ReadAllLines(log.CurrentFilePath!).Select(line => JsonDocument.Parse(line)).ToArray();
			try
			{
				Assert.Contains(records, record => record.RootElement.GetProperty("code").GetString() == "controlhost.binding.authority-load.completed");
			}
			finally
			{
				foreach (var record in records) record.Dispose();
				log.Dispose();
				Directory.Delete(root, recursive: true);
			}
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition, Task<ControlHostExitCode> run)
	{
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while (!condition())
		{
			if (run.IsCompleted) throw new InvalidOperationException($"ControlHost exited early: {await run}.");
			await Task.Delay(20, deadline.Token);
		}
	}

	private sealed class FaultTransport : IControlRuntimeTransportSeam
	{
		private readonly NamedPipeRuntimeHostTransport _inner;
		private readonly string _fault;
		public FaultTransport(NamedPipeRuntimeHostTransport inner, string fault) { _inner = inner; _fault = fault; }
		public bool IsConnected => _inner.IsConnected;
		public string? HostInstanceId => _inner.HostInstanceId;
		public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => _inner.ProviderDescriptors;
		public int ApplyCalls { get; private set; }
		public int AudioCalls { get; private set; }
		public TaskCompletionSource ApplyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AudioRetryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource AllowAudioRestore { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(PreparedExecutionContract preparedExecution, MediaSinkId programSinkId, RuntimeProgramTransitionIntent? transition, CancellationToken cancellationToken = default)
		{
			ApplyCalls++;
			ApplyEntered.TrySetResult();
			if (_fault == "cancel" || ApplyCalls == 1 && _fault == "timeout")
				await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			if (ApplyCalls == 1 && _fault == "transport-failure") throw new IOException("Injected initial apply transport failure.");
			var result = await _inner.ApplyExecutionAsync(preparedExecution, programSinkId, transition, cancellationToken);
			if (ApplyCalls == 1 && _fault == "lost-reply") throw new IOException("Injected lost response after Runtime commit.");
			return result;
		}

		public async ValueTask<RuntimeAudioProductionSnapshot> SetAudioProductionAsync(AudioProductionConfiguration configuration, CancellationToken cancellationToken = default)
		{
			AudioCalls++;
			if (_fault == "audio-restore")
			{
				if (AudioCalls == 1) throw new IOException("Injected audio restore failure after authority confirmation.");
				AudioRetryEntered.TrySetResult();
				await AllowAudioRestore.Task.WaitAsync(cancellationToken);
			}
			return await _inner.SetAudioProductionAsync(configuration, cancellationToken);
		}

		public ValueTask ConnectAsync(CancellationToken cancellationToken = default) =>
			_inner.ConnectAsync(cancellationToken);

		public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) =>
			_inner.GetProviderDescriptorsAsync(cancellationToken);

		public ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			_inner.GetSnapshotAsync(cancellationToken);

		public ValueTask<MediaSourceDiscoverySnapshot> GetMediaSourceDiscoveryAsync(CancellationToken cancellationToken = default) =>
			_inner.GetMediaSourceDiscoveryAsync(cancellationToken);

		public ValueTask<IReadOnlyList<MediaInputHealthSnapshot>> GetMediaInputHealthAsync(CancellationToken cancellationToken = default) =>
			_inner.GetMediaInputHealthAsync(cancellationToken);

		public ValueTask<RuntimeAudioInputSnapshot> SetAudioInputStateAsync(MediaSourceId sourceId, double gain, bool muted, CancellationToken cancellationToken = default) =>
			_inner.SetAudioInputStateAsync(sourceId, gain, muted, cancellationToken);

		public ValueTask<RuntimeAudioProgramSnapshot> SetAudioRoutingAsync(int mode, MediaSourceId? breakawaySourceId, CancellationToken cancellationToken = default) =>
			_inner.SetAudioRoutingAsync(mode, breakawaySourceId, cancellationToken);

		public ValueTask<RuntimeAudioInputSnapshot> SetAudioTestSignalAsync(MediaSourceId sourceId, bool enabled, int mode, double frequencyHz, double peakLevel, CancellationToken cancellationToken = default) =>
			_inner.SetAudioTestSignalAsync(sourceId, enabled, mode, frequencyHz, peakLevel, cancellationToken);

		public ValueTask<bool> SetBroadcastTestPatternAsync(MediaSourceId sourceId, bool enabled, CancellationToken cancellationToken = default) =>
			_inner.SetBroadcastTestPatternAsync(sourceId, enabled, cancellationToken);

		public ValueTask<bool> SetBroadcastTestPatternAsync(MediaSourceId sourceId, bool enabled, bool motionTiming, CancellationToken cancellationToken = default) =>
			_inner.SetBroadcastTestPatternAsync(sourceId, enabled, motionTiming, cancellationToken);

		public ValueTask<RuntimeGraphicsOverlaySnapshot> LoadGraphicsOverlayAsync(string assetName, uint width, uint height, byte[] rgbaPixels, CancellationToken cancellationToken = default) =>
			_inner.LoadGraphicsOverlayAsync(assetName, width, height, rgbaPixels, cancellationToken);

		public ValueTask<RuntimeGraphicsOverlaySnapshot> ApplyProductionCgTextAsync(RuntimeProductionCgTextDefinition definition, CancellationToken cancellationToken = default) =>
			_inner.ApplyProductionCgTextAsync(definition, cancellationToken);

		public ValueTask<RuntimeGraphicsOverlaySnapshot> SetGraphicsOverlayAsync(bool visible, double positionX, double positionY, double scale, CancellationToken cancellationToken = default) =>
			_inner.SetGraphicsOverlayAsync(visible, positionX, positionY, scale, cancellationToken);

		public ValueTask<RuntimeGraphicsOverlaySnapshot> ClearGraphicsOverlayAsync(CancellationToken cancellationToken = default) =>
			_inner.ClearGraphicsOverlayAsync(cancellationToken);

		public ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerStateAsync(string layerId, bool visible, byte opacity, CancellationToken cancellationToken = default) =>
			_inner.SetCompositingLayerStateAsync(layerId, visible, opacity, cancellationToken);

		public ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerTransformAsync(string layerId, double positionX, double positionY, double scale, double rotationDegrees, double anchorX, double anchorY, double cropLeft, double cropTop, double cropRight, double cropBottom, CancellationToken cancellationToken = default) =>
			_inner.SetCompositingLayerTransformAsync(layerId, positionX, positionY, scale, rotationDegrees, anchorX, anchorY, cropLeft, cropTop, cropRight, cropBottom, cancellationToken);

		public ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerProcessingNodeAsync(string layerId, PreparedCompositingProcessingNodeState? processingNode, CancellationToken cancellationToken = default) =>
			_inner.SetCompositingLayerProcessingNodeAsync(layerId, processingNode, cancellationToken);

		public ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> SetCompositingLayerProcessingStackAsync(string layerId, IReadOnlyList<PreparedCompositingProcessingNodeState> processingStack, CancellationToken cancellationToken = default) =>
			_inner.SetCompositingLayerProcessingStackAsync(layerId, processingStack, cancellationToken);

		public ValueTask<IReadOnlyList<RuntimeCompositingLayerSnapshot>> ReorderCompositingLayersAsync(IReadOnlyList<string> orderedLayerIds, CancellationToken cancellationToken = default) =>
			_inner.ReorderCompositingLayersAsync(orderedLayerIds, cancellationToken);

		public ValueTask<RuntimeRecordingCommandResult> StartRecordingAsync(string destinationDirectory, string fileName, CancellationToken cancellationToken = default) =>
			_inner.StartRecordingAsync(destinationDirectory, fileName, cancellationToken);

		public ValueTask<RuntimeRecordingCommandResult> StartRecordingAsync(string destinationDirectory, string fileName, string? profileId, CancellationToken cancellationToken = default) =>
			_inner.StartRecordingAsync(destinationDirectory, fileName, profileId, cancellationToken);

		public ValueTask<RuntimeRecordingCommandResult> StopRecordingAsync(CancellationToken cancellationToken = default) =>
			_inner.StopRecordingAsync(cancellationToken);

		public ValueTask<RuntimeReplaySnapshot> GetReplaySnapshotAsync(CancellationToken cancellationToken = default) =>
			_inner.GetReplaySnapshotAsync(cancellationToken);

		public ValueTask<RuntimeReplaySnapshot> MarkReplayInAsync(TimeSpan? lookback = null, CancellationToken cancellationToken = default) =>
			_inner.MarkReplayInAsync(lookback, cancellationToken);

		public ValueTask<RuntimeReplaySnapshot> MarkReplayOutAsync(CancellationToken cancellationToken = default) =>
			_inner.MarkReplayOutAsync(cancellationToken);

		public ValueTask<RuntimeReplaySnapshot> SetReplayRangeAsync(TimeSpan @in, TimeSpan @out, CancellationToken cancellationToken = default) =>
			_inner.SetReplayRangeAsync(@in, @out, cancellationToken);

		public ValueTask<RuntimeReplayClipResult> CreateReplayClipAsync(string name, CancellationToken cancellationToken = default) =>
			_inner.CreateReplayClipAsync(name, cancellationToken);

		public ValueTask<string> GetAIEffectCatalogJsonAsync(CancellationToken cancellationToken = default) =>
			_inner.GetAIEffectCatalogJsonAsync(cancellationToken);

		public ValueTask<RuntimeAIShowcaseRemoteSnapshot> TestAIEffectAsync(CancellationToken cancellationToken = default) =>
			_inner.TestAIEffectAsync(cancellationToken);

		public ValueTask<RuntimeAIShowcaseRemoteSnapshot> SetAIShowcaseEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
			_inner.SetAIShowcaseEnabledAsync(enabled, cancellationToken);

		public ValueTask<MediaAssetProbeResult> ProbeMediaAssetAsync(string path, MediaAssetId assetId, CancellationToken cancellationToken = default) =>
			_inner.ProbeMediaAssetAsync(path, assetId, cancellationToken);

		public ValueTask<MediaDeckRuntimeSnapshot> GetMediaDeckSnapshotAsync(CancellationToken cancellationToken = default) =>
			_inner.GetMediaDeckSnapshotAsync(cancellationToken);

		public ValueTask<MediaDeckRuntimeSnapshot> OpenMediaDeckAsync(MediaDeckOpenRequest request, PreparedExecutionContract preparedExecution, CancellationToken cancellationToken = default) =>
			_inner.OpenMediaDeckAsync(request, preparedExecution, cancellationToken);

		public ValueTask<MediaTransportCommandResult> ApplyMediaDeckTransportAsync(MediaTransportCommand command, CancellationToken cancellationToken = default) =>
			_inner.ApplyMediaDeckTransportAsync(command, cancellationToken);

		public ValueTask<MediaDeckRuntimeSnapshot> CloseMediaDeckAsync(CancellationToken cancellationToken = default) =>
			_inner.CloseMediaDeckAsync(cancellationToken);

		public ValueTask DisconnectAsync() =>
			_inner.DisconnectAsync();
	}
}
