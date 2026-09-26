// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using System.Text.Json;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class ControlHostRecoveryIntegrationTests
{
	[Fact]
	public async Task ControlHost_restart_restores_checkpoint_and_rebinds_existing_Runtime_without_revision_change()
	{
		var root = TempDirectory();
		var runtimeEndpoint = Endpoint("runtime-control-restart");
		var controlEndpoint = Endpoint("control-restart");
		var options = ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			DurabilityRoot = root,
			ConnectTimeout = TimeSpan.FromMilliseconds(150),
			RequestTimeout = TimeSpan.FromSeconds(2),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		};
		using var runtimeStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);

		try
		{
			Revision committedRevision;
			ProductionRoutingState committedRouting;
			ProductionSourceId committedAuxSource;
			string committedSceneId;
			string runtimeHostInstanceId;
			using (var firstControlStop = new CancellationTokenSource())
			{
				var firstControl = new ControlHostProcess(options);
				var firstControlRun = firstControl.RunAsync(firstControlStop.Token);
				await WaitUntilAsync(() => firstControl.Lifecycle.State == ControlHostProcessState.Ready && firstControl.Control?.HasAuthoritativeState == true);

				var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
				var initial = await client.SynchronizeAsync();
				var scene = initial.Scenes[1];
				var mutation = await client.ActivateSceneAsync(scene.Id);
				Assert.True(mutation.Accepted, mutation.Failure?.ToString());
				var auxSource = initial.Sources.Single(source => source.Id != scene.ProgramSourceId);
				var auxMutation = await client.RouteOutputRoleAsync("aux", auxSource.Id);
				Assert.True(auxMutation.Accepted, auxMutation.Failure?.ToString());
				committedRevision = firstControl.Control!.State.Revision;
				committedRouting = firstControl.Control.State.Routing;
				committedAuxSource = Assert.Single(firstControl.Control.State.OutputRoles, role => role.RoleId == OutputRoleIds.Aux).SourceId;
				committedSceneId = Assert.IsType<SceneId>(firstControl.Control.State.ActiveSceneId).ToString();
				runtimeHostInstanceId = firstControl.RuntimeTransport!.HostInstanceId!;
				Assert.True(committedRevision.Value > Revision.Initial.Value);
				var checkpointWriter = Assert.IsType<BoundedProductionCheckpointWriter>(firstControl.CheckpointWriter);
				await checkpointWriter.FlushAsync();
				Assert.True(
					checkpointWriter.Statistics.Persisted >= 2,
					$"Expected at least two persisted checkpoints, observed {checkpointWriter.Statistics.Persisted}.");

				firstControlStop.Cancel();
				Assert.Equal(ControlHostExitCode.Success, await firstControlRun);
			}

			using var secondControlStop = new CancellationTokenSource();
			var secondControl = new ControlHostProcess(options);
			var secondControlRun = secondControl.RunAsync(secondControlStop.Token);
			await WaitForRecoveredReadyAsync(secondControl, secondControlRun);

			Assert.Equal(committedRevision, secondControl.Control!.State.Revision);
			Assert.Equal(committedRouting, secondControl.Control.State.Routing);
			Assert.Equal(committedAuxSource, Assert.Single(secondControl.Control.State.OutputRoles, role => role.RoleId == OutputRoleIds.Aux).SourceId);
			Assert.Equal(committedSceneId, secondControl.Control.State.ActiveSceneId?.ToString());
			Assert.Equal(runtimeHostInstanceId, secondControl.RuntimeTransport!.HostInstanceId);
			Assert.Contains("aligned", secondControl.Recovery.Detail, StringComparison.OrdinalIgnoreCase);

			var reconnectedClient = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
			var recoveredSnapshot = await reconnectedClient.SynchronizeAsync();
			Assert.Equal(committedRevision, recoveredSnapshot.Production.Revision);
			Assert.Equal(committedRouting, recoveredSnapshot.Production.Routing);
			Assert.Equal(committedAuxSource, Assert.Single(recoveredSnapshot.Production.OutputRoles, role => role.RoleId == OutputRoleIds.Aux).SourceId);
			Assert.Equal(committedSceneId, recoveredSnapshot.Production.ActiveSceneId?.ToString());

			secondControlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await secondControlRun);
		}
		finally
		{
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Control_and_Runtime_restart_restore_durable_show_graphics_before_authority_reapply()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var root = TempDirectory();
		var runtimeEndpoint = Endpoint("runtime-durable-show");
		var controlEndpoint = Endpoint("control-durable-show");
		var options = ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			DurabilityRoot = root,
			ConnectTimeout = TimeSpan.FromMilliseconds(150),
			RequestTimeout = TimeSpan.FromSeconds(3),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		};

		Revision committedRevision;
		string breakawaySourceId = string.Empty;
		try
		{
			using (var firstRuntimeStop = new CancellationTokenSource())
			using (var firstControlStop = new CancellationTokenSource())
			{
				var firstRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
				var firstRuntimeRun = firstRuntime.RunAsync(firstRuntimeStop.Token);
				var firstControl = new ControlHostProcess(options);
				var firstControlRun = firstControl.RunAsync(firstControlStop.Token);
				await WaitUntilAsync(() => firstControl.Lifecycle.State == ControlHostProcessState.Ready && firstControl.Control?.HasAuthoritativeState == true);

				var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
				var initialSnapshot = await client.SynchronizeAsync();
				breakawaySourceId = initialSnapshot.Sources
					.First(source => !string.Equals(source.Id, initialSnapshot.Production.Routing.ProgramSourceId.ToString(), StringComparison.Ordinal))
					.Id;
				await client.SetAudioRoutingAsync(OperatorAudioRoutingMode.Breakaway, breakawaySourceId);
				await client.LoadGraphicsOverlayAsync(new OperatorGraphicsAsset(
					"durable-logo.rgba",
					1,
					1,
					new byte[] { 255, 32, 16, 255 }));
				await client.SetGraphicsOverlayAsync(true, 0.12, 0.18, 1.25);
				await client.ApplyProductionCgTextAsync(OperatorProductionCgText.LowerThird("DURABLE SHOW STATE"));
				await client.SetCompositingLayerStateAsync("bitmap-graphics", visible: true, opacity: 160);
				await client.SetCompositingLayerStateAsync("production-cg", visible: true, opacity: 224);
				var transformedLayers = await client.SetCompositingLayerTransformAsync(
					"bitmap-graphics",
					0.12,
					0.18,
					1.25,
					22.5,
					0.5,
					0.5,
					0.05,
					0.10,
					0.15,
					0.20);
				var transformedBitmap = Assert.Single(transformedLayers, layer => layer.LayerId == "bitmap-graphics");
				Assert.Equal(22.5, transformedBitmap.RotationDegrees, 6);
				Assert.Equal(0.5, transformedBitmap.AnchorX, 6);
				Assert.Equal(0.15, transformedBitmap.CropRight, 6);

				var processedLayers = await client.SetCompositingLayerProcessingNodeAsync(
					"bitmap-graphics",
					new OperatorCompositingProcessingNodeDescriptor(
						"grade-primary",
						1,
						true,
						new OperatorColorGradeDescriptor(0.1, 1.1, 0.9)));
				var processedBitmap = Assert.Single(processedLayers, layer => layer.LayerId == "bitmap-graphics");
				Assert.Equal(22.5, processedBitmap.RotationDegrees, 6);
				Assert.NotNull(processedBitmap.ProcessingNode);

				var beforeRestart = await client.SynchronizeAsync();
				var requestedOrder = beforeRestart.CompositingLayers
					.OrderBy(layer => layer.LayerId == "production-cg" ? 0 : layer.LayerId == "bitmap-graphics" ? 1 : 2)
					.ThenBy(layer => layer.Order)
					.Select(layer => layer.LayerId)
					.ToArray();
				var reorderedLayers = await client.ReorderCompositingLayersAsync(requestedOrder);
				var reorderedBitmap = Assert.Single(reorderedLayers, layer => layer.LayerId == "bitmap-graphics");
				Assert.Equal(22.5, reorderedBitmap.RotationDegrees, 6);
				Assert.NotNull(reorderedBitmap.ProcessingNode);
				beforeRestart = await client.SynchronizeAsync();

				Assert.Equal("durable-logo.rgba", beforeRestart.GraphicsOverlay.AssetName);
				Assert.True(beforeRestart.GraphicsOverlay.Visible);
				Assert.Equal("DURABLE SHOW STATE", beforeRestart.ProductionCgText.Text);
				var beforeBitmap = Assert.Single(beforeRestart.CompositingLayers, layer => layer.LayerId == "bitmap-graphics");
				Assert.Equal(22.5, beforeBitmap.RotationDegrees, 6);
				Assert.Equal(0.5, beforeBitmap.AnchorX, 6);
				Assert.Equal(0.15, beforeBitmap.CropRight, 6);
				Assert.NotNull(beforeBitmap.ProcessingNode);
				Assert.Equal(0.1, beforeBitmap.ProcessingNode!.ColorGrade.Brightness, 6);
				Assert.Equal(OperatorAudioRoutingMode.Breakaway, beforeRestart.AudioProgram.RoutingMode);
				Assert.Equal(breakawaySourceId, beforeRestart.AudioProgram.ActiveAudioSourceId);
				Assert.Equal("SAVED", beforeRestart.ShowProject.State);
				committedRevision = firstControl.Control!.State.Revision;

				var checkpointWriter = Assert.IsType<BoundedProductionCheckpointWriter>(firstControl.CheckpointWriter);
				await checkpointWriter.FlushAsync();

				firstControlStop.Cancel();
				Assert.Equal(ControlHostExitCode.Success, await firstControlRun);
				firstRuntimeStop.Cancel();
				Assert.Equal(RuntimeHostExitCode.Success, await firstRuntimeRun);
			}

			using var secondRuntimeStop = new CancellationTokenSource();
			using var secondControlStop = new CancellationTokenSource();
			var secondRuntime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
			var secondRuntimeRun = secondRuntime.RunAsync(secondRuntimeStop.Token);
			var secondControl = new ControlHostProcess(options);
			var secondControlRun = secondControl.RunAsync(secondControlStop.Token);
			await WaitForRecoveredReadyAsync(secondControl, secondControlRun);

			var restoredClient = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
			var restored = await restoredClient.SynchronizeAsync();
			Assert.Equal(committedRevision, restored.Production.Revision);
			Assert.Equal("durable-logo.rgba", restored.GraphicsOverlay.AssetName);
			Assert.True(restored.GraphicsOverlay.Visible);
			Assert.Equal("DURABLE SHOW STATE", restored.ProductionCgText.Text);
			Assert.True(restored.ProductionCgText.Visible);
			Assert.Equal(OperatorAudioRoutingMode.Breakaway, restored.AudioProgram.RoutingMode);
			Assert.Equal(breakawaySourceId, restored.AudioProgram.ActiveAudioSourceId);
			var restoredBitmap = Assert.Single(restored.CompositingLayers, layer => layer.LayerId == "bitmap-graphics");
			Assert.True(restoredBitmap.Visible);
			Assert.Equal((byte)160, restoredBitmap.Opacity);
			Assert.Equal(22.5, restoredBitmap.RotationDegrees, 6);
			Assert.Equal(0.5, restoredBitmap.AnchorX, 6);
			Assert.Equal(0.5, restoredBitmap.AnchorY, 6);
			Assert.Equal(0.05, restoredBitmap.CropLeft, 6);
			Assert.Equal(0.10, restoredBitmap.CropTop, 6);
			Assert.Equal(0.15, restoredBitmap.CropRight, 6);
			Assert.Equal(0.20, restoredBitmap.CropBottom, 6);
			Assert.NotNull(restoredBitmap.ProcessingNode);
			Assert.Equal("grade-primary", restoredBitmap.ProcessingNode!.NodeId);
			Assert.Equal(0.1, restoredBitmap.ProcessingNode.ColorGrade.Brightness, 6);
			Assert.Equal(1.1, restoredBitmap.ProcessingNode.ColorGrade.Contrast, 6);
			Assert.Equal(0.9, restoredBitmap.ProcessingNode.ColorGrade.Saturation, 6);
			Assert.Contains(restored.CompositingLayers, layer => layer.LayerId == "production-cg" && layer.Visible && layer.Opacity == 224);
			Assert.Equal("RESTORED", restored.ShowProject.State);

			secondControlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await secondControlRun);
			secondRuntimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await secondRuntimeRun);
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Runtime_authority_ahead_of_durable_Control_authority_fails_closed_without_reapply()
	{
		var root = TempDirectory();
		var controlEndpoint = Endpoint("control-conflict");
		var options = RecoveryOptions(root, controlEndpoint, Endpoint("runtime-conflict"));
		await WriteCheckpointAsync(options, controlEndpoint, new Revision(1));

		var transport = new RuntimeSnapshotTransport(
			options.ProductionId.Value,
			executionRevision: new Revision(9),
			authorityRevision: new Revision(2));
		using var stop = new CancellationTokenSource();
		var control = new ControlHostProcess(options, () => transport);
		var run = control.RunAsync(stop.Token);
		try
		{
			await WaitUntilAsync(() => control.Recovery.State == ControlHostRecoveryState.Conflict);
			Assert.Equal(ControlHostProcessState.Degraded, control.Lifecycle.State);
			Assert.Equal(new Revision(1), control.Control!.State.Revision);
			Assert.Equal(new Revision(2), control.Recovery.RecoveredRevision);
			Assert.Equal(0, transport.ApplyCalls);
		}
		finally
		{
			stop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await run);
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Runtime_execution_revision_is_independent_when_committed_authority_matches_Control()
	{
		var root = TempDirectory();
		var controlEndpoint = Endpoint("control-independent-runtime-revision");
		var options = RecoveryOptions(root, controlEndpoint, Endpoint("runtime-independent-runtime-revision"));
		await WriteCheckpointAsync(options, controlEndpoint, new Revision(1));

		var transport = new RuntimeSnapshotTransport(
			options.ProductionId.Value,
			executionRevision: new Revision(9),
			authorityRevision: new Revision(1));
		using var stop = new CancellationTokenSource();
		var control = new ControlHostProcess(options, () => transport);
		var run = control.RunAsync(stop.Token);
		try
		{
			await WaitForRecoveredReadyAsync(control, run);
			Assert.Equal(new Revision(1), control.Control!.State.Revision);
			Assert.Equal(ControlHostRecoveryState.Recovered, control.Recovery.State);
			Assert.Contains("aligned", control.Recovery.Detail, StringComparison.OrdinalIgnoreCase);
			Assert.Equal(0, transport.ApplyCalls);
		}
		finally
		{
			stop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await run);
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Unsupported_checkpoint_format_fails_ControlHost_startup_instead_of_reinitializing_authority()
	{
		var root = TempDirectory();
		var controlEndpoint = Endpoint("control-invalid-checkpoint");
		var options = ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = Endpoint("runtime-invalid-checkpoint"),
			DurabilityRoot = root
		};
		var durabilityDirectory = Path.Combine(root, $"{options.ProductionId}-{controlEndpoint}");
		Directory.CreateDirectory(durabilityDirectory);
		await using (var store = new SqliteManagementStore(Path.Combine(durabilityDirectory, "management.db")))
		{
			await store.WriteAsync(new ProductionCheckpoint(
				Identity.New(),
				options.ProductionId.Value,
				new Revision(4),
				new UtcTimestamp(DateTimeOffset.UtcNow),
				"unsupported.authority.v99",
				Encoding.UTF8.GetBytes("{}")));
		}

		try
		{
			var process = new ControlHostProcess(
				options,
				() => new RuntimeSnapshotTransport(options.ProductionId.Value, new Revision(4), new Revision(4)));
			var result = await process.RunAsync(CancellationToken.None);
			Assert.Equal(ControlHostExitCode.StartupFailure, result);
			Assert.Equal(ControlHostProcessState.Failed, process.Lifecycle.State);
			Assert.False(process.Control?.HasAuthoritativeState ?? false);
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	private sealed class RuntimeSnapshotTransport : IControlRuntimeTransportSeam
	{
		private readonly Identity _authorityStateId;
		private readonly Revision _executionRevision;
		private readonly Revision _authorityRevision;
		private bool _connected;

		public RuntimeSnapshotTransport(Identity authorityStateId, Revision executionRevision, Revision authorityRevision)
		{
			_authorityStateId = authorityStateId;
			_executionRevision = executionRevision;
			_authorityRevision = authorityRevision;
		}

		public bool IsConnected => _connected;
		public string? HostInstanceId => _connected ? "runtime-snapshot-instance" : null;
		public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => Array.Empty<ProviderDescriptor>();
		public int ApplyCalls { get; private set; }

		public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
		{
			_connected = true;
			return ValueTask.CompletedTask;
		}

		public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<IReadOnlyList<ProviderDescriptor>>(Array.Empty<ProviderDescriptor>());

		public ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new RuntimeRemoteSnapshot(
				"runtime-snapshot-instance",
				new RuntimeExecutionState(
					RuntimeContractVersion.Current,
					new ExecutionInstanceId(Identity.New()),
					_executionRevision,
					RuntimeExecutionStatus.Committed,
					null),
				_authorityStateId,
				_authorityRevision,
				1,
				0,
				0,
				VideoFormat.Hd1080p50Rgba8,
				new Dictionary<MediaSourceId, string>(),
				new RuntimeGraphicsOverlaySnapshot(false, null, 0, 0, false, 0.72, 0.06, 1.0),
				new Dictionary<MediaSourceId, RuntimeAudioInputSnapshot>(),
				new RuntimeAudioProgramSnapshot(
					new MediaSourceId(Identity.Parse("7f000000-0000-0000-0000-00000000000a")),
					new AudioStreamId(Identity.Parse("7f000000-0000-0000-0000-00000000000b")),
					1,
					false,
					0,
					0,
					0,
					false,
					"SILENCE"),
				1));

		public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
			PreparedExecutionContract preparedExecution,
			MediaSinkId programSinkId,
			RuntimeProgramTransitionIntent? transition,
			CancellationToken cancellationToken = default)
		{
			ApplyCalls++;
			throw new InvalidOperationException("Aligned or conflicting recovery must not reach Runtime apply in this transport.");
		}

		public ValueTask DisconnectAsync()
		{
			_connected = false;
			return ValueTask.CompletedTask;
		}
	}

	private static ControlHostProcessOptions RecoveryOptions(string root, string controlEndpoint, string runtimeEndpoint) =>
		ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			DurabilityRoot = root,
			ConnectTimeout = TimeSpan.FromMilliseconds(100),
			RequestTimeout = TimeSpan.FromMilliseconds(500),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		};

	private static async Task WriteCheckpointAsync(ControlHostProcessOptions options, string controlEndpoint, Revision revision)
	{
		var durabilityDirectory = Path.Combine(options.DurabilityRoot, $"{options.ProductionId}-{controlEndpoint}");
		Directory.CreateDirectory(durabilityDirectory);
		await using var store = new SqliteManagementStore(Path.Combine(durabilityDirectory, "management.db"));
		var payload = JsonSerializer.SerializeToUtf8Bytes(new
		{
			Version = ControlContractVersion.Current.ToString(),
			ProductionId = options.ProductionId.ToString(),
			Revision = revision.Value,
			PreviewSourceId = options.SourceBId.ToString(),
			ProgramSourceId = options.SourceAId.ToString()
		});
		await store.WriteAsync(new ProductionCheckpoint(
			Identity.New(),
			options.ProductionId.Value,
			revision,
			new UtcTimestamp(DateTimeOffset.UtcNow),
			"rtaime.control.authority.v1",
			payload));
	}

	private static async Task WaitForRecoveredReadyAsync(
		ControlHostProcess process,
		Task<ControlHostExitCode> run,
		int timeoutMilliseconds = 15000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (process.Lifecycle.State != ControlHostProcessState.Ready || process.Recovery.State != ControlHostRecoveryState.Recovered)
		{
			if (run.IsCompleted)
			{
				var exit = await run;
				throw new InvalidOperationException(
					$"Recovered ControlHost exited before Ready. Exit={exit}; lifecycle={process.Lifecycle.State}/{process.Lifecycle.Health}: {process.Lifecycle.Detail}; recovery={process.Recovery.State}: {process.Recovery.Detail}");
			}
			if (DateTime.UtcNow >= deadline)
			{
				throw new TimeoutException(
					$"Recovered ControlHost did not reach Ready within {timeoutMilliseconds} ms. lifecycle={process.Lifecycle.State}/{process.Lifecycle.Health}: {process.Lifecycle.Detail}; recovery={process.Recovery.State}: {process.Recovery.Detail}");
			}
			await Task.Delay(20);
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException("Condition was not reached before the recovery integration-test deadline.");
			await Task.Delay(20);
		}
	}

	private static string Endpoint(string purpose) => $"rtaime.test.{purpose}.{Guid.NewGuid():N}";

	private static string TempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-recovery-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteDirectory(string path)
	{
		try { Directory.Delete(path, recursive: true); }
		catch { }
	}
}
