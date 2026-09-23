// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.Core;

namespace rtaime.ControlHost;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		using var log = HostLog.Open("ControlHost", args);
		using var failureHooks = log.AttachProcessFailureHandlers();
		log.Information("lifecycle", "controlhost.start", "ControlHost process starting.");

		if (StateMaintenanceCli.IsRequested(args))
		{
			log.Information("maintenance", "controlhost.maintenance-start", "State maintenance command requested.");
			var maintenanceExitCode = await StateMaintenanceCli.RunAsync(args[1..], CancellationToken.None).ConfigureAwait(false);
			log.Information(
				"maintenance",
				"controlhost.maintenance-stop",
				"State maintenance command completed.",
				new Dictionary<string, string> { ["exitCode"] = maintenanceExitCode.ToString() });
			return maintenanceExitCode;
		}

		using var shutdown = new CancellationTokenSource();
		ConsoleCancelEventHandler consoleHandler = (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			log.Information("lifecycle", "controlhost.cancel-requested", "Console cancellation requested.");
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
				log.Error("configuration", "controlhost.configuration-error", "ControlHost configuration could not be loaded.", exception);
				Console.Error.WriteLine($"host=ControlHost outcome=configuration-error detail=\"{DiagnosticRedactor.RedactText(exception.Message)}\"");
				return (int)ControlHostExitCode.ConfigurationError;
			}

			log.Information(
				"configuration",
				"controlhost.configuration-loaded",
				"ControlHost configuration loaded.",
				new Dictionary<string, string>
				{
					["controlEndpoint"] = options.ListenEndpoint,
					["runtimeEndpoint"] = options.RuntimeEndpoint
				});

			LocalEndpointLease endpointLease;
			try
			{
				endpointLease = LocalEndpointLease.Acquire(options.ListenEndpoint);
			}
			catch (InvalidOperationException exception)
			{
				await supervision.DisposeAsync().ConfigureAwait(false);
				log.Error("startup", "controlhost.endpoint-acquire-failure", "ControlHost endpoint acquisition failed.", exception);
				Console.Error.WriteLine($"host=ControlHost outcome=startup-failure detail=\"{DiagnosticRedactor.RedactText(exception.Message)}\"");
				return (int)ControlHostExitCode.StartupFailure;
			}

			using (endpointLease)
			await using (supervision.ConfigureAwait(false))
			{
				await supervision.StartAsync(shutdown.Token).ConfigureAwait(false);
				log.Information("supervision", "controlhost.supervision-started", "Managed child supervision started.");
				var process = new ControlHostProcess(options);
				using var monitorStop = new CancellationTokenSource();
				var monitor = MonitorManagedLifecycleAsync(process, supervision, options, shutdown, log, monitorStop.Token);
				try
				{
					var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
					var lifecycle = process.Lifecycle;
					log.Information(
						"lifecycle",
						"controlhost.completed",
						"ControlHost process completed.",
						new Dictionary<string, string>
						{
							["state"] = lifecycle.State.ToString(),
							["health"] = lifecycle.Health.ToString(),
							["exitCode"] = ((int)exitCode).ToString(),
							["detail"] = lifecycle.Detail
						});
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
		catch (Exception exception)
		{
			log.Critical("lifecycle", "controlhost.unexpected-failure", "ControlHost terminated after an unexpected failure.", exception);
			return (int)ControlHostExitCode.UnexpectedFailure;
		}
		finally
		{
			Console.CancelKeyPress -= consoleHandler;
			AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
			log.Information("lifecycle", "controlhost.exit", "ControlHost main loop exited.");
			log.Flush();
		}
	}

	private static async Task MonitorManagedLifecycleAsync(
		ControlHostProcess process,
		ControlHostChildSupervision supervision,
		ControlHostProcessOptions options,
		CancellationTokenSource shutdown,
		HostLog log,
		CancellationToken cancellationToken)
	{
		var readinessValue = Environment.GetEnvironmentVariable("RTAIME_HOST_READINESS_FILE");
		var stopValue = Environment.GetEnvironmentVariable("RTAIME_HOST_STOP_FILE");
		var readinessPath = string.IsNullOrWhiteSpace(readinessValue) ? null : Path.GetFullPath(readinessValue);
		var stopPath = string.IsNullOrWhiteSpace(stopValue) ? null : Path.GetFullPath(stopValue);
		if (readinessPath is not null)
		{
			var directory = Path.GetDirectoryName(readinessPath);
			if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
			DeleteManagedFile(readinessPath);
		}

		var publishedReady = false;
		var lastReady = false;
		ControlHostProcessState? lastState = null;
		ControlHostHealthState? lastHealth = null;
		LocalProcessSupervisionState? lastRuntimeState = null;
		LocalProcessSupervisionState? lastAIState = null;
		LocalEndpointReadinessLease? endpointReadiness = null;
		try
		{
			while (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
			{
				if (stopPath is not null && File.Exists(stopPath))
				{
					log.Information("lifecycle", "controlhost.stop-signal", "Managed stop signal detected.");
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

				if (lifecycle.State != lastState || lifecycle.Health != lastHealth)
				{
					log.Information(
						"lifecycle",
						"controlhost.state-changed",
						$"ControlHost lifecycle changed to {lifecycle.State}/{lifecycle.Health}.",
						new Dictionary<string, string>
						{
							["state"] = lifecycle.State.ToString(),
							["health"] = lifecycle.Health.ToString(),
							["detail"] = lifecycle.Detail
						});
					lastState = lifecycle.State;
					lastHealth = lifecycle.Health;
				}

				if (runtime?.State != lastRuntimeState)
				{
					log.Information(
						"supervision",
						"controlhost.runtime-supervision-changed",
						"RuntimeHost supervision state changed.",
						new Dictionary<string, string>
						{
							["state"] = runtime?.State.ToString() ?? "DISABLED",
							["processId"] = runtime?.OwnedProcessId?.ToString() ?? "none",
							["startAttempts"] = runtime?.StartAttempts.ToString() ?? "0",
							["detail"] = runtime?.Detail ?? "RuntimeHost supervision is disabled."
						});
					lastRuntimeState = runtime?.State;
				}

				if (ai?.State != lastAIState)
				{
					log.Information(
						"supervision",
						"controlhost.ai-supervision-changed",
						"AIHost supervision state changed.",
						new Dictionary<string, string>
						{
							["state"] = ai?.State.ToString() ?? "DISABLED",
							["processId"] = ai?.OwnedProcessId?.ToString() ?? "none",
							["startAttempts"] = ai?.StartAttempts.ToString() ?? "0",
							["detail"] = ai?.Detail ?? "AIHost supervision is disabled."
						});
					lastAIState = ai?.State;
				}

				if (ready != lastReady)
				{
					log.Information(
						"readiness",
						ready ? "controlhost.ready" : "controlhost.not-ready",
						ready ? "ControlHost became ready." : "ControlHost is no longer ready.");
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
						log.Error("readiness", "controlhost.readiness-lease-failure", "ControlHost readiness lease acquisition failed.", exception);
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
			endpointReadiness?.Dispose();
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
