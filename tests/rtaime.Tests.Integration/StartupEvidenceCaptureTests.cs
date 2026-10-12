// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text.Json;
using rtaime.AppHost;

namespace rtaime.Tests.Integration;

public sealed class StartupEvidenceCaptureTests
{
	[Theory]
	[InlineData("pending")]
	[InlineData("degraded")]
	[InlineData("unavailable")]
	public async Task Unqualified_hosts_retain_pending_history_and_separate_graceful_shutdown(string state)
	{
		await using var fixture = new Fixture();
		fixture.Command("ControlHost", state == "pending" ? "pending" : "ready");
		fixture.Command("RuntimeHost", state);
		fixture.Command("AIHost", "ready");
		var host = fixture.Host(TimeSpan.FromSeconds(8));
		await Assert.ThrowsAsync<TimeoutException>(() => host.RunAsync());
		using var before = fixture.ReadCapture("before-shutdown.json");
		Assert.Equal("startup.deadline.expired", before.RootElement.GetProperty("lifecycle")
			.GetProperty("readinessFailure").GetProperty("reasonCode").GetString());
		Assert.False(File.Exists(fixture.Options.ReadinessPath));
		if (state == "unavailable")
			Assert.Contains(before.RootElement.GetProperty("lifecycle").GetProperty("readinessFailure")
				.GetProperty("unmetCriteria").EnumerateArray(), item => item.GetProperty("host").GetString() == "RuntimeHost" &&
					item.GetProperty("code").GetString() == "ipc.reachable");
		Assert.Contains(before.RootElement.GetProperty("events").EnumerateArray(),
			item => item.GetProperty("code").GetString() == "controlhost.binding.ipc-connect.started");
		Assert.DoesNotContain(host.Lifecycle.Stages, stage => stage.Status == LifecycleStageStatus.Ready &&
			stage.Id == ApplicationLifecycleStages.ProductionReadiness);
		fixture.AssertRetainedAndStopped(expectGraceful: true);
	}

	[Fact]
	public async Task Child_exit_before_readiness_preserves_explicit_supervision_failure_and_drains_streams()
	{
		await using var fixture = new Fixture();
		fixture.Command("RuntimeHost", "crash");
		var host = fixture.Host();
		var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync());
		Assert.Contains("RuntimeHost", failure.Message);
		using var before = fixture.ReadCapture("before-shutdown.json");
		Assert.Equal("startup.supervision.failed", before.RootElement.GetProperty("lifecycle")
			.GetProperty("readinessFailure").GetProperty("reasonCode").GetString());
		var runtime = before.RootElement.GetProperty("hosts").EnumerateArray()
			.Single(item => item.GetProperty("host").GetString() == "RuntimeHost");
		Assert.Equal("Failed", runtime.GetProperty("qualification").GetString());
		fixture.AssertRetainedAndStopped(expectGraceful: true);
		Assert.Contains("exitCode=17", File.ReadAllText(fixture.CaptureFile("controlhost-process.log")));
	}

	[Fact]
	public async Task Explicit_control_exit_freezes_failure_before_cleanup_and_never_claims_graceful_stop()
	{
		await using var fixture = new Fixture();
		var host = fixture.Host();
		var run = host.RunAsync();
		await fixture.WaitObserved("ControlHost", "pending");
		fixture.Command("ControlHost", "crash");
		await Assert.ThrowsAsync<InvalidOperationException>(() => run);
		using var before = fixture.ReadCapture("before-shutdown.json");
		Assert.Equal("startup.process.exited", before.RootElement.GetProperty("lifecycle")
			.GetProperty("readinessFailure").GetProperty("reasonCode").GetString());
		fixture.AssertRetainedAndStopped(expectGraceful: false);
		Assert.Contains("exitCode=17", File.ReadAllText(fixture.CaptureFile("controlhost-process.log")));
	}

	[Fact]
	public async Task Late_readiness_requires_all_pipe_barriers_then_cancellation_drains_every_process()
	{
		await using var fixture = new Fixture();
		var host = fixture.Host();
		using var stop = new CancellationTokenSource();
		var run = host.RunAsync(stop.Token);
		await fixture.WaitObserved("ControlHost", "pending");
		await fixture.WaitObserved("RuntimeHost", "pending");
		await fixture.WaitObserved("AIHost", "pending");
		Assert.NotEqual(ApplicationLifecycleState.Healthy, host.State);
		fixture.Command("ControlHost", "ready");
		fixture.Command("RuntimeHost", "ready");
		await fixture.WaitObserved("RuntimeHost", "ready");
		Assert.NotEqual(ApplicationLifecycleState.Healthy, host.State);
		fixture.Command("AIHost", "ready");
		await fixture.Wait(() => host.State == ApplicationLifecycleState.Healthy);
		Assert.All(host.Lifecycle.Stages, stage => Assert.Equal(LifecycleStageStatus.Ready, stage.Status));
		using var qualified = fixture.ReadCapture("qualified.json");
		Assert.Equal(fixture.SessionId, qualified.RootElement.GetProperty("sessionId").GetString());
		stop.Cancel();
		Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(20))).Success);
		fixture.AssertRetainedAndStopped(expectGraceful: true);
	}

	[Fact]
	public async Task Cancellation_before_readiness_preserves_observations_and_retained_restart_does_not_replace_them()
	{
		await using var fixture = new Fixture();
		var host = fixture.Host();
		using var stop = new CancellationTokenSource();
		var run = host.RunAsync(stop.Token);
		await fixture.WaitObserved("ControlHost", "pending");
		stop.Cancel();
		Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(20))).Success);
		fixture.AssertRetainedAndStopped(expectGraceful: true);
		var oldDirectory = fixture.CaptureDirectory;
		var oldEvidence = File.ReadAllText(Path.Combine(oldDirectory, "before-shutdown.json"));
		File.Delete(Path.Combine(fixture.Root, "children-stop.signal"));
		foreach (var role in Fixture.Roles)
		{
			File.Delete(Path.Combine(fixture.Root, role + ".started.json"));
			File.Delete(Path.Combine(fixture.Root, role + ".observed.json"));
			fixture.Command(role, "ready");
		}
		host = fixture.Host();
		using var restartStop = new CancellationTokenSource();
		run = host.RunAsync(restartStop.Token);
		await fixture.Wait(() => host.State == ApplicationLifecycleState.Healthy);
		Assert.NotEqual(oldDirectory, fixture.CaptureDirectory);
		restartStop.Cancel();
		Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(20))).Success);
		Assert.Equal(oldEvidence, File.ReadAllText(Path.Combine(oldDirectory, "before-shutdown.json")));
		Assert.Equal(2, Directory.GetDirectories(Path.Combine(fixture.Options.WorkRoot, "startup-evidence")).Length);
		fixture.AssertRetainedAndStopped(expectGraceful: true);
	}

	private sealed class Fixture : IApplicationHostPlatform, IAsyncDisposable
	{
		public static readonly string[] Roles = ["ControlHost", "RuntimeHost", "AIHost"];
		public string Root { get; } = Path.Combine(Path.GetTempPath(), "rtaime-startup-evidence", Guid.NewGuid().ToString("N"));
		public string SessionId { get; } = Guid.NewGuid().ToString("N");
		public string? DiagnosticSessionId => SessionId;
		public string? DiagnosticLogRoot => Path.Combine(Root, "logs");
		public ApplicationHostOptions Options { get; }
		private readonly SystemApplicationHostPlatform _system;
		private readonly List<int> _processes = [];
		public string CaptureDirectory {
			get { using var doc = JsonDocument.Parse(File.ReadAllText(Options.LifecycleEvidencePath));
				return Path.GetDirectoryName(doc.RootElement.GetProperty("retainedEvidencePath").GetString())!; }
		}
		public string CaptureFile(string name) => Path.Combine(CaptureDirectory, name);
		public JsonDocument ReadCapture(string name) => JsonDocument.Parse(File.ReadAllText(CaptureFile(name)));

		public Fixture()
		{
			Directory.CreateDirectory(Root);
			_system = new SystemApplicationHostPlatform(DiagnosticLogRoot, SessionId);
			var endpoints = new ApplicationEndpointSet("rtaime.test.control." + SessionId,
				"rtaime.test.runtime." + SessionId, "rtaime.test.ai." + SessionId);
			Options = new ApplicationHostOptions(ApplicationStartupProfile.HeadlessEngine, Root, Path.Combine(Root, "state"),
				Path.Combine(Root, "work"), "default", ApplicationLifecycleOwnership.EphemeralLocal,
				false, "rtaime-engine", "", true, false,
				new ApplicationLifecyclePolicy(endpoints, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
					TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25), 2,
					TimeSpan.FromSeconds(10)));
		}
		public UnifiedApplicationHost Host(TimeSpan? timeout = null) =>
			new(Options with { Policy = Options.Policy with { StartupTimeout = timeout ?? TimeSpan.FromSeconds(30) } }, this);
		public void Command(string role, string state)
		{
			var path = Path.Combine(Root, role + ".command");
			File.WriteAllText(path + ".tmp", state);
			File.Move(path + ".tmp", path, true);
		}
		public Task WaitObserved(string role, string state) => Wait(() => {
			var path = Path.Combine(Root, role + ".observed.json");
			if (!File.Exists(path)) return false;
			using var doc = JsonDocument.Parse(File.ReadAllText(path));
			return doc.RootElement.GetProperty("state").GetString() == state;
		});
		public async Task Wait(Func<bool> condition)
		{
			using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			while (!condition()) await Task.Delay(25, deadline.Token);
		}
		public void AssertRetainedAndStopped(bool expectGraceful)
		{
			using var before = ReadCapture("before-shutdown.json");
			using var shutdown = ReadCapture("apphost-shutdown.json");
			using var manifest = ReadCapture("manifest.json");
			using var lifecycle = ReadCapture("apphost-lifecycle.json");
			foreach (var doc in new[] { before, shutdown, manifest, lifecycle })
				Assert.Equal(SessionId, doc.RootElement.GetProperty("sessionId").GetString());
			Assert.True(before.RootElement.GetProperty("capturedAtUtc").GetDateTimeOffset() <=
				shutdown.RootElement.GetProperty("requestedAtUtc").GetDateTimeOffset());
			Assert.Equal(expectGraceful, shutdown.RootElement.GetProperty("graceful").GetBoolean());
			Assert.False(shutdown.RootElement.GetProperty("forcedTermination").GetBoolean());
			Assert.True(shutdown.RootElement.GetProperty("diagnosticsDrained").GetBoolean());
			Assert.Empty(manifest.RootElement.GetProperty("errors").EnumerateArray());
			Assert.All(_processes, pid => Assert.False(_system.IsProcessAlive(pid)));
			foreach (var role in Roles)
			{
				using var pid = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, role + ".started.json")));
				Assert.False(_system.IsProcessAlive(pid.RootElement.GetProperty("processId").GetInt32()));
			}
			var stream = File.ReadAllText(CaptureFile("controlhost-process.log"));
			Assert.DoesNotContain("stream-secret", stream);
			Assert.DoesNotContain("stream-token", stream);
			Assert.Contains("[REDACTED]", stream);
			Assert.Contains("sessionId=" + SessionId, stream);
			Assert.Contains("stream=stdout", stream);
			Assert.Contains("stream=stderr", stream);
			Assert.Contains("stream=process", stream);
			Console.WriteLine($"Startup capture session={SessionId} directory={CaptureDirectory} graceful={expectGraceful} streams=drained");
		}

		public DateTimeOffset UtcNow => _system.UtcNow;
		public string FindProductArtifact(string installRoot, string baseName)
		{
			var directory = new DirectoryInfo(AppContext.BaseDirectory);
			while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "rtaime.slnx"))) directory = directory.Parent;
			return Path.Combine(directory!.FullName, "tests", "rtaime.LifecycleTestHost", "bin", "Release", "net10.0", "rtaime.LifecycleTestHost.dll");
		}
		public int StartProcess(ApplicationProcessSpec spec)
		{
			var environment = new Dictionary<string, string>(spec.Environment) { ["RTAIME_LOG_ROOT"] = DiagnosticLogRoot! };
			var pid = _system.StartProcess(spec with {
				Arguments = ["ControlHost", Root, Options.Endpoints.Control], Environment = environment });
			_processes.Add(pid);
			return pid;
		}
		public bool IsProcessAlive(int processId) => _system.IsProcessAlive(processId);
		public bool IsEndpointLeaseHeld(string endpoint) => _system.IsEndpointLeaseHeld(endpoint);
		public Task FlushProcessDiagnosticsAsync(int processId, CancellationToken token) => _system.FlushProcessDiagnosticsAsync(processId, token);
		public IDisposable? OpenDiagnosticSession(string directory) => _system.OpenDiagnosticSession(directory);
		public IReadOnlyList<ApplicationHostDiagnosticEvent> ReadHostDiagnosticEvents() => _system.ReadHostDiagnosticEvents();
		public void WriteAllTextAtomic(string path, string content) => _system.WriteAllTextAtomic(path, content);
		public void KillProcessTree(int processId) => _system.KillProcessTree(processId);
		public bool FileExists(string path) => _system.FileExists(path);
		public string ReadAllText(string path) => _system.ReadAllText(path);
		public void WriteAllText(string path, string content) => _system.WriteAllText(path, content);
		public void DeleteFile(string path) => _system.DeleteFile(path);
		public void CreateDirectory(string path) => _system.CreateDirectory(path);
		public Task<bool> ProbePipeAsync(string endpoint, TimeSpan timeout, CancellationToken token) => _system.ProbePipeAsync(endpoint, timeout, token);
		public Task DelayAsync(TimeSpan delay, CancellationToken token) => _system.DelayAsync(delay, token);

		public async ValueTask DisposeAsync()
		{
			try
			{
				File.WriteAllText(Path.Combine(Root, "children-stop.signal"), "stop");
				if (Directory.Exists(Options.WorkRoot)) File.WriteAllText(Options.StopPath, "stop");
				foreach (var pid in _processes)
				{
					if (!_system.IsProcessAlive(pid)) continue;
					using var process = Process.GetProcessById(pid);
					try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
					catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
					await _system.FlushProcessDiagnosticsAsync(pid, CancellationToken.None);
				}
			}
			finally
			{
				var targetRoot = Environment.GetEnvironmentVariable("RTAIME_BINDING_EVIDENCE_ROOT");
				if (!string.IsNullOrWhiteSpace(targetRoot))
					foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
					{
						var target = Path.Combine(targetRoot, "synthetic-" + SessionId, Path.GetRelativePath(Root, file));
						Directory.CreateDirectory(Path.GetDirectoryName(target)!);
						File.Copy(file, target, true);
					}
				Directory.Delete(Root, true);
			}
		}
	}
}
