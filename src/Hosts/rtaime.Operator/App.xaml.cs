// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using rtaime.Client;
using rtaime.Core;

namespace rtaime.Operator;

public partial class App : Application
{
	private bool _headlessMode;
	private HostLog? _log;
	private IDisposable? _failureHooks;

	public bool UnexpectedFailureDetected { get; private set; }

	protected override void OnStartup(StartupEventArgs e)
	{
		_log = HostLog.Open("Operator", e.Args);
		_failureHooks = _log.AttachProcessFailureHandlers();
		DispatcherUnhandledException += OnDispatcherUnhandledException;
		base.OnStartup(e);
		var headless = e.Args.Any(argument => string.Equals(argument, "--headless", StringComparison.OrdinalIgnoreCase));
		var headlessOnce = e.Args.Any(argument => string.Equals(argument, "--headless-once", StringComparison.OrdinalIgnoreCase));
		_headlessMode = headless || headlessOnce;
		_log.Information(
			"lifecycle",
			"operator.start",
			"Operator startup initiated.",
			new Dictionary<string, string>
			{
				["headless"] = _headlessMode.ToString(),
				["headlessOnce"] = headlessOnce.ToString()
			});

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
		_log.Information("lifecycle", "operator.window-shown", "Operator main window shown.");
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_log?.Information(
			"lifecycle",
			"operator.exit",
			"Operator application exiting.",
			new Dictionary<string, string> { ["exitCode"] = e.ApplicationExitCode.ToString() });
		_log?.Flush();
		DispatcherUnhandledException -= OnDispatcherUnhandledException;
		_failureHooks?.Dispose();
		_failureHooks = null;
		_log?.Dispose();
		_log = null;
		base.OnExit(e);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		UnexpectedFailureDetected = true;
		var reportPath = TryWriteCrashReport(e.Exception);
		_log?.Critical(
			"ui",
			"operator.dispatcher-unhandled-exception",
			"Operator dispatcher encountered an unhandled exception.",
			e.Exception,
			string.IsNullOrWhiteSpace(reportPath)
				? null
				: new Dictionary<string, string> { ["crashReportPath"] = reportPath });
		_log?.Flush();

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

	private string? TryWriteCrashReport(Exception exception)
	{
		try
		{
			var root = _log?.SessionDirectory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"rtaime",
				"logs");
			Directory.CreateDirectory(root);
			var path = Path.Combine(root, $"operator-crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.log");
			var detail = string.Join(
				Environment.NewLine,
				exception.ToString()
					.Replace("\r\n", "\n", StringComparison.Ordinal)
					.Replace('\r', '\n')
					.Split('\n')
					.Take(96)
					.Select(DiagnosticRedactor.RedactText));
			File.WriteAllText(
				path,
				$"UTC: {DateTimeOffset.UtcNow:O}{Environment.NewLine}Session: {_log?.SessionId ?? "unavailable"}{Environment.NewLine}{detail}");
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
		string? lastFailure = null;
		while (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
		{
			try
			{
				var snapshot = await client.SynchronizeAsync().ConfigureAwait(false);
				if (lastFailure is not null)
				{
					_log?.Information(
						"recovery",
						"operator.reconnect-recovered",
						"Operator headless recovery probe reconnected.",
						new Dictionary<string, string> { ["controlEndpoint"] = endpoint });
					lastFailure = null;
				}

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
					_log?.Information("recovery", "operator.headless-ready", "Operator headless recovery probe completed successfully.");
					await Dispatcher.InvokeAsync(() => Shutdown(0));
					return;
				}
			}
			catch (Exception exception)
			{
				var failure = $"{exception.GetType().FullName}:{exception.Message}";
				if (!string.Equals(lastFailure, failure, StringComparison.Ordinal))
				{
					_log?.Warning(
						"recovery",
						"operator.reconnect-failure",
						"Operator headless recovery probe could not synchronize with ControlHost.",
						exception,
						new Dictionary<string, string> { ["controlEndpoint"] = endpoint });
					lastFailure = failure;
				}
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
