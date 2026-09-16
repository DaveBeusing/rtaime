// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;

namespace rtaime.ControlHost;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		if (StateMaintenanceCli.IsRequested(args))
			return await StateMaintenanceCli.RunAsync(args[1..], CancellationToken.None).ConfigureAwait(false);

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
			ControlHostProcessOptions options;
			ControlHostChildSupervision supervision;
			try
			{
				options = ControlHostProcessOptions.Load(args);
				supervision = ControlHostChildSupervision.Load(options);
			}
			catch (Exception exception) when (exception is ArgumentException or OverflowException or FileNotFoundException)
			{
				Console.Error.WriteLine($"host=ControlHost outcome=configuration-error detail=\"{exception.Message}\"");
				return (int)ControlHostExitCode.ConfigurationError;
			}

			await using (supervision.ConfigureAwait(false))
			{
				await supervision.StartAsync(shutdown.Token).ConfigureAwait(false);
				var process = new ControlHostProcess(options);
				using var monitorStop = new CancellationTokenSource();
				var monitor = MonitorManagedLifecycleAsync(process, supervision, options, shutdown, monitorStop.Token);
				try
				{
					var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
					var lifecycle = process.Lifecycle;
					Console.WriteLine($"host=ControlHost state={lifecycle.State} health={lifecycle.Health} exit={(int)exitCode} detail=\"{lifecycle.Detail}\"");
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
		ControlHostProcess process,
		ControlHostChildSupervision supervision,
		ControlHostProcessOptions options,
		CancellationTokenSource shutdown,
		CancellationToken cancellationToken)
	{
		var readinessValue = Environment.GetEnvironmentVariable("RTAIME_HOST_READINESS_FILE");
		var stopValue = Environment.GetEnvironmentVariable("RTAIME_HOST_STOP_FILE");
		if (string.IsNullOrWhiteSpace(readinessValue) && string.IsNullOrWhiteSpace(stopValue)) return;

		var readinessPath = string.IsNullOrWhiteSpace(readinessValue) ? null : Path.GetFullPath(readinessValue);
		var stopPath = string.IsNullOrWhiteSpace(stopValue) ? null : Path.GetFullPath(stopValue);
		if (readinessPath is not null)
		{
			var directory = Path.GetDirectoryName(readinessPath);
			if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
			DeleteManagedFile(readinessPath);
		}

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
				var runtime = supervision.Runtime;
				var ai = supervision.AI;
				var runtimeReady = runtime is null || runtime.State == LocalProcessSupervisionState.Healthy;
				var aiReady = ai is null || ai.State == LocalProcessSupervisionState.Healthy;
				var ready = lifecycle.State == ControlHostProcessState.Ready &&
					lifecycle.Health == ControlHostHealthState.Healthy &&
					process.IpcServer?.Running == true &&
					runtimeReady &&
					aiReady;

				if (readinessPath is not null && ready && !publishedReady)
				{
					var aiEndpoint = Environment.GetEnvironmentVariable("RTAIME_AI_ENDPOINT");
					if (string.IsNullOrWhiteSpace(aiEndpoint)) aiEndpoint = "rtaime.v1.ai.default";
					var payload = new
					{
						copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
						schemaVersion = "1.0",
						host = "ControlHost",
						processId = Environment.ProcessId,
						state = lifecycle.State.ToString().ToUpperInvariant(),
						health = lifecycle.Health.ToString().ToUpperInvariant(),
						controlEndpoint = options.ListenEndpoint,
						runtimeEndpoint = options.RuntimeEndpoint,
						aiEndpoint,
						runtimeSupervision = runtime is null ? null : new
						{
							state = runtime.State.ToString().ToUpperInvariant(),
							processId = runtime.OwnedProcessId,
							startAttempts = runtime.StartAttempts
						},
						aiSupervision = ai is null ? null : new
						{
							state = ai.State.ToString().ToUpperInvariant(),
							processId = ai.OwnedProcessId,
							startAttempts = ai.StartAttempts
						},
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
