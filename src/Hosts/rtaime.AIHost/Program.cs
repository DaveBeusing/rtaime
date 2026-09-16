// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Core;

namespace rtaime.AIHost;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		using var shutdown = new CancellationTokenSource();
		ConsoleCancelEventHandler consoleHandler = (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			shutdown.Cancel();
		};
		EventHandler processExitHandler = (_, _) => shutdown.Cancel();

		Console.CancelKeyPress += consoleHandler;
		AppDomain.CurrentDomain.ProcessExit += processExitHandler;
		try
		{
			AIHostProcessOptions options;
			try
			{
				options = AIHostProcessOptions.Load(args);
			}
			catch (Exception exception) when (exception is ArgumentException or OverflowException)
			{
				Console.Error.WriteLine($"host=AIHost outcome=configuration-error detail=\"{exception.Message}\"");
				return (int)AIHostExitCode.ConfigurationError;
			}

			LocalEndpointLease endpointLease;
			try
			{
				endpointLease = LocalEndpointLease.Acquire(options.ListenEndpoint);
			}
			catch (InvalidOperationException exception)
			{
				Console.Error.WriteLine($"host=AIHost outcome=startup-failure detail=\"{exception.Message}\"");
				return (int)AIHostExitCode.StartupFailure;
			}

			using (endpointLease)
			{
				var process = new AIHostProcess(options);
				using var monitorStop = new CancellationTokenSource();
				var monitor = MonitorManagedLifecycleAsync(process, options, shutdown, monitorStop.Token);
				try
				{
					var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
					var lifecycle = process.Lifecycle;
					Console.WriteLine($"host=AIHost state={lifecycle.State} health={lifecycle.Health} exit={(int)exitCode} detail=\"{lifecycle.Detail}\"");
					return (int)exitCode;
				}
				finally
				{
					monitorStop.Cancel();
					try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
				}
			}
		}
		finally
		{
			Console.CancelKeyPress -= consoleHandler;
			AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
		}
	}

	private static async Task MonitorManagedLifecycleAsync(
		AIHostProcess process,
		AIHostProcessOptions options,
		CancellationTokenSource shutdown,
		CancellationToken cancellationToken)
	{
		var readinessValue = Environment.GetEnvironmentVariable("RTAIME_HOST_READINESS_FILE");
		var stopValue = Environment.GetEnvironmentVariable("RTAIME_HOST_STOP_FILE");
		if (string.IsNullOrWhiteSpace(readinessValue) && string.IsNullOrWhiteSpace(stopValue)) return;

		var readinessPath = string.IsNullOrWhiteSpace(readinessValue) ? null : Path.GetFullPath(readinessValue);
		var stopPath = string.IsNullOrWhiteSpace(stopValue) ? null : Path.GetFullPath(stopValue);
		PrepareReadinessPath(readinessPath);

		var publishedReady = false;
		try
		{
			while (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
			{
				if (stopPath is not null && File.Exists(stopPath))
				{
					shutdown.Cancel();
					break;
				}

				var lifecycle = process.Lifecycle;
				var ready = lifecycle.State == AIHostProcessState.Ready &&
					lifecycle.Health == AIHostHealthState.Healthy &&
					process.IpcServer?.Running == true;

				if (readinessPath is not null && ready && !publishedReady)
				{
					var payload = new
					{
						copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
						schemaVersion = "1.0",
						host = "AIHost",
						processId = Environment.ProcessId,
						state = lifecycle.State.ToString().ToUpperInvariant(),
						health = lifecycle.Health.ToString().ToUpperInvariant(),
						endpoint = options.ListenEndpoint,
						readyAtUtc = DateTimeOffset.UtcNow
					};
					WriteJsonAtomic(readinessPath, payload);
					publishedReady = true;
				}
				else if (readinessPath is not null && !ready && publishedReady)
				{
					DeleteManagedFile(readinessPath);
					publishedReady = false;
				}

				await Task.Delay(100, cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			DeleteManagedFile(readinessPath);
		}
	}

	private static void PrepareReadinessPath(string? path)
	{
		if (path is null) return;
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
		DeleteManagedFile(path);
	}

	private static void WriteJsonAtomic(string path, object value)
	{
		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(temporary, json + Environment.NewLine);
		File.Move(temporary, path, overwrite: true);
	}

	private static void DeleteManagedFile(string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
	}
}
