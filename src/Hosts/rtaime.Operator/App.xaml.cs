// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using rtaime.Client;

namespace rtaime.Operator;

public partial class App : Application
{
	private bool _headlessMode;

	public bool UnexpectedFailureDetected { get; private set; }

	protected override void OnStartup(StartupEventArgs e)
	{
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		base.OnStartup(e);
		var headless = e.Args.Any(argument => string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase));
		var headlessOnce = e.Args.Any(argument => string.Equals(argument, "--headless-once", StringComparison.OrdinalIgnoreCase));
		_headlessMode = headless || headlessOnce;
		if (_headlessMode)
		{
			ShutdownMode = ShutdownMode.OnExplicitShutdown;
			var endpoint = GetArgument(e.Args, "control-endpoint") ?? Environment.GetEnvironmentVariable("RTAIME_CONTROL_ENDPOINT") ?? "rtaime.v1.control.default";
			var readyFile = GetArgument(e.Args, "ready-file");
			_ = RunHeadlessRecoveryProbeAsync(endpoint, readyFile, headlessOnce);
			return;
		}

		ShutdownMode = ShutdownMode.OnMainWindowClose;
		var window = new MainWindow();
		MainWindow = window;
		window.Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		DispatcherUnhandledException -= OnDispatcherUnhandledException;
		base.OnExit(e);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		UnexpectedFailureDetected = true;
		var reportPath = TryWriteCrashReport(e.Exception);
		var reportDetail = reportPath is null
			? "A diagnostic report could not be written."
			: $"Diagnostic report: {reportPath}";

		if (!_headlessMode)
		{
			MessageBox.Show(
				$"rtaime Operator encountered an unexpected failure and must close.\n\nThe current production state was not advanced by the failed UI operation. Restart rtaime to restore the safe workspace. Saved UI state never auto-starts outputs or other production actions.\n\n{reportDetail}",
				"rtaime Operator — Unexpected failure",
				MessageBoxButton.OK,
				MessageBoxImage.Error);
		}

		e.Handled = true;
		Shutdown(-1);
	}

	private static string? TryWriteCrashReport(Exception exception)
	{
		try
		{
			var root = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"rtaime",
				"logs");
			Directory.CreateDirectory(root);
			var path = Path.Combine(root, $"operator-crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.log");
			File.WriteAllText(
				path,
				$"UTC: {DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}");
			return path;
		}
		catch
		{
			return null;
		}
	}

	private async Task RunHeadlessRecoveryProbeAsync(string endpoint, string? readyFile, bool stopAfterFirstSuccess)
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

				if (stopAfterFirstSuccess)
				{
					await Dispatcher.InvokeAsync(() => Shutdown(0));
					return;
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
