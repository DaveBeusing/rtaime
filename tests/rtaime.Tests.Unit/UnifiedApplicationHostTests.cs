// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.AppHost;

namespace rtaime.Tests.Unit;

public sealed class UnifiedApplicationHostTests
{
	[Fact]
	public async Task Starts_control_then_operator_only_after_readiness()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessAfterDelay = true };

		var result = await new UnifiedApplicationHost(options, platform).RunAsync();

		Assert.True(result.Success);
		Assert.False(result.AdoptedControlHost);
		Assert.Equal(new[] { "rtaime.ControlHost", "rtaime.Operator" }, platform.StartedBaseNames);
		Assert.DoesNotContain("rtaime.RuntimeHost", platform.StartedBaseNames);
		Assert.DoesNotContain("rtaime.AIHost", platform.StartedBaseNames);
		Assert.True(platform.Events.IndexOf("delay") < platform.Events.IndexOf("start:rtaime.Operator"));
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
	public async Task External_managed_never_starts_control_host()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive, ApplicationLifecycleOwnership.ExternalManaged);
		var platform = new FakeApplicationHostPlatform(options);

		await Assert.ThrowsAsync<TimeoutException>(() => new UnifiedApplicationHost(options, platform).RunAsync());

		Assert.Empty(platform.StartedBaseNames);
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
		ApplicationLifecycleOwnership? ownership = null)
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
			false,
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

		public void KillProcessTree(int processId) => _alive.Remove(processId);

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
			Task.FromResult(PipeReachable);

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
