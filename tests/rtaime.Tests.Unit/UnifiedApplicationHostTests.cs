// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.AppHost;

namespace rtaime.Tests.Unit;

public sealed class UnifiedApplicationHostTests
{
	[Fact]
	public async Task Starts_operator_startup_experience_before_control_readiness()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessAfterDelay = true };

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(result.Success);
		Assert.False(result.AdoptedControlHost);
		Assert.Equal(new[] { "rtaime.Operator", "rtaime.ControlHost" }, platform.StartedBaseNames);
		Assert.DoesNotContain("rtaime.RuntimeHost", platform.StartedBaseNames);
		Assert.DoesNotContain("rtaime.AIHost", platform.StartedBaseNames);
		Assert.True(platform.Events.IndexOf("start:rtaime.Operator") < platform.Events.IndexOf("delay"));
	}

	[Fact]
	public async Task Adopts_healthy_control_without_restarting_engine()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options);
		platform.PublishReadiness(42);

		var host = new UnifiedApplicationHost(options, platform);
		var result = await host.RunAsync();

		Assert.True(result.AdoptedControlHost);
		Assert.False(host.OwnsControlLifecycle);
		Assert.Equal(new[] { "rtaime.Operator" }, platform.StartedBaseNames);
	}

	[Fact]
	public async Task Adopts_healthy_control_with_endpoint_adopted_children()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options);
		platform.PublishReadiness(42, includeChildProcessIds: false);

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(result.AdoptedControlHost);
		Assert.Equal(new[] { "rtaime.Operator" }, platform.StartedBaseNames);
	}

	[Fact]
	public async Task Waits_for_leased_control_endpoint_and_adopts_when_readiness_appears()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options) { EndpointLeaseHeld = true };
		platform.OnDelay = () => platform.PublishReadiness(42);

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(result.AdoptedControlHost);
		Assert.Equal(42, result.ControlProcessId);
		Assert.Equal(new[] { "rtaime.Operator" }, platform.StartedBaseNames);
	}

	[Fact]
	public async Task Leased_control_endpoint_without_readiness_times_out_without_competing_start()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options) { EndpointLeaseHeld = true };

		var exception = await Assert.ThrowsAsync<TimeoutException>(
			() => new UnifiedApplicationHost(options, platform).RunAsync());

		Assert.Contains("remains owned", exception.Message, StringComparison.Ordinal);
		Assert.Equal(new[] { "rtaime.Operator" }, platform.StartedBaseNames);
	}

	[Fact]
	public async Task Released_control_endpoint_allows_owned_control_start()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options)
		{
			EndpointLeaseHeld = true,
			PublishReadinessOnControlStart = true
		};
		platform.OnDelay = () => platform.EndpointLeaseHeld = false;

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.False(result.AdoptedControlHost);
		Assert.Equal(new[] { "rtaime.Operator", "rtaime.ControlHost" }, platform.StartedBaseNames);
	}

	[Fact]
	public void Direct_development_startup_resolves_sibling_host_build_output()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-development-startup-tests", Guid.NewGuid().ToString("N"));
		var appHostRoot = Path.Combine(root, "src", "Hosts", "rtaime.AppHost", "bin", "Debug", "net10.0");
		var controlHostRoot = Path.Combine(root, "src", "Hosts", "rtaime.ControlHost", "bin", "Debug", "net10.0");
		Directory.CreateDirectory(appHostRoot);
		Directory.CreateDirectory(controlHostRoot);
		File.WriteAllText(Path.Combine(root, "rtaime.slnx"), "<Solution />");
		var expected = Path.Combine(controlHostRoot, "rtaime.ControlHost.dll");
		File.WriteAllText(expected, string.Empty);

		try
		{
			var platform = new SystemApplicationHostPlatform();
			var resolved = platform.FindProductArtifact(appHostRoot, "rtaime.ControlHost");

			Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(resolved));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Theory]
	[InlineData("Interactive", ApplicationLifecycleOwnership.EphemeralLocal)]
	[InlineData("Showcase", ApplicationLifecycleOwnership.EphemeralLocal)]
	[InlineData("HeadlessEngine", ApplicationLifecycleOwnership.PersistentEngine)]
	public void Startup_profile_defaults_to_expected_lifecycle_ownership(
		string profile,
		ApplicationLifecycleOwnership expectedOwnership)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-apphost-option-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var policyPath = Path.Combine(root, "host-lifecycle-policy.json");
		File.WriteAllText(policyPath, JsonSerializer.Serialize(new
		{
			schemaVersion = "1.0",
			endpoints = new
			{
				control = "rtaime.test.control",
				runtime = "rtaime.test.runtime",
				ai = "rtaime.test.ai"
			},
			startup = new
			{
				timeoutMs = 30000,
				probeTimeoutMs = 500,
				probeIntervalMs = 200,
				childRestartBackoffMs = 500,
				childMaxStartAttempts = 5
			},
			shutdown = new { timeoutMs = 15000 }
		}));

		try
		{
			var options = ApplicationHostOptions.Load(new[]
			{
				$"--profile={profile}",
				$"--install-root={root}",
				$"--state-root={Path.Combine(root, "state")}",
				$"--work-root={Path.Combine(root, "work")}",
				$"--policy={policyPath}"
			});

			Assert.Equal(expectedOwnership, options.Ownership);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public async Task Startup_failure_ends_in_failed_state()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options)
		{
			PublishReadinessAfterDelay = true,
			PipeReachable = false
		};
		var host = new UnifiedApplicationHost(options, platform);

		await Assert.ThrowsAsync<TimeoutException>(() => host.RunAsync());

		Assert.Equal(ApplicationLifecycleState.Failed, host.State);
		Assert.True(platform.StopSignalWritten);
		Assert.Contains(host.Lifecycle.Stages, stage =>
			stage.Status == LifecycleStageStatus.Failed &&
			stage.FailureReason is not null);
	}

	[Fact]
	public async Task Successful_startup_publishes_ready_lifecycle_stages()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };
		var host = new UnifiedApplicationHost(options, platform);

		await host.RunAsync();

		Assert.Equal(7, host.Lifecycle.Stages.Count);
		Assert.All(host.Lifecycle.Stages, stage => Assert.Equal(LifecycleStageStatus.Ready, stage.Status));
		Assert.Null(host.Lifecycle.ActiveStage);
		using var evidence = JsonDocument.Parse(platform.ReadAllText(options.LifecycleEvidencePath));
		Assert.Equal("1.0", evidence.RootElement.GetProperty("schemaVersion").GetString());
		Assert.Equal(7, evidence.RootElement.GetProperty("stages").GetArrayLength());
	}

	[Fact]
	public async Task Lifecycle_provider_reports_pending_starting_ready_degraded_failed_and_retry_state()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options);
		var provider = new ApplicationLifecycleStateProvider(options.LifecycleEvidencePath, requireAI: true, platform);
		var updates = 0;
		provider.StateChanged += (_, _) => updates++;

		provider.Initialize();
		Assert.All(provider.Stages, stage => Assert.Equal(LifecycleStageStatus.Pending, stage.Status));
		Assert.All(
			provider.Stages.Where(stage => stage.Id is
				ApplicationLifecycleStages.ApplicationBootstrap or
				ApplicationLifecycleStages.Configuration or
				ApplicationLifecycleStages.OperatorInterface),
			stage => Assert.Equal(LifecycleStageRequirement.Critical, stage.Requirement));
		Assert.All(
			provider.Stages.Where(stage => stage.Id is
				ApplicationLifecycleStages.ControlHost or
				ApplicationLifecycleStages.RuntimeHost or
				ApplicationLifecycleStages.AIHost or
				ApplicationLifecycleStages.ProductionReadiness),
			stage => Assert.Equal(LifecycleStageRequirement.RequiredForProduction, stage.Requirement));
		provider.StartStage(ApplicationLifecycleStages.ControlHost, "Starting.", canRetry: true);
		Assert.Equal(LifecycleStageStatus.Starting, provider.ActiveStage?.Status);
		Assert.True(provider.ActiveStage?.CanRetry == true);
		await platform.DelayAsync(TimeSpan.FromMilliseconds(184), CancellationToken.None);
		provider.CompleteStage(ApplicationLifecycleStages.ControlHost, "Ready.");
		var control = provider.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.ControlHost);
		Assert.Equal(LifecycleStageStatus.Ready, control.Status);
		Assert.NotNull(control.StartedAt);
		Assert.NotNull(control.CompletedAt);
		Assert.Equal(TimeSpan.FromMilliseconds(184), control.CompletedAt.Value - control.StartedAt.Value);
		provider.DegradeStage(ApplicationLifecycleStages.ProductionReadiness, "Degraded.");
		Assert.Equal(LifecycleStageStatus.Degraded, provider.ActiveStage?.Status);
		provider.FailStage(ApplicationLifecycleStages.ProductionReadiness, "Qualification failed.", canRetry: true);
		Assert.Equal(LifecycleStageStatus.Failed, provider.ActiveStage?.Status);
		Assert.True(provider.ActiveStage?.CanRetry == true);
		Assert.True(updates >= 5);
	}


	[Fact]
	public void Optional_ai_is_classified_without_blocking_critical_shell_startup()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options);
		var provider = new ApplicationLifecycleStateProvider(options.LifecycleEvidencePath, requireAI: false, platform);

		provider.Initialize();

		Assert.Equal(
			LifecycleStageRequirement.Optional,
			provider.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.AIHost).Requirement);
		Assert.Equal(
			LifecycleStageRequirement.Critical,
			provider.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.OperatorInterface).Requirement);
		Assert.Equal(
			LifecycleStageRequirement.RequiredForProduction,
			provider.Stages.Single(stage => stage.Id == ApplicationLifecycleStages.RuntimeHost).Requirement);

		using var evidence = JsonDocument.Parse(platform.ReadAllText(options.LifecycleEvidencePath));
		var aiStage = evidence.RootElement
			.GetProperty("stages")
			.EnumerateArray()
			.Single(stage => stage.GetProperty("id").GetString() == ApplicationLifecycleStages.AIHost);
		Assert.Equal("Optional", aiStage.GetProperty("requirement").GetString());
	}


	[Fact]
	public async Task Showcase_stops_only_control_lifecycle_it_owns()
	{
		var options = CreateOptions(ApplicationStartupProfile.Showcase);
		var ownedPlatform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };
		var ownedHost = new UnifiedApplicationHost(options, ownedPlatform);

		await ownedHost.RunAsync();

		Assert.True(ownedPlatform.StopSignalWritten);

		var adoptedPlatform = new FakeApplicationHostPlatform(options);
		adoptedPlatform.PublishReadiness(42);
		var adoptedHost = new UnifiedApplicationHost(options, adoptedPlatform);

		await adoptedHost.RunAsync();

		Assert.False(adoptedPlatform.StopSignalWritten);
	}

	[Fact]
	public async Task Headless_engine_does_not_start_operator()
	{
		var options = CreateOptions(ApplicationStartupProfile.HeadlessEngine);
		using var cancellation = new CancellationTokenSource();
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };
		platform.OnDelay = () => cancellation.Cancel();

		var result = await new UnifiedApplicationHost(options, platform).RunAsync(cancellation.Token);

		Assert.True(result.Success);
		Assert.DoesNotContain("rtaime.Operator", platform.StartedBaseNames);
		Assert.True(platform.StopSignalWritten);
	}

	[Fact]
	public async Task Windows_service_reclaims_healthy_engine_from_its_service_root()
	{
		var options = CreateOptions(
			ApplicationStartupProfile.HeadlessEngine,
			ApplicationLifecycleOwnership.PersistentEngine,
			windowsService: true);
		using var cancellation = new CancellationTokenSource();
		var platform = new FakeApplicationHostPlatform(options);
		platform.PublishReadiness(42);
		platform.OnDelay = () => cancellation.Cancel();

		var host = new UnifiedApplicationHost(options, platform);
		var result = await host.RunAsync(cancellation.Token);

		Assert.True(result.AdoptedControlHost);
		Assert.True(platform.StopSignalWritten);
		Assert.False(host.OwnsControlLifecycle);
	}

	[Fact]
	public async Task Headless_engine_fails_when_readiness_does_not_recover()
	{
		var options = CreateOptions(ApplicationStartupProfile.HeadlessEngine);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };
		platform.OnDelay = () => platform.PipeReachable = false;
		var host = new UnifiedApplicationHost(options, platform);

		await Assert.ThrowsAsync<TimeoutException>(() => host.RunAsync());

		Assert.Equal(ApplicationLifecycleState.Failed, host.State);
		Assert.True(platform.StopSignalWritten);
	}

	[Fact]
	public async Task Persistent_engine_keeps_owned_engine_running_when_operator_exits()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.PersistentEngine);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };
		var host = new UnifiedApplicationHost(options, platform);

		await host.RunAsync();

		Assert.True(host.OwnsControlLifecycle);
		Assert.False(platform.StopSignalWritten);
	}

	[Fact]
	public async Task Ephemeral_local_stops_owned_engine_when_operator_exits()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.EphemeralLocal);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };

		await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(platform.StopSignalWritten);
	}

	[Fact]
	public async Task Forced_shutdown_is_evidenced_as_failure()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.EphemeralLocal);
		var platform = new FakeApplicationHostPlatform(options)
		{
			PublishReadinessOnControlStart = true,
			IgnoreStopSignal = true
		};

		await new UnifiedApplicationHost(options, platform).RunAsync();

		using var evidence = JsonDocument.Parse(platform.ReadAllText(options.ShutdownEvidencePath));
		Assert.Equal("FAIL", evidence.RootElement.GetProperty("status").GetString());
		Assert.False(evidence.RootElement.GetProperty("graceful").GetBoolean());
		Assert.True(evidence.RootElement.GetProperty("forcedTermination").GetBoolean());
	}

	[Fact]
	public async Task Unexpected_control_exit_is_not_evidenced_as_graceful_shutdown()
	{
		var options = CreateOptions(ApplicationStartupProfile.HeadlessEngine);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };
		platform.OnDelay = platform.KillControlProcess;
		var host = new UnifiedApplicationHost(options, platform);

		await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync());

		using var evidence = JsonDocument.Parse(platform.ReadAllText(options.ShutdownEvidencePath));
		Assert.Equal("FAIL", evidence.RootElement.GetProperty("status").GetString());
		Assert.False(evidence.RootElement.GetProperty("graceful").GetBoolean());
		Assert.False(evidence.RootElement.GetProperty("forcedTermination").GetBoolean());
		Assert.False(evidence.RootElement.GetProperty("processAliveAtRequest").GetBoolean());
		Assert.False(platform.StopSignalWritten);
	}

	[Fact]
	public async Task External_managed_operator_exit_never_stops_adopted_engine()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.ExternalManaged);
		var platform = new FakeApplicationHostPlatform(options);
		platform.PublishReadiness(42);

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(result.AdoptedControlHost);
		Assert.False(platform.StopSignalWritten);
		Assert.True(platform.IsProcessAlive(42));
	}

	[Fact]
	public async Task External_managed_requires_only_operator_facing_control_pipe()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.ExternalManaged);
		var platform = new FakeApplicationHostPlatform(options);
		platform.PublishReadiness(42);
		platform.UnreachableEndpoints.Add(options.Endpoints.Runtime);
		platform.UnreachableEndpoints.Add(options.Endpoints.AI);

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(result.Success);
		Assert.True(result.AdoptedControlHost);
		Assert.False(platform.StopSignalWritten);
	}

	[Fact]
	public async Task External_managed_never_starts_control_host()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.ExternalManaged);
		var platform = new FakeApplicationHostPlatform(options);

		await Assert.ThrowsAsync<TimeoutException>(() => new UnifiedApplicationHost(options, platform).RunAsync());

		Assert.Equal(new[] { "rtaime.Operator" }, platform.StartedBaseNames);
		Assert.DoesNotContain("rtaime.ControlHost", platform.StartedBaseNames);
		Assert.False(platform.StopSignalWritten);
	}

	[Fact]
	public async Task Operator_tracks_replaced_control_host_without_stopping_engine()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.ExternalManaged);
		var platform = new FakeApplicationHostPlatform(options) { OperatorDelayBudget = 3 };
		platform.PublishReadiness(42);
		var delayCount = 0;
		platform.OnDelay = () =>
		{
			delayCount++;
			if (delayCount != 1) return;
			platform.KillProcessTree(42);
			platform.PublishReadiness(43);
		};

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(result.AdoptedControlHost);
		Assert.Equal(43, result.ControlProcessId);
		Assert.False(platform.StopSignalWritten);
	}

	private static ApplicationHostOptions CreateOptions(
		ApplicationStartupProfile profile,
		ApplicationLifecycleOwnership? ownership = null,
		bool windowsService = false)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-apphost-tests", Guid.NewGuid().ToString("N"));
		var resolvedOwnership = ownership ??
			(profile == ApplicationStartupProfile.Showcase
				? ApplicationLifecycleOwnership.EphemeralLocal
				: ApplicationLifecycleOwnership.PersistentEngine);
		return new ApplicationHostOptions(
			profile,
			Path.Combine(root, "install"),
			Path.Combine(root, "state"),
			Path.Combine(root, "work"),
			"default",
			resolvedOwnership,
			windowsService,
			"rtaime-engine",
			windowsService ? "S-1-5-32-545" : string.Empty,
			true,
			false,
			new ApplicationLifecyclePolicy(
				new ApplicationEndpointSet("rtaime.test.control", "rtaime.test.runtime", "rtaime.test.ai"),
				TimeSpan.FromMilliseconds(30),
				TimeSpan.FromMilliseconds(1),
				TimeSpan.FromMilliseconds(10),
				TimeSpan.FromMilliseconds(1),
				2,
				TimeSpan.FromMilliseconds(30)));
	}

	private sealed class FakeApplicationHostPlatform : IApplicationHostPlatform
	{
		private readonly ApplicationHostOptions _options;
		private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<int> _alive = new();
		private int _nextProcessId = 100;
		private int? _operatorProcessId;
		private bool _published;

		public FakeApplicationHostPlatform(ApplicationHostOptions options)
		{
			_options = options;
			UtcNow = new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.Zero);
		}

		public DateTimeOffset UtcNow { get; private set; }
		public bool PublishReadinessOnControlStart { get; init; }
		public bool PublishReadinessAfterDelay { get; init; }
		public bool PipeReachable { get; set; } = true;
		public bool EndpointLeaseHeld { get; set; }
		public HashSet<string> UnreachableEndpoints { get; } = new(StringComparer.Ordinal);
		public bool StopSignalWritten { get; private set; }
		public bool IgnoreStopSignal { get; set; }
		public int OperatorDelayBudget { get; set; }
		public Action? OnDelay { get; set; }
		public List<string> StartedBaseNames { get; } = new();
		public List<string> Events { get; } = new();

		public string FindProductArtifact(string installRoot, string baseName) => Path.Combine(installRoot, "product", baseName + ".dll");

		public int StartProcess(ApplicationProcessSpec spec)
		{
			var baseName = Path.GetFileNameWithoutExtension(spec.ArtifactPath);
			StartedBaseNames.Add(baseName);
			Events.Add("start:" + baseName);
			var processId = ++_nextProcessId;

			if (baseName == "rtaime.Operator")
			{
				_operatorProcessId = processId;
				if (OperatorDelayBudget > 0) _alive.Add(processId);
				return processId;
			}

			_alive.Add(processId);
			if (baseName == "rtaime.ControlHost" && PublishReadinessOnControlStart)
				PublishReadiness(processId);
			return processId;
		}

		public bool IsProcessAlive(int processId) => _alive.Contains(processId);

		public bool IsEndpointLeaseHeld(string endpoint) =>
			EndpointLeaseHeld && string.Equals(endpoint, _options.Endpoints.Control, StringComparison.Ordinal);

		public void KillProcessTree(int processId) => _alive.Remove(processId);

		public void KillControlProcess()
		{
			if (_alive.Count > 0) _alive.Remove(_alive.Min());
		}

		public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));

		public string ReadAllText(string path) => _files[Path.GetFullPath(path)];

		public void WriteAllText(string path, string content)
		{
			_files[Path.GetFullPath(path)] = content;
			if (Path.GetFullPath(path) == Path.GetFullPath(_options.StopPath))
			{
				StopSignalWritten = true;
				if (!IgnoreStopSignal && _alive.Count > 0) _alive.Remove(_alive.Min());
			}
		}

		public void DeleteFile(string path) => _files.Remove(Path.GetFullPath(path));

		public void CreateDirectory(string path)
		{
		}

		public Task<bool> ProbePipeAsync(string endpoint, TimeSpan timeout, CancellationToken cancellationToken) =>
			Task.FromResult(PipeReachable && !UnreachableEndpoints.Contains(endpoint));

		public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			UtcNow += delay;
			Events.Add("delay");
			if (PublishReadinessAfterDelay && !_published)
			{
				var controlProcessId = _alive.Min();
				PublishReadiness(controlProcessId);
			}
			OnDelay?.Invoke();
			if (OperatorDelayBudget > 0)
			{
				OperatorDelayBudget--;
				if (OperatorDelayBudget == 0 && _operatorProcessId is { } operatorProcessId)
					_alive.Remove(operatorProcessId);
			}
			return Task.CompletedTask;
		}

		public void PublishReadiness(int controlProcessId, bool includeChildProcessIds = true)
		{
			_published = true;
			_alive.Add(controlProcessId);
			if (includeChildProcessIds)
			{
				_alive.Add(7001);
				_alive.Add(7002);
			}
			var endpoints = _options.Endpoints;
			var payload = new
			{
				processId = controlProcessId,
				state = "READY",
				health = "HEALTHY",
				controlEndpoint = endpoints.Control,
				runtimeEndpoint = endpoints.Runtime,
				aiEndpoint = endpoints.AI,
				runtimeSupervision = new { state = "HEALTHY", processId = includeChildProcessIds ? 7001 : (int?)null },
				aiSupervision = new { state = "HEALTHY", processId = includeChildProcessIds ? 7002 : (int?)null }
			};
			_files[Path.GetFullPath(_options.ReadinessPath)] = JsonSerializer.Serialize(payload);
		}
	}
}
