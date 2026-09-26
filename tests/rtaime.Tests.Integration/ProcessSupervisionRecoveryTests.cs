// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text.Json;
using rtaime.Client;
using rtaime.Control.Contracts;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class ProcessSupervisionRecoveryTests
{
	private const int ProcessRecoveryTimeoutMilliseconds = 30000;
	private const int EndpointRecoveryAttempts = 150;

	[Fact]
	public async Task Killed_RuntimeHost_is_restarted_and_Control_reapplies_same_authority_revision()
	{
		var root = TempDirectory();
		var runtimeEndpoint = Endpoint("runtime-supervised");
		var controlEndpoint = Endpoint("control-runtime-supervised");
		var runtimeAssembly = HostAssembly("rtaime.RuntimeHost");
		await using var runtimeSupervisor = Supervisor("RuntimeHost", runtimeEndpoint, runtimeAssembly);
		await runtimeSupervisor.StartAsync();
		await WaitUntilAsync(() => runtimeSupervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null }, ProcessRecoveryTimeoutMilliseconds);

		using var controlStop = new CancellationTokenSource();
		var control = new ControlHostProcess(ControlHostProcessOptions.Default with
		{
			ListenEndpoint = controlEndpoint,
			RuntimeEndpoint = runtimeEndpoint,
			DurabilityRoot = root,
			ConnectTimeout = TimeSpan.FromMilliseconds(150),
			RequestTimeout = TimeSpan.FromSeconds(2),
			RuntimeRetryInterval = TimeSpan.FromMilliseconds(25)
		});
		var controlRun = control.RunAsync(controlStop.Token);

		try
		{
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true, ProcessRecoveryTimeoutMilliseconds);
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
			var initial = await client.SynchronizeAsync();
			var mutation = await client.SelectPreviewAsync(initial.Sources[1].Id);
			Assert.True(mutation.Accepted, mutation.Failure?.ToString());
			var authorityRevision = control.Control!.State.Revision;
			var firstRuntimePid = runtimeSupervisor.Snapshot.OwnedProcessId!.Value;

			Kill(firstRuntimePid);
			await WaitUntilAsync(
				() => runtimeSupervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null } snapshot && snapshot.OwnedProcessId != firstRuntimePid,
				ProcessRecoveryTimeoutMilliseconds);
			await WaitUntilAsync(
				() => control.Lifecycle.State == ControlHostProcessState.Ready &&
					control.Control!.State.Revision == authorityRevision &&
					control.Journal!.Entries.Any(entry => entry.Event.Code == "recovery.runtime.reapplied"),
				ProcessRecoveryTimeoutMilliseconds);

			Assert.Equal(authorityRevision, control.Control.State.Revision);
			var recovered = await client.SynchronizeAsync();
			Assert.Equal(authorityRevision, recovered.Production.Revision);
			Assert.Equal("READY", recovered.RuntimeStatus);
			var next = await client.CutPreviewAsync();
			Assert.True(next.Accepted, next.Failure?.ToString());
			Assert.True(control.Control.State.Revision.Value > authorityRevision.Value);
		}
		finally
		{
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Killed_AIHost_is_restarted_with_a_new_process_identity()
	{
		var endpoint = Endpoint("ai-supervised");
		var assembly = HostAssembly("rtaime.AIHost");
		await using var supervisor = Supervisor("AIHost", endpoint, assembly);
		await supervisor.StartAsync();
		await WaitUntilAsync(() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null }, ProcessRecoveryTimeoutMilliseconds);
		var firstPid = supervisor.Snapshot.OwnedProcessId!.Value;

		Kill(firstPid);
		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null } snapshot && snapshot.OwnedProcessId != firstPid,
			ProcessRecoveryTimeoutMilliseconds);

		Assert.NotEqual(firstPid, supervisor.Snapshot.OwnedProcessId);
	}

	[Fact]
	public async Task Killed_ControlHost_restores_durable_authority_while_Runtime_continues_and_stale_client_fails_closed()
	{
		var root = TempDirectory();
		var runtimeEndpoint = Endpoint("runtime-control-process-recovery");
		var controlEndpoint = Endpoint("control-process-recovery");
		var runtimeAssembly = HostAssembly("rtaime.RuntimeHost");
		var controlAssembly = HostAssembly("rtaime.ControlHost");
		await using var runtimeSupervisor = Supervisor("RuntimeHost", runtimeEndpoint, runtimeAssembly);
		await runtimeSupervisor.StartAsync();
		await WaitUntilAsync(() => runtimeSupervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null }, ProcessRecoveryTimeoutMilliseconds);
		var runtimePid = runtimeSupervisor.Snapshot.OwnedProcessId!.Value;

		var controlSupervisorOptions = BaseSupervisorOptions("ControlHost", controlEndpoint, controlAssembly) with
		{
			AdditionalArguments = string.Join(' ', new[]
			{
				$"--runtime-endpoint={runtimeEndpoint}",
				$"--durability-root={Quote(root)}",
				"--connect-timeout-ms=150",
				"--request-timeout-ms=2000",
				"--runtime-retry-ms=25"
			})
		};
		var controlSupervisor = new LocalProcessSupervisor(controlSupervisorOptions);
		await controlSupervisor.StartAsync();
		await WaitUntilAsync(() => controlSupervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null }, ProcessRecoveryTimeoutMilliseconds);

		try
		{
			var transport = new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
			var staleClient = new OperatorControlClient(transport);
			var initial = await RetrySnapshotAsync(staleClient, requireRuntimeReady: true);
			var firstMutation = await staleClient.SelectPreviewAsync(initial.Sources[1].Id);
			Assert.True(firstMutation.Accepted, firstMutation.Failure?.ToString());
			var committed = staleClient.Snapshot!;
			var committedRevision = committed.Production.Revision;
			await WaitForCheckpointAsync(root, controlEndpoint, committed.Production.ProductionId, committedRevision);

			var firstControlPid = controlSupervisor.Snapshot.OwnedProcessId!.Value;
			Kill(firstControlPid);
			await WaitUntilAsync(
				() => controlSupervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null } snapshot && snapshot.OwnedProcessId != firstControlPid,
				ProcessRecoveryTimeoutMilliseconds);
			Assert.Equal(runtimePid, runtimeSupervisor.Snapshot.OwnedProcessId);

			await Assert.ThrowsAsync<RemoteHostSessionChangedException>(() => staleClient.CutPreviewAsync().AsTask());
			Assert.True(transport.RequiresFullSnapshot);
			var recovered = await RetrySnapshotAsync(staleClient, requireRuntimeReady: true);
			Assert.False(transport.RequiresFullSnapshot);
			Assert.Equal(committedRevision, recovered.Production.Revision);
			Assert.Equal(committed.Production.Routing, recovered.Production.Routing);

			var freshClient = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3)));
			await RetrySnapshotAsync(freshClient, requireRuntimeReady: true);
			var advanced = await freshClient.CutPreviewAsync();
			Assert.True(advanced.Accepted, advanced.Failure?.ToString());
			var advancedRevision = freshClient.Snapshot!.Production.Revision;
			Assert.True(advancedRevision.Value > committedRevision.Value);

			staleClient.Disconnect();
			await staleClient.SynchronizeAsync();
			Assert.Equal(advancedRevision, staleClient.Snapshot!.Production.Revision);

			var deliberatelyStale = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3)));
			await deliberatelyStale.SynchronizeAsync();
			var sourceA = deliberatelyStale.Snapshot!.Sources[0].Id;
			var freshAdvance = await freshClient.SelectPreviewAsync(freshClient.Snapshot!.Sources[0].Id);
			Assert.True(freshAdvance.Accepted, freshAdvance.Failure?.ToString());
			var staleResult = await deliberatelyStale.SelectPreviewAsync(sourceA);
			Assert.False(staleResult.Accepted);
			Assert.NotNull(staleResult.Failure);
		}
		finally
		{
			await controlSupervisor.DisposeAsync();
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Supervisor_adopts_existing_endpoint_without_owning_or_terminating_external_process()
	{
		var endpoint = Endpoint("runtime-adoption");
		var assembly = HostAssembly("rtaime.RuntimeHost");
		using var external = StartDotnetHost(assembly, $"--listen-endpoint={endpoint}");
		try
		{
			await WaitForExplicitEndpointReadinessAsync(endpoint, ProcessRecoveryTimeoutMilliseconds);
			await using (var supervisor = Supervisor("RuntimeHost", endpoint, assembly))
			{
				await supervisor.StartAsync();
				await WaitUntilAsync(() => supervisor.Snapshot.State == LocalProcessSupervisionState.Healthy, ProcessRecoveryTimeoutMilliseconds);
				Assert.Null(supervisor.Snapshot.OwnedProcessId);
				Assert.Contains("Adopted explicitly ready external endpoint", supervisor.Snapshot.Detail);
			}
			Assert.False(external.HasExited);
		}
		finally
		{
			if (!external.HasExited) external.Kill(entireProcessTree: true);
			await external.WaitForExitAsync();
		}
	}

	[Fact]
	public async Task Owned_child_persistent_readiness_loss_triggers_graceful_bounded_restart()
	{
		var endpoint = Endpoint("runtime-readiness-loss");
		var assembly = HostAssembly("rtaime.RuntimeHost");
		var options = ReadinessRecoverySupervisorOptions("RuntimeHost", endpoint, assembly, maxStartAttempts: 3);
		await using var supervisor = new LocalProcessSupervisor(options);
		await supervisor.StartAsync();

		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null },
			ProcessRecoveryTimeoutMilliseconds);
		var firstPid = supervisor.Snapshot.OwnedProcessId!.Value;
		var readiness = await ReadOwnedReadinessFileAsync("RuntimeHost", firstPid, ProcessRecoveryTimeoutMilliseconds);

		File.Delete(readiness.Path);

		await WaitUntilAsync(
			() => supervisor.Snapshot is
			{
				State: LocalProcessSupervisionState.ReadinessGrace,
				OwnedProcessId: not null,
				StartAttempts: 1
			} snapshot && snapshot.OwnedProcessId == firstPid,
			ProcessRecoveryTimeoutMilliseconds);
		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.RestartBackoff, StartAttempts: 1 } snapshot &&
				snapshot.Detail.Contains("stopped gracefully", StringComparison.OrdinalIgnoreCase),
			ProcessRecoveryTimeoutMilliseconds);
		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null, StartAttempts: 2 } snapshot &&
				snapshot.OwnedProcessId != firstPid,
			ProcessRecoveryTimeoutMilliseconds);

		Assert.False(ProcessIsAlive(firstPid));
		Assert.NotEqual(firstPid, supervisor.Snapshot.OwnedProcessId);
		Assert.Equal(2, supervisor.Snapshot.StartAttempts);
	}

	[Fact]
	public async Task Owned_child_transient_readiness_loss_recovers_without_restart()
	{
		var endpoint = Endpoint("runtime-readiness-transient");
		var assembly = HostAssembly("rtaime.RuntimeHost");
		var options = ReadinessRecoverySupervisorOptions("RuntimeHost", endpoint, assembly, maxStartAttempts: 3);
		await using var supervisor = new LocalProcessSupervisor(options);
		await supervisor.StartAsync();

		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null, StartAttempts: 1 },
			ProcessRecoveryTimeoutMilliseconds);
		var firstPid = supervisor.Snapshot.OwnedProcessId!.Value;
		var readiness = await ReadOwnedReadinessFileAsync("RuntimeHost", firstPid, ProcessRecoveryTimeoutMilliseconds);

		File.Delete(readiness.Path);
		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.ReadinessGrace, OwnedProcessId: not null } snapshot &&
				snapshot.OwnedProcessId == firstPid,
			ProcessRecoveryTimeoutMilliseconds);

		File.WriteAllText(readiness.Path, readiness.Content);
		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null, StartAttempts: 1 } snapshot &&
				snapshot.OwnedProcessId == firstPid &&
				snapshot.Detail.Contains("restart was not required", StringComparison.OrdinalIgnoreCase),
			ProcessRecoveryTimeoutMilliseconds);

		Assert.True(ProcessIsAlive(firstPid));
		Assert.Equal(1, supervisor.Snapshot.StartAttempts);
		Assert.Equal(firstPid, supervisor.Snapshot.OwnedProcessId);
	}

	[Fact]
	public async Task Adopted_external_readiness_loss_is_non_destructive()
	{
		var endpoint = Endpoint("external-readiness-loss");
		var assembly = HostAssembly("rtaime.RuntimeHost");
		using var endpointLease = LocalEndpointLease.Acquire(endpoint);
		var readinessLease = LocalEndpointReadinessLease.Acquire(endpoint);
		await using var supervisor = new LocalProcessSupervisor(
			ReadinessRecoverySupervisorOptions("RuntimeHost", endpoint, assembly, maxStartAttempts: 3));
		try
		{
			await supervisor.StartAsync();
			await WaitUntilAsync(
				() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: null, StartAttempts: 0 },
				ProcessRecoveryTimeoutMilliseconds);

			readinessLease.Dispose();
			await WaitUntilAsync(
				() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Waiting, OwnedProcessId: null, StartAttempts: 0 },
				ProcessRecoveryTimeoutMilliseconds);
			await Task.Delay(TimeSpan.FromMilliseconds(900));

			Assert.True(LocalEndpointLease.IsHeld(endpoint));
			Assert.False(LocalEndpointReadinessLease.IsHeld(endpoint));
			Assert.Null(supervisor.Snapshot.OwnedProcessId);
			Assert.Equal(0, supervisor.Snapshot.StartAttempts);

			using var restoredReadiness = LocalEndpointReadinessLease.Acquire(endpoint);
			await WaitUntilAsync(
				() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: null, StartAttempts: 0 },
				ProcessRecoveryTimeoutMilliseconds);
		}
		finally
		{
			readinessLease.Dispose();
		}
	}

	[Fact]
	public async Task Repeated_post_healthy_readiness_loss_stops_at_existing_start_budget()
	{
		var endpoint = Endpoint("runtime-readiness-budget");
		var assembly = HostAssembly("rtaime.RuntimeHost");
		var options = ReadinessRecoverySupervisorOptions("RuntimeHost", endpoint, assembly, maxStartAttempts: 2);
		await using var supervisor = new LocalProcessSupervisor(options);
		await supervisor.StartAsync();

		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null, StartAttempts: 1 },
			ProcessRecoveryTimeoutMilliseconds);
		var firstPid = supervisor.Snapshot.OwnedProcessId!.Value;
		var firstReadiness = await ReadOwnedReadinessFileAsync("RuntimeHost", firstPid, ProcessRecoveryTimeoutMilliseconds);
		File.Delete(firstReadiness.Path);

		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null, StartAttempts: 2 } snapshot &&
				snapshot.OwnedProcessId != firstPid,
			ProcessRecoveryTimeoutMilliseconds);
		var secondPid = supervisor.Snapshot.OwnedProcessId!.Value;
		var secondReadiness = await ReadOwnedReadinessFileAsync("RuntimeHost", secondPid, ProcessRecoveryTimeoutMilliseconds);
		File.Delete(secondReadiness.Path);

		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Failed, OwnedProcessId: null, StartAttempts: 2 } snapshot &&
				snapshot.Detail.Contains("start budget exhausted", StringComparison.OrdinalIgnoreCase),
			ProcessRecoveryTimeoutMilliseconds);

		var attemptsAfterFailure = supervisor.Snapshot.StartAttempts;
		await Task.Delay(TimeSpan.FromMilliseconds(900));

		Assert.False(ProcessIsAlive(firstPid));
		Assert.False(ProcessIsAlive(secondPid));
		Assert.Equal(2, attemptsAfterFailure);
		Assert.Equal(attemptsAfterFailure, supervisor.Snapshot.StartAttempts);
		Assert.Null(supervisor.Snapshot.OwnedProcessId);
	}

	[Fact]
	public async Task Supervisor_exhausts_start_budget_without_unbounded_crash_loop()
	{
		var endpoint = Endpoint("runtime-start-budget");
		var assembly = HostAssembly("rtaime.RuntimeHost");
		var options = new LocalProcessSupervisionOptions(
			"RuntimeHost",
			endpoint,
			assembly,
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(100),
			2)
		{
			AdditionalArguments = "--source-a-id=not-an-identity"
		};
		await using var supervisor = new LocalProcessSupervisor(options);
		await supervisor.StartAsync();
		await WaitUntilAsync(
			() => supervisor.Snapshot is { State: LocalProcessSupervisionState.Failed, StartAttempts: 2, OwnedProcessId: null },
			ProcessRecoveryTimeoutMilliseconds);

		var attemptsAfterFailure = supervisor.Snapshot.StartAttempts;
		await Task.Delay(500);
		Assert.Equal(2, attemptsAfterFailure);
		Assert.Equal(attemptsAfterFailure, supervisor.Snapshot.StartAttempts);
		Assert.Equal(LocalProcessSupervisionState.Failed, supervisor.Snapshot.State);
		Assert.Null(supervisor.Snapshot.OwnedProcessId);
	}

	private static LocalProcessSupervisionOptions ReadinessRecoverySupervisorOptions(
		string name,
		string endpoint,
		string assembly,
		int maxStartAttempts) =>
		new(
			name,
			endpoint,
			assembly,
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(150),
			TimeSpan.FromMilliseconds(600),
			maxStartAttempts)
		{
			GracefulStopTimeout = TimeSpan.FromSeconds(5),
			InitialAdoptionWindow = TimeSpan.FromMilliseconds(150)
		};

	private static async Task<(string Path, string Content)> ReadOwnedReadinessFileAsync(
		string hostName,
		int processId,
		int timeoutMilliseconds)
	{
		var directory = Path.Combine(Path.GetTempPath(), "rtaime", "supervision");
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (DateTime.UtcNow < deadline)
		{
			if (Directory.Exists(directory))
			{
				foreach (var path in Directory.EnumerateFiles(directory, $"{hostName}-*.ready.json"))
				{
					try
					{
						var content = await File.ReadAllTextAsync(path);
						using var document = JsonDocument.Parse(content);
						if (document.RootElement.TryGetProperty("processId", out var processIdElement) &&
							processIdElement.GetInt32() == processId)
						{
							return (path, content);
						}
					}
					catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
					{
						// Readiness publication is atomic but directory enumeration can race replacement cleanup.
					}
				}
			}
			await Task.Delay(25);
		}
		throw new TimeoutException($"Managed readiness file for {hostName} process {processId} was not found.");
	}

	private static bool ProcessIsAlive(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return !process.HasExited;
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	private static LocalProcessSupervisor Supervisor(string name, string endpoint, string assembly) =>
		new(BaseSupervisorOptions(name, endpoint, assembly));

	private static LocalProcessSupervisionOptions BaseSupervisorOptions(string name, string endpoint, string assembly) =>
		new(
			name,
			endpoint,
			assembly,
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(100),
			TimeSpan.FromMilliseconds(100),
			5);

	private static async Task<OperatorStatusSnapshot> RetrySnapshotAsync(OperatorControlClient client, bool requireRuntimeReady)
	{
		Exception? last = null;
		for (var attempt = 0; attempt < EndpointRecoveryAttempts; attempt++)
		{
			try
			{
				var snapshot = await client.SynchronizeAsync();
				if (!requireRuntimeReady || snapshot.RuntimeStatus == "READY")
					return snapshot;
			}
			catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
			{
				last = exception;
			}
			await Task.Delay(100);
		}
		throw new TimeoutException("Operator client did not recover before the process-recovery deadline.", last);
	}

	private static async Task WaitForCheckpointAsync(string root, string controlEndpoint, ProductionId productionId, Revision revision)
	{
		var path = Path.Combine(root, $"{productionId}-{controlEndpoint}", "management.db");
		for (var attempt = 0; attempt < EndpointRecoveryAttempts; attempt++)
		{
			try
			{
				if (File.Exists(path))
				{
					await using var store = new SqliteManagementStore(path);
					var checkpoint = await store.ReadLatestAsync(productionId.Value);
					if (checkpoint?.AuthoritativeRevision == revision)
						return;
				}
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException)
			{
				// The live ControlHost may transiently own a SQLite write transaction; retry within the test deadline.
			}
			await Task.Delay(100);
		}
		throw new TimeoutException($"Durable checkpoint for revision {revision} was not observed before ControlHost termination.");
	}

	private static Process StartDotnetHost(string assembly, string arguments) =>
		Process.Start(new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = $"\"{assembly}\" {arguments}",
			UseShellExecute = false,
			CreateNoWindow = true
		}) ?? throw new InvalidOperationException($"Failed to start host '{assembly}'.");

	private static async Task WaitForExplicitEndpointReadinessAsync(string endpoint, int timeoutMilliseconds)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (DateTime.UtcNow < deadline)
		{
			if (LocalEndpointLease.IsHeld(endpoint) && LocalEndpointReadinessLease.IsHeld(endpoint))
				return;
			await Task.Delay(50);
		}
		throw new TimeoutException($"Endpoint '{endpoint}' did not publish explicit readiness before the test deadline.");
	}

	private static void Kill(int processId)
	{
		using var process = Process.GetProcessById(processId);
		process.Kill(entireProcessTree: true);
		process.WaitForExit(5000);
	}

	private static string HostAssembly(string hostName)
	{
		var repo = FindRepositoryRoot();
		var target = hostName == "rtaime.Operator" ? "net10.0-windows" : "net10.0";
		var path = Path.Combine(repo, "src", "Hosts", hostName, "bin", "Release", target, $"{hostName}.dll");
		Assert.True(File.Exists(path), $"Host build output was not found at '{path}'.");
		return path;
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "rtaime.slnx"))) return directory.FullName;
			directory = directory.Parent;
		}
		throw new InvalidOperationException("Repository root could not be located.");
	}

	private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
		while (!condition())
		{
			if (DateTime.UtcNow >= deadline)
				throw new TimeoutException("Condition was not reached before the process-recovery deadline.");
			await Task.Delay(50);
		}
	}

	private static string Quote(string value) => value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
	private static string Endpoint(string purpose) => $"rtaime.test.{purpose}.{Guid.NewGuid():N}";

	private static string TempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-process-recovery-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteDirectory(string path)
	{
		try { Directory.Delete(path, recursive: true); }
		catch { }
	}
}
