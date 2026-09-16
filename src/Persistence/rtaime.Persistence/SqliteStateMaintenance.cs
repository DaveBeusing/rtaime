// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace rtaime.Persistence;

public sealed record SqliteSchemaState(string Component, int Version);

public sealed record SqliteStateSnapshot(
	string DatabasePath,
	string BackupPath,
	long BackupSize,
	string BackupSha256,
	IReadOnlyList<SqliteSchemaState> Schema,
	DateTimeOffset CreatedAtUtc);

public sealed record SqliteMigrationStep(
	string Component,
	int FromVersion,
	int ToVersion,
	string Sql);

public sealed record SqliteMigrationResult(
	bool Applied,
	string Component,
	int PreviousVersion,
	int CurrentVersion,
	SqliteStateSnapshot? Backup);

/// <summary>
/// Provides explicit backup, registered forward-only schema migration and verified recovery for SQLite-backed
/// persistent state. This type is not part of the synchronous media execution path and requires callers to own
/// process quiescence before replacement or migration operations.
/// </summary>
public sealed class SqliteStateMaintenance
{
	public async ValueTask<IReadOnlyList<SqliteSchemaState>> InspectSchemaAsync(
		string databasePath,
		CancellationToken cancellationToken = default)
	{
		var path = ResolveExistingDatabase(databasePath);
		await using var connection = CreateConnection(path, SqliteOpenMode.ReadOnly);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await VerifyIntegrityAsync(connection, cancellationToken).ConfigureAwait(false);
		return await ReadSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask<SqliteStateSnapshot> CreateBackupAsync(
		string databasePath,
		string backupPath,
		CancellationToken cancellationToken = default)
	{
		var sourcePath = ResolveExistingDatabase(databasePath);
		var targetPath = ResolvePath(backupPath);
		if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException("Backup path must differ from the source database path.", nameof(backupPath));
		if (File.Exists(targetPath))
			throw new IOException($"Backup target already exists at '{targetPath}'. Existing backup evidence is never overwritten implicitly.");

		var parent = Path.GetDirectoryName(targetPath);
		if (string.IsNullOrWhiteSpace(parent))
			throw new ArgumentException("Backup path must have a parent directory.", nameof(backupPath));
		Directory.CreateDirectory(parent);

		var sourceSchema = await InspectSchemaAsync(sourcePath, cancellationToken).ConfigureAwait(false);
		try
		{
			await using var source = CreateConnection(sourcePath, SqliteOpenMode.ReadOnly);
			await using var destination = CreateConnection(targetPath, SqliteOpenMode.ReadWriteCreate);
			await source.OpenAsync(cancellationToken).ConfigureAwait(false);
			await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			source.BackupDatabase(destination);
			cancellationToken.ThrowIfCancellationRequested();
		}
		catch
		{
			DeleteDatabaseFiles(targetPath);
			throw;
		}

		var backupSchema = await InspectSchemaAsync(targetPath, cancellationToken).ConfigureAwait(false);
		if (!SchemasEqual(sourceSchema, backupSchema))
		{
			DeleteDatabaseFiles(targetPath);
			throw new InvalidDataException("SQLite backup schema inventory differs from the source database.");
		}

		var hash = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
		var size = new FileInfo(targetPath).Length;
		return new SqliteStateSnapshot(
			sourcePath,
			targetPath,
			size,
			hash,
			backupSchema,
			DateTimeOffset.UtcNow);
	}

	public async ValueTask RestoreBackupAsync(
		SqliteStateSnapshot snapshot,
		string databasePath,
		bool acknowledgeExclusiveAccess,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		if (!acknowledgeExclusiveAccess)
			throw new InvalidOperationException("SQLite restore requires explicit acknowledgement that all processes using the database are stopped.");

		var targetPath = ResolvePath(databasePath);
		var backupPath = ResolveExistingDatabase(snapshot.BackupPath);
		var backupInfo = new FileInfo(backupPath);
		if (backupInfo.Length != snapshot.BackupSize)
			throw new InvalidDataException("SQLite backup size differs from the recorded snapshot evidence.");
		var hash = await ComputeSha256Async(backupPath, cancellationToken).ConfigureAwait(false);
		if (!string.Equals(hash, snapshot.BackupSha256, StringComparison.Ordinal))
			throw new InvalidDataException("SQLite backup SHA-256 differs from the recorded snapshot evidence.");
		var backupSchema = await InspectSchemaAsync(backupPath, cancellationToken).ConfigureAwait(false);
		if (!SchemasEqual(snapshot.Schema, backupSchema))
			throw new InvalidDataException("SQLite backup schema differs from the recorded snapshot evidence.");

		var parent = Path.GetDirectoryName(targetPath);
		if (string.IsNullOrWhiteSpace(parent))
			throw new ArgumentException("Database path must have a parent directory.", nameof(databasePath));
		Directory.CreateDirectory(parent);
		var stagePath = Path.Combine(parent, $".{Path.GetFileName(targetPath)}.restore-stage-{Guid.NewGuid():N}");
		var previousPath = Path.Combine(parent, $".{Path.GetFileName(targetPath)}.restore-previous-{Guid.NewGuid():N}");
		var previousMoved = false;
		var activated = false;

		try
		{
			File.Copy(backupPath, stagePath, overwrite: false);
			var stagedSchema = await InspectSchemaAsync(stagePath, cancellationToken).ConfigureAwait(false);
			if (!SchemasEqual(snapshot.Schema, stagedSchema))
				throw new InvalidDataException("Staged restore database schema differs from snapshot evidence.");

			if (File.Exists(targetPath))
			{
				await CheckpointWalAsync(targetPath, cancellationToken).ConfigureAwait(false);
				DeleteSidecars(targetPath);
				File.Move(targetPath, previousPath);
				previousMoved = true;
			}

			File.Move(stagePath, targetPath);
			activated = true;
			var restoredSchema = await InspectSchemaAsync(targetPath, cancellationToken).ConfigureAwait(false);
			if (!SchemasEqual(snapshot.Schema, restoredSchema))
				throw new InvalidDataException("Restored SQLite schema differs from snapshot evidence.");
			var restoredHash = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
			if (!string.Equals(restoredHash, snapshot.BackupSha256, StringComparison.Ordinal))
				throw new InvalidDataException("Restored SQLite database hash differs from snapshot evidence.");

			if (previousMoved && File.Exists(previousPath))
				DeleteDatabaseFiles(previousPath);
		}
		catch
		{
			if (activated)
				DeleteDatabaseFiles(targetPath);
			if (previousMoved && File.Exists(previousPath))
				File.Move(previousPath, targetPath);
			throw;
		}
		finally
		{
			DeleteDatabaseFiles(stagePath);
			if (File.Exists(previousPath))
				DeleteDatabaseFiles(previousPath);
		}
	}

	public async ValueTask<SqliteMigrationResult> MigrateAsync(
		string databasePath,
		string component,
		int targetVersion,
		IReadOnlyCollection<SqliteMigrationStep> migrations,
		string backupPath,
		bool acknowledgeExclusiveAccess,
		Func<string, CancellationToken, ValueTask>? postMigrationVerifier = null,
		CancellationToken cancellationToken = default)
	{
		if (!acknowledgeExclusiveAccess)
			throw new InvalidOperationException("SQLite migration requires explicit acknowledgement that all processes using the database are stopped.");
		if (string.IsNullOrWhiteSpace(component))
			throw new ArgumentException("Schema component is required.", nameof(component));
		if (targetVersion <= 0)
			throw new ArgumentOutOfRangeException(nameof(targetVersion));
		ArgumentNullException.ThrowIfNull(migrations);

		var path = ResolveExistingDatabase(databasePath);
		var initialSchema = await InspectSchemaAsync(path, cancellationToken).ConfigureAwait(false);
		var current = GetComponentVersion(initialSchema, component);
		if (current == targetVersion)
			return new SqliteMigrationResult(false, component, current, current, null);
		if (current > targetVersion)
			throw new NotSupportedException($"SQLite schema downgrade for component '{component}' from {current} to {targetVersion} is not supported.");

		var chain = BuildMigrationChain(component, current, targetVersion, migrations);
		SqliteStateSnapshot? snapshot = null;
		try
		{
			snapshot = await CreateBackupAsync(path, backupPath, cancellationToken).ConfigureAwait(false);
			await CheckpointWalAsync(path, cancellationToken).ConfigureAwait(false);
			await using (var connection = CreateConnection(path, SqliteOpenMode.ReadWrite))
			{
				await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
				await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
				foreach (var step in chain)
				{
					await using var migration = connection.CreateCommand();
					migration.Transaction = (SqliteTransaction)transaction;
					migration.CommandText = step.Sql;
					await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

					await using var version = connection.CreateCommand();
					version.Transaction = (SqliteTransaction)transaction;
					version.CommandText = "UPDATE schema_metadata SET schema_version = @to WHERE component = @component AND schema_version = @from;";
					version.Parameters.AddWithValue("@to", step.ToVersion);
					version.Parameters.AddWithValue("@component", step.Component);
					version.Parameters.AddWithValue("@from", step.FromVersion);
					if (await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
						throw new InvalidDataException($"Schema metadata changed unexpectedly while applying '{step.Component}' {step.FromVersion}->{step.ToVersion}.");
				}
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			}

			var migratedSchema = await InspectSchemaAsync(path, cancellationToken).ConfigureAwait(false);
			var migratedVersion = GetComponentVersion(migratedSchema, component);
			if (migratedVersion != targetVersion)
				throw new InvalidDataException($"SQLite migration completed with schema version {migratedVersion}, expected {targetVersion}.");
			if (postMigrationVerifier is not null)
				await postMigrationVerifier(path, cancellationToken).ConfigureAwait(false);

			return new SqliteMigrationResult(true, component, current, migratedVersion, snapshot);
		}
		catch (Exception migrationFailure) when (snapshot is not null)
		{
			try
			{
				await RestoreBackupAsync(snapshot, path, acknowledgeExclusiveAccess: true, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception restoreFailure)
			{
				throw new AggregateException("SQLite migration failed and snapshot restoration also failed.", migrationFailure, restoreFailure);
			}
			throw;
		}
	}

	private static IReadOnlyList<SqliteMigrationStep> BuildMigrationChain(
		string component,
		int currentVersion,
		int targetVersion,
		IReadOnlyCollection<SqliteMigrationStep> migrations)
	{
		var chain = new List<SqliteMigrationStep>();
		var version = currentVersion;
		while (version < targetVersion)
		{
			var matches = migrations.Where(step =>
				string.Equals(step.Component, component, StringComparison.Ordinal) &&
				step.FromVersion == version).ToArray();
			if (matches.Length != 1)
				throw new NotSupportedException($"Exactly one registered migration is required for component '{component}' from schema version {version}; found {matches.Length}.");
			var step = matches[0];
			if (step.ToVersion != step.FromVersion + 1 || step.ToVersion > targetVersion)
				throw new NotSupportedException($"Migration '{component}' {step.FromVersion}->{step.ToVersion} is not a single forward schema step.");
			if (string.IsNullOrWhiteSpace(step.Sql))
				throw new InvalidDataException($"Migration '{component}' {step.FromVersion}->{step.ToVersion} has no SQL payload.");
			chain.Add(step);
			version = step.ToVersion;
		}
		return chain.AsReadOnly();
	}

	private static int GetComponentVersion(IReadOnlyList<SqliteSchemaState> schema, string component)
	{
		var matches = schema.Where(entry => string.Equals(entry.Component, component, StringComparison.Ordinal)).ToArray();
		if (matches.Length != 1)
			throw new NotSupportedException($"Expected exactly one schema_metadata row for component '{component}', found {matches.Length}.");
		return matches[0].Version;
	}

	private static async ValueTask<IReadOnlyList<SqliteSchemaState>> ReadSchemaAsync(
		SqliteConnection connection,
		CancellationToken cancellationToken)
	{
		await using (var table = connection.CreateCommand())
		{
			table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_metadata';";
			var count = Convert.ToInt32(await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
			if (count != 1)
				throw new InvalidDataException("SQLite database does not contain exactly one schema_metadata table.");
		}

		var result = new List<SqliteSchemaState>();
		var names = new HashSet<string>(StringComparer.Ordinal);
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT component, schema_version FROM schema_metadata ORDER BY component;";
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var component = reader.GetString(0);
			var version = reader.GetInt32(1);
			if (string.IsNullOrWhiteSpace(component) || version <= 0 || !names.Add(component))
				throw new InvalidDataException("SQLite schema_metadata contains an invalid or duplicate component entry.");
			result.Add(new SqliteSchemaState(component, version));
		}
		if (result.Count == 0)
			throw new InvalidDataException("SQLite schema_metadata is empty.");
		return result.AsReadOnly();
	}

	private static async ValueTask VerifyIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA integrity_check;";
		var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
		if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"SQLite integrity_check returned '{result}'.");
	}

	private static async ValueTask CheckpointWalAsync(string databasePath, CancellationToken cancellationToken)
	{
		if (!File.Exists(databasePath)) return;
		await using var connection = CreateConnection(databasePath, SqliteOpenMode.ReadWrite);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA busy_timeout=2000; PRAGMA wal_checkpoint(TRUNCATE);";
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode)
	{
		var builder = new SqliteConnectionStringBuilder
		{
			DataSource = path,
			Mode = mode,
			Cache = SqliteCacheMode.Private,
			Pooling = false
		};
		return new SqliteConnection(builder.ToString());
	}

	private static bool SchemasEqual(IReadOnlyList<SqliteSchemaState> left, IReadOnlyList<SqliteSchemaState> right) =>
		left.Count == right.Count && left.Zip(right).All(pair => pair.First == pair.Second);

	private static string ResolveExistingDatabase(string path)
	{
		var fullPath = ResolvePath(path);
		if (!File.Exists(fullPath))
			throw new FileNotFoundException("SQLite database was not found.", fullPath);
		return fullPath;
	}

	private static string ResolvePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("SQLite database path is required.", nameof(path));
		return Path.GetFullPath(path);
	}

	private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		using var sha = SHA256.Create();
		var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	private static void DeleteSidecars(string path)
	{
		foreach (var sidecar in new[] { $"{path}-wal", $"{path}-shm" })
		{
			if (File.Exists(sidecar)) File.Delete(sidecar);
		}
	}

	private static void DeleteDatabaseFiles(string path)
	{
		if (File.Exists(path)) File.Delete(path);
		DeleteSidecars(path);
	}
}
