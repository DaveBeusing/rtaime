// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using Microsoft.Data.Sqlite;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class PersistentStateRecoveryIntegrationTests
{
	[Fact]
	public async Task Verified_backup_can_restore_previous_management_state()
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "management.db");
			var backupPath = Path.Combine(root, "backup", "management.db");
			await using (var store = new SqliteManagementStore(databasePath))
			{
				var created = await store.PutDocumentAsync("configuration", "control", "{\"generation\":1}", 0);
				Assert.True(created.Written);
			}

			var maintenance = new SqliteStateMaintenance();
			var snapshot = await maintenance.CreateBackupAsync(databasePath, backupPath);
			Assert.Equal(64, snapshot.BackupSha256.Length);
			Assert.Single(snapshot.Schema);
			Assert.Equal(new SqliteSchemaState("management", 1), snapshot.Schema[0]);

			await using (var store = new SqliteManagementStore(databasePath))
			{
				var updated = await store.PutDocumentAsync("configuration", "control", "{\"generation\":2}", 1);
				Assert.True(updated.Written);
			}

			await maintenance.RestoreBackupAsync(snapshot, databasePath, acknowledgeExclusiveAccess: true);
			await using var restored = new SqliteManagementStore(databasePath);
			var document = await restored.GetDocumentAsync("configuration", "control");
			Assert.NotNull(document);
			Assert.Equal(1UL, document.Version);
			Assert.Equal("{\"generation\":1}", document.Json);
			Assert.True((await restored.VerifyIntegrityAsync()).Healthy);
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Registered_forward_migration_requires_backup_and_commits_transactionally()
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			var maintenance = new SqliteStateMaintenance();
			var migration = new SqliteMigrationStep(
				"test",
				1,
				2,
				"ALTER TABLE durable_state ADD COLUMN detail TEXT NOT NULL DEFAULT ''; UPDATE durable_state SET detail = 'migrated';");

			var result = await maintenance.MigrateAsync(
				databasePath,
				"test",
				2,
				new[] { migration },
				backupPath,
				acknowledgeExclusiveAccess: true);

			Assert.True(result.Applied);
			Assert.Equal(1, result.PreviousVersion);
			Assert.Equal(2, result.CurrentVersion);
			Assert.NotNull(result.Backup);
			Assert.True(File.Exists(backupPath));
			Assert.Equal(new SqliteSchemaState("test", 2), Assert.Single(await maintenance.InspectSchemaAsync(databasePath)));
			Assert.Equal("migrated", await ScalarAsync(databasePath, "SELECT detail FROM durable_state WHERE id = 1;"));
			Assert.Equal(new SqliteSchemaState("test", 1), Assert.Single(await maintenance.InspectSchemaAsync(backupPath)));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Post_migration_verification_failure_restores_snapshot()
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			var maintenance = new SqliteStateMaintenance();
			var migration = new SqliteMigrationStep(
				"test",
				1,
				2,
				"ALTER TABLE durable_state ADD COLUMN detail TEXT NOT NULL DEFAULT ''; UPDATE durable_state SET detail = 'migrated';");

			await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.MigrateAsync(
				databasePath,
				"test",
				2,
				new[] { migration },
				backupPath,
				acknowledgeExclusiveAccess: true,
				postMigrationVerifier: static (_, _) => ValueTask.FromException(new InvalidOperationException("Synthetic post-migration verification failure."))).AsTask());

			Assert.Equal(new SqliteSchemaState("test", 1), Assert.Single(await maintenance.InspectSchemaAsync(databasePath)));
			Assert.Equal("before", await ScalarAsync(databasePath, "SELECT payload FROM durable_state WHERE id = 1;"));
			Assert.Equal("0", await ScalarAsync(databasePath, "SELECT COUNT(*) FROM pragma_table_info('durable_state') WHERE name = 'detail';"));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Unknown_migration_chain_fails_before_backup_or_mutation()
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			var maintenance = new SqliteStateMaintenance();

			await Assert.ThrowsAsync<NotSupportedException>(() => maintenance.MigrateAsync(
				databasePath,
				"test",
				2,
				Array.Empty<SqliteMigrationStep>(),
				backupPath,
				acknowledgeExclusiveAccess: true).AsTask());

			Assert.False(File.Exists(backupPath));
			Assert.Equal(new SqliteSchemaState("test", 1), Assert.Single(await maintenance.InspectSchemaAsync(databasePath)));
			Assert.Equal("before", await ScalarAsync(databasePath, "SELECT payload FROM durable_state WHERE id = 1;"));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Tampered_backup_is_rejected_without_modifying_current_state()
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			var maintenance = new SqliteStateMaintenance();
			var snapshot = await maintenance.CreateBackupAsync(databasePath, backupPath);
			await ExecuteAsync(databasePath, "UPDATE durable_state SET payload = 'current' WHERE id = 1;");

			await using (var stream = new FileStream(backupPath, FileMode.Append, FileAccess.Write, FileShare.None))
				stream.WriteByte(0x7f);

			await Assert.ThrowsAsync<InvalidDataException>(() => maintenance.RestoreBackupAsync(
				snapshot,
				databasePath,
				acknowledgeExclusiveAccess: true).AsTask());
			Assert.Equal("current", await ScalarAsync(databasePath, "SELECT payload FROM durable_state WHERE id = 1;"));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Restore_and_migration_require_explicit_exclusive_access_acknowledgement()
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			var maintenance = new SqliteStateMaintenance();
			var snapshot = await maintenance.CreateBackupAsync(databasePath, backupPath);

			await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.RestoreBackupAsync(
				snapshot,
				databasePath,
				acknowledgeExclusiveAccess: false).AsTask());
			await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.MigrateAsync(
				databasePath,
				"test",
				2,
				new[] { new SqliteMigrationStep("test", 1, 2, "SELECT 1;") },
				Path.Combine(root, "second-backup.db"),
				acknowledgeExclusiveAccess: false).AsTask());
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	private static async Task CreateTestDatabaseAsync(string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await using var connection = Connection(path, SqliteOpenMode.ReadWriteCreate);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = """
			PRAGMA journal_mode=WAL;
			PRAGMA synchronous=FULL;
			CREATE TABLE schema_metadata(component TEXT PRIMARY KEY, schema_version INTEGER NOT NULL);
			INSERT INTO schema_metadata(component, schema_version) VALUES('test', 1);
			CREATE TABLE durable_state(id INTEGER PRIMARY KEY, payload TEXT NOT NULL);
			INSERT INTO durable_state(id, payload) VALUES(1, 'before');
			""";
		await command.ExecuteNonQueryAsync();
	}

	private static async Task ExecuteAsync(string path, string sql)
	{
		await using var connection = Connection(path, SqliteOpenMode.ReadWrite);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<string> ScalarAsync(string path, string sql)
	{
		await using var connection = Connection(path, SqliteOpenMode.ReadOnly);
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = sql;
		return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
	}

	private static SqliteConnection Connection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
	{
		DataSource = path,
		Mode = mode,
		Cache = SqliteCacheMode.Private,
		Pooling = false
	}.ToString());

	private static string TempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-state-recovery-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteDirectory(string path)
	{
		try { Directory.Delete(path, recursive: true); }
		catch { }
	}
}
