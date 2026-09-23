// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using rtaime.Core;

namespace rtaime.AppHost;

internal static class Program
{
	private const string WindowsServiceArgument = "--windows-service";

	private static async Task<int> Main(string[] args)
	{
		using var log = HostLog.Open("AppHost", args);
		using var failureHooks = log.AttachProcessFailureHandlers();
		try
		{
			var consoleState = WindowsConsoleBootstrap.Initialize(args);
			var serviceMode = args.Contains(WindowsServiceArgument, StringComparer.OrdinalIgnoreCase);
			log.Information(
				"lifecycle",
				"apphost.start",
				"AppHost startup initiated.",
				new Dictionary<string, string>
				{
					["mode"] = serviceMode ? "windows-service" : "interactive",
					["consoleAvailable"] = consoleState.ConsoleAvailable.ToString()
				});

			if (serviceMode)
				return await RunWindowsServiceAsync(args, log).ConfigureAwait(false);

			return await RunApplicationAsync(args, consoleState.ConsoleAvailable, log).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			var diagnosticPath = ApplicationStartupDiagnostics.TryPersistFailure(args, exception);
			log.Critical(
				"startup",
				"apphost.startup-failure",
				"AppHost startup failed.",
				exception,
				string.IsNullOrWhiteSpace(diagnosticPath)
					? null
					: new Dictionary<string, string> { ["startupDiagnosticPath"] = diagnosticPath });
			WriteFailureToStandardError(exception, diagnosticPath);
			return 1;
		}
		finally
		{
			log.Information("lifecycle", "apphost.exit", "AppHost main loop exited.");
			log.Flush();
		}
	}

	private static async Task<int> RunApplicationAsync(string[] args, bool consoleAvailable, HostLog log)
	{
		using var shutdown = new CancellationTokenSource();
		ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			log.Information("lifecycle", "apphost.cancel-requested", "Console cancellation requested.");
			shutdown.Cancel();
		};

		if (consoleAvailable)
			Console.CancelKeyPress += cancelHandler;

		try
		{
			var options = ApplicationHostOptions.Load(args);
			log.Information(
				"configuration",
				"apphost.configuration-loaded",
				"Application host configuration loaded.",
				new Dictionary<string, string>
				{
					["profile"] = options.Profile.ToString(),
					["ownership"] = options.Ownership.ToString(),
					["instanceId"] = options.InstanceId
				});
			var host = new UnifiedApplicationHost(options, new SystemApplicationHostPlatform());
			host.StateChanged += state =>
			{
				Console.WriteLine($"app=rtaime state={state}");
				log.Information(
					"lifecycle",
					"apphost.state-changed",
					$"Application lifecycle changed to {state}.",
					new Dictionary<string, string> { ["state"] = state.ToString() });
			};
			var result = await host.RunAsync(shutdown.Token).ConfigureAwait(false);
			log.Information(
				"lifecycle",
				"apphost.completed",
				"Application host completed.",
				new Dictionary<string, string> { ["success"] = result.Success.ToString() });
			return result.Success ? 0 : 1;
		}
		catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
		{
			log.Information("lifecycle", "apphost.cancelled", "Application host stopped after cancellation.");
			return 0;
		}
		finally
		{
			if (consoleAvailable)
				Console.CancelKeyPress -= cancelHandler;
		}
	}

	private static async Task<int> RunWindowsServiceAsync(string[] args, HostLog log)
	{
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Windows service mode is supported only on Windows.");

		var options = ApplicationHostOptions.Load(args);
		log.Information(
			"lifecycle",
			"apphost.service-start",
			"Windows service host is starting.",
			new Dictionary<string, string>
			{
				["serviceName"] = options.WindowsServiceName,
				["instanceId"] = options.InstanceId
			});

		var builder = Host.CreateApplicationBuilder();
		builder.Services.AddWindowsService(serviceOptions => serviceOptions.ServiceName = options.WindowsServiceName);
		builder.Services.AddSingleton(options);
		builder.Services.AddSingleton(log);
		builder.Services.AddSingleton<IApplicationHostPlatform, SystemApplicationHostPlatform>();
		builder.Services.AddSingleton<UnifiedApplicationHost>();
		builder.Services.AddHostedService<WindowsEngineBackgroundService>();

		using var serviceHost = builder.Build();
		await serviceHost.RunAsync().ConfigureAwait(false);
		log.Information(
			"lifecycle",
			"apphost.service-stop",
			"Windows service host stopped.",
			new Dictionary<string, string> { ["exitCode"] = Environment.ExitCode.ToString() });
		return Environment.ExitCode;
	}

	private static void WriteFailureToStandardError(Exception exception, string? diagnosticPath)
	{
		try
		{
			var diagnosticSuffix = string.IsNullOrWhiteSpace(diagnosticPath)
				? string.Empty
				: $" diagnostics=\"{diagnosticPath}\"";
			Console.Error.WriteLine(
				$"app=rtaime state=FAILED detail=\"{exception.Message}\"{diagnosticSuffix}");
		}
		catch
		{
		}
	}

	private sealed class WindowsEngineBackgroundService(
		UnifiedApplicationHost applicationHost,
		ILogger<WindowsEngineBackgroundService> logger,
		HostLog hostLog,
		IHostApplicationLifetime applicationLifetime) : BackgroundService
	{
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			void StateChanged(ApplicationLifecycleState state)
			{
				logger.LogInformation("rtaime engine lifecycle state {LifecycleState}", state);
				hostLog.Information(
					"lifecycle",
					"apphost.service-state-changed",
					$"Persistent engine lifecycle changed to {state}.",
					new Dictionary<string, string> { ["state"] = state.ToString() });
			}

			applicationHost.StateChanged += StateChanged;
			try
			{
				var result = await applicationHost.RunAsync(stoppingToken).ConfigureAwait(false);
				if (!stoppingToken.IsCancellationRequested)
				{
					Environment.ExitCode = 1;
					logger.LogCritical("Persistent engine lifecycle exited unexpectedly with success={Success}.", result.Success);
					hostLog.Critical(
						"lifecycle",
						"apphost.service-unexpected-exit",
						"Persistent engine lifecycle exited unexpectedly.",
						dimensions: new Dictionary<string, string> { ["success"] = result.Success.ToString() });
					applicationLifetime.StopApplication();
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				hostLog.Information("lifecycle", "apphost.service-cancelled", "Persistent engine lifecycle cancelled.");
			}
			catch (Exception exception)
			{
				Environment.ExitCode = 1;
				logger.LogCritical(exception, "Persistent engine lifecycle failed.");
				hostLog.Critical(
					"lifecycle",
					"apphost.service-failure",
					"Persistent engine lifecycle failed.",
					exception);
				applicationLifetime.StopApplication();
			}
			finally
			{
				applicationHost.StateChanged -= StateChanged;
				hostLog.Flush();
			}
		}
	}
}
