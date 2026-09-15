// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Text.Json;
using rtaime.Client;
using rtaime.ControlHost;
using rtaime.Core;
using rtaime.RuntimeHost;

namespace rtaime.Tests.Integration;

public sealed class OperatorProcessRecoveryTests
{
	private const int ProcessRecoveryTimeoutMilliseconds = 30000;

	[Fact]
	public async Task Killed_Operator_process_restarts_with_a_full_current_authoritative_snapshot()
	{
		var root = TempDirectory();
		var runtimeEndpoint = Endpoint("runtime-operator-recovery");
		var controlEndpoint = Endpoint("control-operator-recovery");
		var operatorAssembly = HostAssembly("rtaime.Operator", "net10.0-windows");

		using var runtimeStop = new CancellationTokenSource();
		var runtime = new RuntimeHostProcess(RuntimeHostProcessOptions.Default with { ListenEndpoint = runtimeEndpoint });
		var runtimeRun = runtime.RunAsync(runtimeStop.Token);

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

		Process? firstOperator = null;
		Process? secondOperator = null;
		try
		{
			await WaitUntilAsync(() => control.Lifecycle.State == ControlHostProcessState.Ready && control.Control?.HasAuthoritativeState == true, ProcessRecoveryTimeoutMilliseconds);
			var client = new OperatorControlClient(new NamedPipeOperatorControlTransport(controlEndpoint, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3)));
			var initial = await client.SynchronizeAsync();
			var firstMutation = await client.SelectPreviewAsync(initial.Sources[1].Id);
			Assert.True(firstMutation.Accepted, firstMutation.Failure?.ToString());
			var firstRevision = control.Control!.State.Revision;

			var firstMarkerPath = Path.Combine(root, "operator-first.json");
			firstOperator = StartOperator(operatorAssembly, controlEndpoint, firstMarkerPath);
			await WaitUntilAsync(() => TryReadMarker(firstMarkerPath, out var marker) && marker.Revision == firstRevision.Value && marker.RuntimeStatus == "READY", ProcessRecoveryTimeoutMilliseconds);
			var firstMarker = ReadMarker(firstMarkerPath);
			Assert.False(string.IsNullOrWhiteSpace(firstMarker.HostInstanceId));

			Kill(firstOperator);
			firstOperator = null;

			var secondMutation = await client.CutPreviewAsync();
			Assert.True(secondMutation.Accepted, secondMutation.Failure?.ToString());
			var secondRevision = control.Control.State.Revision;
			Assert.True(secondRevision.Value > firstRevision.Value);

			var secondMarkerPath = Path.Combine(root, "operator-second.json");
			secondOperator = StartOperator(operatorAssembly, controlEndpoint, secondMarkerPath);
			await WaitUntilAsync(() => TryReadMarker(secondMarkerPath, out var marker) && marker.Revision == secondRevision.Value && marker.RuntimeStatus == "READY", ProcessRecoveryTimeoutMilliseconds);
			var secondMarker = ReadMarker(secondMarkerPath);
			Assert.Equal(firstMarker.HostInstanceId, secondMarker.HostInstanceId);
			Assert.Equal(secondRevision.Value, secondMarker.Revision);
		}
		finally
		{
			if (firstOperator is not null) Kill(firstOperator);
			if (secondOperator is not null) Kill(secondOperator);
			controlStop.Cancel();
			Assert.Equal(ControlHostExitCode.Success, await controlRun);
			runtimeStop.Cancel();
			Assert.Equal(RuntimeHostExitCode.Success, await runtimeRun);
			DeleteDirectory(root);
		}
	}

	private static Process StartOperator(string assembly, string controlEndpoint, string markerPath) =>
		Process.Start(new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = $"\"{assembly}\" --headless --control-endpoint={controlEndpoint} --ready-file=\"{markerPath}\"",
			UseShellExecute = false,
			CreateNoWindow = true
		}) ?? throw new InvalidOperationException("Failed to start headless Operator process.");

	private static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit(5000);
			}
		}
		finally { process.Dispose(); }
	}

	private static bool TryReadMarker(string path, out OperatorMarker marker)
	{
		marker = default!;
		try
		{
			if (!File.Exists(path)) return false;
			marker = ReadMarker(path);
			return true;
		}
		catch (IOException) { return false; }
		catch (JsonException) { return false; }
	}

	private static OperatorMarker ReadMarker(string path) =>
		JsonSerializer.Deserialize<OperatorMarker>(File.ReadAllText(path))
		?? throw new InvalidDataException("Operator recovery marker is required.");

	private static string HostAssembly(string hostName, string target)
	{
		var repo = FindRepositoryRoot();
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
				throw new TimeoutException("Condition was not reached before the Operator recovery deadline.");
			await Task.Delay(50);
		}
	}

	private static string Endpoint(string purpose) => $"rtaime.test.{purpose}.{Guid.NewGuid():N}";

	private static string TempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-operator-recovery-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteDirectory(string path)
	{
		try { Directory.Delete(path, recursive: true); }
		catch { }
	}

	private sealed record OperatorMarker(string HostInstanceId, ulong Revision, string RuntimeStatus, DateTimeOffset ObservedAtUtc);
}