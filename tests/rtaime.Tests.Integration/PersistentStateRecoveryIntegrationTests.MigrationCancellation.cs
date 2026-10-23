// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed partial class PersistentStateRecoveryIntegrationTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Cancellation_after_migration_commit_restores_snapshot_before_reporting_cancellation(bool verifierThrows)
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			using var cancellation = new CancellationTokenSource();
			var originalFailure = new OperationCanceledException("Migration verification cancelled.", cancellation.Token);
			var maintenance = new SqliteStateMaintenance();

			var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenance.MigrateAsync(
				databasePath, "test", 2, new[] { CancellationTestMigration() }, backupPath,
				acknowledgeExclusiveAccess: true,
				postMigrationVerifier: async (path, token) =>
				{
					Assert.Equal(cancellation.Token, token);
					await AssertCommittedMigrationAsync(path);
					cancellation.Cancel();
					if (verifierThrows) throw originalFailure;
				},
				cancellationToken: cancellation.Token).AsTask());

			Assert.Equal(cancellation.Token, failure.CancellationToken);
			if (verifierThrows) Assert.Same(originalFailure, failure);
			await AssertMigrationSnapshotRestoredAsync(maintenance, databasePath, backupPath);
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Cancellation_while_async_post_migration_verification_is_pending_restores_schema_and_payload()
	{
		var root = TempDirectory();
		using var cancellation = new CancellationTokenSource();
		Task<SqliteMigrationResult>? migration = null;
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			var verifierEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var maintenance = new SqliteStateMaintenance();
			migration = maintenance.MigrateAsync(
				databasePath, "test", 2, new[] { CancellationTestMigration() }, backupPath,
				acknowledgeExclusiveAccess: true,
				postMigrationVerifier: async (path, token) =>
				{
					await AssertCommittedMigrationAsync(path);
					verifierEntered.SetResult();
					await Task.Delay(Timeout.InfiniteTimeSpan, token);
				},
				cancellationToken: cancellation.Token).AsTask();

			await verifierEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.False(migration.IsCompleted);
			cancellation.Cancel();
			var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => migration.WaitAsync(TimeSpan.FromSeconds(10)));

			Assert.Equal(cancellation.Token, failure.CancellationToken);
			await AssertMigrationSnapshotRestoredAsync(maintenance, databasePath, backupPath);
		}
		finally
		{
			cancellation.Cancel();
			try
			{
				if (migration is not null)
				{
					try { await migration.WaitAsync(TimeSpan.FromSeconds(10)); }
					catch (OperationCanceledException) { }
				}
			}
			finally
			{
				DeleteDirectory(root);
			}
		}
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Cancelled_migration_reports_recovery_deadline_or_io_failure_and_retains_backup(bool expireRecoveryBudget)
	{
		var root = TempDirectory();
		try
		{
			var databasePath = Path.Combine(root, "state.db");
			var backupPath = Path.Combine(root, "backup", "state-v1.db");
			await CreateTestDatabaseAsync(databasePath);
			using var cancellation = new CancellationTokenSource();
			var migrationFailure = new OperationCanceledException("Migration verification cancelled.", cancellation.Token);
			var recoveryFailure = new IOException("Recovery activation failed.");
			var clock = new RecoveryDeadlineTimeProvider();
			var maintenance = new SqliteStateMaintenance(
				(source, destination) =>
				{
					if (source.Contains(".restore-stage-", StringComparison.Ordinal))
					{
						if (!expireRecoveryBudget) throw recoveryFailure;
						clock.Expire();
					}
					File.Move(source, destination);
				}, File.Delete, clock);

			var failure = await Assert.ThrowsAsync<AggregateException>(() => maintenance.MigrateAsync(
				databasePath, "test", 2, new[] { CancellationTestMigration() }, backupPath,
				acknowledgeExclusiveAccess: true,
				postMigrationVerifier: (_, _) =>
				{
					cancellation.Cancel();
					return ValueTask.FromException(migrationFailure);
				},
				cancellationToken: cancellation.Token).AsTask());

			Assert.Equal(2, failure.InnerExceptions.Count);
			Assert.Same(migrationFailure, failure.InnerExceptions[0]);
			if (expireRecoveryBudget)
			{
				var deadlineFailure = Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerExceptions[1]);
				Assert.NotEqual(cancellation.Token, deadlineFailure.CancellationToken);
				Assert.True(deadlineFailure.CancellationToken.IsCancellationRequested);
			}
			else
			{
				Assert.Same(recoveryFailure, failure.InnerExceptions[1]);
			}
			Assert.Contains(databasePath, failure.Message);
			Assert.Contains(backupPath, failure.Message);
			await AssertCommittedMigrationAsync(databasePath);
			Assert.Equal(new SqliteSchemaState("test", 1), Assert.Single(await maintenance.InspectSchemaAsync(backupPath)));
			Assert.Equal("before", await ScalarAsync(backupPath, "SELECT payload FROM durable_state WHERE id = 1;"));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	private static SqliteMigrationStep CancellationTestMigration() => new(
		"test", 1, 2,
		"ALTER TABLE durable_state ADD COLUMN detail TEXT NOT NULL DEFAULT ''; UPDATE durable_state SET payload = 'after', detail = 'migrated';");

	private static async Task AssertCommittedMigrationAsync(string databasePath)
	{
		Assert.Equal("2", await ScalarAsync(databasePath, "SELECT schema_version FROM schema_metadata WHERE component = 'test';"));
		Assert.Equal("after", await ScalarAsync(databasePath, "SELECT payload FROM durable_state WHERE id = 1;"));
		Assert.Equal("migrated", await ScalarAsync(databasePath, "SELECT detail FROM durable_state WHERE id = 1;"));
	}

	private static async Task AssertMigrationSnapshotRestoredAsync(SqliteStateMaintenance maintenance, string databasePath, string backupPath)
	{
		Assert.Equal(new SqliteSchemaState("test", 1), Assert.Single(await maintenance.InspectSchemaAsync(databasePath)));
		Assert.Equal("before", await ScalarAsync(databasePath, "SELECT payload FROM durable_state WHERE id = 1;"));
		Assert.Equal("0", await ScalarAsync(databasePath, "SELECT COUNT(*) FROM pragma_table_info('durable_state') WHERE name = 'detail';"));
		Assert.Equal(await File.ReadAllBytesAsync(backupPath), await File.ReadAllBytesAsync(databasePath));
		Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(databasePath)!, "*.restore-*"));
	}

	private sealed class RecoveryDeadlineTimeProvider : TimeProvider
	{
		private Action? _expire;

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			_expire = () => callback(state);
			return new RecoveryTimer();
		}

		public void Expire() => (_expire ?? throw new InvalidOperationException("Recovery deadline was not scheduled."))();

		private sealed class RecoveryTimer : ITimer
		{
			public bool Change(TimeSpan dueTime, TimeSpan period) => true;
			public void Dispose() { }
			public ValueTask DisposeAsync() => ValueTask.CompletedTask;
		}
	}
}
