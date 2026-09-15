// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.ControlHost;

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
			ControlHostProcessOptions options;
			try
			{
				options = ControlHostProcessOptions.Load(args);
			}
			catch (Exception exception) when (exception is ArgumentException or OverflowException)
			{
				Console.Error.WriteLine($"host=ControlHost outcome=configuration-error detail=\"{exception.Message}\"");
				return (int)ControlHostExitCode.ConfigurationError;
			}

			var process = new ControlHostProcess(options);
			var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
			var lifecycle = process.Lifecycle;
			Console.WriteLine($"host=ControlHost state={lifecycle.State} health={lifecycle.Health} exit={(int)exitCode} detail=\"{lifecycle.Detail}\"");
			return (int)exitCode;
		}
		finally
		{
			Console.CancelKeyPress -= consoleHandler;
			AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
		}
	}
}
