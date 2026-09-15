// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using rtaime.Core;

namespace rtaime.Persistence;

/// <summary>
/// Purpose-built SQLite-backed append-only production journal. It maintains an event checksum, a chained entry
/// hash and a separately persisted head so corruption, reordering and tail truncation are detectable.
/// </summary>
public sealed class SqliteProductionJournalStore : IProductionJournalStore
{
	private const int SchemaVersion = 1;
	private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
	private readonly string _path;
	private readonly SemaphoreSlim _schemaGate = new(1, 1);
	private volatile bool _initialized;
	private bool _disposed;

	public SqliteProductionJournalStore(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Production journal database path is required.", nameof(path));
		_path = System.IO.Path.GetFullPath(path);
	}

	public string Path => _path;

	public async ValueTask<ProductionJournalEntry> AppendAsync(
		ProductionJournalEvent journalEvent,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(journalEvent);
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
		using var transaction = connection.BeginTransaction();

		var eventChecksum = ComputeEventChecksum(journalEvent);
		await using (var existing = connection.CreateCommand())
		{
			existing.Transaction = transaction;
			existing.CommandText = "SELECT ordinal, event_checksum FROM production_journal WHERE event_id = @eventId;";
			existing.Parameters.AddWithValue("@eventId", journalEvent.EventId.ToString());
			await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				if (!string.Equals(reader.GetString(1), eventChecksum, StringComparison.Ordinal))
					throw new InvalidDataException("Production journal EventId was reused with different event content.");
				var existingOrdinal = checked((ulong)reader.GetInt64(0));
				transaction.Rollback();
				return new ProductionJournalEntry(existingOrdinal, journalEvent);
			}
		}

		long headOrdinal;
		string previousHash;
		await using (var head = connection.CreateCommand())
		{
			head.Transaction = transaction;
			head.CommandText = "SELECT last_ordinal, last_hash FROM production_journal_head WHERE singleton_id = 1;";
			await using var reader = await head.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				throw new InvalidDataException("Production journal head metadata is missing.");
			headOrdinal = reader.GetInt64(0);
			previousHash = reader.GetString(1);
		}

		var entryHash = ComputeEntryHash(previousHash, eventChecksum);
		long ordinal;
		await using (var insert = connection.CreateCommand())
		{
			insert.Transaction = transaction;
			insert.CommandText = """
				INSERT INTO production_journal(
					event_id, production_id, authoritative_revision, timestamp_utc,
					category, code, detail, causation_id, failure_code, failure_message,
					event_checksum, previous_hash, entry_hash)
				VALUES(
					@eventId, @productionId, @revision, @timestamp,
					@category, @code, @detail, @causationId, @failureCode, @failureMessage,
					@eventChecksum, @previousHash, @entryHash);
				SELECT last_insert_rowid();
				""";
			insert.Parameters.AddWithValue("@eventId", journalEvent.EventId.ToString());
			insert.Parameters.AddWithValue("@productionId", journalEvent.ProductionId.ToString());
			insert.Parameters.AddWithValue("@revision", FormatRevision(journalEvent.AuthoritativeRevision.Value));
			insert.Parameters.AddWithValue("@timestamp", journalEvent.Timestamp.ToString());
			insert.Parameters.AddWithValue("@category", journalEvent.Category);
			insert.Parameters.AddWithValue("@code", journalEvent.Code);
			insert.Parameters.AddWithValue("@detail", journalEvent.Detail);
			insert.Parameters.AddWithValue("@causationId", journalEvent.CausationId?.ToString() ?? (object)DBNull.Value);
			insert.Parameters.AddWithValue("@failureCode", journalEvent.Failure?.Code ?? (object)DBNull.Value);
			insert.Parameters.AddWithValue("@failureMessage", journalEvent.Failure?.Message ?? (object)DBNull.Value);
			insert.Parameters.AddWithValue("@eventChecksum", eventChecksum);
			insert.Parameters.AddWithValue("@previousHash", previousHash);
			insert.Parameters.AddWithValue("@entryHash", entryHash);
			var value = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			ordinal = Convert.ToInt64(value, CultureInfo.InvariantCulture);
		}

		if (ordinal <= headOrdinal)
			throw new InvalidDataException("Production journal ordinal did not advance monotonically.");

		await using (var updateHead = connection.CreateCommand())
		{
			updateHead.Transaction = transaction;
			updateHead.CommandText = "UPDATE production_journal_head SET last_ordinal = @ordinal, last_hash = @hash WHERE singleton_id = 1;";
			updateHead.Parameters.AddWithValue("@ordinal", ordinal);
			updateHead.Parameters.AddWithValue("@hash", entryHash);
			if (await updateHead.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
				throw new InvalidDataException("Production journal head update failed.");
		}

		transaction.Commit();
		return new ProductionJournalEntry(checked((ulong)ordinal), journalEvent);
	}

	public async ValueTask<IReadOnlyList<ProductionJournalEntry>> ReadAsync(
		ulong afterOrdinal,
		int limit,
		CancellationToken cancellationToken = default)
	{
		if (limit <= 0 || limit > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
		if (afterOrdinal > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(afterOrdinal));
		await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
		await using var connection = CreateConnection();
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT ordinal, event_id, production_id, authoritative_revision, timestamp_utc,
				category, code, detail, causation_id, failure_code, failure_message, event_checksum
			FROM production_journal
			WHERE ordinal > @after
			ORDER BY ordinal
			LIMIT @limit;
			""";
		command.Parameters.AddWithValue("@after", checked((long)afterOrdinal));
		command.Parameters.AddWithValue("@limit", limit);
		await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		var entries = new List<ProductionJournalEntry>();
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var entry = ReadEntry(reader);
			if (!string.Equals(ComputeEventChecksum(entry.Event), reader.GetString(11), StringComparison.Ordinal))
				throw new InvalidDataException($"Production journal checksum mismatch at ordinal {entry.Ordinal}.");
			entries.Add(entry);
		}
		return entries.AsReadOnly();
	}

	public async ValueTask<ProductionJournalIntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
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
				return new ProductionJournalIntegrityReport(false, 0, $"SQLite integrity_check returned '{result}'.");
		}

		var expectedPrevious = GenesisHash;
		long lastOrdinal = 0;
		string lastHash = GenesisHash;
		ulong count = 0;
		await using (var command = connection.CreateCommand())
		{
			command.CommandText = """
				SELECT ordinal, event_id, production_id, authoritative_revision, timestamp_utc,
					category, code, detail, causation_id, failure_code, failure_message,
					event_checksum, previous_hash, entry_hash
				FROM production_journal
				ORDER BY ordinal;
				""";
			await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				var entry = ReadEntry(reader);
				var storedEventChecksum = reader.GetString(11);
				var previousHash = reader.GetString(12);
				var entryHash = reader.GetString(13);
				var eventChecksum = ComputeEventChecksum(entry.Event);
				if (!string.Equals(eventChecksum, storedEventChecksum, StringComparison.Ordinal))
					return new ProductionJournalIntegrityReport(false, count, $"Event checksum mismatch at ordinal {entry.Ordinal}.");
				if (!string.Equals(previousHash, expectedPrevious, StringComparison.Ordinal))
					return new ProductionJournalIntegrityReport(false, count, $"Hash-chain predecessor mismatch at ordinal {entry.Ordinal}.");
				if (!string.Equals(entryHash, ComputeEntryHash(previousHash, eventChecksum), StringComparison.Ordinal))
					return new ProductionJournalIntegrityReport(false, count, $"Hash-chain entry mismatch at ordinal {entry.Ordinal}.");
				expectedPrevious = entryHash;
				lastHash = entryHash;
				lastOrdinal = checked((long)entry.Ordinal);
				count++;
			}
		}

		await using (var head = connection.CreateCommand())
		{
			head.CommandText = "SELECT last_ordinal, last_hash FROM production_journal_head WHERE singleton_id = 1;";
			await using var reader = await head.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				return new ProductionJournalIntegrityReport(false, count, "Production journal head metadata is missing.");
			if (reader.GetInt64(0) != lastOrdinal || !string.Equals(reader.GetString(1), lastHash, StringComparison.Ordinal))
				return new ProductionJournalIntegrityReport(false, count, "Production journal head does not match the append-only chain tail.");
		}

		return new ProductionJournalIntegrityReport(true, count, "Production journal SQLite integrity and hash chain are valid.");
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
			version.CommandText = "SELECT schema_version FROM schema_metadata WHERE component = 'production-journal';";
			var value = await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (Convert.ToInt32(value, CultureInfo.InvariantCulture) != SchemaVersion)
				throw new NotSupportedException("Unsupported production journal schema version.");
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

	private static ProductionJournalEntry ReadEntry(SqliteDataReader reader)
	{
		var failureCode = reader.IsDBNull(9) ? null : reader.GetString(9);
		var failureMessage = reader.IsDBNull(10) ? null : reader.GetString(10);
		if ((failureCode is null) != (failureMessage is null))
			throw new InvalidDataException("Production journal failure columns are inconsistent.");
		var journalEvent = new ProductionJournalEvent(
			Identity.Parse(reader.GetString(2)),
			new Revision(ParseRevision(reader.GetString(3))),
			UtcTimestamp.Parse(reader.GetString(4)),
			reader.GetString(5),
			reader.GetString(6),
			reader.GetString(7),
			reader.IsDBNull(8) ? null : Identity.Parse(reader.GetString(8)),
			failureCode is null ? null : new Failure(failureCode, failureMessage!),
			Identity.Parse(reader.GetString(1)));
		return new ProductionJournalEntry(checked((ulong)reader.GetInt64(0)), journalEvent);
	}

	private static string ComputeEventChecksum(ProductionJournalEvent journalEvent)
	{
		var canonical = string.Join('\n', new[]
		{
			journalEvent.EventId.ToString(),
			journalEvent.ProductionId.ToString(),
			FormatRevision(journalEvent.AuthoritativeRevision.Value),
			journalEvent.Timestamp.ToString(),
			journalEvent.Category,
			journalEvent.Code,
			journalEvent.Detail,
			journalEvent.CausationId?.ToString() ?? string.Empty,
			journalEvent.Failure?.Code ?? string.Empty,
			journalEvent.Failure?.Message ?? string.Empty
		});
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
	}

	private static string ComputeEntryHash(string previousHash, string eventChecksum) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + "\n" + eventChecksum)));

	private static string FormatRevision(ulong value) => value.ToString("D20", CultureInfo.InvariantCulture);
	private static ulong ParseRevision(string value) => ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

	private const string SchemaSql = """
		CREATE TABLE IF NOT EXISTS schema_metadata(
			component TEXT PRIMARY KEY,
			schema_version INTEGER NOT NULL
		);
		INSERT OR IGNORE INTO schema_metadata(component, schema_version) VALUES('production-journal', 1);
		CREATE TABLE IF NOT EXISTS production_journal(
			ordinal INTEGER PRIMARY KEY AUTOINCREMENT,
			event_id TEXT NOT NULL UNIQUE,
			production_id TEXT NOT NULL,
			authoritative_revision TEXT NOT NULL,
			timestamp_utc TEXT NOT NULL,
			category TEXT NOT NULL,
			code TEXT NOT NULL,
			detail TEXT NOT NULL,
			causation_id TEXT NULL,
			failure_code TEXT NULL,
			failure_message TEXT NULL,
			event_checksum TEXT NOT NULL,
			previous_hash TEXT NOT NULL,
			entry_hash TEXT NOT NULL
		);
		CREATE INDEX IF NOT EXISTS ix_production_journal_production_ordinal
			ON production_journal(production_id, ordinal);
		CREATE TABLE IF NOT EXISTS production_journal_head(
			singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
			last_ordinal INTEGER NOT NULL,
			last_hash TEXT NOT NULL
		);
		INSERT OR IGNORE INTO production_journal_head(singleton_id, last_ordinal, last_hash)
			VALUES(1, 0, '0000000000000000000000000000000000000000000000000000000000000000');
		""";
}
