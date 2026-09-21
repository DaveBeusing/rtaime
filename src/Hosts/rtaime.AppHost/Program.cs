// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace rtaime.AppHost;

internal static class Program
{
	private const string WindowsServiceArgument = "--windows-service";

	private static async Task<int> Main(string[] args)
	{
		try
		{
			var consoleState = WindowsConsoleBootstrap.Initialize(args);
			if (args.Contains(WindowsServiceArgument, StringComparer.OrdinalIgnoreCase))
				return await RunWindowsServiceAsync(args).ConfigureAwait(false);

			return await RunApplicationAsync(args, consoleState.ConsoleAvailable).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			var diagnosticPath = ApplicationStartupDiagnostics.TryPersistFailure(args, exception);
			WriteFailureToStandardError(exception, diagnosticPath);
			return 1;
		}
	}

	private static async Task<int> RunApplicationAsync(string[] args, bool consoleAvailable)
	{
		using var shutdown = new CancellationTokenSource();
		ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			shutdown.Cancel();
		};

		if (consoleAvailable)
			Console.CancelKeyPress += cancelHandler;

		try
		{
			var options = ApplicationHostOptions.Load(args);
			var host = new UnifiedApplicationHost(options, new SystemApplicationHostPlatform());
			host.StateChanged += state => Console.WriteLine($"app=rtaime state={state}");
			var result = await host.RunAsync(shutdown.Token).ConfigureAwait(false);
			return result.Success ? 0 : 1;
		}
		catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
		{
			return 0;
		}
		finally
		{
			if (consoleAvailable)
				Console.CancelKeyPress -= cancelHandler;
		}
	}

	private static async Task<int> RunWindowsServiceAsync(string[] args)
	{
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Windows service mode is supported only on Windows.");

		var options = ApplicationHostOptions.Load(args);
		var builder = Host.CreateApplicationBuilder();
		builder.Services.AddWindowsService(serviceOptions => serviceOptions.ServiceName = options.WindowsServiceName);
		builder.Services.AddSingleton(options);
		builder.Services.AddSingleton<IApplicationHostPlatform, SystemApplicationHostPlatform>();
		builder.Services.AddSingleton<UnifiedApplicationHost>();
		builder.Services.AddHostedService<WindowsEngineBackgroundService>();

		using var serviceHost = builder.Build();
		await serviceHost.RunAsync().ConfigureAwait(false);
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
		IHostApplicationLifetime applicationLifetime) : BackgroundService
	{
		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			void StateChanged(ApplicationLifecycleState state) =>
				logger.LogInformation("rtaime engine lifecycle state {LifecycleState}", state);

			applicationHost.StateChanged += StateChanged;
			try
			{
				var result = await applicationHost.RunAsync(stoppingToken).ConfigureAwait(false);
				if (!stoppingToken.IsCancellationRequested)
				{
					Environment.ExitCode = 1;
					logger.LogCritical("Persistent engine lifecycle exited unexpectedly with success={Success}.", result.Success);
					applicationLifetime.StopApplication();
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
			}
			catch (Exception exception)
			{
				Environment.ExitCode = 1;
				logger.LogCritical(exception, "Persistent engine lifecycle failed.");
				applicationLifetime.StopApplication();
			}
			finally
			{
				applicationHost.StateChanged -= StateChanged;
			}
		}
	}
}
