// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using rtaime.Core;

namespace rtaime.Persistence;

public sealed record ProductionCheckpoint
{
	public ProductionCheckpoint(
		Identity checkpointId,
		Identity productionId,
		Revision authoritativeRevision,
		UtcTimestamp createdAt,
		string format,
		ReadOnlyMemory<byte> payload)
	{
		if (checkpointId.IsEmpty) throw new ArgumentException("Checkpoint identity must not be empty.", nameof(checkpointId));
		if (productionId.IsEmpty) throw new ArgumentException("Production identity must not be empty.", nameof(productionId));
		if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("Checkpoint format is required.", nameof(format));
		if (payload.IsEmpty) throw new ArgumentException("Checkpoint payload must not be empty.", nameof(payload));

		CheckpointId = checkpointId;
		ProductionId = productionId;
		AuthoritativeRevision = authoritativeRevision;
		CreatedAt = createdAt;
		Format = format.Trim();
		Payload = payload.ToArray();
		ChecksumSha256 = Convert.ToHexString(SHA256.HashData(Payload.Span));
	}

	public Identity CheckpointId { get; }
	public Identity ProductionId { get; }
	public Revision AuthoritativeRevision { get; }
	public UtcTimestamp CreatedAt { get; }
	public string Format { get; }
	public ReadOnlyMemory<byte> Payload { get; }
	public string ChecksumSha256 { get; }
}

public interface IProductionCheckpointStore
{
	ValueTask WriteAsync(ProductionCheckpoint checkpoint, CancellationToken cancellationToken = default);
	ValueTask<ProductionCheckpoint?> ReadLatestAsync(Identity productionId, CancellationToken cancellationToken = default);
}

/// <summary>An atomic, ordered checkpoint transaction. A failure must leave every batch item uncommitted.</summary>
public interface IProductionCheckpointBatchStore : IProductionCheckpointStore
{
	const int MaximumBatchSize = 64;
	ValueTask WriteBatchAsync(IReadOnlyList<ProductionCheckpoint> checkpoints, CancellationToken cancellationToken = default);
}

public readonly record struct ProductionCheckpointWriterStatistics(
	ulong Accepted,
	ulong Persisted,
	ulong Dropped,
	ulong Failed,
	int Pending,
	int Capacity)
{
	public ulong WriteOperations { get; init; }
	public int ActiveBatchSize { get; init; }
	public int LastBatchSize { get; init; }
	public double ActiveWriteMilliseconds { get; init; }
	public double TotalWriteMilliseconds { get; init; }
	public double MaximumWriteMilliseconds { get; init; }
	public double MaximumQueueWaitMilliseconds { get; init; }
}

/// <summary>
/// Bounded asynchronous checkpoint writer. Checkpoint durability is deliberately decoupled from authoritative
/// commit and media execution; queue pressure or storage failure is observable but never changes production truth.
/// </summary>
public sealed class BoundedProductionCheckpointWriter : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly IProductionCheckpointStore _store;
	private readonly IAsyncDisposable? _ownedStore;
	private readonly Channel<CheckpointWorkItem> _channel;
	private readonly int _capacity;
	private readonly Task _worker;
	private ulong _accepted;
	private ulong _persisted;
	private ulong _dropped;
	private ulong _failed;
	private int _pending;
	private bool _disposed;
	private Failure? _lastFailure;
	private Task? _disposal;
	private ulong _writeOperations;
	private int _activeBatchSize;
	private int _lastBatchSize;
	private long _activeWriteStarted;
	private double _totalWriteMilliseconds;
	private double _maximumWriteMilliseconds;
	private double _maximumQueueWaitMilliseconds;

	public BoundedProductionCheckpointWriter(IProductionCheckpointStore store, int capacity = 64, bool ownsStore = false)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		_ownedStore = ownsStore
			? store as IAsyncDisposable ?? throw new ArgumentException("An owned checkpoint store must support asynchronous disposal.", nameof(store))
			: null;
		_capacity = capacity;
		_channel = Channel.CreateBounded<CheckpointWorkItem>(new BoundedChannelOptions(capacity)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
		_worker = Task.Run(WorkerAsync);
	}

	public ProductionCheckpointWriterStatistics Statistics
	{
		get
		{
			lock (_gate)
				return new ProductionCheckpointWriterStatistics(_accepted, _persisted, _dropped, _failed, _pending, _capacity)
				{
					WriteOperations = _writeOperations,
					ActiveBatchSize = _activeBatchSize,
					LastBatchSize = _lastBatchSize,
					ActiveWriteMilliseconds = _activeBatchSize == 0 ? 0 : Stopwatch.GetElapsedTime(_activeWriteStarted).TotalMilliseconds,
					TotalWriteMilliseconds = _totalWriteMilliseconds,
					MaximumWriteMilliseconds = _maximumWriteMilliseconds,
					MaximumQueueWaitMilliseconds = _maximumQueueWaitMilliseconds
				};
		}
	}

	public Failure? LastFailure
	{
		get { lock (_gate) return _lastFailure; }
	}

	public bool TryWrite(ProductionCheckpoint checkpoint)
	{
		ArgumentNullException.ThrowIfNull(checkpoint);
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_pending >= _capacity)
			{
				_dropped++;
				return false;
			}
			if (_channel.Writer.TryWrite(new CheckpointWorkItem(checkpoint, null, Stopwatch.GetTimestamp())))
			{
				_pending++;
				_accepted++;
				return true;
			}
			_dropped++;
			return false;
		}
	}

	public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
	{
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate)
			ObjectDisposedException.ThrowIf(_disposed, this);
		await _channel.Writer.WriteAsync(new CheckpointWorkItem(null, completion, 0), cancellationToken).ConfigureAwait(false);
		await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposal is null)
			{
				_disposed = true;
				_channel.Writer.TryComplete();
				_disposal = DrainAndDisposeAsync();
				_ = ObserveDisposalFailureAsync(_disposal);
			}
			return new ValueTask(_disposal);
		}
	}

	private async Task DrainAndDisposeAsync()
	{
		await _worker.ConfigureAwait(false);
		if (_ownedStore is not null)
			await _ownedStore.DisposeAsync().ConfigureAwait(false);
	}

	private async Task ObserveDisposalFailureAsync(Task disposal)
	{
		try { await disposal.ConfigureAwait(false); }
		catch (Exception exception)
		{
			// The host may stop waiting at its deadline. Retain and observe the eventual
			// cleanup failure while the same disposal task remains faulted for later joins.
			lock (_gate)
				_lastFailure ??= new Failure("checkpoint.disposal.failed", exception.Message);
		}
	}

	private void CompleteBarrier(TaskCompletionSource barrier)
	{
		Failure? failure;
		lock (_gate) failure = _lastFailure;
		if (failure is { } value)
			barrier.TrySetException(new IOException($"Checkpoint durability is degraded: {value.Code}: {value.Message}"));
		else
			barrier.TrySetResult();
	}

	private void ObserveQueueWait(long enqueuedAt)
	{
		var milliseconds = Stopwatch.GetElapsedTime(enqueuedAt).TotalMilliseconds;
		lock (_gate) _maximumQueueWaitMilliseconds = Math.Max(_maximumQueueWaitMilliseconds, milliseconds);
	}

	private async Task WorkerAsync()
	{
		var batch = new List<ProductionCheckpoint>(Math.Min(_capacity, IProductionCheckpointBatchStore.MaximumBatchSize));
		while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
		{
			if (!_channel.Reader.TryRead(out var item)) continue;
			if (item.Barrier is not null) { CompleteBarrier(item.Barrier); continue; }
			if (item.Checkpoint is null) continue;

			batch.Clear();
			batch.Add(item.Checkpoint);
			ObserveQueueWait(item.EnqueuedAt);
			TaskCompletionSource? barrier = null;
			if (_store is IProductionCheckpointBatchStore)
			{
				while (batch.Count < IProductionCheckpointBatchStore.MaximumBatchSize && _channel.Reader.TryRead(out var next))
				{
					// Never absorb a later checkpoint across an admitted flush fence.
					if (next.Barrier is not null) { barrier = next.Barrier; break; }
					if (next.Checkpoint is null) continue;
					batch.Add(next.Checkpoint);
					ObserveQueueWait(next.EnqueuedAt);
				}
			}

			var started = Stopwatch.GetTimestamp();
			lock (_gate)
			{
				_writeOperations++;
				_activeBatchSize = batch.Count;
				_activeWriteStarted = started;
			}
			try
			{
				if (_store is IProductionCheckpointBatchStore batchStore)
					await batchStore.WriteBatchAsync(batch).ConfigureAwait(false);
				else
					await _store.WriteAsync(item.Checkpoint).ConfigureAwait(false);
				lock (_gate)
				{
					_persisted += (ulong)batch.Count;
					_pending -= batch.Count;
				}
			}
			catch (Exception exception)
			{
				lock (_gate)
				{
					_failed += (ulong)batch.Count;
					_pending -= batch.Count;
					_lastFailure = new Failure("checkpoint.persistence.failed", exception.Message);
				}
			}
			finally
			{
				var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				lock (_gate)
				{
					_totalWriteMilliseconds += milliseconds;
					_maximumWriteMilliseconds = Math.Max(_maximumWriteMilliseconds, milliseconds);
					_lastBatchSize = batch.Count;
					_activeBatchSize = 0;
				}
			}
			if (barrier is not null) CompleteBarrier(barrier);
		}
	}

	private sealed record CheckpointWorkItem(ProductionCheckpoint? Checkpoint, TaskCompletionSource? Barrier, long EnqueuedAt);
}

