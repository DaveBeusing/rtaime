// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using rtaime.Client;
using rtaime.Core;

namespace rtaime.Operator;

public interface ISupportBundleExporter
{
	Task<SupportBundleExportResult> ExportAsync(
		string destinationPath,
		string? lifecycleEvidencePath,
		CancellationToken cancellationToken = default);
}

public sealed record SupportBundleExportResult(
	string Path,
	int FileCount,
	long SourceBytes,
	IReadOnlyList<string> Warnings);

public sealed class SupportBundleExporter : ISupportBundleExporter
{
	public const string CurrentSchemaVersion = "1.0";
	public const long DefaultMaximumSourceBytes = 256L * 1024L * 1024L;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly IHealthSnapshotProvider _healthProvider;
	private readonly Func<DateTimeOffset> _clock;
	private readonly long _maximumSourceBytes;

	public SupportBundleExporter(
		IHealthSnapshotProvider healthProvider,
		Func<DateTimeOffset>? clock = null,
		long maximumSourceBytes = DefaultMaximumSourceBytes)
	{
		_healthProvider = healthProvider ?? throw new ArgumentNullException(nameof(healthProvider));
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
		if (maximumSourceBytes <= 0)
			throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));
		_maximumSourceBytes = maximumSourceBytes;
	}

	public async Task<SupportBundleExportResult> ExportAsync(
		string destinationPath,
		string? lifecycleEvidencePath,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(destinationPath))
			throw new ArgumentException("Support bundle destination path is required.", nameof(destinationPath));

		var fullDestinationPath = Path.GetFullPath(destinationPath);
		var destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
		if (string.IsNullOrWhiteSpace(destinationDirectory))
			throw new ArgumentException("Support bundle destination directory is required.", nameof(destinationPath));

		Directory.CreateDirectory(destinationDirectory);
		var temporaryPath = fullDestinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		var warnings = new List<string>();
		var includedFileCount = 0;
		long includedSourceBytes = 0;
		var capturedAtUtc = _clock().ToUniversalTime();
		var sessionId = ResolveOptionalIdentifier(Environment.GetEnvironmentVariable("RTAIME_LOG_SESSION_ID"));
		var logRoot = ResolveLogRoot();
		var sessionDirectory = sessionId is null ? null : Path.Combine(logRoot, sessionId);
		var health = CaptureHealth();
		var assembly = Assembly.GetEntryAssembly() ?? typeof(SupportBundleExporter).Assembly;
		var build = ProductBuildInfo.FromAssembly(assembly);

		try
		{
			await using (var output = new FileStream(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.ReadWrite,
				FileShare.None,
				64 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan))
			using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
			{
				cancellationToken.ThrowIfCancellationRequested();

				await WriteJsonEntryAsync(
					archive,
					"health/OperatorHealth.json",
					health,
					cancellationToken).ConfigureAwait(false);
				includedFileCount++;

				if (!string.IsNullOrWhiteSpace(lifecycleEvidencePath))
				includedFileCount += await TryAddSanitizedJsonFileAsync(
					archive,
					lifecycleEvidencePath,
					"startup/AppHostLifecycle.json",
					warnings,
					cancellationToken).ConfigureAwait(false);

				if (sessionDirectory is not null && Directory.Exists(sessionDirectory))
				includedFileCount += await AddSessionFilesAsync(
					archive,
					sessionDirectory,
					warnings,
					cancellationToken,
					bytes => includedSourceBytes += bytes,
					() => includedSourceBytes).ConfigureAwait(false);
				else
					warnings.Add("The active structured-log session directory was not available.");

				var manifest = new
				{
					schemaVersion = CurrentSchemaVersion,
					capturedAtUtc,
					product = new
					{
						name = "rtaime",
						version = build.ProductVersion,
						releaseStage = build.ReleaseStage
					},
					process = new
					{
						host = "Operator",
						processId = Environment.ProcessId,
						is64BitProcess = Environment.Is64BitProcess
					},
					runtime = new
					{
						framework = RuntimeInformation.FrameworkDescription,
						os = RuntimeInformation.OSDescription,
						osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
						processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
						processorCount = Environment.ProcessorCount
					},
					session = new
					{
						id = sessionId ?? "unavailable",
						logsAvailable = sessionDirectory is not null && Directory.Exists(sessionDirectory)
					},
					content = new
					{
						fileCount = includedFileCount + 1,
						sourceBytes = includedSourceBytes,
						maximumSourceBytes = _maximumSourceBytes
					},
					warnings = warnings.Select(DiagnosticRedactor.RedactText).ToArray()
				};

				await WriteJsonEntryAsync(
					archive,
					"Manifest.json",
					manifest,
					cancellationToken).ConfigureAwait(false);
				includedFileCount++;
			}

			File.Move(temporaryPath, fullDestinationPath, overwrite: true);
			return new SupportBundleExportResult(
				fullDestinationPath,
				includedFileCount,
				includedSourceBytes,
				warnings.AsReadOnly());
		}
		catch
		{
			TryDelete(temporaryPath);
			throw;
		}
	}

	private IReadOnlyList<object> CaptureHealth() =>
		_healthProvider.GetCurrent()
			.OrderBy(snapshot => snapshot.Category, StringComparer.Ordinal)
			.ThenBy(snapshot => snapshot.Id, StringComparer.Ordinal)
			.Select(snapshot => (object)new
			{
				id = DiagnosticRedactor.RedactText(snapshot.Id),
				displayName = DiagnosticRedactor.RedactText(snapshot.DisplayName),
				category = DiagnosticRedactor.RedactText(snapshot.Category),
				state = snapshot.State.ToString(),
				detail = DiagnosticRedactor.RedactText(snapshot.Detail),
				statusSinceUtc = snapshot.StatusSince.ToUniversalTime(),
				lastSuccessfulCheckUtc = snapshot.LastSuccessfulCheck?.ToUniversalTime(),
				metrics = snapshot.Metrics.Select(metric => new
				{
					label = DiagnosticRedactor.RedactText(metric.Label),
					value = DiagnosticRedactor.SanitizeValue(metric.Label, metric.Value),
					unit = metric.Unit is null ? null : DiagnosticRedactor.RedactText(metric.Unit)
				}).ToArray(),
				recoveryStatus = DiagnosticRedactor.RedactText(snapshot.RecoveryStatus),
				technicalDetail = DiagnosticRedactor.RedactText(snapshot.TechnicalDetail),
				canRecover = snapshot.CanRecover,
				recoveryActionLabel = snapshot.RecoveryActionLabel is null
					? null
					: DiagnosticRedactor.RedactText(snapshot.RecoveryActionLabel)
			})
			.ToArray();

	private async Task<int> AddSessionFilesAsync(
		ZipArchive archive,
		string sessionDirectory,
		List<string> warnings,
		CancellationToken cancellationToken,
		Action<long> addBytes,
		Func<long> currentBytes)
	{
		var files = Directory.EnumerateFiles(sessionDirectory, "*", SearchOption.TopDirectoryOnly)
			.Where(path =>
				path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
				Path.GetFileName(path).StartsWith("operator-crash-", StringComparison.OrdinalIgnoreCase))
			.Select(path => new FileInfo(path))
			.OrderByDescending(file => file.LastWriteTimeUtc)
			.ThenBy(file => file.Name, StringComparer.Ordinal)
			.ToArray();
		var included = 0;

		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			long length;
			try
			{
				length = file.Length;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				warnings.Add($"Unable to inspect diagnostic file '{file.Name}': {exception.Message}");
				continue;
			}

			if (length < 0 || currentBytes() + length > _maximumSourceBytes)
			{
				warnings.Add($"Diagnostic file '{file.Name}' was omitted because the support bundle source limit was reached.");
				continue;
			}

			var entryName = "logs/" + SanitizeEntryFileName(file.Name);
			if (await TryAddFileAsync(
				archive,
				file.FullName,
				entryName,
				length,
				warnings,
				cancellationToken).ConfigureAwait(false))
			{
				addBytes(length);
				included++;
			}
		}

		return included;
	}

	private static async Task<int> TryAddSanitizedJsonFileAsync(
		ZipArchive archive,
		string sourcePath,
		string entryName,
		List<string> warnings,
		CancellationToken cancellationToken)
	{
		try
		{
			var fullPath = Path.GetFullPath(sourcePath);
			if (!File.Exists(fullPath))
				return 0;

			await using var source = new FileStream(
				fullPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				32 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			using var document = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
			var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
			await using var target = entry.Open();
			await using var writer = new Utf8JsonWriter(target, new JsonWriterOptions { Indented = true });
			WriteSanitizedElement(writer, document.RootElement, null);
			await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
			return 1;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			warnings.Add($"Unable to include lifecycle evidence: {exception.Message}");
			return 0;
		}
	}

	private static async Task<bool> TryAddFileAsync(
		ZipArchive archive,
		string sourcePath,
		string entryName,
		long maximumBytes,
		List<string> warnings,
		CancellationToken cancellationToken)
	{
		try
		{
			await using var source = new FileStream(
				sourcePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				64 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			var bytesToCopy = Math.Min(maximumBytes, source.Length);
			var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
			await using var target = entry.Open();
			var buffer = new byte[64 * 1024];
			var remaining = bytesToCopy;
			while (remaining > 0)
			{
				var read = await source.ReadAsync(
					buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
					cancellationToken).ConfigureAwait(false);
				if (read == 0)
					break;
				await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				remaining -= read;
			}
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			warnings.Add($"Unable to include diagnostic file '{Path.GetFileName(sourcePath)}': {exception.Message}");
			return false;
		}
	}

	private static async Task WriteJsonEntryAsync(
		ZipArchive archive,
		string entryName,
		object value,
		CancellationToken cancellationToken)
	{
		var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
		await using var stream = entry.Open();
		await JsonSerializer.SerializeAsync(
			stream,
			value,
			value.GetType(),
			JsonOptions,
			cancellationToken).ConfigureAwait(false);
	}

	private static void WriteSanitizedElement(
		Utf8JsonWriter writer,
		JsonElement element,
		string? propertyName)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				writer.WriteStartObject();
				foreach (var property in element.EnumerateObject())
				{
					writer.WritePropertyName(property.Name);
					WriteSanitizedElement(writer, property.Value, property.Name);
				}
				writer.WriteEndObject();
				break;
			case JsonValueKind.Array:
				writer.WriteStartArray();
				foreach (var item in element.EnumerateArray())
					WriteSanitizedElement(writer, item, propertyName);
				writer.WriteEndArray();
				break;
			case JsonValueKind.String:
				writer.WriteStringValue(DiagnosticRedactor.SanitizeValue(
					propertyName ?? "value",
					element.GetString()));
				break;
			case JsonValueKind.Number:
				element.WriteTo(writer);
				break;
			case JsonValueKind.True:
			case JsonValueKind.False:
				writer.WriteBooleanValue(element.GetBoolean());
				break;
			case JsonValueKind.Null:
			case JsonValueKind.Undefined:
				writer.WriteNullValue();
				break;
			default:
				writer.WriteStringValue(DiagnosticRedactor.RedactText(element.GetRawText()));
				break;
		}
	}

	private static string ResolveLogRoot()
	{
		var explicitRoot = Environment.GetEnvironmentVariable("RTAIME_LOG_ROOT");
		if (!string.IsNullOrWhiteSpace(explicitRoot))
		{
			try
			{
				return Path.GetFullPath(explicitRoot);
			}
			catch
			{
			}
		}

		var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localApplicationData))
			localApplicationData = Path.GetTempPath();
		return Path.Combine(localApplicationData, "rtaime", "logs");
	}

	private static string? ResolveOptionalIdentifier(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return null;
		var trimmed = value.Trim();
		return trimmed.All(character =>
			char.IsLetterOrDigit(character) ||
			character is '.' or '_' or '-')
				? trimmed
				: null;
	}

	private static string SanitizeEntryFileName(string value) =>
		new(value.Select(character =>
			char.IsLetterOrDigit(character) ||
			character is '.' or '_' or '-'
				? character
				: '_').ToArray());

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
