// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

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

			var process = new AIHostProcess(options);
			var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
			var lifecycle = process.Lifecycle;
			Console.WriteLine($"host=AIHost state={lifecycle.State} health={lifecycle.Health} exit={(int)exitCode} detail=\"{lifecycle.Detail}\"");
			return (int)exitCode;
		}
		finally
		{
			Console.CancelKeyPress -= consoleHandler;
			AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
		}
	}
}
