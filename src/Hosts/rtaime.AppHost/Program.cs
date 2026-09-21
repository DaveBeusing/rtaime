// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace rtaime.AppHost;

internal static class Program
{
	private const string WindowsServiceArgument = "--windows-service";

	private static async Task<int> Main(string[] args)
	{
		if (args.Contains(WindowsServiceArgument, StringComparer.OrdinalIgnoreCase))
			return await RunWindowsServiceAsync(args).ConfigureAwait(false);

		return await RunConsoleApplicationAsync(args).ConfigureAwait(false);
	}

	private static async Task<int> RunConsoleApplicationAsync(string[] args)
	{
		using var shutdown = new CancellationTokenSource();
		ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			shutdown.Cancel();
		};

		Console.CancelKeyPress += cancelHandler;
		try
		{
			var options = ApplicationHostOptions.Load(args);
			var host = new UnifiedApplicationHost(options, new SystemApplicationHostPlatform());
			var hideInteractiveConsole =
				OperatingSystem.IsWindows() &&
				options.Profile != ApplicationStartupProfile.HeadlessEngine &&
				!args.Contains("--show-console", StringComparer.OrdinalIgnoreCase) &&
				!Console.IsOutputRedirected &&
				!Console.IsErrorRedirected;
			var consoleHidden = 0;

			if (hideInteractiveConsole)
			{
				host.Lifecycle.StateChanged += (_, eventArgs) =>
				{
					if (Interlocked.CompareExchange(ref consoleHidden, 1, 0) != 0)
						return;
					if (!eventArgs.Stages.Any(stage =>
						string.Equals(stage.Id, ApplicationLifecycleStages.OperatorInterface, StringComparison.Ordinal) &&
						stage.Status == LifecycleStageStatus.Ready))
					{
						Interlocked.Exchange(ref consoleHidden, 0);
						return;
					}

					HideConsoleWindow();
				};
			}

			host.StateChanged += state => Console.WriteLine($"app=rtaime state={state}");
			var result = await host.RunAsync(shutdown.Token).ConfigureAwait(false);
			return result.Success ? 0 : 1;
		}
		catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
		{
			return 0;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"app=rtaime state=FAILED detail=\"{exception.Message}\"");
			return 1;
		}
		finally
		{
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

	private static void HideConsoleWindow()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var consoleWindow = GetConsoleWindow();
		if (consoleWindow != IntPtr.Zero)
			ShowWindow(consoleWindow, 0);
	}

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindow(IntPtr windowHandle, int command);

}
