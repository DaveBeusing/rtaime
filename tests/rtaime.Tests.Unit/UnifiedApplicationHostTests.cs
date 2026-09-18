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
	public async Task Interactive_operator_exit_keeps_owned_engine_running_by_default()
	{
		var options = CreateOptions(ApplicationStartupProfile.Interactive);
		var platform = new FakeApplicationHostPlatform(options) { PublishReadinessOnControlStart = true };
		var host = new UnifiedApplicationHost(options, platform);

		await host.RunAsync();

		Assert.True(host.OwnsControlLifecycle);
		Assert.False(platform.StopSignalWritten);
	}

	private static ApplicationHostOptions CreateOptions(ApplicationStartupProfile profile)
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-apphost-tests", Guid.NewGuid().ToString("N"));
		return new ApplicationHostOptions(
			profile,
			Path.Combine(root, "install"),
			Path.Combine(root, "state"),
			Path.Combine(root, "work"),
			"default",
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
		private bool _published;

		public FakeApplicationHostPlatform(ApplicationHostOptions options)
		{
			_options = options;
			UtcNow = new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.Zero);
		}

		public DateTimeOffset UtcNow { get; private set; }
		public bool PublishReadinessOnControlStart { get; init; }
		public bool PublishReadinessAfterDelay { get; init; }
		public bool PipeReachable { get; init; } = true;
		public bool StopSignalWritten { get; private set; }
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
				return processId;

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
				if (_alive.Count > 0) _alive.Remove(_alive.Min());
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
			return Task.CompletedTask;
		}

		public void PublishReadiness(int controlProcessId)
		{
			_published = true;
			_alive.Add(controlProcessId);
			_alive.Add(7001);
			_alive.Add(7002);
			var endpoints = _options.Endpoints;
			var payload = new
			{
				processId = controlProcessId,
				state = "READY",
				health = "HEALTHY",
				controlEndpoint = endpoints.Control,
				runtimeEndpoint = endpoints.Runtime,
				aiEndpoint = endpoints.AI,
				runtimeSupervision = new { state = "HEALTHY", processId = 7001 },
				aiSupervision = new { state = "HEALTHY", processId = 7002 }
			};
			_files[Path.GetFullPath(_options.ReadinessPath)] = JsonSerializer.Serialize(payload);
		}
	}
}
