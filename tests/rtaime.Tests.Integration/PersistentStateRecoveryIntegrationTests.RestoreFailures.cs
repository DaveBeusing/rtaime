// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using Microsoft.Data.Sqlite;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed partial class PersistentStateRecoveryIntegrationTests
{
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Failed_restore_compensation_retains_newer_database_and_both_errors(bool failTargetDeletion)
	{
		var root = TempDirectory();
		try
		{
			var (databasePath, snapshot, currentBytes) = await CreateRestoreFixtureAsync(root);
			var rollbackFailure = new IOException(failTargetDeletion ? "Target deletion failed." : "Previous database rename failed.");
			var files = new RestoreFileOperations(databasePath)
			{
				CorruptActivatedDatabase = true,
				RollbackFailure = failTargetDeletion ? null : rollbackFailure,
				BeforeDelete = path =>
				{
					if (failTargetDeletion && path == databasePath)
						throw rollbackFailure;
				}
			};
			var maintenance = new SqliteStateMaintenance(files.Move, files.Delete);

			var failure = await Assert.ThrowsAsync<AggregateException>(() => maintenance.RestoreBackupAsync(
				snapshot, databasePath, acknowledgeExclusiveAccess: true).AsTask());

			Assert.Collection(failure.InnerExceptions,
				error => Assert.IsType<SqliteException>(error),
				error => Assert.Same(rollbackFailure, error));
			AssertRecoveryLocations(failure, databasePath, snapshot, files);
			var previousPath = Assert.IsType<string>(files.PreviousPath);
			Assert.Equal(currentBytes, await File.ReadAllBytesAsync(previousPath));
			Assert.Equal("current", await ScalarAsync(previousPath, "SELECT payload FROM durable_state WHERE id = 1;"));
			Assert.Equal("before", await ScalarAsync(snapshot.BackupPath, "SELECT payload FROM durable_state WHERE id = 1;"));
			Assert.Equal(failTargetDeletion, File.Exists(databasePath));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Failed_activation_and_compensation_retain_previous_and_staged_databases()
	{
		var root = TempDirectory();
		try
		{
			var (databasePath, snapshot, currentBytes) = await CreateRestoreFixtureAsync(root);
			var activationFailure = new IOException("Staged database activation failed.");
			var rollbackFailure = new IOException("Previous database rename failed.");
			var files = new RestoreFileOperations(databasePath)
			{
				ActivationFailure = activationFailure,
				RollbackFailure = rollbackFailure
			};
			var maintenance = new SqliteStateMaintenance(files.Move, files.Delete);

			var failure = await Assert.ThrowsAsync<AggregateException>(() => maintenance.RestoreBackupAsync(
				snapshot, databasePath, acknowledgeExclusiveAccess: true).AsTask());

			Assert.Collection(failure.InnerExceptions,
				error => Assert.Same(activationFailure, error),
				error => Assert.Same(rollbackFailure, error));
			AssertRecoveryLocations(failure, databasePath, snapshot, files);
			Assert.Equal(currentBytes, await File.ReadAllBytesAsync(Assert.IsType<string>(files.PreviousPath)));
			Assert.Equal(await File.ReadAllBytesAsync(snapshot.BackupPath),
				await File.ReadAllBytesAsync(Assert.IsType<string>(files.StagePath)));
			Assert.False(File.Exists(databasePath));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Failed_restore_with_successful_compensation_restores_exact_newer_bytes()
	{
		var root = TempDirectory();
		try
		{
			var (databasePath, snapshot, currentBytes) = await CreateRestoreFixtureAsync(root);
			var files = new RestoreFileOperations(databasePath) { CorruptActivatedDatabase = true };
			var maintenance = new SqliteStateMaintenance(files.Move, files.Delete);

			await Assert.ThrowsAsync<SqliteException>(() => maintenance.RestoreBackupAsync(
				snapshot, databasePath, acknowledgeExclusiveAccess: true).AsTask());

			Assert.Equal(currentBytes, await File.ReadAllBytesAsync(databasePath));
			Assert.Equal("current", await ScalarAsync(databasePath, "SELECT payload FROM durable_state WHERE id = 1;"));
			Assert.Empty(Directory.EnumerateFileSystemEntries(root, "*.restore-*"));
			Assert.True(File.Exists(snapshot.BackupPath));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Successful_compensation_does_not_hide_original_failure_when_stage_cleanup_fails()
	{
		var root = TempDirectory();
		try
		{
			var (databasePath, snapshot, currentBytes) = await CreateRestoreFixtureAsync(root);
			var activationFailure = new IOException("Staged database activation failed.");
			var cleanupFailure = new IOException("Staged database cleanup failed.");
			var files = new RestoreFileOperations(databasePath) { ActivationFailure = activationFailure };
			files.BeforeDelete = path =>
			{
				if (path == files.StagePath)
					throw cleanupFailure;
			};
			var maintenance = new SqliteStateMaintenance(files.Move, files.Delete);

			var failure = await Assert.ThrowsAsync<AggregateException>(() => maintenance.RestoreBackupAsync(
				snapshot, databasePath, acknowledgeExclusiveAccess: true).AsTask());

			Assert.Collection(failure.InnerExceptions,
				error => Assert.Same(activationFailure, error),
				error => Assert.Same(cleanupFailure, error));
			AssertRecoveryLocations(failure, databasePath, snapshot, files);
			Assert.Equal(currentBytes, await File.ReadAllBytesAsync(databasePath));
			Assert.Equal(await File.ReadAllBytesAsync(snapshot.BackupPath),
				await File.ReadAllBytesAsync(Assert.IsType<string>(files.StagePath)));
			Assert.False(File.Exists(files.PreviousPath));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Verified_restore_remains_active_when_previous_database_cleanup_fails()
	{
		var root = TempDirectory();
		try
		{
			var (databasePath, snapshot, currentBytes) = await CreateRestoreFixtureAsync(root);
			var cleanupFailure = new IOException("Previous database cleanup failed.");
			var files = new RestoreFileOperations(databasePath);
			files.BeforeDelete = path =>
			{
				if (path == files.PreviousPath)
					throw cleanupFailure;
			};
			var maintenance = new SqliteStateMaintenance(files.Move, files.Delete);

			var failure = await Assert.ThrowsAsync<IOException>(() => maintenance.RestoreBackupAsync(
				snapshot, databasePath, acknowledgeExclusiveAccess: true).AsTask());

			Assert.Same(cleanupFailure, failure.InnerException);
			Assert.Contains("restore was verified", failure.Message, StringComparison.Ordinal);
			AssertRecoveryLocations(failure, databasePath, snapshot, files);
			Assert.Equal(await File.ReadAllBytesAsync(snapshot.BackupPath), await File.ReadAllBytesAsync(databasePath));
			Assert.Equal("before", await ScalarAsync(databasePath, "SELECT payload FROM durable_state WHERE id = 1;"));
			Assert.Equal(currentBytes, await File.ReadAllBytesAsync(Assert.IsType<string>(files.PreviousPath)));
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	private static async Task<(string DatabasePath, SqliteStateSnapshot Snapshot, byte[] CurrentBytes)> CreateRestoreFixtureAsync(string root)
	{
		var databasePath = Path.Combine(root, "state.db");
		await CreateTestDatabaseAsync(databasePath);
		var snapshot = await new SqliteStateMaintenance().CreateBackupAsync(databasePath, Path.Combine(root, "backup", "state.db"));
		await ExecuteAsync(databasePath, "UPDATE durable_state SET payload = 'current' WHERE id = 1;");
		return (databasePath, snapshot, await File.ReadAllBytesAsync(databasePath));
	}

	private static void AssertRecoveryLocations(Exception failure, string databasePath, SqliteStateSnapshot snapshot, RestoreFileOperations files)
	{
		Assert.Contains(databasePath, failure.Message, StringComparison.Ordinal);
		Assert.Contains(snapshot.BackupPath, failure.Message, StringComparison.Ordinal);
		Assert.Contains(Assert.IsType<string>(files.PreviousPath), failure.Message, StringComparison.Ordinal);
		Assert.Contains(Assert.IsType<string>(files.StagePath), failure.Message, StringComparison.Ordinal);
	}

	private sealed class RestoreFileOperations(string databasePath)
	{
		public string? PreviousPath { get; private set; }
		public string? StagePath { get; private set; }
		public bool CorruptActivatedDatabase { get; init; }
		public Exception? ActivationFailure { get; init; }
		public Exception? RollbackFailure { get; init; }
		public Action<string>? BeforeDelete { get; set; }

		public void Move(string sourcePath, string destinationPath)
		{
			if (sourcePath == databasePath)
				PreviousPath = destinationPath;
			var activating = destinationPath == databasePath && sourcePath != PreviousPath;
			if (activating)
			{
				StagePath = sourcePath;
				if (ActivationFailure is { } activationFailure)
					throw activationFailure;
			}
			if (sourcePath == PreviousPath && RollbackFailure is { } rollbackFailure)
				throw rollbackFailure;

			File.Move(sourcePath, destinationPath);
			if (activating && CorruptActivatedDatabase)
				File.WriteAllText(destinationPath, "invalid SQLite database");
		}

		public void Delete(string path)
		{
			BeforeDelete?.Invoke(path);
			File.Delete(path);
		}
	}
}
