// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using rtaime.Core;

namespace rtaime.Persistence;

public sealed record ManagementDocument(
	string Area,
	string Key,
	ulong Version,
	UtcTimestamp UpdatedAt,
	string Json,
	string ChecksumSha256);

public sealed record ManagementWriteResult(
	bool Written,
	ManagementDocument? Document,
	Failure? Failure);

public sealed record PersistenceIntegrityReport(
	bool Healthy,
	int Documents,
	int Checkpoints,
	string Detail);

/// <summary>
/// Transactional SQLite management/configuration persistence. This store is intentionally separate from the
/// production journal and must never be placed on the synchronous media execution path.
/// </summary>
public sealed class SqliteManagementStore : IProductionCheckpointStore, IAsyncDisposable
{
	private const int SchemaVersion = 1;
	private readonly string _path;
	private readonly SemaphoreSlim _schemaGate = new(1, 1);
	private volatile bool _initialized;
	private bool _disposed;

	public SqliteManagementStore(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Management database path is required.", nameof(path));
		_path = System.IO.Path.GetFullPath(path);
	}

	public string Path => _path;

	public async ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

	public async ValueTask<ManagementDocument?> GetDocumentAsync(
		string area,
		string key,
		CancellationToken cancellationToken = default)
	{
		ValidateDocumentKey(area, key);
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT version, updated_utc, payload_json, checksum_sha256 FROM management_documents WHERE area = @area AND document_key = @key;";
		command.Parameters.AddWithValue("@area", area.Trim());
		command.Parameters.AddWithValue("@key", key.Trim());
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			return null;

		var document = new ManagementDocument(
			area.Trim(),
			key.Trim(),
			ParseVersion(reader.GetString(0)),
			UtcTimestamp.Parse(reader.GetString(1)),
			reader.GetString(2),
			reader.GetString(3));
		EnsureChecksum(document.Json, document.ChecksumSha256, "Management document checksum mismatch.");
		return document;
	}

	public async ValueTask<ManagementWriteResult> PutDocumentAsync(
		string area,
		string key,
		string json,
		ulong? expectedVersion = null,
		CancellationToken cancellationToken = default)
	{
		ValidateDocumentKey(area, key);
		if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Management document JSON is required.", nameof(json));
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
		using var transaction = connection.BeginTransaction();

		ulong currentVersion = 0;
		await using (var current = connection.CreateCommand())
		{
			current.Transaction = transaction;
			current.CommandText = "SELECT version FROM management_documents WHERE area = @area AND document_key = @key;";
			current.Parameters.AddWithValue("@area", area.Trim());
			current.Parameters.AddWithValue("@key", key.Trim());
			var value = await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (value is string text)
				currentVersion = ParseVersion(text);
		}

		if (expectedVersion.HasValue && expectedVersion.Value != currentVersion)
		{
			transaction.Rollback();
			return new ManagementWriteResult(
				false,
				null,
				new Failure("persistence.version_conflict", $"Expected management document version {expectedVersion.Value}, current version is {currentVersion}."));
		}

		var nextVersion = checked(currentVersion + 1);
		var updatedAt = new UtcTimestamp(DateTimeOffset.UtcNow);
		var checksum = ComputeChecksum(json);
		await using (var write = connection.CreateCommand())
		{
			write.Transaction = transaction;
			write.CommandText = """
				INSERT INTO management_documents(area, document_key, version, updated_utc, payload_json, checksum_sha256)
				VALUES(@area, @key, @version, @updated, @payload, @checksum)
				ON CONFLICT(area, document_key) DO UPDATE SET
					version = excluded.version,
					updated_utc = excluded.updated_utc,
					payload_json = excluded.payload_json,
					checksum_sha256 = excluded.checksum_sha256;
				""";
			write.Parameters.AddWithValue("@area", area.Trim());
			write.Parameters.AddWithValue("@key", key.Trim());
			write.Parameters.AddWithValue("@version", FormatVersion(nextVersion));
			write.Parameters.AddWithValue("@updated", updatedAt.ToString());
			write.Parameters.AddWithValue("@payload", json);
			write.Parameters.AddWithValue("@checksum", checksum);
			await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		transaction.Commit();
		var document = new ManagementDocument(area.Trim(), key.Trim(), nextVersion, updatedAt, json, checksum);
		return new ManagementWriteResult(true, document, null);
	}

	public async ValueTask WriteAsync(ProductionCheckpoint checkpoint, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(checkpoint);
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
		using var transaction = connection.BeginTransaction();

		await using (var existing = connection.CreateCommand())
		{
			existing.Transaction = transaction;
			existing.CommandText = "SELECT checksum_sha256 FROM production_checkpoints WHERE production_id = @production AND authoritative_revision = @revision;";
			existing.Parameters.AddWithValue("@production", checkpoint.ProductionId.ToString());
			existing.Parameters.AddWithValue("@revision", FormatVersion(checkpoint.AuthoritativeRevision.Value));
			var value = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (value is string checksum)
			{
				if (!string.Equals(checksum, checkpoint.ChecksumSha256, StringComparison.Ordinal))
					throw new InvalidDataException("Checkpoint revision already exists with a different payload checksum.");
				transaction.Rollback();
				return;
			}
		}

		await using (var insert = connection.CreateCommand())
		{
			insert.Transaction = transaction;
			insert.CommandText = """
				INSERT INTO production_checkpoints(
					checkpoint_id, production_id, authoritative_revision, created_utc, format, payload, checksum_sha256)
				VALUES(@checkpoint, @production, @revision, @created, @format, @payload, @checksum);
				""";
			insert.Parameters.AddWithValue("@checkpoint", checkpoint.CheckpointId.ToString());
			insert.Parameters.AddWithValue("@production", checkpoint.ProductionId.ToString());
			insert.Parameters.AddWithValue("@revision", FormatVersion(checkpoint.AuthoritativeRevision.Value));
			insert.Parameters.AddWithValue("@created", checkpoint.CreatedAt.ToString());
			insert.Parameters.AddWithValue("@format", checkpoint.Format);
			insert.Parameters.Add("@payload", SqliteType.Blob).Value = checkpoint.Payload.ToArray();
			insert.Parameters.AddWithValue("@checksum", checkpoint.ChecksumSha256);
			await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		transaction.Commit();
	}

	public async ValueTask<ProductionCheckpoint?> ReadLatestAsync(
		Identity productionId,
		CancellationToken cancellationToken = default)
	{
		if (productionId.IsEmpty) throw new ArgumentException("Production identity must not be empty.", nameof(productionId));
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT checkpoint_id, authoritative_revision, created_utc, format, payload, checksum_sha256
			FROM production_checkpoints
			WHERE production_id = @production
			ORDER BY authoritative_revision DESC
			LIMIT 1;
			""";
		command.Parameters.AddWithValue("@production", productionId.ToString());
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			return null;

		var checkpoint = new ProductionCheckpoint(
			Identity.Parse(reader.GetString(0)),
			productionId,
			new Revision(ParseVersion(reader.GetString(1))),
			UtcTimestamp.Parse(reader.GetString(2)),
			reader.GetString(3),
			(byte[])reader[4]);
		if (!string.Equals(checkpoint.ChecksumSha256, reader.GetString(5), StringComparison.Ordinal))
			throw new InvalidDataException("Checkpoint checksum mismatch.");
		return checkpoint;
	}

	public async ValueTask<PersistenceIntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

		await using (var integrity = connection.CreateCommand())
		{
			integrity.CommandText = "PRAGMA integrity_check;";
			var result = Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
			if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
				return new PersistenceIntegrityReport(false, 0, 0, $"SQLite integrity_check returned '{result}'.");
		}

		var documents = 0;
		await using (var command = connection.CreateCommand())
		{
			command.CommandText = "SELECT payload_json, checksum_sha256 FROM management_documents;";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				documents++;
				if (!ChecksumMatches(reader.GetString(0), reader.GetString(1)))
					return new PersistenceIntegrityReport(false, documents, 0, "Management document checksum mismatch.");
			}
		}

		var checkpoints = 0;
		await using (var command = connection.CreateCommand())
		{
			command.CommandText = "SELECT payload, checksum_sha256 FROM production_checkpoints;";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				checkpoints++;
				var checksum = Convert.ToHexString(SHA256.HashData((byte[])reader[0]));
				if (!string.Equals(checksum, reader.GetString(1), StringComparison.Ordinal))
					return new PersistenceIntegrityReport(false, documents, checkpoints, "Production checkpoint checksum mismatch.");
			}
		}

		return new PersistenceIntegrityReport(true, documents, checkpoints, "SQLite management persistence integrity is valid.");
	}

	public ValueTask DisposeAsync()
	{
		if (_disposed) return ValueTask.CompletedTask;
		_disposed = true;
		_schemaGate.Dispose();
		return ValueTask.CompletedTask;
	}

	private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_initialized) return;
		await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_initialized) return;
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
			await using var connection = CreateConnection();
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
			await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
			await using var command = connection.CreateCommand();
			command.CommandText = SchemaSql;
			await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

			await using var version = connection.CreateCommand();
			version.CommandText = "SELECT schema_version FROM schema_metadata WHERE component = 'management';";
			var value = await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (Convert.ToInt32(value, CultureInfo.InvariantCulture) != SchemaVersion)
				throw new NotSupportedException("Unsupported management persistence schema version.");
			_initialized = true;
		}
		finally
		{
			_schemaGate.Release();
		}
	}

	private SqliteConnection CreateConnection()
	{
		var builder = new SqliteConnectionStringBuilder
		{
			DataSource = _path,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Private,
			Pooling = false
		};
		return new SqliteConnection(builder.ToString());
	}

	private static async ValueTask ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static void ValidateDocumentKey(string area, string key)
	{
		if (string.IsNullOrWhiteSpace(area)) throw new ArgumentException("Management document area is required.", nameof(area));
		if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Management document key is required.", nameof(key));
	}

	private static string FormatVersion(ulong value) => value.ToString("D20", CultureInfo.InvariantCulture);
	private static ulong ParseVersion(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
	private static string ComputeChecksum(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
	private static bool ChecksumMatches(string value, string checksum) => string.Equals(ComputeChecksum(value), checksum, StringComparison.Ordinal);

	private static void EnsureChecksum(string value, string checksum, string message)
	{
		if (!ChecksumMatches(value, checksum)) throw new InvalidDataException(message);
	}

	private const string SchemaSql = """
		CREATE TABLE IF NOT EXISTS schema_metadata(
			component TEXT PRIMARY KEY,
			schema_version INTEGER NOT NULL
		);
		INSERT OR IGNORE INTO schema_metadata(component, schema_version) VALUES('management', 1);
		CREATE TABLE IF NOT EXISTS management_documents(
			area TEXT NOT NULL,
			document_key TEXT NOT NULL,
			version TEXT NOT NULL,
			updated_utc TEXT NOT NULL,
			payload_json TEXT NOT NULL,
			checksum_sha256 TEXT NOT NULL,
			PRIMARY KEY(area, document_key)
		);
		CREATE TABLE IF NOT EXISTS production_checkpoints(
			checkpoint_id TEXT PRIMARY KEY,
			production_id TEXT NOT NULL,
			authoritative_revision TEXT NOT NULL,
			created_utc TEXT NOT NULL,
			format TEXT NOT NULL,
			payload BLOB NOT NULL,
			checksum_sha256 TEXT NOT NULL,
			UNIQUE(production_id, authoritative_revision)
		);
		CREATE INDEX IF NOT EXISTS ix_production_checkpoints_latest
			ON production_checkpoints(production_id, authoritative_revision DESC);
		""";
}
