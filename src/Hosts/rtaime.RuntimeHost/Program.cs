// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.RuntimeHost;

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
			RuntimeHostProcessOptions options;
			try
			{
				options = RuntimeHostProcessOptions.Load(args);
			}
			catch (Exception exception) when (exception is ArgumentException or OverflowException)
			{
				Console.Error.WriteLine($"host=RuntimeHost outcome=configuration-error detail=\"{exception.Message}\"");
				return (int)RuntimeHostExitCode.ConfigurationError;
			}

			var process = new RuntimeHostProcess(options);
			using var monitorStop = new CancellationTokenSource();
			var stopWatcher = WatchManagedStopAsync(shutdown, monitorStop.Token);
			try
			{
				var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
				var lifecycle = process.Lifecycle;
				Console.WriteLine($"host=RuntimeHost state={lifecycle.State} health={lifecycle.Health} exit={(int)exitCode} detail=\"{lifecycle.Detail}\"");
				return (int)exitCode;
			}
			finally
			{
				monitorStop.Cancel();
				try { await stopWatcher.ConfigureAwait(false); } catch (OperationCanceledException) { }
			}
		}
		finally
		{
			Console.CancelKeyPress -= consoleHandler;
			AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
		}
	}

	private static async Task WatchManagedStopAsync(CancellationTokenSource shutdown, CancellationToken cancellationToken)
	{
		var stopFile = Environment.GetEnvironmentVariable("RTAIME_HOST_STOP_FILE");
		if (string.IsNullOrWhiteSpace(stopFile)) return;
		var path = Path.GetFullPath(stopFile);
		while (!cancellationToken.IsCancellationRequested && !shutdown.IsCancellationRequested)
		{
			if (File.Exists(path))
			{
				shutdown.Cancel();
				return;
			}
			await Task.Delay(100, cancellationToken).ConfigureAwait(false);
		}
	}
}
