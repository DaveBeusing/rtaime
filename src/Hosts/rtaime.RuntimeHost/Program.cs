// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Core;

namespace rtaime.RuntimeHost;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		using var log = HostLog.Open("RuntimeHost", args);
		using var failureHooks = log.AttachProcessFailureHandlers();
		log.Information("lifecycle", "runtimehost.start", "RuntimeHost process starting.");

		using var shutdown = new CancellationTokenSource();
		ConsoleCancelEventHandler consoleHandler = (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			log.Information("lifecycle", "runtimehost.cancel-requested", "Console cancellation requested.");
			shutdown.Cancel();
		};
		EventHandler processExitHandler = (_, _) => shutdown.Cancel();

		Console.CancelKeyPress += consoleHandler;
		AppDomain.CurrentDomain.ProcessExit += processExitHandler;
		try
		{
			RuntimeHostProcessOptions options;
			try
			{
				options = RuntimeHostProcessOptions.Load(args);
			}
			catch (Exception exception) when (exception is ArgumentException or OverflowException)
			{
				log.Error("configuration", "runtimehost.configuration-error", "RuntimeHost configuration could not be loaded.", exception);
				Console.Error.WriteLine($"host=RuntimeHost outcome=configuration-error detail=\"{exception.Message}\"");
				return (int)RuntimeHostExitCode.ConfigurationError;
			}

			log.Information(
				"configuration",
				"runtimehost.configuration-loaded",
				"RuntimeHost configuration loaded.",
				new Dictionary<string, string>
				{
					["runtimeEndpoint"] = options.ListenEndpoint
				});

			LocalEndpointLease endpointLease;
			try
			{
				endpointLease = LocalEndpointLease.Acquire(options.ListenEndpoint);
			}
			catch (InvalidOperationException exception)
			{
				log.Error("startup", "runtimehost.endpoint-acquire-failure", "RuntimeHost endpoint acquisition failed.", exception);
				Console.Error.WriteLine($"host=RuntimeHost outcome=startup-failure detail=\"{exception.Message}\"");
				return (int)RuntimeHostExitCode.StartupFailure;
			}

			using (endpointLease)
			{
				var process = new RuntimeHostProcess(options);
				using var monitorStop = new CancellationTokenSource();
				var monitor = MonitorManagedLifecycleAsync(process, options, shutdown, log, monitorStop.Token);
				try
				{
					var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
					var lifecycle = process.Lifecycle;
					log.Information(
						"lifecycle",
						"runtimehost.completed",
						"RuntimeHost process completed.",
						new Dictionary<string, string>
						{
							["state"] = lifecycle.State.ToString(),
							["health"] = lifecycle.Health.ToString(),
							["exitCode"] = ((int)exitCode).ToString(),
							["detail"] = lifecycle.Detail
						});
					Console.WriteLine($"host=RuntimeHost state={lifecycle.State} health={lifecycle.Health} exit={(int)exitCode} detail=\"{lifecycle.Detail}\"");
					return (int)exitCode;
				}
				finally
				{
					monitorStop.Cancel();
					try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
				}
			}
		}
		catch (Exception exception)
		{
			log.Critical("lifecycle", "runtimehost.unexpected-failure", "RuntimeHost terminated after an unexpected failure.", exception);
			return (int)RuntimeHostExitCode.StartupFailure;
		}
		finally
		{
			Console.CancelKeyPress -= consoleHandler;
			AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
			log.Information("lifecycle", "runtimehost.exit", "RuntimeHost main loop exited.");
			log.Flush();
		}
	}

	private static async Task MonitorManagedLifecycleAsync(
		RuntimeHostProcess process,
		RuntimeHostProcessOptions options,
		CancellationTokenSource shutdown,
		HostLog log,
		CancellationToken cancellationToken)
	{
		var readinessValue = Environment.GetEnvironmentVariable("RTAIME_HOST_READINESS_FILE");
		var stopValue = Environment.GetEnvironmentVariable("RTAIME_HOST_STOP_FILE");
		var readinessPath = string.IsNullOrWhiteSpace(readinessValue) ? null : Path.GetFullPath(readinessValue);
		var stopPath = string.IsNullOrWhiteSpace(stopValue) ? null : Path.GetFullPath(stopValue);
		PrepareReadinessPath(readinessPath);

		var publishedReady = false;
		var lastReady = false;
		RuntimeHostProcessState? lastState = null;
		RuntimeHostHealthState? lastHealth = null;
		LocalEndpointReadinessLease? endpointReadiness = null;
		try
		{
			while (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
			{
				if (stopPath is not null && File.Exists(stopPath))
				{
					log.Information("lifecycle", "runtimehost.stop-signal", "Managed stop signal detected.");
					shutdown.Cancel();
					break;
				}

				var lifecycle = process.Lifecycle;
				var ready = lifecycle.State == RuntimeHostProcessState.Ready &&
					lifecycle.Health == RuntimeHostHealthState.Healthy &&
					process.IpcServer?.Running == true;

				if (lifecycle.State != lastState || lifecycle.Health != lastHealth)
				{
					log.Information(
						"lifecycle",
						"runtimehost.state-changed",
						$"RuntimeHost lifecycle changed to {lifecycle.State}/{lifecycle.Health}.",
						new Dictionary<string, string>
						{
							["state"] = lifecycle.State.ToString(),
							["health"] = lifecycle.Health.ToString(),
							["detail"] = lifecycle.Detail
						});
					lastState = lifecycle.State;
					lastHealth = lifecycle.Health;
				}

				if (ready != lastReady)
				{
					log.Information(
						"readiness",
						ready ? "runtimehost.ready" : "runtimehost.not-ready",
						ready ? "RuntimeHost became ready." : "RuntimeHost is no longer ready.");
					lastReady = ready;
				}

				if (ready && endpointReadiness is null)
				{
					try
					{
						endpointReadiness = LocalEndpointReadinessLease.Acquire(options.ListenEndpoint);
					}
					catch (InvalidOperationException exception)
					{
						log.Error("readiness", "runtimehost.readiness-lease-failure", "RuntimeHost readiness lease acquisition failed.", exception);
						shutdown.Cancel();
						throw;
					}
				}
				else if (!ready && endpointReadiness is not null)
				{
					endpointReadiness.Dispose();
					endpointReadiness = null;
				}

				if (readinessPath is not null && ready && !publishedReady)
				{
					var payload = new
					{
						copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
						schemaVersion = "1.0",
						host = "RuntimeHost",
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
			endpointReadiness?.Dispose();
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
