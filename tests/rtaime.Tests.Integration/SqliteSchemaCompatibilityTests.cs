// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class SqliteSchemaCompatibilityTests
{
	[Theory]
	[InlineData(false, "newer")]
	[InlineData(true, "newer")]
	[InlineData(false, "older")]
	[InlineData(true, "older")]
	[InlineData(false, "wrong-component")]
	[InlineData(true, "wrong-component")]
	[InlineData(false, "missing-metadata")]
	[InlineData(true, "missing-metadata")]
	[InlineData(false, "empty-metadata")]
	[InlineData(true, "empty-metadata")]
	[InlineData(false, "fractional-version")]
	[InlineData(true, "fractional-version")]
	[InlineData(false, "duplicate-component")]
	[InlineData(true, "duplicate-component")]
	public async Task Incompatible_store_preserves_retired_schema_data_and_database_bytes(bool journal, string scenario)
	{
		using var fixture = new DatabaseFixture();
		var component = journal ? "production-journal" : "management";
		await using (var connection = await OpenAsync(fixture.Path))
		{
			await ExecuteAsync(connection, """
				PRAGMA journal_mode=DELETE;
				CREATE TABLE retained_future_state(id INTEGER PRIMARY KEY, payload TEXT NOT NULL, bytes BLOB NOT NULL);
				INSERT INTO retained_future_state VALUES(7, 'retained future state', X'0100FF02');
				CREATE INDEX future_state_payload ON retained_future_state(payload);
				""");
			if (scenario != "missing-metadata")
			{
				await ExecuteAsync(connection, "CREATE TABLE schema_metadata(component TEXT NOT NULL, schema_version INTEGER NOT NULL);");
				if (scenario != "empty-metadata")
				{
					await using var metadata = connection.CreateCommand();
					metadata.CommandText = "INSERT INTO schema_metadata VALUES(@component, @version);";
					metadata.Parameters.AddWithValue("@component", scenario == "wrong-component" ? "future-component" : component);
					metadata.Parameters.AddWithValue("@version", scenario switch
					{
						"newer" => 2.0,
						"older" => 0.0,
						"fractional-version" => 1.5,
						_ => 1.0
					});
					await metadata.ExecuteNonQueryAsync();
					if (scenario == "duplicate-component")
						await metadata.ExecuteNonQueryAsync();
				}
			}
		}

		var beforeBytes = await File.ReadAllBytesAsync(fixture.Path);
		var beforeSchema = await ReadRowsAsync(fixture.Path, "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_schema ORDER BY type, name;");
		var beforeData = await ReadRowsAsync(fixture.Path, "SELECT id, payload, hex(bytes) FROM retained_future_state;");
		var beforeMetadata = scenario == "missing-metadata" ? null
			: await ReadRowsAsync(fixture.Path, "SELECT component, schema_version, typeof(schema_version) FROM schema_metadata ORDER BY rowid;");

		// Repeat rejection through separate store instances; no failed attempt may bootstrap legacy objects.
		for (var attempt = 0; attempt < 2; attempt++)
			await Assert.ThrowsAsync<NotSupportedException>(() => InitializeAsync(fixture.Path, journal));

		Assert.Equal(beforeSchema, await ReadRowsAsync(fixture.Path, "SELECT type, name, tbl_name, rootpage, sql FROM sqlite_schema ORDER BY type, name;"));
		Assert.Equal(beforeData, await ReadRowsAsync(fixture.Path, "SELECT id, payload, hex(bytes) FROM retained_future_state;"));
		if (beforeMetadata is not null)
			Assert.Equal(beforeMetadata, await ReadRowsAsync(fixture.Path, "SELECT component, schema_version, typeof(schema_version) FROM schema_metadata ORDER BY rowid;"));
		Assert.Equal("[[\"delete\"]]", await ReadRowsAsync(fixture.Path, "PRAGMA journal_mode;"));
		Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(fixture.Path));
		Assert.False(File.Exists(fixture.Path + "-wal"));
	}

	[Theory]
	[InlineData(false, "user_version")]
	[InlineData(true, "user_version")]
	[InlineData(false, "application_id")]
	[InlineData(true, "application_id")]
	public async Task Empty_application_schema_with_existing_header_identity_is_not_a_new_store(bool journal, string pragma)
	{
		using var fixture = new DatabaseFixture();
		await using (var connection = await OpenAsync(fixture.Path))
			await ExecuteAsync(connection, $"PRAGMA {pragma}=17;");
		var before = await File.ReadAllBytesAsync(fixture.Path);
		await Assert.ThrowsAsync<NotSupportedException>(() => InitializeAsync(fixture.Path, journal));
		Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Path));
		Assert.Equal("[[\"0\"]]", await ReadRowsAsync(fixture.Path, "SELECT COUNT(*) FROM sqlite_schema;"));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task Fresh_and_precreated_empty_stores_initialize_and_reopen_without_recreating_schema(bool journal, bool precreate)
	{
		using var fixture = new DatabaseFixture();
		if (precreate)
			await File.WriteAllBytesAsync(fixture.Path, []);
		await InitializeAsync(fixture.Path, journal);
		var before = await ReadRowsAsync(fixture.Path, "SELECT type, name, sql FROM sqlite_schema ORDER BY type, name;");
		var schemaRevision = await ReadRowsAsync(fixture.Path, "PRAGMA schema_version;");
		await InitializeAsync(fixture.Path, journal);
		Assert.Equal(before, await ReadRowsAsync(fixture.Path, "SELECT type, name, sql FROM sqlite_schema ORDER BY type, name;"));
		Assert.Equal(schemaRevision, await ReadRowsAsync(fixture.Path, "PRAGMA schema_version;"));
		Assert.Equal(journal ? "[[\"production-journal\",\"1\"]]" : "[[\"management\",\"1\"]]",
			await ReadRowsAsync(fixture.Path, "SELECT component, schema_version FROM schema_metadata;"));
		if (journal)
			Assert.Equal("[[\"1\",\"0\"]]", await ReadRowsAsync(fixture.Path, "SELECT COUNT(*), last_ordinal FROM production_journal_head;"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Missing_required_table_is_not_silently_replaced_with_empty_state(bool journal)
	{
		using var fixture = new DatabaseFixture();
		await InitializeAsync(fixture.Path, journal);
		await using (var connection = await OpenAsync(fixture.Path))
			await ExecuteAsync(connection, journal ? "DROP TABLE production_journal_head;" : "DROP TABLE management_documents;");
		var before = await ReadRowsAsync(fixture.Path, "SELECT type, name, sql FROM sqlite_schema ORDER BY type, name;");
		await Assert.ThrowsAsync<InvalidDataException>(() => InitializeAsync(fixture.Path, journal));
		Assert.Equal(before, await ReadRowsAsync(fixture.Path, "SELECT type, name, sql FROM sqlite_schema ORDER BY type, name;"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Failed_fresh_bootstrap_rolls_back_all_objects_and_allows_a_clean_retry(bool journal)
	{
		using var fixture = new DatabaseFixture();
		await using (var connection = await OpenAsync(fixture.Path))
		{
			await Assert.ThrowsAsync<SqliteException>(() => SqliteStoreSchema.EnsureAsync(connection,
				journal ? "production-journal" : "management", 1,
				"CREATE TABLE first_bootstrap_object(value TEXT); INSERT INTO first_bootstrap_object VALUES('must roll back'); INSERT INTO missing_table VALUES(1);",
				[], CancellationToken.None).AsTask());
		}
		Assert.Equal("[[\"0\"]]", await ReadRowsAsync(fixture.Path, "SELECT COUNT(*) FROM sqlite_schema;"));
		await InitializeAsync(fixture.Path, journal);
		Assert.Equal("[[\"1\"]]", await ReadRowsAsync(fixture.Path, "SELECT COUNT(*) FROM schema_metadata;"));
	}

	private static async Task InitializeAsync(string path, bool journal)
	{
		if (journal)
		{
			await using var store = new SqliteProductionJournalStore(path);
			await store.ReadAsync(0, 1);
		}
		else
		{
			await using var store = new SqliteManagementStore(path);
			await store.InitializeAsync();
		}
	}

	private static async Task<SqliteConnection> OpenAsync(string path)
	{
		var connection = new SqliteConnection(new SqliteConnectionStringBuilder
		{
			DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
		}.ToString());
		await connection.OpenAsync();
		return connection;
	}

	private static async Task ExecuteAsync(SqliteConnection connection, string sql)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<string> ReadRowsAsync(string path, string sql)
	{
		await using var connection = await OpenAsync(path);
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		await using var reader = await command.ExecuteReaderAsync();
		var rows = new List<string?[]>();
		while (await reader.ReadAsync())
		{
			var row = new string?[reader.FieldCount];
			for (var index = 0; index < row.Length; index++)
				row[index] = reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
			rows.Add(row);
		}
		return JsonSerializer.Serialize(rows);
	}

	private sealed class DatabaseFixture : IDisposable
	{
		private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rtaime-schema-" + Guid.NewGuid().ToString("N"));
		internal DatabaseFixture() => Directory.CreateDirectory(_root);
		internal string Path => System.IO.Path.Combine(_root, "store.db");
		public void Dispose() => Directory.Delete(_root, true);
	}
}
