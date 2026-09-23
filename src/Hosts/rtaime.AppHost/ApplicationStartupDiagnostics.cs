// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using System.Text.Json;
using rtaime.Core;

namespace rtaime.AppHost;

internal static class ApplicationStartupDiagnostics
{
	public const string FileName = "apphost-startup.log";

	public static string? TryPersistFailure(string[] args, Exception exception)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(exception);

		try
		{
			var path = ResolveDiagnosticPath(args);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			var payload = JsonSerializer.Serialize(new
			{
				copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
				schemaVersion = "1.0",
				observedAtUtc = DateTimeOffset.UtcNow,
				state = "FAILED",
				exceptionType = exception.GetType().FullName,
				message = DiagnosticRedactor.RedactText(exception.Message),
				detail = DiagnosticRedactor.RedactExceptionDetail(exception.ToString())
			});
			File.AppendAllText(path, payload + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return path;
		}
		catch
		{
			return null;
		}
	}

	public static string ResolveDiagnosticPath(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		var explicitWorkRoot = ResolveArgumentOrEnvironment(args, "work-root", "RTAIME_APPHOST_WORK_ROOT");
		if (!string.IsNullOrWhiteSpace(explicitWorkRoot))
			return Path.Combine(Path.GetFullPath(explicitWorkRoot), FileName);

		var instanceId = ResolveArgumentOrEnvironment(args, "instance-id", "RTAIME_INSTANCE_ID");
		if (string.IsNullOrWhiteSpace(instanceId) ||
			!System.Text.RegularExpressions.Regex.IsMatch(instanceId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
		{
			instanceId = "default";
		}

		var windowsService = args.Contains("--windows-service", StringComparer.OrdinalIgnoreCase);
		var ownership = ResolveArgumentOrEnvironment(args, "ownership", "RTAIME_LIFECYCLE_OWNERSHIP");
		if (windowsService || string.Equals(ownership, "ExternalManaged", StringComparison.OrdinalIgnoreCase))
		{
			var stateRoot = ResolveArgumentOrEnvironment(args, "state-root", "RTAIME_STATE_ROOT");
			if (string.IsNullOrWhiteSpace(stateRoot))
			{
				stateRoot = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
					"rtaime");
			}

			return Path.Combine(Path.GetFullPath(stateRoot), "service", instanceId, FileName);
		}

		var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localApplicationData))
			localApplicationData = Path.GetTempPath();

		return Path.Combine(
			Path.GetFullPath(localApplicationData),
			"rtaime",
			"apphost",
			instanceId,
			FileName);
	}

	private static string? ResolveArgumentOrEnvironment(
		IReadOnlyList<string> args,
		string argumentName,
		string environmentName)
	{
		var prefix = $"--{argumentName}=";
		var argument = args.FirstOrDefault(candidate =>
			candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		if (argument is not null)
			return argument[prefix.Length..];

		return Environment.GetEnvironmentVariable(environmentName);
	}

}
