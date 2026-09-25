// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using rtaime.AI.Contracts;
using rtaime.AIHost;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Provider.Inference;
using rtaime.Provider.VirtualMedia;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ProductionIpcIntegrationTests
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	[Fact]
	public async Task RuntimeHost_named_pipe_exposes_provider_and_runtime_snapshot()
	{
		var endpoint = Endpoint("runtime");
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = process.RunAsync(stop.Token);

		var transport = new NamedPipeRuntimeHostTransport(endpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
		await RetryAsync(() => transport.ConnectAsync().AsTask());
		var providers = await transport.GetProviderDescriptorsAsync();
		var snapshot = await transport.GetSnapshotAsync();

		Assert.True(transport.IsConnected);
		Assert.False(string.IsNullOrWhiteSpace(transport.HostInstanceId));
		Assert.NotEmpty(providers);
		Assert.Equal(RuntimeExecutionStatus.Idle, snapshot.Runtime.Status);
		Assert.Equal(Revision.Initial, snapshot.Runtime.ExecutionRevision);

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	[Fact]
	public async Task Operator_commands_cross_ControlHost_and_RuntimeHost_process_boundaries()
	{
		var runtimeEndpoint = Endpoint("runtime");
		var controlEndpoint = Endpoint("control");
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

		var transport = new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
		var client = new OperatorControlClient(transport);
		var initial = await client.SynchronizeAsync();
		var sourceA = initial.Sources[0];
		var sourceB = initial.Sources[1];

		Assert.Equal(2, initial.Production.OutputRoles.Count);
		Assert.Equal(sourceA.Id, Assert.Single(initial.Production.OutputRoles, role => role.RoleId == OutputRoleIds.Program).SourceId.ToString());
		Assert.Equal(sourceB.Id, Assert.Single(initial.Production.OutputRoles, role => role.RoleId == OutputRoleIds.Aux).SourceId.ToString());
		await WaitUntilAsync(() =>
			runtime.Runtime!.Snapshot.OutputRoles?.Any(role =>
				role.RoleId == "aux" &&
				role.SourceId.ToString() == sourceB.Id &&
				role.HealthState == RuntimeOutputRoleHealthState.Healthy &&
				role.AuthoritativeActive) == true);
		var initialOutputEvidence = await client.SynchronizeAsync();
		var initialAux = Assert.Single(initialOutputEvidence.OutputRoles, role => role.RoleId == "aux");
		Assert.Equal("AUX", initialAux.RoleKind);
		Assert.Equal(sourceB.Id, initialAux.SourceId);
		Assert.True(initialAux.AuthoritativeActive);
		Assert.Equal("PASS", initialAux.HealthState);
		Assert.Contains("provider confirmed frame sequence", initialAux.Evidence, StringComparison.OrdinalIgnoreCase);
		Assert.NotEmpty(runtime.Runtime!.AuxFrames);

		var auxRoute = await client.RouteOutputRoleAsync("aux", sourceA.Id);
		Assert.True(auxRoute.Accepted, auxRoute.Failure?.ToString());
		Assert.Equal(sourceA.Id, Assert.Single(client.Snapshot!.Production.OutputRoles, role => role.RoleId == OutputRoleIds.Aux).SourceId.ToString());
		Assert.Equal(sourceA.Id, client.Snapshot.Production.Routing.ProgramSourceId.ToString());
		await WaitUntilAsync(() =>
			runtime.Runtime!.Snapshot.OutputRoles?.Any(role =>
				role.RoleId == "aux" &&
				role.SourceId.ToString() == sourceA.Id &&
				role.HealthState == RuntimeOutputRoleHealthState.Healthy &&
				role.AuthoritativeActive) == true);
		var routedOutputEvidence = await client.SynchronizeAsync();
		var routedAux = Assert.Single(routedOutputEvidence.OutputRoles, role => role.RoleId == "aux");
		Assert.Equal(sourceA.Id, routedAux.SourceId);
		Assert.Equal("PASS", routedAux.HealthState);
		Assert.Equal(sourceA.Id, runtime.Runtime!.AuxFrames[^1].Frame.SourceId.ToString());

		Assert.All(initial.Sources, source =>
		{
			Assert.Equal("LIVE", source.Type);
			Assert.Contains("1920×1080", source.Format);
			Assert.Equal("VALID", source.Health);
			Assert.Equal("—", source.MediaState);
			Assert.Null(source.Remaining);
		});
		runtime.Runtime!.SetInputSignalState(new MediaSourceId(Identity.Parse(sourceB.Id)), V1InputSignalState.Lost);
		var lostSignal = await client.SynchronizeAsync();
		Assert.Equal("LOST", lostSignal.Sources.Single(source => source.Id == sourceB.Id).Health);
		runtime.Runtime.SetInputSignalState(new MediaSourceId(Identity.Parse(sourceB.Id)), V1InputSignalState.Valid);
		await client.SynchronizeAsync();

		var revisionBeforeAudio = client.Snapshot!.Production.Revision;
		Assert.Equal(2, client.Snapshot.AudioInputs.Count);
		Assert.Equal(sourceA.Id, client.Snapshot.AudioProgram.ActiveVideoSourceId);

		var gainedAudio = await client.SetAudioInputStateAsync(sourceA.Id, 0.5, muted: false);
		Assert.Equal(sourceA.Id, gainedAudio.SourceId);
		Assert.Equal(0.5, gainedAudio.Gain, 6);
		Assert.False(gainedAudio.Muted);
		Assert.Equal(revisionBeforeAudio, client.Snapshot!.Production.Revision);

		var mutedAudio = await client.SetAudioInputStateAsync(sourceA.Id, 0.5, muted: true);
		Assert.True(mutedAudio.Muted);
		Assert.Equal("MUTED", client.Snapshot!.AudioInputs.Single(input => input.SourceId == sourceA.Id).Health);
		Assert.Equal(revisionBeforeAudio, client.Snapshot.Production.Revision);

		await client.SetAudioInputStateAsync(sourceA.Id, 1.0, muted: false);

		var generatedAudio = await client.SetAudioTestSignalAsync(
			sourceA.Id,
			true,
			2,
			1_000,
			0.25);
		Assert.True(generatedAudio.TestSignalEnabled);
		Assert.Equal(2, generatedAudio.TestSignalMode);
		Assert.Equal("ALL", generatedAudio.TestSignalActiveChannel);
		Assert.Equal(1_000, generatedAudio.TestSignalFrequencyHz);
		Assert.Equal(0.25, generatedAudio.TestSignalPeakLevel);
		Assert.Equal(revisionBeforeAudio, client.Snapshot!.Production.Revision);
		Assert.True(client.Snapshot.AudioInputs.Single(input => input.SourceId == sourceA.Id).TestSignalEnabled);

		var generatedAudioOff = await client.SetAudioTestSignalAsync(
			sourceA.Id,
			false,
			2,
			1_000,
			0.25);
		Assert.False(generatedAudioOff.TestSignalEnabled);
		Assert.Equal(revisionBeforeAudio, client.Snapshot!.Production.Revision);

		var revisionBeforeGraphics = client.Snapshot!.Production.Revision;
		var graphicsAsset = new OperatorGraphicsAsset(
			"operator-logo.rgba",
			2,
			2,
			new byte[]
			{
				255, 0, 0, 255,
				0, 255, 0, 128,
				0, 0, 255, 255,
				255, 255, 0, 64
			});
		var loadedGraphics = await client.LoadGraphicsOverlayAsync(graphicsAsset);
		Assert.True(loadedGraphics.AssetLoaded);
		Assert.False(loadedGraphics.Visible);
		Assert.Equal("operator-logo.rgba", client.Snapshot!.GraphicsOverlay.AssetName);
		Assert.True(client.Snapshot.Production.Revision.CompareTo(revisionBeforeGraphics) > 0);
		Assert.NotNull(client.Snapshot.Production.CompositingState);
		Assert.Contains(
			client.Snapshot.Production.CompositingState!.Layers,
			layer => layer.LayerId == ProductionCompositingLayerIds.BitmapGraphics);

		var onAirGraphics = await client.SetGraphicsOverlayAsync(true, 0.25, 0.10, 1.5);
		Assert.True(onAirGraphics.Visible);
		Assert.True(client.Snapshot!.VisualLayerEnabled);
		Assert.Equal(0.25, client.Snapshot.GraphicsOverlay.PositionX, 6);
		Assert.Equal(0.10, client.Snapshot.GraphicsOverlay.PositionY, 6);
		Assert.Equal(1.5, client.Snapshot.GraphicsOverlay.Scale, 6);
		Assert.True(runtime.Runtime!.Snapshot.GraphicsOverlay.Visible);
		Assert.Equal(revisionBeforeGraphics, client.Snapshot.Production.Revision);

		var clearedGraphics = await client.ClearGraphicsOverlayAsync();
		Assert.False(clearedGraphics.AssetLoaded);
		Assert.False(client.Snapshot!.GraphicsOverlay.Visible);
		Assert.False(runtime.Runtime.Snapshot.GraphicsOverlay.AssetLoaded);
		Assert.Equal(revisionBeforeGraphics, client.Snapshot.Production.Revision);

		var preview = await client.SelectPreviewAsync(sourceB.Id);
		Assert.True(preview.Accepted, preview.Failure?.ToString());
		Assert.Equal(sourceB.Id, client.Snapshot!.Production.Routing.PreviewSourceId.ToString());
		Assert.Equal(sourceA.Id, client.Snapshot.Production.Routing.ProgramSourceId.ToString());

		var cut = await client.CutPreviewAsync();
		Assert.True(cut.Accepted, cut.Failure?.ToString());
		Assert.Equal(sourceB.Id, client.Snapshot!.Production.Routing.ProgramSourceId.ToString());
		await WaitUntilAsync(() => runtime.Runtime!.Snapshot.AudioProgram.ActiveVideoSourceId.ToString() == sourceB.Id);
		await client.SynchronizeAsync();
		Assert.Equal(sourceB.Id, client.Snapshot!.AudioProgram.ActiveVideoSourceId);
		Assert.Equal("HEALTHY", client.Snapshot.AudioProgram.Health);

		var previewBack = await client.SelectPreviewAsync(sourceA.Id);
		Assert.True(previewBack.Accepted, previewBack.Failure?.ToString());
		Assert.Equal(sourceA.Id, client.Snapshot!.Production.Routing.PreviewSourceId.ToString());
		Assert.Equal(sourceB.Id, client.Snapshot.Production.Routing.ProgramSourceId.ToString());

		var dissolve = await client.DissolvePreviewAsync(12);
		Assert.True(dissolve.Accepted, dissolve.Failure?.ToString());
		await WaitUntilAsync(() => runtime.Runtime!.Snapshot.AudioProgram.ActiveVideoSourceId.ToString() == sourceA.Id);
		await client.SynchronizeAsync();

		Assert.Equal(sourceA.Id, client.Snapshot!.Production.Routing.ProgramSourceId.ToString());
		Assert.Equal(sourceA.Id, client.Snapshot.AudioProgram.ActiveVideoSourceId);

		Assert.Equal(2, client.Snapshot.Scenes.Count);
		var sceneB = Assert.Single(client.Snapshot.Scenes, scene => scene.ProgramSourceId == sourceB.Id);
		var sceneTake = await client.ActivateSceneAsync(sceneB.Id);
		Assert.True(sceneTake.Accepted, sceneTake.Failure?.ToString());
		Assert.Equal(sceneB.Id, client.Snapshot!.Production.ActiveSceneId?.ToString());
		Assert.Equal(sourceB.Id, client.Snapshot.Production.Routing.PreviewSourceId.ToString());
		Assert.Equal(sourceB.Id, client.Snapshot.Production.Routing.ProgramSourceId.ToString());
		await WaitUntilAsync(() => runtime.Runtime!.Snapshot.AudioProgram.ActiveVideoSourceId.ToString() == sourceB.Id);

		Assert.True(client.Snapshot.Production.Revision.Value >= 5);
		Assert.True(transport.Connected);
		Assert.True(transport.StateVersion > 1);
		Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Runtime!.Snapshot.Runtime.Status);

		controlStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		runtimeStop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	[Fact]
	public async Task Serialized_operator_preview_takes_remain_authoritative_under_rapid_actions()
	{
		var runtimeEndpoint = Endpoint("runtime-rapid-takes");
		var controlEndpoint = Endpoint("control-rapid-takes");
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

		var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
		var initial = await client.SynchronizeAsync();
		var sourceA = initial.Sources[0];
		var sourceB = initial.Sources[1];
		var previousRevision = initial.Production.Revision.Value;

		for (var index = 0; index < 8; index++)
		{
			var next = index % 2 == 0 ? sourceB : sourceA;
			var preview = await client.SelectPreviewAsync(next.Id);
			Assert.True(preview.Accepted, preview.Failure?.ToString());
			Assert.Equal(next.Id, client.Snapshot!.Production.Routing.PreviewSourceId.ToString());

			var take = index % 3 == 0
				? await client.DissolvePreviewAsync(2)
				: await client.CutPreviewAsync();
			Assert.True(take.Accepted, take.Failure?.ToString());
			Assert.Equal(next.Id, client.Snapshot!.Production.Routing.ProgramSourceId.ToString());
			Assert.True(client.Snapshot.Production.Revision.Value > previousRevision);
			previousRevision = client.Snapshot.Production.Revision.Value;
			Assert.False(control.Control!.HasPendingExecution);
		}

		Assert.Equal(RuntimeExecutionStatus.Committed, runtime.Runtime!.Snapshot.Runtime.Status);

		controlStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		runtimeStop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	[Fact]
	public async Task Media_deck_crosses_operator_control_and_runtime_boundaries_with_persistent_markers()
	{
		using var referenceAsset = LocalMediaTestAsset.ExtractReference1080p50();
		var runtimeEndpoint = Endpoint("runtime-deck");
		var controlEndpoint = Endpoint("control-deck");
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

		var transport = new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
		var client = new OperatorControlClient(transport);
		var production = await client.SynchronizeAsync();
		var sourceId = new MediaSourceId(Identity.Parse(production.Sources[0].Id));
		await using var deck = new MediaDeckController(client);

		var opened = await deck.OpenAsync(referenceAsset.Path, sourceId);
		Assert.True(opened.IsLoaded, opened.Failure?.Message);
		Assert.Equal(Path.GetFileName(referenceAsset.Path), opened.Probe!.FileName);
		Assert.Equal(MediaDeckState.Ready, opened.State);
		Assert.Equal(50, opened.Transport!.Position.TotalFrames);

		var stablePlayback = await deck.ConfigurePlaybackAsync(
			autoPlayOnProgram: true,
			MediaDeckEndBehavior.Loop);
		Assert.True(stablePlayback.Transport!.AutoPlayOnProgram);
		Assert.Equal(MediaDeckEndBehavior.Loop, stablePlayback.Transport.EndBehavior);

		var autoplayDeadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < autoplayDeadline)
		{
			await deck.RefreshAsync();
			if (deck.Snapshot.Transport is { State: MediaTransportState.Playing, IsOnProgram: true })
				break;
			await Task.Delay(20);
		}
		Assert.Equal(MediaDeckState.Playing, deck.Snapshot.State);
		Assert.True(deck.Snapshot.Transport!.IsOnProgram);

		OperatorSourceDescriptor? mediaTile = null;
		var sourceBinDeadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < sourceBinDeadline)
		{
			var sourceBinOpened = await client.SynchronizeAsync();
			mediaTile = sourceBinOpened.Sources.Single(source => source.Id == sourceId.ToString());
			if (string.Equals(mediaTile.MediaState, "PLAYING", StringComparison.Ordinal))
				break;
			await Task.Delay(20);
		}
		Assert.NotNull(mediaTile);
		Assert.Equal("MEDIA", mediaTile.Type);
		Assert.Equal("READY", mediaTile.Health);
		Assert.Equal("PLAYING", mediaTile.MediaState);
		Assert.Contains("1920×1080", mediaTile.Format);
		Assert.Equal(Path.GetFileName(referenceAsset.Path), mediaTile.MediaFileName);
		Assert.NotNull(mediaTile.Remaining);

		var playbackDeadline = DateTime.UtcNow.AddSeconds(2);
		do
		{
			await Task.Delay(40);
			await deck.RefreshAsync();
		}
		while (deck.Snapshot.Transport!.Position.CurrentFrame == 0 && DateTime.UtcNow < playbackDeadline);
		Assert.True(deck.Snapshot.Transport!.Position.CurrentFrame > 0);
		var sourceBinPlaying = await client.SynchronizeAsync();
		var playingTile = sourceBinPlaying.Sources.Single(source => source.Id == sourceId.ToString());
		Assert.Equal("PLAYING", playingTile.MediaState);
		Assert.True(playingTile.Remaining < opened.Transport.Position.Remaining);

		await deck.PauseAsync();
		await deck.Timeline.SeekToFrameAsync(10);
		Assert.Equal(10, deck.Timeline.State.ConfirmedFrame);
		Assert.True(await deck.Markers.SetInAtCurrentFrameAsync());
		var cueId = await deck.Markers.AddCueAtCurrentFrameAsync("Decision");
		Assert.True(cueId.HasValue);
		Assert.Equal(10, deck.Snapshot.Markers!.InPointFrame);
		Assert.Single(deck.Snapshot.Markers.CuePoints);

		await deck.CloseAsync();
		var reopened = await deck.OpenAsync(referenceAsset.Path, sourceId);
		Assert.Equal(10, reopened.Markers!.InPointFrame);
		Assert.Equal("Decision", reopened.Markers.CuePoints.Single().Name);

		controlStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		runtimeStop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
	}

	[Fact]
	public async Task RuntimeHost_restart_resynchronizes_without_advancing_authoritative_revision()
	{
		var runtimeEndpoint = Endpoint("runtime-restart");
		var controlEndpoint = Endpoint("control-restart");
		using var firstRuntimeStop = new CancellationTokenSource();
		using var secondRuntimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var firstRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			ConnectTimeout = TimeSpan.FromMilliseconds(150),
			RequestTimeout = TimeSpan.FromSeconds(2),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});

		var firstRun = firstRuntime.RunAsync(firstRuntimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);

		var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
		var initial = await client.SynchronizeAsync();
		Assert.True((await client.SelectPreviewAsync(initial.Sources[1].Id)).Accepted);
		var revisionBeforeRestart = control.Control!.State.Revision;
		var firstInstance = control.RuntimeTransport!.HostInstanceId;

		firstRuntimeStop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await firstRun);

		var secondRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var secondRun = secondRuntime.RunAsync(secondRuntimeStop.Token);
		await WaitUntilAsync(() =>
			control.Lifecycle.State == ControlHostProcessState.Ready &&
			!string.IsNullOrWhiteSpace(control.RuntimeTransport?.HostInstanceId) &&
			!string.Equals(firstInstance, control.RuntimeTransport!.HostInstanceId, StringComparison.Ordinal));

		Assert.Equal(revisionBeforeRestart, control.Control.State.Revision);
		Assert.Equal(RuntimeExecutionStatus.Committed, secondRuntime.Runtime!.Snapshot.Runtime.Status);

		controlStop.Cancel();
		secondRuntimeStop.Cancel();
		Assert.Equal(ControlHostExitCode.Success, await controlRun);
		Assert.Equal(RuntimeHostExitCode.Success, await secondRun);
	}

	[Fact]
	public async Task AIHost_executes_managed_reference_inference_over_named_pipe()
	{
		var endpoint = Endpoint("ai");
		using var stop = new CancellationTokenSource();
		var process = new AIHostProcess(AIHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = process.RunAsync(stop.Token);
		await WaitUntilAsync(() => process.Lifecycle.State == AIHostProcessState.Ready);

		var sourceA = new MediaSourceId(Identity.Parse("76000000-0000-0000-0000-00000000000a"));
		var sourceB = new MediaSourceId(Identity.Parse("76000000-0000-0000-0000-00000000000b"));
		var media = new VirtualMediaReferenceProvider(sourceA, sourceB, VideoFormat.Hd1080p50Rgba8);
		var frame = media.SourceA.GenerateFrame(0);
		var payload = CreateAiExecutionPayload(frame);

		using var response = await ExchangeRawAsync(
			endpoint,
			"ControlHost",
			new Dictionary<string, string>
			{
				["ai"] = AIContractVersion.Current.ToString(),
				["media"] = MediaContractVersion.Current.ToString()
			},
			"ai.inference.execute",
			payload);

		Assert.Equal("ai.inference.execute.response", response.RootElement.GetProperty("messageType").GetString());
		var result = response.RootElement.GetProperty("payload").GetProperty("result");
		Assert.Equal((int)InferenceExecutionStatus.Succeeded, result.GetProperty("status").GetInt32());
		Assert.Equal(0U, process.Service!.Snapshot.ActiveRequests);

		stop.Cancel();
		Assert.Equal(AIHostExitCode.Success, await run);
	}

	[Fact]
	public async Task RuntimeHost_rejects_wrong_handshake_role_fail_closed()
	{
		var endpoint = Endpoint("runtime-role");
		using var stop = new CancellationTokenSource();
		var process = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = endpoint });
		var run = process.RunAsync(stop.Token);

		using var response = await ExchangeRawAsync(
			endpoint,
			"OperatorClient",
			new Dictionary<string, string>
			{
				["runtime"] = RuntimeContractVersion.Current.ToString(),
				["provider"] = "1.0"
			},
			messageType: null,
			payload: null,
			expectHandshakeError: true);

		Assert.Equal("error", response.RootElement.GetProperty("messageType").GetString());
		Assert.Equal("ipc.role.invalid", response.RootElement.GetProperty("payload").GetProperty("code").GetString());

		stop.Cancel();
		Assert.Equal(RuntimeHostExitCode.Success, await run);
	}

	[Fact]
	public async Task RuntimeHost_can_be_addressed_as_a_separate_OS_process()
	{
		var endpoint = Endpoint("runtime-process");
		var repo = FindRepositoryRoot();
		var assembly = Path.Combine(repo, "src", "Hosts", "rtaime.RuntimeHost", "bin", "Release", "net10.0", "rtaime.RuntimeHost.dll");
		Assert.True(File.Exists(assembly), $"RuntimeHost build output was not found at '{assembly}'.");

		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = $"\"{assembly}\" --listen-endpoint={endpoint}",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		}) ?? throw new InvalidOperationException("Failed to start RuntimeHost process.");

		try
		{
			var transport = new NamedPipeRuntimeHostTransport(endpoint, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
			await RetryAsync(() => transport.ConnectAsync().AsTask(), attempts: 20, delay: TimeSpan.FromMilliseconds(100));
			var providers = await transport.GetProviderDescriptorsAsync();

			Assert.True(transport.IsConnected);
			Assert.NotEmpty(providers);
			Assert.False(process.HasExited);
		}
		finally
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync();
		}
	}

	[Fact]
	public void Remote_state_synchronizer_requires_exact_base_version_and_host_identity()
	{
		var synchronizer = new RemoteStateSynchronizer();
		synchronizer.AcceptFullSnapshot("host-a", 5);

		Assert.False(synchronizer.TryApplyDelta("host-b", 5, 6));
		Assert.False(synchronizer.TryApplyDelta("host-a", 4, 6));
		Assert.True(synchronizer.TryApplyDelta("host-a", 5, 6));
		Assert.Equal(6UL, synchronizer.StateVersion);
	}

	private static object CreateAiExecutionPayload(FrameDescriptor frame)
	{
		return new
		{
			version = AIContractVersion.Current.ToString(),
			request = new
			{
				requestId = InferenceRequestId.New().ToString(),
				capabilityId = ManagedReferencePersonSegmentationProvider.PersonSegmentationCapabilityId.ToString(),
				inputFrame = new
				{
					sourceId = frame.SourceId.ToString(),
					surfaceId = frame.Surface.SurfaceId.ToString(),
					format = new
					{
						width = frame.Surface.Format.Width,
						height = frame.Surface.Format.Height,
						frameRate = frame.Surface.Format.FrameRate.ToString(),
						pixelFormat = (int)frame.Surface.Format.PixelFormat,
						scanMode = (int)frame.Surface.Format.ScanMode
					},
					storageDomain = (int)frame.Surface.StorageDomain,
					ownership = (int)frame.Surface.Ownership,
					generation = frame.Surface.Lifetime.Generation.Value,
					leaseId = frame.Surface.Lifetime.LeaseId?.ToString(),
					handleKind = frame.Surface.Handle?.Kind,
					handleValue = frame.Surface.Handle?.Value,
					sequenceNumber = frame.Timing.SequenceNumber,
					presentationTimestamp = frame.Timing.PresentationTimestamp,
					timebase = frame.Timing.Timebase.ToString()
				},
				deadlineUtc = new UtcTimestamp(DateTimeOffset.UtcNow.AddSeconds(10)).ToString(),
				parameters = Array.Empty<object>()
			},
			context = new
			{
				timingDomainId = Identity.New().ToString(),
				productionTimestamp = frame.Timing.PresentationTimestamp,
				productionTimebase = frame.Timing.Timebase.ToString(),
				computeUnits = 10U,
				vramBytes = 64UL * 1024 * 1024,
				maxInferenceRatePerSecond = 25U,
				inputResourceKind = "surface.descriptor",
				inputResourceValue = frame.Surface.SurfaceId.ToString()
			}
		};
	}

	private static async Task<JsonDocument> ExchangeRawAsync(
		string endpoint,
		string role,
		Dictionary<string, string> contractVersions,
		string? messageType,
		object? payload,
		bool expectHandshakeError = false)
	{
		await using var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		await pipe.ConnectAsync(3000);
		var correlationId = Identity.New().ToString();
		await WriteEnvelopeAsync(pipe, "client.hello", Identity.New().ToString(), correlationId, new
		{
			protocolVersion = "1.0",
			role,
			hostInstanceId = Identity.New().ToString(),
			contractVersions
		});
		var hello = await ReadEnvelopeAsync(pipe);
		if (expectHandshakeError)
			return hello;

		Assert.Equal("server.hello", hello.RootElement.GetProperty("messageType").GetString());
		hello.Dispose();
		await WriteEnvelopeAsync(pipe, messageType!, Identity.New().ToString(), correlationId, payload!);
		return await ReadEnvelopeAsync(pipe);
	}

	private static async Task WriteEnvelopeAsync(Stream stream, string messageType, string requestId, string correlationId, object payload)
	{
		var envelope = new
		{
			protocolVersion = "1.0",
			messageType,
			requestId,
			correlationId,
			hostInstanceId = Identity.New().ToString(),
			sentAtUtc = DateTimeOffset.UtcNow,
			stateVersion = 0UL,
			sequence = 0UL,
			payload
		};
		var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
		var prefix = new byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
		await stream.WriteAsync(prefix);
		await stream.WriteAsync(bytes);
		await stream.FlushAsync();
	}

	private static async Task<JsonDocument> ReadEnvelopeAsync(Stream stream)
	{
		var prefix = new byte[4];
		await ReadExactlyAsync(stream, prefix);
		var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
		Assert.InRange(length, 1U, 1024U * 1024U);
		var bytes = new byte[checked((int)length)];
		await ReadExactlyAsync(stream, bytes);
		return JsonDocument.Parse(bytes);
	}

	private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
	{
		var offset = 0;
		while (offset < buffer.Length)
		{
			var read = await stream.ReadAsync(buffer[offset..]);
			if (read == 0) throw new EndOfStreamException();
			offset += read;
		}
	}

	private static async Task RetryAsync(Func<Task> action, int attempts = 10, TimeSpan? delay = null)
	{
		Exception? last = null;
		for (var attempt = 0; attempt < attempts; attempt++)
		{
			try
			{
				await action();
				return;
			}
			catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
			{
				last = exception;
				await Task.Delay(delay ?? TimeSpan.FromMilliseconds(50));
			}
		}
		throw new InvalidOperationException("IPC endpoint did not become available.", last);
	}

	[Fact]
	public async Task Production_CG_text_crosses_operator_control_and_runtime_boundaries()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var runtimeEndpoint = Endpoint("runtime-cg");
		var controlEndpoint = Endpoint("control-cg");
		using var runtimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		try
		{
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
			await client.SynchronizeAsync();

			var graphics = await client.ApplyProductionCgTextAsync(OperatorProductionCgText.LowerThird("END TO END CG"));
			Assert.True(graphics.AssetLoaded);
			Assert.True(graphics.Visible);
			Assert.Equal("production-cg-text", graphics.AssetName);
			Assert.NotNull(client.Snapshot);
			Assert.True(client.Snapshot!.ProductionCgText.Active);
			Assert.Equal("END TO END CG", client.Snapshot.ProductionCgText.Text);
			Assert.False(string.IsNullOrWhiteSpace(client.Snapshot.ProductionCgText.ResolvedTypeface));
			Assert.True(runtime.Runtime!.Snapshot.ProductionCgText!.Active);
			Assert.Equal("END TO END CG", runtime.Runtime.Snapshot.ProductionCgText.Text);

			await client.SetGraphicsOverlayAsync(false, graphics.PositionX, graphics.PositionY, graphics.Scale);
			Assert.False(client.Snapshot!.ProductionCgText.Visible);
			await client.SetGraphicsOverlayAsync(true, graphics.PositionX, graphics.PositionY, graphics.Scale);
			Assert.True(client.Snapshot!.ProductionCgText.Visible);
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
		}
	}

	[Fact]
	public async Task Graphics_layer_stack_is_restored_after_RuntimeHost_restart()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var runtimeEndpoint = Endpoint("runtime-graphics-recovery");
		var controlEndpoint = Endpoint("control-graphics-recovery");
		using var firstRuntimeStop = new CancellationTokenSource();
		using var controlStop = new CancellationTokenSource();
		var firstRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});
		var firstRuntimeRun = firstRuntime.RunAsync(firstRuntimeStop.Token);
		var controlRun = control.RunAsync(controlStop.Token);
		Task<RuntimeHostExitCode>? secondRuntimeRun = null;
		CancellationTokenSource? secondRuntimeStop = null;
		try
		{
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true);
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
			await client.SynchronizeAsync();
			await client.LoadGraphicsOverlayAsync(new OperatorGraphicsAsset(
				"recovered-logo.rgba",
				1,
				1,
				new byte[] { 255, 0, 0, 255 }));
			await client.SetGraphicsOverlayAsync(true, 0.10, 0.20, 1.25);
			await client.ApplyProductionCgTextAsync(OperatorProductionCgText.LowerThird("RECOVERED CG"));
			await client.SetCompositingLayerStateAsync("bitmap-graphics", visible: true, opacity: 128);
			await client.ReorderCompositingLayersAsync(new[] { "production-cg", "bitmap-graphics" });

			var beforeRestart = firstRuntime.Runtime!.Snapshot;
			Assert.Equal("recovered-logo.rgba", beforeRestart.GraphicsOverlay.AssetName);
			Assert.True(beforeRestart.ProductionCgText!.Visible);
			Assert.Equal(
				new[] { "production-cg", "bitmap-graphics" },
				beforeRestart.CompositingLayers!.Select(layer => layer.LayerId));
			Assert.Equal(
				128,
				Assert.Single(beforeRestart.CompositingLayers!, layer => layer.LayerId == "bitmap-graphics").Opacity);

			firstRuntimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await firstRuntimeRun);

			secondRuntimeStop = new CancellationTokenSource();
			var secondRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
			secondRuntimeRun = secondRuntime.RunAsync(secondRuntimeStop.Token);

			await WaitUntilAsync(
				() =>
					secondRuntime.Runtime?.Snapshot is { } snapshot &&
					snapshot.GraphicsOverlay is { AssetLoaded: true, AssetName: "recovered-logo.rgba", Visible: true } &&
					snapshot.ProductionCgText is { Active: true, Visible: true, Text: "RECOVERED CG" } &&
					snapshot.CompositingLayers?.Count == 2 &&
					snapshot.CompositingLayers[0].LayerId == "production-cg" &&
					snapshot.CompositingLayers[1].LayerId == "bitmap-graphics" &&
					snapshot.CompositingLayers[1].Opacity == 128,
				timeoutMilliseconds: 15000);

			var restored = await client.SynchronizeAsync();
			Assert.True(restored.ProductionCgText.Active);
			Assert.True(restored.ProductionCgText.Visible);
			Assert.Equal("RECOVERED CG", restored.ProductionCgText.Text);
			Assert.Equal("recovered-logo.rgba", restored.GraphicsOverlay.AssetName);
			Assert.Equal(0.10, restored.GraphicsOverlay.PositionX, 6);
			Assert.Equal(0.20, restored.GraphicsOverlay.PositionY, 6);
			Assert.Equal(1.25, restored.GraphicsOverlay.Scale, 6);
			Assert.Equal(
				new[] { "production-cg", "bitmap-graphics" },
				restored.CompositingLayers.Select(layer => layer.LayerId));
			Assert.Equal(
				128,
				Assert.Single(restored.CompositingLayers, layer => layer.LayerId == "bitmap-graphics").Opacity);
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			if (secondRuntimeStop is not null && secondRuntimeRun is not null)
			{
				secondRuntimeStop.Cancel();
				Assert.Equal(RuntimeHostExitCode.Success, await secondRuntimeRun);
				secondRuntimeStop.Dispose();
			}
			else if (!firstRuntimeRun.IsCompleted)
			{
				firstRuntimeStop.Cancel();
				Assert.Equal(RuntimeHostExitCode.Success, await firstRuntimeRun);
			}
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException("Condition was not reached before the integration-test deadline.");
			await Task.Delay(20);
		}
	}

	private static string Endpoint(string purpose) => $"rtaime.test.{purpose}.{Guid.NewGuid():N}";

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}
		throw new DirectoryNotFoundException("Repository root containing rtaime.slnx was not found.");
	}
}
