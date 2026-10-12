// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using rtaime.Core;

namespace rtaime.LifecycleTestHost;

// Test-only process topology. Command files are barriers; this executable is never packaged.
internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		var role = args[0];
		var root = args[1];
		var endpoint = args[2];
		Directory.CreateDirectory(root);
		using var log = HostLog.Open(role);
		using var stop = new CancellationTokenSource();
		using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, watchdog.Token);
		var children = new List<Process>();
		var streams = new List<Task>();
		var listeners = new List<Task>();
		WriteJson(Path.Combine(root, role + ".started.json"), new { processId = Environment.ProcessId });
		var exit = 0;
		try
		{
			if (role == "ControlHost")
			{
				foreach (var childRole in new[] { "RuntimeHost", "AIHost" })
				{
					var child = StartChild(childRole, root,
						Environment.GetEnvironmentVariable(childRole == "RuntimeHost" ? "RTAIME_RUNTIME_ENDPOINT" : "RTAIME_AI_ENDPOINT")!);
					children.Add(child);
					streams.Add(Forward(child.StandardOutput, Console.Out));
					streams.Add(Forward(child.StandardError, Console.Error));
				}
				await WaitUntil(() => children.All(child => File.Exists(Path.Combine(root,
					children.IndexOf(child) == 0 ? "RuntimeHost.started.json" : "AIHost.started.json"))), lifetime.Token);
				listeners.Add(Listen(endpoint, lifetime.Token));
				listeners.Add(Listen(endpoint + ".monitor", lifetime.Token));
			}
			var published = false;
			var lastState = "";
			while (!lifetime.IsCancellationRequested)
			{
				var commandPath = Path.Combine(root, role + ".command");
				var state = File.Exists(commandPath) ? File.ReadAllText(commandPath).Trim() : "pending";
				if (state != lastState)
				{
					lastState = state;
					log.Information("lifecycle", role.ToLowerInvariant() + ".state-changed", "Synthetic lifecycle barrier changed.",
						new Dictionary<string, string> { ["state"] = state is "ready" or "unavailable" ? "Ready" : "Degraded",
							["health"] = state is "ready" or "unavailable" ? "Healthy" : "Degraded" });
					Console.WriteLine("barrier=" + state);
					// These sentinels prove redaction at the parent stream boundary.
					Console.Error.WriteLine("password=stream-secret token=stream-token");
					WriteJson(Path.Combine(root, role + ".observed.json"), new { state, processId = Environment.ProcessId });
				}
				var stopPath = role == "ControlHost" ? Environment.GetEnvironmentVariable("RTAIME_HOST_STOP_FILE") :
					Path.Combine(root, "children-stop.signal");
				if (state == "crash") { exit = 17; break; }
				if (stopPath is not null && File.Exists(stopPath)) break;

				if (role != "ControlHost" && state == "ready" && !published)
				{
					listeners.Add(Listen(endpoint, lifetime.Token));
					if (role == "AIHost") listeners.Add(Listen(endpoint + ".monitor", lifetime.Token));
					published = true;
				}
				if (role == "ControlHost")
				{
					var runtime = children[0];
					var ai = children[1];
					var runtimeState = ChildState(root, "RuntimeHost", runtime);
					var aiState = ChildState(root, "AIHost", ai);
					foreach (var (name, child, childState) in new[] { ("Runtime", runtime, runtimeState), ("AI", ai, aiState) })
						log.Information("supervision", "controlhost." + name.ToLowerInvariant() + "-supervision-changed",
							"Synthetic supervision observation.", new Dictionary<string, string> {
								["state"] = childState, ["processId"] = child.Id.ToString() });
					log.Information("binding", "controlhost.binding.ipc-connect.started", "Synthetic reconciliation pending.",
						new Dictionary<string, string> { ["phase"] = "ipc-connect", ["outcome"] = "started" });
					if (state == "ready" && runtimeState == "Healthy" && aiState == "Healthy")
					{
						WriteJson(Environment.GetEnvironmentVariable("RTAIME_HOST_READINESS_FILE")!, new {
							processId = Environment.ProcessId, state = "READY", health = "HEALTHY",
							controlEndpoint = endpoint, runtimeEndpoint = Environment.GetEnvironmentVariable("RTAIME_RUNTIME_ENDPOINT"),
							aiEndpoint = Environment.GetEnvironmentVariable("RTAIME_AI_ENDPOINT"), readyAtUtc = DateTimeOffset.UtcNow,
							runtimeSupervision = new { state = "HEALTHY", processId = runtime.Id },
							aiSupervision = new { state = "HEALTHY", processId = ai.Id } });
					}
				}
				await Task.Delay(25, lifetime.Token);
			}
		}
		catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
		finally
		{
			if (role == "ControlHost") File.WriteAllText(Path.Combine(root, "children-stop.signal"), "stop");
			foreach (var child in children)
			{
				try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
				catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); exit = 18; }

			}
			await Task.WhenAll(streams);
			foreach (var child in children) child.Dispose();
			stop.Cancel();
			try { await Task.WhenAll(listeners); } catch (OperationCanceledException) { }
			log.Information("lifecycle", role.ToLowerInvariant() + ".completed", "Synthetic process completed.",
				new Dictionary<string, string> { ["exitCode"] = exit.ToString() });
			log.Flush();
			Console.WriteLine("exitCode=" + exit);
		}
		return exit;
	}

	private static string ChildState(string root, string role, Process child)
	{
		if (child.HasExited) return "Failed";
		var path = Path.Combine(root, role + ".observed.json");
		if (!File.Exists(path)) return "Starting";
		using var document = JsonDocument.Parse(File.ReadAllText(path));
		return document.RootElement.GetProperty("state").GetString() is "ready" or "unavailable" ? "Healthy" : "Waiting";
	}

	private static Process StartChild(string role, string root, string endpoint)
	{
		var info = new ProcessStartInfo("dotnet") {
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var value in new[] { Assembly.GetExecutingAssembly().Location, role, root, endpoint })
			info.ArgumentList.Add(value);
		return Process.Start(info) ?? throw new InvalidOperationException("Synthetic child failed to launch.");
	}

	private static async Task Forward(StreamReader source, TextWriter destination)
	{
		while (await source.ReadLineAsync() is { } line) await destination.WriteLineAsync(line);
	}

	private static async Task Listen(string endpoint, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			await using var server = new NamedPipeServerStream(endpoint, PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
			await server.WaitForConnectionAsync(token);
		}
	}

	private static async Task WaitUntil(Func<bool> condition, CancellationToken token)
	{
		while (!condition()) await Task.Delay(25, token);
	}

	private static void WriteJson(string path, object payload)
	{
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(payload));
		File.Move(temporary, path, true);
	}
}
