// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace rtaime.Core;

public sealed class HostLog : IDisposable
{
	public const string CurrentSchemaVersion = "1.0";
	public const long DefaultMaxFileBytes = 16L * 1024L * 1024L;
	public const int DefaultRetentionDays = 14;

	private static readonly TimeSpan FileRetryInterval = TimeSpan.FromSeconds(30);
	private static readonly Regex SafeIdentifierPattern = new(
		"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly object _gate = new();
	private readonly string _host;
	private readonly string _sessionId;
	private readonly string? _instanceId;
	private readonly string _logRoot;
	private readonly string _sessionDirectory;
	private readonly DiagnosticSeverity _minimumSeverity;
	private readonly long _maxFileBytes;
	private readonly int _retentionDays;
	private readonly int _processId;
	private readonly DateTimeOffset _startedAtUtc;
	private StreamWriter? _writer;
	private string? _currentFilePath;
	private DateTimeOffset _nextFileRetryUtc;
	private ulong _sequence;
	private int _segment;
	private bool _disposed;

	private HostLog(
		string host,
		string sessionId,
		string? instanceId,
		string logRoot,
		DiagnosticSeverity minimumSeverity,
		long maxFileBytes,
		int retentionDays)
	{
		_host = host;
		_sessionId = sessionId;
		_instanceId = instanceId;
		_logRoot = logRoot;
		_sessionDirectory = Path.Combine(logRoot, sessionId);
		_minimumSeverity = minimumSeverity;
		_maxFileBytes = maxFileBytes;
		_retentionDays = retentionDays;
		_processId = Environment.ProcessId;
		_startedAtUtc = DateTimeOffset.UtcNow;

		TryPruneExpiredSessions();
		TryOpenWriter();

		var build = ProductBuildInfo.FromAssembly(Assembly.GetEntryAssembly() ?? typeof(HostLog).Assembly);
		Information(
			"process",
			"process.start",
			$"{_host} process started.",
			dimensions: new Dictionary<string, string>
			{
				["productVersion"] = build.ProductVersion,
				["releaseStage"] = build.ReleaseStage,
				["runtime"] = Environment.Version.ToString(),
				["os"] = Environment.OSVersion.VersionString
			});
	}

	public string Host => _host;
	public string SessionId => _sessionId;
	public string? InstanceId => _instanceId;
	public string LogRoot => _logRoot;
	public string SessionDirectory => _sessionDirectory;
	public string? CurrentFilePath { get { lock (_gate) return _currentFilePath; } }

	public static HostLog Open(string host, IReadOnlyList<string>? args = null, bool publishEnvironment = true)
	{
		if (string.IsNullOrWhiteSpace(host))
			throw new ArgumentException("Host log name is required.", nameof(host));

		args ??= Array.Empty<string>();
		var normalizedHost = NormalizeIdentifier(host, "host");
		var logRoot = ResolveLogRoot(args);
		var sessionId = ResolveSessionId(args);
		var instanceId = ResolveOptionalIdentifier(
			ResolveArgumentOrEnvironment(args, "instance-id", "RTAIME_INSTANCE_ID"));
		var minimumSeverity = ResolveMinimumSeverity(
			ResolveArgumentOrEnvironment(args, "log-level", "RTAIME_LOG_LEVEL"));
		var maxFileBytes = ResolveMaxFileBytes(
			ResolveArgumentOrEnvironment(args, "log-max-file-mb", "RTAIME_LOG_MAX_FILE_MB"));
		var retentionDays = ResolveRetentionDays(
			ResolveArgumentOrEnvironment(args, "log-retention-days", "RTAIME_LOG_RETENTION_DAYS"));

		if (publishEnvironment)
		{
			try
			{
				Environment.SetEnvironmentVariable("RTAIME_LOG_ROOT", logRoot);
				Environment.SetEnvironmentVariable("RTAIME_LOG_SESSION_ID", sessionId);
			}
			catch
			{
				// Environment propagation is best-effort; file logging remains process-local.
			}
		}

		return new HostLog(
			normalizedHost,
			sessionId,
			instanceId,
			logRoot,
			minimumSeverity,
			maxFileBytes,
			retentionDays);
	}

	public IDisposable AttachProcessFailureHandlers() => new FailureSubscription(this);

	public void Trace(
		string category,
		string code,
		string message,
		IReadOnlyDictionary<string, string>? dimensions = null) =>
		Write(DiagnosticSeverity.Trace, category, code, message, null, dimensions);

	public void Information(
		string category,
		string code,
		string message,
		IReadOnlyDictionary<string, string>? dimensions = null) =>
		Write(DiagnosticSeverity.Information, category, code, message, null, dimensions);

	public void Warning(
		string category,
		string code,
		string message,
		Exception? exception = null,
		IReadOnlyDictionary<string, string>? dimensions = null) =>
		Write(DiagnosticSeverity.Warning, category, code, message, exception, dimensions);

	public void Error(
		string category,
		string code,
		string message,
		Exception? exception = null,
		IReadOnlyDictionary<string, string>? dimensions = null) =>
		Write(DiagnosticSeverity.Error, category, code, message, exception, dimensions);

	public void Critical(
		string category,
		string code,
		string message,
		Exception? exception = null,
		IReadOnlyDictionary<string, string>? dimensions = null) =>
		Write(DiagnosticSeverity.Critical, category, code, message, exception, dimensions);

	public void Write(
		DiagnosticSeverity severity,
		string category,
		string code,
		string message,
		Exception? exception = null,
		IReadOnlyDictionary<string, string>? dimensions = null)
	{
		if (severity < _minimumSeverity)
			return;
		if (!Enum.IsDefined(typeof(DiagnosticSeverity), severity))
			throw new ArgumentOutOfRangeException(nameof(severity));
		if (string.IsNullOrWhiteSpace(category))
			throw new ArgumentException("Host log category is required.", nameof(category));
		if (string.IsNullOrWhiteSpace(code))
			throw new ArgumentException("Host log code is required.", nameof(code));
		if (string.IsNullOrWhiteSpace(message))
			throw new ArgumentException("Host log message is required.", nameof(message));

		lock (_gate)
		{
			if (_disposed)
				return;

			var timestampUtc = DateTimeOffset.UtcNow;
			var record = new HostLogRecord(
				CurrentSchemaVersion,
				timestampUtc,
				_sequence++,
				severity.ToString(),
				_host,
				_processId,
				Environment.CurrentManagedThreadId,
				_sessionId,
				_instanceId,
				DiagnosticRedactor.RedactText(category.Trim()),
				DiagnosticRedactor.RedactText(code.Trim()),
				DiagnosticRedactor.RedactText(message.Trim()),
				ToException(exception),
				DiagnosticRedactor.Sanitize(dimensions));

			var line = JsonSerializer.Serialize(record, JsonOptions);
			TryWriteLine(line, severity, timestampUtc);
		}
	}

	public void Flush()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			try { _writer?.Flush(); } catch (IOException) { }
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			try { _writer?.Flush(); } catch (IOException) { }
			try { _writer?.Dispose(); } catch (IOException) { }
			_writer = null;
		}
	}

	private void TryWriteLine(string line, DiagnosticSeverity severity, DateTimeOffset timestampUtc)
	{
		try
		{
			EnsureWriter(timestampUtc, Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length);
			if (_writer is null)
				return;
			_writer.WriteLine(line);
			if (severity >= DiagnosticSeverity.Error)
				_writer.Flush();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			DisableWriter(timestampUtc);
			TryWriteFallback(exception);
		}
	}

	private void EnsureWriter(DateTimeOffset timestampUtc, int pendingBytes)
	{
		if (_writer is null)
		{
			if (timestampUtc >= _nextFileRetryUtc)
				TryOpenWriter();
			return;
		}

		try
		{
			if (_writer.BaseStream.Length + pendingBytes <= _maxFileBytes)
				return;
		}
		catch (ObjectDisposedException)
		{
			DisableWriter(timestampUtc);
			return;
		}

		try { _writer.Flush(); } catch (IOException) { }
		try { _writer.Dispose(); } catch (IOException) { }
		_writer = null;
		_currentFilePath = null;
		_segment++;
		TryOpenWriter();
	}

	private void TryOpenWriter()
	{
		try
		{
			Directory.CreateDirectory(_sessionDirectory);
			var hostToken = NormalizeFileToken(_host);
			var fileName = $"rtaime-{hostToken}-{_processId}-{_startedAtUtc:yyyyMMdd-HHmmssfff}-{_segment:000}.jsonl";
			var path = Path.Combine(_sessionDirectory, fileName);
			var stream = new FileStream(
				path,
				FileMode.Append,
				FileAccess.Write,
				FileShare.ReadWrite,
				16 * 1024,
				FileOptions.SequentialScan);
			_writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024)
			{
				AutoFlush = true
			};
			_currentFilePath = path;
			_nextFileRetryUtc = DateTimeOffset.MinValue;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			_writer = null;
			_currentFilePath = null;
			_nextFileRetryUtc = DateTimeOffset.UtcNow + FileRetryInterval;
			TryWriteFallback(exception);
		}
	}

	private void DisableWriter(DateTimeOffset timestampUtc)
	{
		try { _writer?.Dispose(); } catch (IOException) { }
		_writer = null;
		_currentFilePath = null;
		_nextFileRetryUtc = timestampUtc + FileRetryInterval;
	}

	private void TryPruneExpiredSessions()
	{
		try
		{
			if (!Directory.Exists(_logRoot))
				return;

			var cutoffUtc = DateTime.UtcNow.AddDays(-_retentionDays);
			foreach (var directory in Directory.EnumerateDirectories(_logRoot))
			{
				if (string.Equals(
					Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
					Path.GetFullPath(_sessionDirectory).TrimEnd(Path.DirectorySeparatorChar),
					StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				try
				{
					var newestWriteUtc = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly)
						.Select(File.GetLastWriteTimeUtc)
						.DefaultIfEmpty(Directory.GetLastWriteTimeUtc(directory))
						.Max();
					if (newestWriteUtc < cutoffUtc)
						Directory.Delete(directory, recursive: true);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static HostLogException? ToException(Exception? exception)
	{
		if (exception is null)
			return null;

		return new HostLogException(
			exception.GetType().FullName ?? exception.GetType().Name,
			DiagnosticRedactor.RedactText(exception.Message),
			DiagnosticRedactor.RedactExceptionDetail(exception.ToString()));
	}


	private static string ResolveLogRoot(IReadOnlyList<string> args)
	{
		var explicitRoot = ResolveArgumentOrEnvironment(args, "log-root", "RTAIME_LOG_ROOT");
		if (!string.IsNullOrWhiteSpace(explicitRoot))
		{
			try { return Path.GetFullPath(explicitRoot); }
			catch { }
		}

		var serviceMode = args.Any(argument =>
			string.Equals(argument, "--windows-service", StringComparison.OrdinalIgnoreCase));
		var ownership = ResolveArgumentOrEnvironment(args, "ownership", "RTAIME_LIFECYCLE_OWNERSHIP");
		if (serviceMode || string.Equals(ownership, "ExternalManaged", StringComparison.OrdinalIgnoreCase))
		{
			var stateRoot = ResolveArgumentOrEnvironment(args, "state-root", "RTAIME_STATE_ROOT");
			if (string.IsNullOrWhiteSpace(stateRoot))
				stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "rtaime");
			try { return Path.Combine(Path.GetFullPath(stateRoot), "logs"); } catch { }
		}

		var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localApplicationData))
			localApplicationData = Path.GetTempPath();

		try { return Path.Combine(Path.GetFullPath(localApplicationData), "rtaime", "logs"); }
		catch { return Path.Combine(Path.GetTempPath(), "rtaime", "logs"); }
	}

	private static string ResolveSessionId(IReadOnlyList<string> args)
	{
		var supplied = ResolveArgumentOrEnvironment(args, "log-session-id", "RTAIME_LOG_SESSION_ID");
		return ResolveOptionalIdentifier(supplied) ?? Guid.NewGuid().ToString("N");
	}

	private static DiagnosticSeverity ResolveMinimumSeverity(string? value) =>
		Enum.TryParse<DiagnosticSeverity>(value, ignoreCase: true, out var severity) &&
		Enum.IsDefined(typeof(DiagnosticSeverity), severity)
			? severity
			: DiagnosticSeverity.Information;

	private static long ResolveMaxFileBytes(string? value)
	{
		if (!int.TryParse(value, out var megabytes))
			return DefaultMaxFileBytes;
		megabytes = Math.Clamp(megabytes, 1, 256);
		return megabytes * 1024L * 1024L;
	}

	private static int ResolveRetentionDays(string? value)
	{
		if (!int.TryParse(value, out var days))
			return DefaultRetentionDays;
		return Math.Clamp(days, 1, 90);
	}

	private static string? ResolveArgumentOrEnvironment(
		IReadOnlyList<string> args,
		string argumentName,
		string environmentName)
	{
		var prefix = $"--{argumentName}=";
		var argument = args.LastOrDefault(candidate =>
			candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		if (argument is not null)
			return argument[prefix.Length..].Trim().Trim('"');

		return Environment.GetEnvironmentVariable(environmentName);
	}

	private static string NormalizeIdentifier(string value, string parameterName)
	{
		var trimmed = value.Trim();
		if (!SafeIdentifierPattern.IsMatch(trimmed))
			throw new ArgumentException($"Invalid {parameterName} identifier '{trimmed}'.", parameterName);
		return trimmed;
	}

	private static string? ResolveOptionalIdentifier(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;
		var trimmed = value.Trim();
		return SafeIdentifierPattern.IsMatch(trimmed) ? trimmed : null;
	}

	private static string NormalizeFileToken(string value) =>
		new(value.Select(character =>
			char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
				? char.ToLowerInvariant(character)
				: '-').ToArray());

	private void TryWriteFallback(Exception exception)
	{
		try
		{
			Console.Error.WriteLine(
				$"host={_host} logging=file-unavailable detail=\"{DiagnosticRedactor.RedactText(exception.Message)}\"");
		}
		catch
		{
		}
	}

	private sealed record HostLogRecord(
		string SchemaVersion,
		DateTimeOffset TimestampUtc,
		ulong Sequence,
		string Level,
		string Host,
		int ProcessId,
		int ThreadId,
		string SessionId,
		string? InstanceId,
		string Category,
		string Code,
		string Message,
		HostLogException? Exception,
		IReadOnlyDictionary<string, string> Dimensions);

	private sealed record HostLogException(
		string Type,
		string Message,
		string Detail);

	private sealed class FailureSubscription : IDisposable
	{
		private readonly HostLog _log;
		private bool _disposed;

		public FailureSubscription(HostLog log)
		{
			_log = log;
			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
			TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
			AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
			TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
			AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
		}

		private void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
		{
			var exception = eventArgs.ExceptionObject as Exception;
			_log.Critical(
				"process",
				"process.unhandled-exception",
				eventArgs.IsTerminating
					? "Unhandled process exception is terminating the host."
					: "Unhandled process exception observed.",
				exception);
			_log.Flush();
		}

		private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
		{
			_log.Error(
				"task",
				"task.unobserved-exception",
				"Unobserved task exception reached the finalizer boundary.",
				eventArgs.Exception);
			_log.Flush();
		}

		private void OnProcessExit(object? sender, EventArgs eventArgs)
		{
			_log.Information("process", "process.exit-signal", "Process exit signal observed.");
			_log.Flush();
		}
	}
}
