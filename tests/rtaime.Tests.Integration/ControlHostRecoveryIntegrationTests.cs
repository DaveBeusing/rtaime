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
			string runtimeHostInstanceId;
			using (var firstControlStop = new CancellationTokenSource())
			{
				var firstControl = new ControlHostProcess(options);
				var firstControlRun = firstControl.RunAsync(firstControlStop.Token);
				await WaitUntilAsync(() => firstControl.Lifecycle.State == ControlHostProcessState.Ready && firstControl.Control?.HasAuthoritativeState == true);

				var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
				var initial = await client.SynchronizeAsync();
				var mutation = await client.SelectPreviewAsync(initial.Sources[1].Id);
				Assert.True(mutation.Accepted, mutation.Failure?.ToString());
				committedRevision = firstControl.Control!.State.Revision;
				committedRouting = firstControl.Control.State.Routing;
				runtimeHostInstanceId = firstControl.RuntimeTransport!.HostInstanceId!;
				Assert.True(committedRevision.Value > Revision.Initial.Value);
				await WaitUntilAsync(() => firstControl.CheckpointWriter?.Statistics.Persisted >= 2);

				firstControlStop.Cancel();
				Assert.Equal(ControlHostExitCode.Success, await firstControlRun);
			}

			using var secondControlStop = new CancellationTokenSource();
			var secondControl = new ControlHostProcess(options);
			var secondControlRun = secondControl.RunAsync(secondControlStop.Token);
			await WaitForRecoveredReadyAsync(secondControl, secondControlRun);

			Assert.Equal(committedRevision, secondControl.Control!.State.Revision);
			Assert.Equal(committedRouting, secondControl.Control.State.Routing);
			Assert.Equal(runtimeHostInstanceId, secondControl.RuntimeTransport!.HostInstanceId);
			Assert.Contains("aligned", secondControl.Recovery.Detail, StringComparison.OrdinalIgnoreCase);

			var reconnectedClient = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
			var recoveredSnapshot = await reconnectedClient.SynchronizeAsync();
			Assert.Equal(committedRevision, recoveredSnapshot.Production.Revision);
			Assert.Equal(committedRouting, recoveredSnapshot.Production.Routing);

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
	public async Task Runtime_ahead_of_durable_Control_authority_fails_closed_without_reapply()
	{
		var root = TempDirectory();
		var controlEndpoint = Endpoint("control-conflict");
		var options = ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = Endpoint("runtime-conflict"),
			DurabilityRoot = root,
			ConnectTimeout = TimeSpan.FromMilliseconds(100),
			RequestTimeout = TimeSpan.FromMilliseconds(500),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		};
		var durabilityDirectory = Path.Combine(root, $"{options.ProductionId}-{controlEndpoint}");
		Directory.CreateDirectory(durabilityDirectory);
		await using (var store = new SqliteManagementStore(Path.Combine(durabilityDirectory, "management.db")))
		{
			var payload = JsonSerializer.SerializeToUtf8Bytes(new
			{
				Version = ControlContractVersion.Current.ToString(),
				ProductionId = options.ProductionId.ToString(),
				Revision = 1UL,
				PreviewSourceId = options.SourceBId.ToString(),
				ProgramSourceId = options.SourceAId.ToString()
			});
			await store.WriteAsync(new ProductionCheckpoint(
				Identity.New(),
				options.ProductionId.Value,
				new Revision(1),
				new UtcTimestamp(DateTimeOffset.UtcNow),
				"rtaime.control.authority.v1",
				payload));
		}

		var transport = new RuntimeAheadTransport(new Revision(2));
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
			var process = new ControlHostProcess(options, () => new RuntimeAheadTransport(new Revision(4)));
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

	private sealed class RuntimeAheadTransport : IControlRuntimeTransportSeam
	{
		private readonly Revision _runtimeRevision;
		private bool _connected;

		public RuntimeAheadTransport(Revision runtimeRevision) => _runtimeRevision = runtimeRevision;

		public bool IsConnected => _connected;
		public string? HostInstanceId => _connected ? "runtime-ahead-instance" : null;
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
				"runtime-ahead-instance",
				new RuntimeExecutionState(
					RuntimeContractVersion.Current,
					new ExecutionInstanceId(Identity.New()),
					_runtimeRevision,
					RuntimeExecutionStatus.Committed,
					null),
				1,
				0,
				0,
				1));

		public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
			PreparedExecutionContract preparedExecution,
			MediaSinkId programSinkId,
			RuntimeProgramTransitionIntent? transition,
			CancellationToken cancellationToken = default)
		{
			ApplyCalls++;
			throw new InvalidOperationException("Recovery conflict must be detected before Runtime apply.");
		}

		public ValueTask DisconnectAsync()
		{
			_connected = false;
			return ValueTask.CompletedTask;
		}
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
