// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace rtaime.Core;

public enum DiagnosticSeverity
{
	Trace = 1,
	Information = 2,
	Warning = 3,
	Error = 4,
	Critical = 5
}

public sealed record DiagnosticEvent(
	ulong Sequence,
	DateTimeOffset TimestampUtc,
	DiagnosticSeverity Severity,
	string Category,
	string Code,
	string Message,
	Failure? Failure,
	IReadOnlyDictionary<string, string> Dimensions);

public sealed class BoundedDiagnosticBuffer
{
	private readonly object _gate = new();
	private readonly DiagnosticEvent?[] _items;
	private int _nextIndex;
	private int _count;
	private ulong _nextSequence;

	public BoundedDiagnosticBuffer(int capacity)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		_items = new DiagnosticEvent[capacity];
	}

	public int Capacity => _items.Length;
	public int Count { get { lock (_gate) return _count; } }

	public DiagnosticEvent Record(
		DiagnosticSeverity severity,
		string category,
		string code,
		string message,
		Failure? failure = null,
		IReadOnlyDictionary<string, string>? dimensions = null,
		DateTimeOffset? timestampUtc = null)
	{
		if (!Enum.IsDefined(typeof(DiagnosticSeverity), severity)) throw new ArgumentOutOfRangeException(nameof(severity));
		if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Diagnostic category is required.", nameof(category));
		if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Diagnostic code is required.", nameof(code));
		if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Diagnostic message is required.", nameof(message));

		lock (_gate)
		{
			var item = new DiagnosticEvent(
				_nextSequence++,
				(timestampUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
				severity,
				category.Trim(),
				code.Trim(),
				DiagnosticRedactor.RedactText(message.Trim()),
				failure is null ? null : new Failure(failure.Value.Code, DiagnosticRedactor.RedactText(failure.Value.Message)),
				DiagnosticRedactor.Sanitize(dimensions));
			_items[_nextIndex] = item;
			_nextIndex = (_nextIndex + 1) % _items.Length;
			if (_count < _items.Length) _count++;
			return item;
		}
	}

	public IReadOnlyList<DiagnosticEvent> Snapshot()
	{
		lock (_gate)
		{
			var result = new List<DiagnosticEvent>(_count);
			var start = (_nextIndex - _count + _items.Length) % _items.Length;
			for (var offset = 0; offset < _count; offset++)
			{
				var item = _items[(start + offset) % _items.Length];
				if (item is not null) result.Add(item);
			}
			return result.AsReadOnly();
		}
	}
}

public static class DiagnosticRedactor
{
	private const string Redacted = "[REDACTED]";
	private const int MaximumValueLength = 512;
	private static readonly string[] SensitiveKeyTokens =
	{
		"password", "passwd", "pwd", "secret", "token", "apikey", "api_key", "api-key",
		"authorization", "credential", "cookie", "connectionstring", "connection_string", "sas", "signature", "privatekey"
	};
	private static readonly Regex AssignmentPattern = new(
		"(?i)(password|passwd|pwd|secret|token|api[-_]?key|authorization|credential|cookie|connection[-_]?string|sas|signature|private[-_]?key)\\s*[:=]\\s*(?:Bearer\\s+)?([^\\s;,]+)",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);
	private static readonly Regex BearerPattern = new(
		"(?i)bearer\\s+[A-Za-z0-9\\-._~+/]+=*",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	public static string SanitizeValue(string key, string? value)
	{
		if (SensitiveKeyTokens.Any(token => NormalizeKey(key).Contains(token, StringComparison.Ordinal))) return Redacted;
		return Truncate(RedactText(value ?? string.Empty));
	}

	public static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? values)
	{
		if (values is null || values.Count == 0) return new SortedDictionary<string, string>(StringComparer.Ordinal);
		var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var pair in values)
			result[pair.Key] = SanitizeValue(pair.Key, pair.Value);
		return result;
	}

	public static string RedactText(string value)
	{
		if (string.IsNullOrEmpty(value)) return value;
		var redacted = AssignmentPattern.Replace(value, match => $"{match.Groups[1].Value}={Redacted}");
		redacted = BearerPattern.Replace(redacted, $"Bearer {Redacted}");
		return Truncate(redacted);
	}

	public static string RedactExceptionDetail(string value, int maximumLines = 96)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (maximumLines <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLines));

		var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
		var lines = normalized.Split('\n');
		var retained = lines.Take(maximumLines).Select(RedactText);
		var result = string.Join(Environment.NewLine, retained);
		return lines.Length <= maximumLines
			? result
			: result + Environment.NewLine + "[TRUNCATED]";
	}

	private static string NormalizeKey(string key) =>
		new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

	private static string Truncate(string value) =>
		value.Length <= MaximumValueLength ? value : value[..MaximumValueLength] + "…";
}

public sealed record ProductBuildInfo(string ProductVersion, string ReleaseStage)
{
	public static ProductBuildInfo FromAssembly(Assembly assembly)
	{
		ArgumentNullException.ThrowIfNull(assembly);
		var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		var version = string.IsNullOrWhiteSpace(informational)
			? assembly.GetName().Version?.ToString() ?? "unknown"
			: informational.Split('+', 2)[0];
		var separator = version.IndexOf("-", StringComparison.Ordinal);
		var stage = separator < 0 ? "STABLE" : version[(separator + 1)..].ToUpperInvariant();
		return new ProductBuildInfo(version, stage);
	}
}

public sealed record SupportSnapshot(
	string SchemaVersion,
	DateTimeOffset CapturedAtUtc,
	string ProductVersion,
	string ReleaseStage,
	string Host,
	int ProcessId,
	string LifecycleState,
	string HealthState,
	IReadOnlyDictionary<string, string> Identity,
	IReadOnlyDictionary<string, string> Status,
	IReadOnlyDictionary<string, long> Counters,
	IReadOnlyDictionary<string, string> Configuration,
	IReadOnlyList<DiagnosticEvent> Events);

public sealed class SupportSnapshotBuilder
{
	public const string CurrentSchemaVersion = "1.0";
	private readonly string _productVersion;
	private readonly string _releaseStage;
	private readonly string _host;
	private readonly int _processId;
	private readonly string _lifecycleState;
	private readonly string _healthState;
	private readonly SortedDictionary<string, string> _identity = new(StringComparer.Ordinal);
	private readonly SortedDictionary<string, string> _status = new(StringComparer.Ordinal);
	private readonly SortedDictionary<string, long> _counters = new(StringComparer.Ordinal);
	private readonly SortedDictionary<string, string> _configuration = new(StringComparer.Ordinal);
	private IReadOnlyList<DiagnosticEvent> _events = Array.Empty<DiagnosticEvent>();

	public SupportSnapshotBuilder(
		ProductBuildInfo build,
		string host,
		int processId,
		string lifecycleState,
		string healthState)
	{
		ArgumentNullException.ThrowIfNull(build);
		if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Support snapshot host is required.", nameof(host));
		if (processId < 0) throw new ArgumentOutOfRangeException(nameof(processId));
		_productVersion = build.ProductVersion;
		_releaseStage = build.ReleaseStage;
		_host = host.Trim();
		_processId = processId;
		_lifecycleState = lifecycleState?.Trim() ?? string.Empty;
		_healthState = healthState?.Trim() ?? string.Empty;
	}

	public SupportSnapshotBuilder Identity(string key, string value) { _identity[key] = DiagnosticRedactor.SanitizeValue(key, value); return this; }
	public SupportSnapshotBuilder Status(string key, string value) { _status[key] = DiagnosticRedactor.SanitizeValue(key, value); return this; }
	public SupportSnapshotBuilder Counter(string key, long value) { _counters[key] = value; return this; }
	public SupportSnapshotBuilder Configuration(string key, string value) { _configuration[key] = DiagnosticRedactor.SanitizeValue(key, value); return this; }
	public SupportSnapshotBuilder Events(IEnumerable<DiagnosticEvent> events)
	{
		ArgumentNullException.ThrowIfNull(events);
		_events = events.OrderBy(item => item.Sequence).ToArray();
		return this;
	}

	public SupportSnapshot Build(DateTimeOffset? capturedAtUtc = null) => new(
		CurrentSchemaVersion,
		(capturedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime(),
		_productVersion,
		_releaseStage,
		_host,
		_processId,
		_lifecycleState,
		_healthState,
		new SortedDictionary<string, string>(_identity, StringComparer.Ordinal),
		new SortedDictionary<string, string>(_status, StringComparer.Ordinal),
		new SortedDictionary<string, long>(_counters, StringComparer.Ordinal),
		new SortedDictionary<string, string>(_configuration, StringComparer.Ordinal),
		_events.ToArray());
}

public static class SupportSnapshotSerializer
{
	public static byte[] SerializeUtf8(SupportSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
		{
			writer.WriteStartObject();
			writer.WriteString("schemaVersion", snapshot.SchemaVersion);
			writer.WriteString("capturedAtUtc", snapshot.CapturedAtUtc.ToUniversalTime());
			writer.WriteString("productVersion", snapshot.ProductVersion);
			writer.WriteString("releaseStage", snapshot.ReleaseStage);
			writer.WriteString("host", snapshot.Host);
			writer.WriteNumber("processId", snapshot.ProcessId);
			writer.WriteString("lifecycleState", snapshot.LifecycleState);
			writer.WriteString("healthState", snapshot.HealthState);
			WriteStrings(writer, "identity", snapshot.Identity);
			WriteStrings(writer, "status", snapshot.Status);
			WriteCounters(writer, snapshot.Counters);
			WriteStrings(writer, "configuration", snapshot.Configuration);
			writer.WriteStartArray("events");
			foreach (var item in snapshot.Events.OrderBy(item => item.Sequence))
			{
				writer.WriteStartObject();
				writer.WriteNumber("sequence", item.Sequence);
				writer.WriteString("timestampUtc", item.TimestampUtc.ToUniversalTime());
				writer.WriteString("severity", item.Severity.ToString());
				writer.WriteString("category", item.Category);
				writer.WriteString("code", item.Code);
				writer.WriteString("message", item.Message);
				if (item.Failure is { } failure)
				{
					writer.WriteStartObject("failure");
					writer.WriteString("code", failure.Code);
					writer.WriteString("message", failure.Message);
					writer.WriteEndObject();
				}
				else writer.WriteNull("failure");
				WriteStrings(writer, "dimensions", item.Dimensions);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return stream.ToArray();
	}

	public static string Serialize(SupportSnapshot snapshot) => Encoding.UTF8.GetString(SerializeUtf8(snapshot));

	private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string> values)
	{
		writer.WriteStartObject(name);
		foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			writer.WriteString(pair.Key, DiagnosticRedactor.SanitizeValue(pair.Key, pair.Value));
		writer.WriteEndObject();
	}

	private static void WriteCounters(Utf8JsonWriter writer, IReadOnlyDictionary<string, long> values)
	{
		writer.WriteStartObject("counters");
		foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			writer.WriteNumber(pair.Key, pair.Value);
		writer.WriteEndObject();
	}
}
