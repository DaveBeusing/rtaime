// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class DurablePersistenceIntegrationTests
{
	[Fact]
	public async Task Management_documents_and_checkpoints_survive_reopen_with_integrity()
	{
		var root = TempDirectory();
		try
		{
			var path = Path.Combine(root, "management.db");
			var productionId = Identity.New();
			await using (var store = new SqliteManagementStore(path))
			{
				var created = await store.PutDocumentAsync("configuration", "control", "{\"mode\":\"reference\"}", 0);
				Assert.True(created.Written);
				Assert.Equal(1UL, created.Document!.Version);

				var updated = await store.PutDocumentAsync("configuration", "control", "{\"mode\":\"qualified\"}", 1);
				Assert.True(updated.Written);
				Assert.Equal(2UL, updated.Document!.Version);

				var conflict = await store.PutDocumentAsync("configuration", "control", "{\"mode\":\"stale\"}", 1);
				Assert.False(conflict.Written);
				Assert.Equal("persistence.version_conflict", conflict.Failure?.Code);

				var checkpoint = new ProductionCheckpoint(
					Identity.New(),
					productionId,
					new Revision(7),
					new UtcTimestamp(DateTimeOffset.UtcNow),
					"rtaime.control.authority.v1",
					Encoding.UTF8.GetBytes("{\"revision\":7}"));
				await store.WriteAsync(checkpoint);
				await store.WriteAsync(checkpoint);
			}

			await using (var reopened = new SqliteManagementStore(path))
			{
				var document = await reopened.GetDocumentAsync("configuration", "control");
				Assert.NotNull(document);
				Assert.Equal(2UL, document.Version);
				Assert.Equal("{\"mode\":\"qualified\"}", document.Json);

				var checkpoint = await reopened.ReadLatestAsync(productionId);
				Assert.NotNull(checkpoint);
				Assert.Equal(new Revision(7), checkpoint.AuthoritativeRevision);
				Assert.Equal("{\"revision\":7}", Encoding.UTF8.GetString(checkpoint.Payload.Span));

				var integrity = await reopened.VerifyIntegrityAsync();
				Assert.True(integrity.Healthy, integrity.Detail);
				Assert.Equal(1, integrity.Documents);
				Assert.Equal(1, integrity.Checkpoints);

				var conflicting = new ProductionCheckpoint(
					Identity.New(),
					productionId,
					new Revision(7),
					new UtcTimestamp(DateTimeOffset.UtcNow),
					"rtaime.control.authority.v1",
					Encoding.UTF8.GetBytes("{\"revision\":7,\"different\":true}"));
				await Assert.ThrowsAsync<InvalidDataException>(() => reopened.WriteAsync(conflicting).AsTask());
			}
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Production_journal_is_durable_ordered_idempotent_and_integrity_verifiable()
	{
		var root = TempDirectory();
		try
		{
			var path = Path.Combine(root, "production-journal.db");
			var productionId = Identity.New();
			var firstId = Identity.New();
			var first = new ProductionJournalEvent(
				productionId,
				Revision.Initial,
				new UtcTimestamp(DateTimeOffset.UtcNow),
				"control",
				"control.authoritative.committed",
				"Initial state committed.",
				eventId: firstId);
			var second = new ProductionJournalEvent(
				productionId,
				new Revision(1),
				new UtcTimestamp(DateTimeOffset.UtcNow.AddMilliseconds(1)),
				"control",
				"control.authoritative.committed",
				"Revision one committed.",
				firstId,
				eventId: Identity.New());

			await using (var store = new SqliteProductionJournalStore(path))
			{
				var firstAppend = await store.AppendAsync(first);
				var secondAppend = await store.AppendAsync(second);
				var replay = await store.AppendAsync(first);
				Assert.Equal(1UL, firstAppend.Ordinal);
				Assert.Equal(2UL, secondAppend.Ordinal);
				Assert.Equal(firstAppend.Ordinal, replay.Ordinal);

				var conflict = new ProductionJournalEvent(
					productionId,
					Revision.Initial,
					first.Timestamp,
					"control",
					"control.authoritative.committed",
					"Different content.",
					eventId: firstId);
				await Assert.ThrowsAsync<InvalidDataException>(() => store.AppendAsync(conflict).AsTask());
			}

			await using (var reopened = new SqliteProductionJournalStore(path))
			{
				var entries = await reopened.ReadAsync(0, 10);
				Assert.Equal(2, entries.Count);
				Assert.Equal(firstId, entries[0].Event.EventId);
				Assert.Equal(second.EventId, entries[1].Event.EventId);
				Assert.Equal(firstId, entries[1].Event.CausationId);
				var integrity = await reopened.VerifyIntegrityAsync();
				Assert.True(integrity.Healthy, integrity.Detail);
				Assert.Equal(2UL, integrity.Entries);
			}
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	[Fact]
	public async Task Bounded_journal_flush_persists_events_across_store_reopen()
	{
		var root = TempDirectory();
		try
		{
			var path = Path.Combine(root, "bounded-journal.db");
			var productionId = Identity.New();
			await using (var journal = new BoundedProductionJournal(8, new SqliteProductionJournalStore(path), 4))
			{
				Assert.True(journal.TryAppend(new ProductionJournalEvent(
					productionId,
					Revision.Initial,
					new UtcTimestamp(DateTimeOffset.UtcNow),
					"runtime",
					"runtime.commit.observed",
					"Commit observed.")));
				await journal.FlushAsync();
				Assert.Equal(1UL, journal.Statistics.Persisted);
				Assert.Equal(ProductionJournalHealthState.Healthy, journal.Health.State);
			}

			await using var reopened = new SqliteProductionJournalStore(path);
			var entries = await reopened.ReadAsync(0, 10);
			Assert.Single(entries);
			Assert.Equal("runtime.commit.observed", entries[0].Event.Code);
		}
		finally
		{
			DeleteDirectory(root);
		}
	}

	private static string TempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "rtaime-persistence-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static void DeleteDirectory(string path)
	{
		try { Directory.Delete(path, recursive: true); }
		catch { }
	}
}
