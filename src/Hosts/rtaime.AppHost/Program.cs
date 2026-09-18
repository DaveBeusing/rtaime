// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.AppHost;

internal static class Program
{
	private static async Task<int> Main(string[] args)
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
}
