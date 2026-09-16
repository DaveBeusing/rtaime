// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

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
				var exitCode = await process.RunAsync(shutdown.Token).ConfigureAwait(false);
				var lifecycle = process.Lifecycle;
				Console.WriteLine($"host=ControlHost state={lifecycle.State} health={lifecycle.Health} exit={(int)exitCode} detail=\"{lifecycle.Detail}\"");
				return (int)exitCode;
			}
		}
		finally
		{
			Console.CancelKeyPress -= consoleHandler;
			AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
		}
	}
}
