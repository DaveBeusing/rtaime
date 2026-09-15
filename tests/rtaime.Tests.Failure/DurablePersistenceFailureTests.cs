// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.Tests.Failure;

public sealed class DurablePersistenceFailureTests
{
	[Fact]
	public async Task Journal_stall_drops_when_bounded_without_blocking_producer()
	{
		var store = new BlockingJournalStore();
		await using var journal = new BoundedProductionJournal(1, store, 4);
		var productionId = Identity.New();
		Assert.True(journal.TryAppend(Event(productionId, "first")));
		await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

		var stopwatch = Stopwatch.StartNew();
		var accepted = journal.TryAppend(Event(productionId, "second"));
		stopwatch.Stop();

		Assert.False(accepted);
		Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"TryAppend blocked for {stopwatch.Elapsed}.");
		Assert.Equal(1UL, journal.Statistics.Dropped);
		Assert.Equal(ProductionJournalHealthState.Degraded, journal.Health.State);

		store.Release.TrySetResult();
		await journal.FlushAsync();
		Assert.Equal(1UL, journal.Statistics.Persisted);
	}

	[Fact]
	public async Task Journal_storage_failure_is_observable_and_flush_fails_closed()
	{
		await using var journal = new BoundedProductionJournal(4, new FailingJournalStore(), 4);
		Assert.True(journal.TryAppend(Event(Identity.New(), "failure")));

		var exception = await Assert.ThrowsAsync<IOException>(() => journal.FlushAsync().AsTask());
		Assert.Contains("journal.persistence.failed", exception.Message, StringComparison.Ordinal);
		Assert.Equal(1UL, journal.Statistics.Failed);
		Assert.Equal(ProductionJournalHealthState.Failed, journal.Health.State);
		Assert.Equal("journal.persistence.failed", journal.Health.LastFailure?.Code);
	}

	[Fact]
	public async Task Invalid_management_storage_root_fails_initialization()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-persistence-failure", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		var blocker = Path.Combine(root, "not-a-directory");
		await File.WriteAllTextAsync(blocker, "block");
		try
		{
			await using var store = new SqliteManagementStore(Path.Combine(blocker, "management.db"));
			await Assert.ThrowsAnyAsync<Exception>(() => store.InitializeAsync().AsTask());
		}
		finally
		{
			try { Directory.Delete(root, recursive: true); }
			catch { }
		}
	}

	private static ProductionJournalEvent Event(Identity productionId, string detail) => new(
		productionId,
		Revision.Initial,
		new UtcTimestamp(DateTimeOffset.UtcNow),
		"test",
		"journal.test",
		detail);

	private sealed class BlockingJournalStore : IProductionJournalStore
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<ProductionJournalEntry> AppendAsync(ProductionJournalEvent journalEvent, CancellationToken cancellationToken = default)
		{
			Started.TrySetResult();
			await Release.Task.WaitAsync(cancellationToken);
			return new ProductionJournalEntry(1, journalEvent);
		}

		public ValueTask<IReadOnlyList<ProductionJournalEntry>> ReadAsync(ulong afterOrdinal, int limit, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<IReadOnlyList<ProductionJournalEntry>>(Array.Empty<ProductionJournalEntry>());

		public ValueTask<ProductionJournalIntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new ProductionJournalIntegrityReport(true, 0, "test"));

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class FailingJournalStore : IProductionJournalStore
	{
		public ValueTask<ProductionJournalEntry> AppendAsync(ProductionJournalEvent journalEvent, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<ProductionJournalEntry>(new IOException("Injected journal write failure."));

		public ValueTask<IReadOnlyList<ProductionJournalEntry>> ReadAsync(ulong afterOrdinal, int limit, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<IReadOnlyList<ProductionJournalEntry>>(Array.Empty<ProductionJournalEntry>());

		public ValueTask<ProductionJournalIntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(new ProductionJournalIntegrityReport(false, 0, "test failure"));

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
