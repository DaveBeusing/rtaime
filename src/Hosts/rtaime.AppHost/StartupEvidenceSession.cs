// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using System.Text.RegularExpressions;
using rtaime.Core;

namespace rtaime.AppHost;

/// <summary>Retained diagnostics are observations and never qualify a production host.</summary>
internal sealed class StartupEvidenceSession : IDisposable
{
	private readonly ApplicationHostOptions _options;
	private readonly IApplicationHostPlatform _platform;
	private readonly DateTimeOffset _startedAtUtc;
	private readonly List<string> _errors = [];
	private IDisposable? _lease;
	private bool _captured;
	public string SessionId { get; }
	public string CaptureId { get; } = Guid.NewGuid().ToString("N");
	public string DirectoryPath { get; }
	public string ProcessLogPath => Path.Combine(DirectoryPath, "controlhost-process.log");
	public string LifecyclePath => Path.Combine(DirectoryPath, "apphost-lifecycle.json");
	public string? LogSessionDirectory { get; }

	public StartupEvidenceSession(ApplicationHostOptions options, IApplicationHostPlatform platform)
	{
		_options = options;
		_platform = platform;
		_startedAtUtc = platform.UtcNow;
		var supplied = platform.DiagnosticSessionId;
		SessionId = supplied is not null && Regex.IsMatch(supplied, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
			? supplied : Guid.NewGuid().ToString("N");
		DirectoryPath = Path.Combine(options.WorkRoot, "startup-evidence", CaptureId);
		var logRoot = platform.DiagnosticLogRoot;
		if (!string.IsNullOrWhiteSpace(logRoot))
			LogSessionDirectory = Path.Combine(Path.GetFullPath(logRoot), SessionId);
	}

	public void Begin()
	{
		Try(() => {
			_platform.CreateDirectory(DirectoryPath);
			_lease = _platform.OpenDiagnosticSession(DirectoryPath);
		});
		PublishManifest(null);
	}

	public void CaptureQualified(ApplicationReadinessEvidence readiness) =>
		Write("qualified.json", new { schemaVersion = "1.0", sessionId = SessionId, captureId = CaptureId,
			capturedAtUtc = _platform.UtcNow, readiness });

	public void CaptureBeforeShutdown(string reason, IReadOnlyList<ApplicationHostReadinessObservation> hosts)
	{
		if (_captured) return;
		_captured = true;
		JsonElement? lifecycle = null;
		ApplicationReadinessEvidence? readiness = null;
		Try(() => {
			if (_platform.FileExists(_options.LifecycleEvidencePath))
			{
				using var document = JsonDocument.Parse(_platform.ReadAllText(_options.LifecycleEvidencePath));
				lifecycle = document.RootElement.Clone();
			}
			if (_platform.FileExists(_options.ReadinessPath))
				ApplicationReadinessEvidence.TryParse(_platform.ReadAllText(_options.ReadinessPath), out readiness);
		});
		// Structured history is already bounded by the platform reader. Exclude unrelated PIDs.
		var pids = hosts.Where(host => host.ProcessId is not null).Select(host => host.ProcessId!.Value).ToHashSet();
		var events = _platform.ReadHostDiagnosticEvents().Where(item => pids.Contains(item.ProcessId))
			.OrderBy(item => item.ObservedAtUtc).TakeLast(512).ToArray();
		Write("before-shutdown.json", new { schemaVersion = "1.0", sessionId = SessionId, captureId = CaptureId,
			capturedAtUtc = _platform.UtcNow, reason, hosts, readiness, lifecycle, events });
	}

	public void Complete(string outcome)
	{
		Try(() => {
			if (_platform.FileExists(_options.ShutdownEvidencePath))
				_platform.WriteAllTextAtomic(Path.Combine(DirectoryPath, "apphost-shutdown.json"),
					_platform.ReadAllText(_options.ShutdownEvidencePath));
		});
		PublishManifest(outcome);
	}

	private void PublishManifest(string? outcome) => Write("manifest.json", new {
		schemaVersion = "1.0", sessionId = SessionId, captureId = CaptureId, startedAtUtc = _startedAtUtc,
		completedAtUtc = outcome is null ? (DateTimeOffset?)null : _platform.UtcNow, outcome,
		lifecyclePath = LifecyclePath, beforeShutdownPath = Path.Combine(DirectoryPath, "before-shutdown.json"),
		qualifiedPath = Path.Combine(DirectoryPath, "qualified.json"),
		shutdownPath = Path.Combine(DirectoryPath, "apphost-shutdown.json"), processLogPath = ProcessLogPath,
		logSessionDirectory = LogSessionDirectory, errors = _errors.ToArray()
	});

	private void Write(string name, object value) => Try(() =>
	{
		using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions {
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
		using var output = new MemoryStream();
		using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
			WriteSanitized(writer, document.RootElement, null);
		_platform.WriteAllTextAtomic(Path.Combine(DirectoryPath, name),
			System.Text.Encoding.UTF8.GetString(output.ToArray()) + Environment.NewLine);
	});

	private static void WriteSanitized(Utf8JsonWriter writer, JsonElement element, string? key)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			writer.WriteStartObject();
			foreach (var property in element.EnumerateObject())
			{
				writer.WritePropertyName(property.Name);
				WriteSanitized(writer, property.Value, property.Name);
			}
			writer.WriteEndObject();
		}
		else if (element.ValueKind == JsonValueKind.Array)
		{
			writer.WriteStartArray();
			foreach (var item in element.EnumerateArray()) WriteSanitized(writer, item, key);
			writer.WriteEndArray();
		}
		else if (element.ValueKind == JsonValueKind.String)
			writer.WriteStringValue(DiagnosticRedactor.SanitizeValue(key ?? "value", element.GetString()));
		else element.WriteTo(writer);
	}

	private void Try(Action action)
	{
		try { action(); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			if (_errors.Count < 8) _errors.Add(DiagnosticRedactor.RedactText(exception.Message));
		}
	}

	public void Dispose() => _lease?.Dispose();

	// Only the owned capture directories are candidates; active sessions hold an exclusive lease.
	internal static IDisposable OpenDirectory(string directory)
	{
		var lease = new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate,
			FileAccess.ReadWrite, FileShare.None);
		try
		{
			var root = Path.GetDirectoryName(directory)!;
			var candidates = new DirectoryInfo(root).EnumerateDirectories()
				.Where(item => item.FullName != directory && Regex.IsMatch(item.Name, "^[a-f0-9]{32}$") &&
					File.Exists(Path.Combine(item.FullName, "manifest.json")))
				.OrderByDescending(item => File.GetLastWriteTimeUtc(Path.Combine(item.FullName, "manifest.json")))
				.ToArray();
			for (var index = 0; index < candidates.Length; index++)
			{
				var candidate = candidates[index];
				if (index < 63 && File.GetLastWriteTimeUtc(Path.Combine(candidate.FullName, "manifest.json")) >=
					DateTime.UtcNow.AddDays(-HostLog.DefaultRetentionDays)) continue;
				try
				{
					using (new FileStream(Path.Combine(candidate.FullName, "active.lock"), FileMode.OpenOrCreate,
						FileAccess.ReadWrite, FileShare.None)) { }
					Directory.Delete(candidate.FullName, recursive: true);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		return lease;
	}
}
