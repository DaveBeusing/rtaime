// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using Microsoft.Data.Sqlite;

namespace rtaime.Persistence;

/// <summary>Validates existing store identity before permitting transactional initialization of an empty database.</summary>
internal static class SqliteStoreSchema
{
	internal static async ValueTask EnsureAsync(
		SqliteConnection connection,
		string component,
		int supportedVersion,
		string bootstrapSql,
		IReadOnlyList<string> requiredTables,
		CancellationToken cancellationToken)
	{
		// The immediate transaction serializes independent store instances through inspection and bootstrap.
		// In particular, do not set journal_mode or execute any DDL before an existing identity is accepted.
		using var transaction = connection.BeginTransaction();
		await using (var inventory = connection.CreateCommand())
		{
			inventory.Transaction = transaction;
			inventory.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT GLOB 'sqlite_*';";
			var count = (long)(await inventory.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
			if (count == 0)
			{
				foreach (var pragma in new[] { "PRAGMA user_version;", "PRAGMA application_id;" })
				{
					await using var identity = connection.CreateCommand();
					identity.Transaction = transaction;
					identity.CommandText = pragma;
					if ((long)(await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0)
						throw new NotSupportedException($"Existing SQLite database has an unrecognized '{component}' schema identity.");
				}

				await using var bootstrap = connection.CreateCommand();
				bootstrap.Transaction = transaction;
				bootstrap.CommandText = bootstrapSql;
				await bootstrap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
			}
		}

		await using (var metadata = connection.CreateCommand())
		{
			metadata.Transaction = transaction;
			metadata.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'schema_metadata';";
			if ((long)(await metadata.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 1)
				throw new NotSupportedException($"Existing SQLite database has no supported '{component}' schema metadata.");
		}

		await using (var version = connection.CreateCommand())
		{
			version.Transaction = transaction;
			version.CommandText = "SELECT schema_version FROM schema_metadata WHERE component = @component;";
			version.Parameters.AddWithValue("@component", component);
			await using var reader = await version.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
				|| reader.GetValue(0) is not long currentVersion
				|| currentVersion != supportedVersion
				|| await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
				throw new NotSupportedException($"Unsupported '{component}' persistence schema version; expected {supportedVersion}.");
		}

		foreach (var table in requiredTables)
		{
			await using var required = connection.CreateCommand();
			required.Transaction = transaction;
			required.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = @name;";
			required.Parameters.AddWithValue("@name", table);
			if ((long)(await required.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 1)
				throw new InvalidDataException($"Existing '{component}' store is missing table '{table}'; explicit state recovery is required.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		transaction.Commit();
	}
}
