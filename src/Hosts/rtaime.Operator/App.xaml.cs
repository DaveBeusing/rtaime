// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using System.Windows;
using rtaime.Client;

namespace rtaime.Operator;

public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		if (e.Args.Any(argument => string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase)))
		{
			ShutdownMode = ShutdownMode.OnExplicitShutdown;
			var endpoint = GetArgument(e.Args, "control-endpoint") ?? Environment.GetEnvironmentVariable("RTAIME_CONTROL_ENDPOINT") ?? "rtaime.v1.control.default";
			var readyFile = GetArgument(e.Args, "ready-file");
			_ = RunHeadlessRecoveryProbeAsync(endpoint, readyFile);
			return;
		}

		ShutdownMode = ShutdownMode.OnMainWindowClose;
		var window = new MainWindow();
		MainWindow = window;
		window.Show();
	}

	private async Task RunHeadlessRecoveryProbeAsync(string endpoint, string? readyFile)
	{
		var transport = new NamedPipeOperatorControlTransport(endpoint, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
		var client = new OperatorControlClient(transport);
		while (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
		{
			try
			{
				var snapshot = await client.SynchronizeAsync().ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(readyFile))
				{
					var fullPath = Path.GetFullPath(readyFile);
					Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
					var marker = JsonSerializer.Serialize(new OperatorRecoveryMarker(
						transport.HostInstanceId ?? string.Empty,
						snapshot.Production.Revision.Value,
						snapshot.RuntimeStatus,
						DateTimeOffset.UtcNow));
					await File.WriteAllTextAsync(fullPath, marker).ConfigureAwait(false);
				}
			}
			catch
			{
				// Headless mode is a reconnect probe: endpoint loss is expected and retried.
			}
			await Task.Delay(200).ConfigureAwait(false);
		}
	}

	private static string? GetArgument(IReadOnlyList<string> args, string key)
	{
		var prefix = $"--{key}=";
		var value = args.LastOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		return value is null ? null : value[prefix.Length..].Trim().Trim('"');
	}

	private sealed record OperatorRecoveryMarker(
		string HostInstanceId,
		ulong Revision,
		string RuntimeStatus,
		DateTimeOffset ObservedAtUtc);
}
