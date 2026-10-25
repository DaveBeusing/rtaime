// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using Microsoft.Data.Sqlite;
using rtaime.Core;
using rtaime.Persistence;

namespace rtaime.Tests.Integration;

public sealed class ProductionCheckpointBatchIntegrationTests
{
	[Fact]
	public async Task Sqlite_batch_retains_every_revision_and_identical_duplicates_do_not_rollback_other_rows()
	{
		var root = TemporaryRoot();
		try
		{
			var path = Path.Combine(root, "management.db");
			var first = Checkpoint(1);
			var second = Checkpoint(2);
			var third = Checkpoint(3);
			await using (var store = new SqliteManagementStore(path))
			{
				await store.WriteAsync(first);
				await store.WriteBatchAsync([second, Checkpoint(1), third, Checkpoint(3)]);
				await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.WriteBatchAsync([]).AsTask());
				await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
					store.WriteBatchAsync(Enumerable.Range(1, 65).Select(Checkpoint).ToArray()).AsTask());
				await Assert.ThrowsAsync<ArgumentException>(() => store.WriteBatchAsync([null!]).AsTask());
			}
			await using var reopened = new SqliteManagementStore(path);
			Assert.Equal(new Revision(3), (await reopened.ReadLatestAsync(Production))!.AuthoritativeRevision);
			var integrity = await reopened.VerifyIntegrityAsync();
			Assert.True(integrity.Healthy, integrity.Detail);
			Assert.Equal(3, integrity.Checkpoints);
			Assert.Equal(new[] { first.CheckpointId, second.CheckpointId, third.CheckpointId }, await ReadCheckpointIdsAsync(path));
		}
		finally { Directory.Delete(root, recursive: true); }
	}

	[Fact]
	public async Task Conflicting_revision_rolls_back_the_whole_transaction_and_preserves_prior_durable_history()
	{
		var root = TemporaryRoot();
		try
		{
			var path = Path.Combine(root, "management.db");
			var first = Checkpoint(1);
			await using (var store = new SqliteManagementStore(path))
			{
				await store.WriteAsync(first);
				var conflict = new ProductionCheckpoint(Identity.New(), Production, first.AuthoritativeRevision,
					first.CreatedAt, first.Format, Encoding.UTF8.GetBytes("different payload"));
				await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteBatchAsync([Checkpoint(2), conflict, Checkpoint(3)]).AsTask());
			}
			await using var reopened = new SqliteManagementStore(path);
			Assert.Equal(first.CheckpointId, Assert.Single(await ReadCheckpointIdsAsync(path)));
			var integrity = await reopened.VerifyIntegrityAsync();
			Assert.True(integrity.Healthy, integrity.Detail);
			Assert.Equal(1, integrity.Checkpoints);
			Assert.Equal(first.ChecksumSha256, (await reopened.ReadLatestAsync(Production))!.ChecksumSha256);
		}
		finally { Directory.Delete(root, recursive: true); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Worker_preserves_capacity_order_and_flush_fences_with_atomic_batch_success_or_failure(bool failBatch)
	{
		var store = new ControlledBatchStore { FailSecondBatch = failBatch, HoldSecond = true, HoldThird = true };
		var writer = new BoundedProductionCheckpointWriter(store, capacity: 61);
		var checkpoints = Enumerable.Range(1, 62).Select(Checkpoint).ToArray();
		try
		{
			Assert.True(writer.TryWrite(checkpoints[0]));
			await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
			foreach (var checkpoint in checkpoints.Skip(1).Take(60)) Assert.True(writer.TryWrite(checkpoint));
			Assert.False(writer.TryWrite(Checkpoint(99)));
			var flush = writer.FlushAsync().AsTask();
			store.ReleaseFirst.TrySetResult();
			await store.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.False(flush.IsCompleted);
			Assert.Equal(60, writer.Statistics.Pending);
			Assert.Equal(60, writer.Statistics.ActiveBatchSize);
			Assert.True(writer.TryWrite(checkpoints[^1]));
			store.ReleaseSecond.TrySetResult();
			if (failBatch) await Assert.ThrowsAsync<IOException>(() => flush);
			else await flush;
			await store.ThirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.Equal(1, writer.Statistics.Pending);
			Assert.False(store.ReleaseThird.Task.IsCompleted);
		}
		finally
		{
			store.ReleaseFirst.TrySetResult();
			store.ReleaseSecond.TrySetResult();
			store.ReleaseThird.TrySetResult();
			await writer.DisposeAsync();
		}
		Assert.Equal(new[] { 1, 60, 1 }, store.Batches.Select(batch => batch.Length));
		Assert.Equal(checkpoints.Select(checkpoint => checkpoint.CheckpointId), store.Batches.SelectMany(batch => batch).Select(checkpoint => checkpoint.CheckpointId));
		Assert.Equal(62UL, writer.Statistics.Accepted);
		Assert.Equal(failBatch ? 2UL : 62UL, writer.Statistics.Persisted);
		Assert.Equal(failBatch ? 60UL : 0UL, writer.Statistics.Failed);
		Assert.Equal(1UL, writer.Statistics.Dropped);
		Assert.Equal(0, writer.Statistics.Pending);
		Assert.Equal(3UL, writer.Statistics.WriteOperations);
		Assert.Equal(0, writer.Statistics.ActiveBatchSize);
		Assert.False(store.Disposed);
	}

	[Fact]
	public async Task Backlog_is_split_at_the_transaction_bound_without_coalescing_revisions()
	{
		var store = new ControlledBatchStore();
		await using var writer = new BoundedProductionCheckpointWriter(store, capacity: 129);
		try
		{
			Assert.True(writer.TryWrite(Checkpoint(1)));
			await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
			foreach (var checkpoint in Enumerable.Range(2, 128).Select(Checkpoint)) Assert.True(writer.TryWrite(checkpoint));
			var flush = writer.FlushAsync().AsTask();
			store.ReleaseFirst.TrySetResult();
			await flush.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.Equal(new[] { 1, 64, 64 }, store.Batches.Select(batch => batch.Length));
			Assert.Equal(Enumerable.Range(1, 129).Select(value => (ulong)value), store.Persisted.Select(checkpoint => checkpoint.AuthoritativeRevision.Value));
			Assert.Equal(129UL, writer.Statistics.Persisted);
		}
		finally { store.ReleaseFirst.TrySetResult(); }
	}

	[Fact]
	public async Task Cancelled_flush_wait_does_not_cancel_accepted_checkpoint_storage_or_lose_its_fence()
	{
		var store = new ControlledBatchStore();
		await using var writer = new BoundedProductionCheckpointWriter(store);
		try
		{
			Assert.True(writer.TryWrite(Checkpoint(1)));
			await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.True(writer.TryWrite(Checkpoint(2)));
			using var deadline = new CancellationTokenSource();
			var timedOutFlush = writer.FlushAsync(deadline.Token).AsTask();
			deadline.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timedOutFlush);
			Assert.Equal(2, writer.Statistics.Pending);
			var laterFlush = writer.FlushAsync().AsTask();
			store.ReleaseFirst.TrySetResult();
			await laterFlush.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.Equal(new ulong[] { 1, 2 }, store.Persisted.Select(checkpoint => checkpoint.AuthoritativeRevision.Value));
			Assert.Equal(2UL, writer.Statistics.Persisted);
			Assert.All(store.Tokens, token => Assert.False(token.CanBeCanceled));
		}
		finally { store.ReleaseFirst.TrySetResult(); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Expired_shutdown_wait_preserves_owned_store_until_the_same_disposal_task_finishes(bool failDisposal)
	{
		var store = new ControlledBatchStore { FailDisposal = failDisposal };
		var writer = new BoundedProductionCheckpointWriter(store, ownsStore: true);
		Assert.True(writer.TryWrite(Checkpoint(1)));
		await store.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.True(writer.TryWrite(Checkpoint(2)));
		var firstDisposal = writer.DisposeAsync().AsTask();
		var secondDisposal = writer.DisposeAsync().AsTask();
		Assert.Same(firstDisposal, secondDisposal);
		try
		{
			using var deadline = new CancellationTokenSource();
			var boundedWait = firstDisposal.WaitAsync(deadline.Token);
			deadline.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundedWait);
			Assert.False(firstDisposal.IsCompleted);
			Assert.False(secondDisposal.IsCompleted);
			Assert.False(store.Disposed);
			Assert.Equal(0, store.DisposeCalls);
		}
		finally { store.ReleaseFirst.TrySetResult(); }
		if (failDisposal)
		{
			await Assert.ThrowsAsync<IOException>(() => firstDisposal);
			await Assert.ThrowsAsync<IOException>(() => secondDisposal);
		}
		else
		{
			await firstDisposal.WaitAsync(TimeSpan.FromSeconds(3));
			await secondDisposal;
		}
		Assert.True(store.Disposed);
		Assert.Equal(1, store.DisposeCalls);
		Assert.Equal(2UL, writer.Statistics.Persisted);
		Assert.Equal(0UL, writer.Statistics.Failed);
		Assert.Equal(new ulong[] { 1, 2 }, store.Persisted.Select(checkpoint => checkpoint.AuthoritativeRevision.Value));
	}

	[Fact]
	public async Task Non_batch_store_retains_per_item_failure_accounting_and_latched_flush_failure()
	{
		var store = new SingleCheckpointStore();
		await using var writer = new BoundedProductionCheckpointWriter(store);
		foreach (var checkpoint in Enumerable.Range(1, 3).Select(Checkpoint)) Assert.True(writer.TryWrite(checkpoint));
		await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync().AsTask());
		await Assert.ThrowsAsync<IOException>(() => writer.FlushAsync().AsTask());
		Assert.Equal(new ulong[] { 1, 3 }, store.Persisted.Select(checkpoint => checkpoint.AuthoritativeRevision.Value));
		Assert.Equal(3UL, writer.Statistics.Accepted);
		Assert.Equal(2UL, writer.Statistics.Persisted);
		Assert.Equal(1UL, writer.Statistics.Failed);
		Assert.Equal(0, writer.Statistics.Pending);
		Assert.Equal(3UL, writer.Statistics.WriteOperations);
	}

	private static readonly Identity Production = Identity.Parse("98000000-0000-0000-0000-000000000001");
	private static ProductionCheckpoint Checkpoint(int revision) => new(Identity.New(), Production, new Revision((ulong)revision),
		new UtcTimestamp(DateTimeOffset.UnixEpoch.AddSeconds(revision)), "checkpoint-qualification.v1", Encoding.UTF8.GetBytes($"revision:{revision}"));
	private static string TemporaryRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "rtaime-checkpoint-batch-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static async Task<IReadOnlyList<Identity>> ReadCheckpointIdsAsync(string path)
	{
		await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
		await connection.OpenAsync();
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT checkpoint_id FROM production_checkpoints ORDER BY authoritative_revision;";
		await using var reader = await command.ExecuteReaderAsync();
		var ids = new List<Identity>();
		while (await reader.ReadAsync()) ids.Add(Identity.Parse(reader.GetString(0)));
		return ids;
	}

	private sealed class ControlledBatchStore : IProductionCheckpointBatchStore, IAsyncDisposable
	{
		public bool FailSecondBatch { get; init; }
		public bool FailDisposal { get; init; }
		public bool HoldSecond { get; init; }
		public bool HoldThird { get; init; }
		public bool Disposed { get; private set; }
		public int DisposeCalls { get; private set; }
		public List<ProductionCheckpoint[]> Batches { get; } = [];
		public List<ProductionCheckpoint> Persisted { get; } = [];
		public List<CancellationToken> Tokens { get; } = [];
		public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ThirdStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseThird { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public async ValueTask WriteBatchAsync(IReadOnlyList<ProductionCheckpoint> checkpoints, CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(Disposed, this);
			var batch = checkpoints.ToArray();
			Batches.Add(batch);
			Tokens.Add(cancellationToken);
			var call = Batches.Count;
			if (call == 1) { FirstStarted.TrySetResult(); await ReleaseFirst.Task.WaitAsync(cancellationToken); }
			if (call == 2)
			{
				SecondStarted.TrySetResult();
				if (HoldSecond) await ReleaseSecond.Task.WaitAsync(cancellationToken);
				if (FailSecondBatch) throw new IOException("Injected atomic checkpoint transaction failure.");
			}
			if (call == 3)
			{
				ThirdStarted.TrySetResult();
				if (HoldThird) await ReleaseThird.Task.WaitAsync(cancellationToken);
			}
			ObjectDisposedException.ThrowIf(Disposed, this);
			Persisted.AddRange(batch);
		}
		public ValueTask WriteAsync(ProductionCheckpoint checkpoint, CancellationToken cancellationToken = default) => WriteBatchAsync([checkpoint], cancellationToken);
		public ValueTask<ProductionCheckpoint?> ReadLatestAsync(Identity productionId, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(Persisted.LastOrDefault(checkpoint => checkpoint.ProductionId == productionId));
		public ValueTask DisposeAsync()
		{
			DisposeCalls++;
			Disposed = true;
			return FailDisposal ? ValueTask.FromException(new IOException("Injected store disposal failure.")) : ValueTask.CompletedTask;
		}
	}

	private sealed class SingleCheckpointStore : IProductionCheckpointStore
	{
		public List<ProductionCheckpoint> Persisted { get; } = [];
		public ValueTask WriteAsync(ProductionCheckpoint checkpoint, CancellationToken cancellationToken = default)
		{
			if (checkpoint.AuthoritativeRevision.Value == 2) return ValueTask.FromException(new IOException("Injected single-checkpoint failure."));
			Persisted.Add(checkpoint);
			return ValueTask.CompletedTask;
		}
		public ValueTask<ProductionCheckpoint?> ReadLatestAsync(Identity productionId, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(Persisted.LastOrDefault(checkpoint => checkpoint.ProductionId == productionId));
	}
}
