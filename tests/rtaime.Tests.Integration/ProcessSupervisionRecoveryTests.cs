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

	[Theory]
	[InlineData("RuntimeHost", "rtaime.RuntimeHost")]
	[InlineData("AIHost", "rtaime.AIHost")]
	public async Task Repeated_owned_process_cycles_leave_no_child_or_endpoint_lease(string host, string assemblyName)
	{
		var endpoint = Endpoint("owned-cycle");
		var assembly = HostAssembly(assemblyName);
		for (var cycle = 0; cycle < 3; cycle++)
		{
			var supervisor = Supervisor(host, endpoint, assembly);
			int? pid = null;
			await using (supervisor)
			{
				await supervisor.StartAsync();
				await WaitUntilAsync(() => supervisor.Snapshot is
					{ State: LocalProcessSupervisionState.Healthy, OwnedProcessId: not null },
					ProcessRecoveryTimeoutMilliseconds);
				pid = supervisor.Snapshot.OwnedProcessId;
				Assert.True(ProcessIsAlive(pid!.Value));
				Assert.True(LocalEndpointLease.IsHeld(endpoint));
				Assert.True(LocalEndpointReadinessLease.IsHeld(endpoint));
			}
			Assert.Equal(LocalProcessSupervisionState.Stopped, supervisor.Snapshot.State);
			Assert.Null(supervisor.Snapshot.OwnedProcessId);
			if (pid is { } id) Assert.False(ProcessIsAlive(id));
			Assert.False(LocalEndpointLease.IsHeld(endpoint));
			Assert.False(LocalEndpointReadinessLease.IsHeld(endpoint));
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

	[Fact]
	public async Task Clean_and_retained_profiles_qualify_real_Control_Runtime_and_AI_processes_within_startup_deadline()
	{
		var root = TempDirectory();
		var controlEndpoint = Endpoint("control-binding-profile");
		var runtimeEndpoint = Endpoint("runtime-binding-profile");
		var aiEndpoint = Endpoint("ai-binding-profile");
		Revision? retainedRevision = null;
		var processIds = new HashSet<int>();
		try
		{
			foreach (var profile in new[] { "clean", "retained" })
			{
				var work = Path.Combine(root, profile);
				Directory.CreateDirectory(work);
				var readyPath = Path.Combine(work, "control-readiness.json");
				var stopPath = Path.Combine(work, "control-stop.signal");
				var session = $"binding-{profile}-{Guid.NewGuid():N}";
				var start = new ProcessStartInfo("dotnet")
				{
					UseShellExecute = false,
					CreateNoWindow = true
				};
				start.ArgumentList.Add(HostAssembly("rtaime.ControlHost"));
				start.ArgumentList.Add($"--listen-endpoint={controlEndpoint}");
				start.ArgumentList.Add($"--runtime-endpoint={runtimeEndpoint}");
				start.ArgumentList.Add($"--durability-root={Path.Combine(root, "state")}");
				start.Environment["RTAIME_RUNTIME_EXECUTABLE"] = HostAssembly("rtaime.RuntimeHost");
				start.Environment["RTAIME_AI_EXECUTABLE"] = HostAssembly("rtaime.AIHost");
				start.Environment["RTAIME_AI_ENDPOINT"] = aiEndpoint;
				start.Environment["RTAIME_HOST_READINESS_FILE"] = readyPath;
				start.Environment["RTAIME_HOST_STOP_FILE"] = stopPath;
				start.Environment["RTAIME_LOG_ROOT"] = Path.Combine(work, "logs");
				start.Environment["RTAIME_LOG_SESSION_ID"] = session;
				using var process = Process.Start(start) ?? throw new InvalidOperationException("ControlHost did not start.");
				var clock = Stopwatch.StartNew();
				int? runtimePid = null;
				int? aiPid = null;
				try
				{
					await WaitUntilAsync(() => File.Exists(readyPath) || process.HasExited, ProcessRecoveryTimeoutMilliseconds);
					Assert.False(process.HasExited, $"ControlHost exited before readiness for {profile}.");
					Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
					var readiness = await File.ReadAllTextAsync(readyPath);
					using var document = JsonDocument.Parse(readiness);
					var evidence = document.RootElement;
					Assert.Equal("READY", evidence.GetProperty("state").GetString());
					Assert.Equal("HEALTHY", evidence.GetProperty("health").GetString());
					Assert.Equal(process.Id, evidence.GetProperty("processId").GetInt32());
					Assert.Equal("HEALTHY", evidence.GetProperty("runtimeSupervision").GetProperty("state").GetString());
					Assert.Equal("HEALTHY", evidence.GetProperty("aiSupervision").GetProperty("state").GetString());
					runtimePid = evidence.GetProperty("runtimeSupervision").GetProperty("processId").GetInt32();
					aiPid = evidence.GetProperty("aiSupervision").GetProperty("processId").GetInt32();
					Assert.True(processIds.Add(process.Id));
					Assert.True(processIds.Add(runtimePid.Value));
					Assert.True(processIds.Add(aiPid.Value));
					Assert.True(ProcessIsAlive(runtimePid.Value));
					Assert.True(ProcessIsAlive(aiPid.Value));

					var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
					var snapshot = await client.SynchronizeAsync();
					if (retainedRevision is { } revision) Assert.Equal(revision, snapshot.Production.Revision);
					else retainedRevision = snapshot.Production.Revision;
					var logFiles = Directory.GetFiles(Path.Combine(work, "logs", session), "*.jsonl");
					var controlLog = Assert.Single(logFiles, path => Path.GetFileName(path).Contains("-controlhost-", StringComparison.Ordinal));
					var records = ReadSharedLogLines(controlLog).Select(line => JsonDocument.Parse(line)).ToArray();
					try
					{
						Assert.Contains(records, record => record.RootElement.GetProperty("code").GetString() == "controlhost.binding.qualified-ready.completed");
						Assert.All(records, record => Assert.Equal(session, record.RootElement.GetProperty("sessionId").GetString()));
					}
					finally { foreach (var record in records) record.Dispose(); }
					// Preserve the qualified live state before the stop signal removes readiness.
					await File.WriteAllTextAsync(Path.Combine(work, "readiness-before-stop.json"), readiness);
					Console.WriteLine($"Binding qualification profile={profile} elapsedMs={clock.ElapsedMilliseconds} controlPid={process.Id} runtimePid={runtimePid} aiPid={aiPid} session={session} revision={retainedRevision}");
				}
				finally
				{
					await File.WriteAllTextAsync(stopPath, "stop");
					await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
					Assert.Equal(0, process.ExitCode);
					if (runtimePid is { } runtimeId) Assert.False(ProcessIsAlive(runtimeId));
					if (aiPid is { } aiId) Assert.False(ProcessIsAlive(aiId));
					var evidenceRoot = Environment.GetEnvironmentVariable("RTAIME_BINDING_EVIDENCE_ROOT");
					if (!string.IsNullOrWhiteSpace(evidenceRoot))
					{
						var destination = Path.Combine(evidenceRoot, session);
						Directory.CreateDirectory(destination);
						foreach (var file in Directory.GetFiles(work, "*", SearchOption.AllDirectories))
						{
							var target = Path.Combine(destination, Path.GetRelativePath(work, file));
							Directory.CreateDirectory(Path.GetDirectoryName(target)!);
							File.Copy(file, target, overwrite: true);
						}
					}
				}
			}
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task AppHost_clean_and_retained_launches_qualify_all_hosts_before_thirty_second_deadline()
	{
		var root = TempDirectory();
		var controlEndpoint = Endpoint("control-binding-profile");
		var runtimeEndpoint = Endpoint("runtime-binding-profile");
		var aiEndpoint = Endpoint("ai-binding-profile");
		Revision? retainedRevision = null;
		var processIds = new HashSet<int>();
		try
		{
			foreach (var profile in new[] { "clean", "retained" })
			{
				var work = Path.Combine(root, "shared-work");
				Directory.CreateDirectory(work);
				var readyPath = Path.Combine(work, "control-readiness.json");
				var stopPath = Path.Combine(work, "control-stop.signal");
				
				var session = $"binding-{profile}-{Guid.NewGuid():N}";
				var appRoot = Path.Combine(FindRepositoryRoot(), "src", "Hosts", "rtaime.AppHost", "bin", "Release", "net10.0");
				var policyPath = Path.Combine(work, "policy.json");
				await File.WriteAllTextAsync(policyPath, JsonSerializer.Serialize(new
				{
					schemaVersion = "1.0",
					endpoints = new { control = controlEndpoint, runtime = runtimeEndpoint, ai = aiEndpoint },
					startup = new { timeoutMs = 30000, probeTimeoutMs = 500, probeIntervalMs = 200, childRestartBackoffMs = 500, childMaxStartAttempts = 5 },
					shutdown = new { timeoutMs = 15000 }
				}));
				var start = new ProcessStartInfo(Path.Combine(appRoot, "rtaime.exe"))
				{
					UseShellExecute = false,
					CreateNoWindow = true
				};
				start.ArgumentList.Add("--profile=HeadlessEngine");
				start.ArgumentList.Add("--ownership=EphemeralLocal");
				start.ArgumentList.Add("--instance-id=default");
				start.ArgumentList.Add($"--install-root={appRoot}");
				start.ArgumentList.Add($"--state-root={Path.Combine(root, "state")}");
				start.ArgumentList.Add($"--work-root={work}");
				start.ArgumentList.Add($"--policy={policyPath}");
				start.Environment["RTAIME_LOG_ROOT"] = Path.Combine(work, "logs");
				start.Environment["RTAIME_LOG_SESSION_ID"] = session;
				using var process = Process.Start(start) ?? throw new InvalidOperationException("AppHost did not start.");
				var clock = Stopwatch.StartNew();
				int? controlPid = null;
				int? runtimePid = null;
				int? aiPid = null;
				try
				{
					await WaitUntilAsync(() => File.Exists(readyPath) || process.HasExited, ProcessRecoveryTimeoutMilliseconds);
					Assert.False(process.HasExited, $"AppHost exited before readiness for {profile}.");
					Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
					await WaitUntilAsync(() =>
					{
						if (process.HasExited) return true;
						var files = Directory.GetFiles(Path.Combine(work, "logs", session), "*-apphost-*.jsonl");
						return files.Any(path => ReadSharedLogLines(path).Any(line =>
						{
							using var record = JsonDocument.Parse(line);
							return record.RootElement.GetProperty("code").GetString() == "apphost.state-changed" &&
								record.RootElement.GetProperty("dimensions").GetProperty("state").GetString() == "Healthy";
						}));
					}, ProcessRecoveryTimeoutMilliseconds);
					Assert.False(process.HasExited);
					Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
					var readiness = await File.ReadAllTextAsync(readyPath);
					using var document = JsonDocument.Parse(readiness);
					var evidence = document.RootElement;
					Assert.Equal("READY", evidence.GetProperty("state").GetString());
					Assert.Equal("HEALTHY", evidence.GetProperty("health").GetString());
					controlPid = evidence.GetProperty("processId").GetInt32();
					Assert.NotEqual(process.Id, controlPid.Value);
					Assert.True(ProcessIsAlive(controlPid.Value));
					Assert.Equal("HEALTHY", evidence.GetProperty("runtimeSupervision").GetProperty("state").GetString());
					Assert.Equal("HEALTHY", evidence.GetProperty("aiSupervision").GetProperty("state").GetString());
					runtimePid = evidence.GetProperty("runtimeSupervision").GetProperty("processId").GetInt32();
					aiPid = evidence.GetProperty("aiSupervision").GetProperty("processId").GetInt32();
					Assert.True(processIds.Add(process.Id));
					Assert.True(processIds.Add(controlPid!.Value));
					Assert.True(processIds.Add(runtimePid.Value));
					Assert.True(processIds.Add(aiPid.Value));
					Assert.True(ProcessIsAlive(runtimePid.Value));
					Assert.True(ProcessIsAlive(aiPid.Value));

					var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)));
					var snapshot = await client.SynchronizeAsync();
					if (retainedRevision is { } revision) Assert.Equal(revision, snapshot.Production.Revision);
					else retainedRevision = snapshot.Production.Revision;
					var logFiles = Directory.GetFiles(Path.Combine(work, "logs", session), "*.jsonl");
					var controlLog = Assert.Single(logFiles, path => Path.GetFileName(path).Contains("-controlhost-", StringComparison.Ordinal));
					var records = ReadSharedLogLines(controlLog).Select(line => JsonDocument.Parse(line)).ToArray();
					try
					{
						Assert.Contains(records, record => record.RootElement.GetProperty("code").GetString() == "controlhost.binding.qualified-ready.completed");
						Assert.All(records, record => Assert.Equal(session, record.RootElement.GetProperty("sessionId").GetString()));
					}
					finally { foreach (var record in records) record.Dispose(); }
					// Preserve the qualified live state before the stop signal removes readiness.
					await File.WriteAllTextAsync(Path.Combine(work, "readiness-before-stop.json"), readiness);
					Console.WriteLine($"AppHost binding qualification profile={profile} elapsedMs={clock.ElapsedMilliseconds} appPid={process.Id} controlPid={controlPid} runtimePid={runtimePid} aiPid={aiPid} session={session} revision={retainedRevision}");
				}
				catch (Exception exception)
				{
					Console.WriteLine($"AppHost qualification failure profile={profile}: {exception}");
					throw;
				}
				finally
				{
					try
					{
						// Exercise the existing HeadlessEngine contract: an explicit engine stop
						// drains all service hosts, then AppHost reports observed lifecycle loss.
						await File.WriteAllTextAsync(stopPath, "stop");
						await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
						Assert.Equal(1, process.ExitCode);
						var appLog = Assert.Single(Directory.GetFiles(Path.Combine(work, "logs", session), "*-apphost-*.jsonl"));
						Assert.Contains(ReadSharedLogLines(appLog), line =>
						{
							using var record = JsonDocument.Parse(line);
							return record.RootElement.GetProperty("code").GetString() == "apphost.lifecycle-failure";
						});
						using (var shutdownEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "apphost-shutdown.json"))))
						{
							Assert.False(shutdownEvidence.RootElement.GetProperty("forcedTermination").GetBoolean());
							Assert.True(shutdownEvidence.RootElement.GetProperty("diagnosticsDrained").GetBoolean());
							Assert.Equal(session, shutdownEvidence.RootElement.GetProperty("sessionId").GetString());
						}
						foreach (var host in new[] { "ControlHost", "RuntimeHost", "AIHost" })
						{
							var hostLog = Assert.Single(Directory.GetFiles(Path.Combine(work, "logs", session), "*.jsonl"),
								path => Path.GetFileName(path).Contains($"-{host.ToLowerInvariant()}-", StringComparison.Ordinal));
							Assert.Contains(ReadSharedLogLines(hostLog), line =>
							{
								using var record = JsonDocument.Parse(line);
								return record.RootElement.GetProperty("code").GetString() == $"{host.ToLowerInvariant()}.completed" &&
									record.RootElement.GetProperty("dimensions").GetProperty("exitCode").GetString() == "0";
							});
						}
						AssertRetainedStartupCapture(work, session, qualified: true);
						if (controlPid is { } controlId) Assert.False(ProcessIsAlive(controlId));
						if (runtimePid is { } runtimeId) Assert.False(ProcessIsAlive(runtimeId));
						if (aiPid is { } aiId) Assert.False(ProcessIsAlive(aiId));
					}
					finally
					{
						var evidenceRoot = Environment.GetEnvironmentVariable("RTAIME_BINDING_EVIDENCE_ROOT");
						if (!string.IsNullOrWhiteSpace(evidenceRoot))
						{
							var destination = Path.Combine(evidenceRoot, session);
							Directory.CreateDirectory(destination);
							foreach (var file in Directory.GetFiles(work, "*", SearchOption.AllDirectories))
							{
								var target = Path.Combine(destination, Path.GetRelativePath(work, file));
								Directory.CreateDirectory(Path.GetDirectoryName(target)!);
								if (Path.GetExtension(file) == ".jsonl")
									File.WriteAllLines(target, ReadSharedLogLines(file));
								else File.Copy(file, target, overwrite: true);
							}
						}
					}
				}
			}
		}
		finally { DeleteDirectory(root); }
	}

	[Fact]
	public async Task AppHost_deadline_freezes_pre_shutdown_evidence_without_fabricating_child_crashes()
	{
		var root = TempDirectory();
		var work = Path.Combine(root, "deadline");
		Directory.CreateDirectory(work);
		var runtimeEndpoint = Endpoint("runtime-unqualified");
		using var externalRuntimeLease = LocalEndpointLease.Acquire(runtimeEndpoint);
		var session = $"readiness-deadline-{Guid.NewGuid():N}";
		var appRoot = Path.Combine(FindRepositoryRoot(), "src", "Hosts", "rtaime.AppHost", "bin", "Release", "net10.0");
		var policyPath = Path.Combine(work, "policy.json");
		await File.WriteAllTextAsync(policyPath, JsonSerializer.Serialize(new
		{
			schemaVersion = "1.0",
			endpoints = new { control = Endpoint("control-deadline"), runtime = runtimeEndpoint, ai = Endpoint("ai-deadline") },
			startup = new { timeoutMs = 30000, probeTimeoutMs = 500, probeIntervalMs = 200, childRestartBackoffMs = 500, childMaxStartAttempts = 5 },
			shutdown = new { timeoutMs = 15000 }
		}));
		var start = new ProcessStartInfo(Path.Combine(appRoot, "rtaime.exe"))
		{
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var argument in new[]
		{
			"--profile=HeadlessEngine", "--ownership=EphemeralLocal", "--instance-id=default",
			$"--install-root={appRoot}", $"--state-root={Path.Combine(root, "state")}",
			$"--work-root={work}", $"--policy={policyPath}"
		}) start.ArgumentList.Add(argument);
		start.Environment["RTAIME_LOG_ROOT"] = Path.Combine(work, "logs");
		start.Environment["RTAIME_LOG_SESSION_ID"] = session;
		using var process = Process.Start(start) ?? throw new InvalidOperationException("AppHost did not start.");
		try
		{
			await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55));
			Assert.Equal(1, process.ExitCode);
			using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "apphost-lifecycle.json")));
			var evidence = document.RootElement;
			var failure = evidence.GetProperty("readinessFailure");
			Assert.Equal("startup.deadline.expired", failure.GetProperty("reasonCode").GetString());
			Assert.True(failure.GetProperty("aggregateDeadlineExpired").GetBoolean());
			var hosts = failure.GetProperty("hosts").EnumerateArray().ToArray();
			var control = hosts.Single(host => host.GetProperty("host").GetString() == "ControlHost");
			Assert.True(control.GetProperty("processAlive").GetBoolean());
			Assert.NotNull(control.GetProperty("lifecycleState").GetString());
			Assert.NotNull(control.GetProperty("lastProgressPhase").GetString());
			Assert.NotEqual("Stopped", control.GetProperty("lifecycleState").GetString());
			Assert.False(ProcessIsAlive(control.GetProperty("processId").GetInt32()));
			var runtime = hosts.Single(host => host.GetProperty("host").GetString() == "RuntimeHost");
			Assert.Equal(JsonValueKind.Null, runtime.GetProperty("processId").ValueKind);
			Assert.NotEqual("Failed", runtime.GetProperty("qualification").GetString());
			Assert.Contains(failure.GetProperty("unmetCriteria").EnumerateArray(),
				item => item.GetProperty("host").GetString() == "RuntimeHost" && item.GetProperty("code").GetString() == "supervision.healthy");
			Assert.Equal("Failed", evidence.GetProperty("stages").EnumerateArray()
				.Single(stage => stage.GetProperty("id").GetString() == "production-readiness").GetProperty("status").GetString());
			Assert.DoesNotContain(evidence.GetProperty("stages").EnumerateArray(),
				stage => stage.GetProperty("id").GetString() is "control-host" or "runtime-host" or "ai-host" &&
					stage.GetProperty("status").GetString() == "Failed");
			using var shutdown = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "apphost-shutdown.json")));
			Assert.Equal("PASS", shutdown.RootElement.GetProperty("status").GetString());
			Assert.False(shutdown.RootElement.GetProperty("forcedTermination").GetBoolean());
			Assert.True(failure.GetProperty("capturedAtUtc").GetDateTimeOffset() <=
				shutdown.RootElement.GetProperty("requestedAtUtc").GetDateTimeOffset());
			AssertRetainedStartupCapture(work, session, qualified: false);
			Console.WriteLine($"AppHost deadline evidence session={session} controlPid={control.GetProperty("processId")} phase={control.GetProperty("lastProgressPhase")} shutdown=PASS");
		}
		finally
		{
			try
			{
				if (!process.HasExited)
				{
					await File.WriteAllTextAsync(Path.Combine(work, "control-stop.signal"), "stop");
					try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
					catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
				}
			}
			finally
			{
				var evidenceRoot = Environment.GetEnvironmentVariable("RTAIME_BINDING_EVIDENCE_ROOT");
				if (!string.IsNullOrWhiteSpace(evidenceRoot))
				{
					var destination = Path.Combine(evidenceRoot, session);
					foreach (var file in Directory.GetFiles(work, "*", SearchOption.AllDirectories))
					{
						var target = Path.Combine(destination, Path.GetRelativePath(work, file));
						Directory.CreateDirectory(Path.GetDirectoryName(target)!);
						if (Path.GetExtension(file) == ".jsonl") File.WriteAllLines(target, ReadSharedLogLines(file));
						else File.Copy(file, target, overwrite: true);
					}
				}
				DeleteDirectory(root);
			}
		}
	}

	private static void AssertRetainedStartupCapture(string work, string session, bool qualified)
	{
		using var live = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, "apphost-lifecycle.json")));
		var capture = Path.GetDirectoryName(live.RootElement.GetProperty("retainedEvidencePath").GetString())!;
		using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "manifest.json")));
		using var before = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "before-shutdown.json")));
		using var shutdown = JsonDocument.Parse(File.ReadAllText(Path.Combine(capture, "apphost-shutdown.json")));
		Assert.Equal(session, live.RootElement.GetProperty("sessionId").GetString());
		foreach (var document in new[] { manifest, before, shutdown })
		{
			Assert.Equal(session, document.RootElement.GetProperty("sessionId").GetString());
			Assert.Equal(live.RootElement.GetProperty("captureId").GetString(),
				document.RootElement.GetProperty("captureId").GetString());
		}
		Assert.True(before.RootElement.GetProperty("capturedAtUtc").GetDateTimeOffset() <=
			shutdown.RootElement.GetProperty("requestedAtUtc").GetDateTimeOffset());
		Assert.True(shutdown.RootElement.GetProperty("diagnosticsDrained").GetBoolean());
		Assert.Equal(qualified, File.Exists(Path.Combine(capture, "qualified.json")));
		Assert.Contains(before.RootElement.GetProperty("events").EnumerateArray(),
			item => item.GetProperty("host").GetString() == "ControlHost");
		var stream = File.ReadAllText(Path.Combine(capture, "controlhost-process.log"));
		Assert.Contains("sessionId=" + session, stream);
		Assert.Contains("stream=process exitCode=0", stream);
		Assert.Empty(manifest.RootElement.GetProperty("errors").EnumerateArray());
		Console.WriteLine($"Retained startup capture session={session} path={capture} qualified={qualified} streams=drained");
	}

	private static string[] ReadSharedLogLines(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var reader = new StreamReader(stream);
		var content = reader.ReadToEnd();
		var completeLength = content.LastIndexOf('\n') + 1;
		return content[..completeLength].Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
