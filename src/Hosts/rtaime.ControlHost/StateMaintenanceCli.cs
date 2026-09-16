// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using System.Text.Json.Serialization;
using rtaime.Persistence;

namespace rtaime.ControlHost;

internal static class StateMaintenanceCli
{
	private const int Success = 0;
	private const int UsageError = 64;
	private const int OperationFailure = 70;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public static bool IsRequested(string[] args) =>
		args.Length > 0 && string.Equals(args[0], "state-maintenance", StringComparison.OrdinalIgnoreCase);

	public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
	{
		if (args.Length == 0)
		{
			WriteUsage("A state-maintenance command is required.");
			return UsageError;
		}

		try
		{
			var command = args[0].Trim().ToLowerInvariant();
			var options = ParseOptions(args[1..]);
			return command switch
			{
				"inspect" => await InspectAsync(options, cancellationToken).ConfigureAwait(false),
				"backup" => await BackupAsync(options, cancellationToken).ConfigureAwait(false),
				"migrate" => await MigrateAsync(options, cancellationToken).ConfigureAwait(false),
				"restore" => await RestoreAsync(options, cancellationToken).ConfigureAwait(false),
				_ => throw new ArgumentException($"Unknown state-maintenance command '{args[0]}'.")
			};
		}
		catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or FormatException or JsonException)
		{
			WriteUsage(exception.Message);
			return UsageError;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"host=ControlHost mode=state-maintenance outcome=failed detail=\"{Escape(exception.Message)}\"");
			return OperationFailure;
		}
	}

	private static async Task<int> InspectAsync(
		IReadOnlyDictionary<string, string?> options,
		CancellationToken cancellationToken)
	{
		var database = RequireOption(options, "database");
		var output = RequireOption(options, "output");
		var maintenance = new SqliteStateMaintenance();
		var schema = await maintenance.InspectSchemaAsync(database, cancellationToken).ConfigureAwait(false);
		var document = new StateInspectionDocument(
			"1.0",
			Path.GetFullPath(database),
			schema.Select(static value => new SchemaDocument(value.Component, value.Version)).ToArray(),
			DateTimeOffset.UtcNow);
		WriteJson(output, document);
		Console.WriteLine($"host=ControlHost mode=state-maintenance command=inspect outcome=pass database=\"{Escape(document.DatabasePath)}\"");
		return Success;
	}

	private static async Task<int> BackupAsync(
		IReadOnlyDictionary<string, string?> options,
		CancellationToken cancellationToken)
	{
		var database = RequireOption(options, "database");
		var backup = RequireOption(options, "backup");
		var output = RequireOption(options, "output");
		var maintenance = new SqliteStateMaintenance();
		var snapshot = await maintenance.CreateBackupAsync(database, backup, cancellationToken).ConfigureAwait(false);
		var document = SnapshotDocument.From(snapshot);
		WriteJson(output, document);
		Console.WriteLine($"host=ControlHost mode=state-maintenance command=backup outcome=pass database=\"{Escape(document.DatabasePath)}\" backup=\"{Escape(document.BackupPath)}\"");
		return Success;
	}

	private static async Task<int> MigrateAsync(
		IReadOnlyDictionary<string, string?> options,
		CancellationToken cancellationToken)
	{
		var planPath = RequireOption(options, "plan");
		var output = RequireOption(options, "output");
		RequireAcknowledgement(options);
		var plan = ReadJson<StateMigrationPlanDocument>(planPath);
		if (!string.Equals(plan.SchemaVersion, "1.0", StringComparison.Ordinal))
			throw new ArgumentException($"Unsupported state migration plan schema version '{plan.SchemaVersion}'.");
		if (plan.TargetVersion <= 0)
			throw new ArgumentException("State migration targetVersion must be greater than zero.");
		if (string.IsNullOrWhiteSpace(plan.Component))
			throw new ArgumentException("State migration component is required.");
		if (plan.Migrations is null)
			throw new ArgumentException("State migration migrations collection is required.");

		var migrations = plan.Migrations.Select(step => new SqliteMigrationStep(
			step.Component,
			step.FromVersion,
			step.ToVersion,
			step.Sql)).ToArray();
		var maintenance = new SqliteStateMaintenance();
		var result = await maintenance.MigrateAsync(
			plan.DatabasePath,
			plan.Component,
			plan.TargetVersion,
			migrations,
			plan.BackupPath,
			acknowledgeExclusiveAccess: true,
			postMigrationVerifier: null,
			cancellationToken).ConfigureAwait(false);
		var receipt = new StateMigrationReceiptDocument(
			"1.0",
			result.Applied ? "PASS" : "NOT_APPLICABLE",
			Path.GetFullPath(plan.DatabasePath),
			result.Component,
			result.PreviousVersion,
			result.CurrentVersion,
			result.Backup is null ? null : SnapshotDocument.From(result.Backup),
			DateTimeOffset.UtcNow);
		WriteJson(output, receipt);
		Console.WriteLine($"host=ControlHost mode=state-maintenance command=migrate outcome={receipt.Status.ToLowerInvariant()} component={receipt.Component} from={receipt.PreviousVersion} to={receipt.CurrentVersion}");
		return Success;
	}

	private static async Task<int> RestoreAsync(
		IReadOnlyDictionary<string, string?> options,
		CancellationToken cancellationToken)
	{
		var snapshotPath = RequireOption(options, "snapshot");
		RequireAcknowledgement(options);
		var document = ReadJson<SnapshotDocument>(snapshotPath);
		if (!string.Equals(document.SchemaVersion, "1.0", StringComparison.Ordinal))
			throw new ArgumentException($"Unsupported state snapshot schema version '{document.SchemaVersion}'.");
		var database = options.TryGetValue("database", out var databaseOverride) && !string.IsNullOrWhiteSpace(databaseOverride)
			? databaseOverride!
			: document.DatabasePath;
		var snapshot = document.ToSnapshot();
		var maintenance = new SqliteStateMaintenance();
		await maintenance.RestoreBackupAsync(snapshot, database, acknowledgeExclusiveAccess: true, cancellationToken).ConfigureAwait(false);
		Console.WriteLine($"host=ControlHost mode=state-maintenance command=restore outcome=pass database=\"{Escape(Path.GetFullPath(database))}\"");
		return Success;
	}

	private static Dictionary<string, string?> ParseOptions(string[] args)
	{
		var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		foreach (var argument in args)
		{
			if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length <= 2)
				throw new ArgumentException($"Invalid state-maintenance option '{argument}'. Options must use --name=value or --flag syntax.");
			var payload = argument[2..];
			var separator = payload.IndexOf('=');
			var name = separator < 0 ? payload : payload[..separator];
			var value = separator < 0 ? null : payload[(separator + 1)..];
			if (string.IsNullOrWhiteSpace(name) || result.ContainsKey(name))
				throw new ArgumentException($"Duplicate or invalid state-maintenance option '{argument}'.");
			result.Add(name, value);
		}
		return result;
	}

	private static string RequireOption(IReadOnlyDictionary<string, string?> options, string name)
	{
		if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
			throw new ArgumentException($"Required state-maintenance option '--{name}=...' is missing.");
		return value;
	}

	private static void RequireAcknowledgement(IReadOnlyDictionary<string, string?> options)
	{
		if (!options.ContainsKey("acknowledge-exclusive-access"))
			throw new ArgumentException("State mutation requires '--acknowledge-exclusive-access'.");
	}

	private static T ReadJson<T>(string path)
	{
		var fullPath = Path.GetFullPath(path);
		if (!File.Exists(fullPath))
			throw new FileNotFoundException("State-maintenance JSON input was not found.", fullPath);
		return JsonSerializer.Deserialize<T>(File.ReadAllText(fullPath), JsonOptions)
			?? throw new JsonException($"State-maintenance JSON input '{fullPath}' was empty or invalid.");
	}

	private static void WriteJson<T>(string path, T document)
	{
		var fullPath = Path.GetFullPath(path);
		var parent = Path.GetDirectoryName(fullPath);
		if (string.IsNullOrWhiteSpace(parent))
			throw new ArgumentException("State-maintenance output path must have a parent directory.", nameof(path));
		Directory.CreateDirectory(parent);
		File.WriteAllText(fullPath, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine, new System.Text.UTF8Encoding(false));
	}

	private static void WriteUsage(string detail)
	{
		Console.Error.WriteLine($"host=ControlHost mode=state-maintenance outcome=usage-error detail=\"{Escape(detail)}\"");
		Console.Error.WriteLine("usage: rtaime.ControlHost state-maintenance inspect --database=<path> --output=<json>");
		Console.Error.WriteLine("usage: rtaime.ControlHost state-maintenance backup --database=<path> --backup=<path> --output=<snapshot-json>");
		Console.Error.WriteLine("usage: rtaime.ControlHost state-maintenance migrate --plan=<json> --output=<receipt-json> --acknowledge-exclusive-access");
		Console.Error.WriteLine("usage: rtaime.ControlHost state-maintenance restore --snapshot=<json> [--database=<path>] --acknowledge-exclusive-access");
	}

	private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

	private sealed record SchemaDocument(string Component, int Version);

	private sealed record StateInspectionDocument(
		string SchemaVersion,
		string DatabasePath,
		IReadOnlyList<SchemaDocument> Schema,
		DateTimeOffset InspectedAtUtc);

	private sealed record SnapshotDocument(
		string SchemaVersion,
		string DatabasePath,
		string BackupPath,
		long BackupSize,
		string BackupSha256,
		IReadOnlyList<SchemaDocument> Schema,
		DateTimeOffset CreatedAtUtc)
	{
		public static SnapshotDocument From(SqliteStateSnapshot snapshot) => new(
			"1.0",
			snapshot.DatabasePath,
			snapshot.BackupPath,
			snapshot.BackupSize,
			snapshot.BackupSha256,
			snapshot.Schema.Select(static value => new SchemaDocument(value.Component, value.Version)).ToArray(),
			snapshot.CreatedAtUtc);

		public SqliteStateSnapshot ToSnapshot() => new(
			DatabasePath,
			BackupPath,
			BackupSize,
			BackupSha256,
			Schema.Select(static value => new SqliteSchemaState(value.Component, value.Version)).ToArray(),
			CreatedAtUtc);
	}

	private sealed record StateMigrationPlanDocument(
		string SchemaVersion,
		string DatabasePath,
		string BackupPath,
		string Component,
		int TargetVersion,
		IReadOnlyList<StateMigrationStepDocument> Migrations);

	private sealed record StateMigrationStepDocument(
		string Component,
		int FromVersion,
		int ToVersion,
		string Sql);

	private sealed record StateMigrationReceiptDocument(
		string SchemaVersion,
		string Status,
		string DatabasePath,
		string Component,
		int PreviousVersion,
		int CurrentVersion,
		SnapshotDocument? MigrationBackup,
		DateTimeOffset CompletedAtUtc);
}
