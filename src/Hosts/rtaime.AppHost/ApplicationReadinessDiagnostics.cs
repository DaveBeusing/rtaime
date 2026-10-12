// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using System.Text.Json;

namespace rtaime.AppHost;

/// <summary>Diagnostic observations never grant production readiness.</summary>
public sealed record ApplicationHostDiagnosticEvent(
	string Host,
	int ProcessId,
	DateTimeOffset ObservedAtUtc,
	string Code,
	IReadOnlyDictionary<string, string> Dimensions);

public sealed record ApplicationReadinessCriterion(
	string Host,
	string Code,
	bool Satisfied,
	DateTimeOffset? ObservedAtUtc,
	string Detail);

public sealed record ApplicationHostReadinessObservation(
	string Host,
	int? ProcessId,
	bool? ProcessAlive,
	DateTimeOffset? ProcessObservedAtUtc,
	string? LifecycleState,
	string? HealthState,
	DateTimeOffset? LifecycleObservedAtUtc,
	string? SupervisionState,
	DateTimeOffset? SupervisionObservedAtUtc,
	bool? IpcReachable,
	DateTimeOffset? IpcObservedAtUtc,
	string? LastProgressPhase,
	string? PhaseOutcome,
	DateTimeOffset? PhaseObservedAtUtc,
	string Qualification,
	string? ReasonCode);

public sealed record ApplicationReadinessFailureSnapshot(
	DateTimeOffset CapturedAtUtc,
	string ReasonCode,
	string OriginalFailure,
	bool AggregateDeadlineExpired,
	IReadOnlyList<ApplicationHostReadinessObservation> Hosts,
	IReadOnlyList<ApplicationReadinessCriterion> UnmetCriteria,
	string ReadinessEvidencePath,
	string ProcessDiagnosticPath);

internal static class ApplicationHostDiagnosticReader
{
	private static readonly HashSet<string> AllowedDimensions = new(StringComparer.Ordinal)
	{
		"state", "health", "processId", "phase", "outcome", "reason", "exitCode"
	};

	// Read a bounded tail of this session only. Partial live-writer lines are ignored.
	public static IReadOnlyList<ApplicationHostDiagnosticEvent> ReadSession(string? logRoot, string? sessionId)
	{
		if (string.IsNullOrWhiteSpace(logRoot) || string.IsNullOrWhiteSpace(sessionId) ||
			sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
			sessionId is "." or ".." || sessionId.Contains(Path.DirectorySeparatorChar) ||
			sessionId.Contains(Path.AltDirectorySeparatorChar))
			return Array.Empty<ApplicationHostDiagnosticEvent>();

		var result = new List<ApplicationHostDiagnosticEvent>();
		try
		{
			var directory = Path.Combine(Path.GetFullPath(logRoot), sessionId);
			if (!Directory.Exists(directory)) return result;
			foreach (var path in Directory.EnumerateFiles(directory, "rtaime-*.jsonl")
				.OrderByDescending(File.GetLastWriteTimeUtc).Take(16))
			{
				using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				var offset = Math.Max(0, stream.Length - 65536);
				stream.Seek(offset, SeekOrigin.Begin);
				using var reader = new StreamReader(stream, Encoding.UTF8);
				if (offset > 0) reader.ReadLine();
				var tail = reader.ReadToEnd();
				var lines = tail.Split('\n');
				for (var index = 0; index < lines.Length - 1; index++)
				{
					var observation = ParseLine(lines[index]);
					if (observation is not null) result.Add(observation);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			// Missing diagnostics leave observation unknown; they cannot fail or qualify a host.
		}
		return result.OrderBy(item => item.ObservedAtUtc).TakeLast(512).ToArray();
	}

	internal static ApplicationHostDiagnosticEvent? ParseLine(string line)
	{
		try
		{
			using var document = JsonDocument.Parse(line);
			var root = document.RootElement;
			var host = root.GetProperty("host").GetString();
			if (host is not ("ControlHost" or "RuntimeHost" or "AIHost")) return null;
			var code = root.GetProperty("code").GetString() ?? string.Empty;
			if (!code.Contains("state-changed", StringComparison.Ordinal) &&
				!code.Contains("supervision-changed", StringComparison.Ordinal) &&
				!code.StartsWith("controlhost.binding.", StringComparison.Ordinal) &&
				!code.EndsWith(".completed", StringComparison.Ordinal))
				return null;
			var dimensions = new Dictionary<string, string>(StringComparer.Ordinal);
			if (root.TryGetProperty("dimensions", out var values) && values.ValueKind == JsonValueKind.Object)
				foreach (var value in values.EnumerateObject())
					if (AllowedDimensions.Contains(value.Name) && value.Value.ValueKind == JsonValueKind.String)
						dimensions[value.Name] = value.Value.GetString() ?? string.Empty;
			return new ApplicationHostDiagnosticEvent(host, root.GetProperty("processId").GetInt32(),
				root.GetProperty("timestampUtc").GetDateTimeOffset(), code, dimensions);
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
		{
			return null;
		}
	}
}
