// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class ProductionJournalBatchIntegrationTests
{
    [Fact]
    public async Task Sqlite_batch_preserves_every_event_hash_chain_and_idempotent_ordinal()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-journal-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new SqliteProductionJournalStore(Path.Combine(root, "journal.db"));
            var first = Event(1);
            var second = Event(2);
            var third = Event(3);
            Assert.Equal(1UL, (await store.AppendAsync(first)).Ordinal);
            var result = await store.AppendBatchAsync([second, third, first]);
            Assert.Equal(new ulong[] { 2, 3, 1 }, result.Select(entry => entry.Ordinal));
            Assert.Equal(new[] { first.EventId, second.EventId, third.EventId },
                (await store.ReadAsync(0, 100)).Select(entry => entry.Event.EventId));
            var integrity = await store.VerifyIntegrityAsync();
            Assert.True(integrity.Healthy, integrity.Detail);
            Assert.Equal(3UL, integrity.Entries);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.AppendBatchAsync([]));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await store.AppendBatchAsync(Enumerable.Range(0, 65).Select(Event).ToArray()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Conflicting_event_rolls_back_the_entire_batch_without_changing_existing_durable_history()
    {
        var root = Path.Combine(Path.GetTempPath(), "rtaime-journal-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "journal.db");
            var first = Event(1);
            var rejected = Event(2);
            await using (var store = new SqliteProductionJournalStore(path))
            {
                await store.AppendAsync(first);
                var conflict = new ProductionJournalEvent(first.ProductionId, first.AuthoritativeRevision,
                    first.Timestamp, first.Category, first.Code, "different content", eventId: first.EventId);
                await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    await store.AppendBatchAsync([rejected, conflict]));
            }
            await using var reopened = new SqliteProductionJournalStore(path);
            var entries = await reopened.ReadAsync(0, 100);
            Assert.Equal(first.EventId, Assert.Single(entries).Event.EventId);
            var integrity = await reopened.VerifyIntegrityAsync();
            Assert.True(integrity.Healthy, integrity.Detail);
            Assert.Equal(1UL, integrity.Entries);
            Assert.Equal(2UL, (await reopened.AppendAsync(Event(3))).Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bounded_worker_keeps_flush_fences_ordering_failure_counts_and_all_accepted_events(bool failBatch)
    {
        var store = new ControlledBatchStore { FailSecondBatch = failBatch };
        var journal = new BoundedProductionJournal(64, store, 64);
        var events = Enumerable.Range(1, 62).Select(Event).ToArray();
        try
        {
            Assert.True(journal.TryAppend(events[0]));
            await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var item in events.Skip(1).Take(60)) Assert.True(journal.TryAppend(item));
            var flush = journal.FlushAsync().AsTask();
            store.ReleaseFirst.TrySetResult();
            await store.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(flush.IsCompleted);
            Assert.Equal(60, journal.Statistics.Pending);
            Assert.True(journal.TryAppend(events[^1]));
            store.ReleaseSecond.TrySetResult();
            if (failBatch) await Assert.ThrowsAsync<IOException>(async () => await flush);
            else await flush;
        }
        finally
        {
            store.ReleaseFirst.TrySetResult();
            store.ReleaseSecond.TrySetResult();
            await journal.DisposeAsync();
        }
        Assert.Equal(new[] { 1, 60, 1 }, store.Batches.Select(batch => batch.Length));
        Assert.Equal(events.Take(1).Concat(events.Skip(1).Take(60)).Select(item => item.EventId),
            store.Batches.Take(2).SelectMany(batch => batch).Select(item => item.EventId));
        Assert.Equal(events[^1].EventId, Assert.Single(store.Batches[^1]).EventId);
        Assert.Equal(62UL, journal.Statistics.Accepted);
        Assert.Equal(failBatch ? 2UL : 62UL, journal.Statistics.Persisted);
        Assert.Equal(failBatch ? 60UL : 0UL, journal.Statistics.Failed);
        Assert.Equal(0UL, journal.Statistics.Dropped);
        Assert.Equal(0, journal.Statistics.Pending);
        Assert.True(store.Disposed);
    }

    private static ProductionJournalEvent Event(int number) => new(
        Identity.Parse("97000000-0000-0000-0000-000000000001"), new Revision((ulong)number),
        new UtcTimestamp(DateTimeOffset.UnixEpoch.AddSeconds(number)), "qualification", "journal.batch",
        $"Event {number}");

    private sealed class ControlledBatchStore : IProductionJournalBatchStore
    {
        private readonly List<ProductionJournalEntry> _entries = [];
        private int _calls;
        public bool FailSecondBatch { get; init; }
        public bool Disposed { get; private set; }
        public List<ProductionJournalEvent[]> Batches { get; } = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IReadOnlyList<ProductionJournalEntry>> AppendBatchAsync(
            IReadOnlyList<ProductionJournalEvent> events, CancellationToken cancellationToken = default)
        {
            var snapshot = events.ToArray();
            Batches.Add(snapshot);
            var call = ++_calls;
            if (call == 1) { FirstStarted.TrySetResult(); await ReleaseFirst.Task.WaitAsync(cancellationToken); }
            if (call == 2)
            {
                SecondStarted.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
                if (FailSecondBatch) throw new IOException("Injected atomic batch failure.");
            }
            var result = snapshot.Select(item => new ProductionJournalEntry((ulong)(_entries.Count + 1), item)).ToArray();
            // Assign ordinals before publication, preserving causal order within the transaction.
            for (var index = 0; index < result.Length; index++)
                result[index] = new ProductionJournalEntry((ulong)(_entries.Count + index + 1), snapshot[index]);
            _entries.AddRange(result);
            return result;
        }
        public async ValueTask<ProductionJournalEntry> AppendAsync(ProductionJournalEvent item, CancellationToken cancellationToken = default) =>
            (await AppendBatchAsync([item], cancellationToken))[0];
        public ValueTask<IReadOnlyList<ProductionJournalEntry>> ReadAsync(ulong afterOrdinal, int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ProductionJournalEntry>>(_entries.Where(entry => entry.Ordinal > afterOrdinal).Take(limit).ToArray());
        public ValueTask<ProductionJournalIntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProductionJournalIntegrityReport(true, (ulong)_entries.Count, "Controlled store."));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
