// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

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

public readonly record struct ProductionCheckpointWriterStatistics(
	ulong Accepted,
	ulong Persisted,
	ulong Dropped,
	ulong Failed,
	int Pending,
	int Capacity);

/// <summary>
/// Bounded asynchronous checkpoint writer. Checkpoint durability is deliberately decoupled from authoritative
/// commit and media execution; queue pressure or storage failure is observable but never changes production truth.
/// </summary>
public sealed class BoundedProductionCheckpointWriter : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly IProductionCheckpointStore _store;
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

	public BoundedProductionCheckpointWriter(IProductionCheckpointStore store, int capacity = 64)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
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
				return new ProductionCheckpointWriterStatistics(_accepted, _persisted, _dropped, _failed, _pending, _capacity);
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
			_pending++;
			_accepted++;
		}

		if (_channel.Writer.TryWrite(new CheckpointWorkItem(checkpoint, null)))
			return true;

		lock (_gate)
		{
			_pending--;
			_accepted--;
			_dropped++;
		}
		return false;
	}

	public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
	{
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate)
			ObjectDisposedException.ThrowIf(_disposed, this);
		await _channel.Writer.WriteAsync(new CheckpointWorkItem(null, completion), cancellationToken).ConfigureAwait(false);
		await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
		}
		_channel.Writer.TryComplete();
		await _worker.ConfigureAwait(false);
	}

	private async Task WorkerAsync()
	{
		await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
		{
			if (item.Barrier is not null)
			{
				Failure? failure;
				lock (_gate)
					failure = _lastFailure;
				if (failure is { } value)
					item.Barrier.TrySetException(new IOException($"Checkpoint durability is degraded: {value.Code}: {value.Message}"));
				else
					item.Barrier.TrySetResult();
				continue;
			}

			if (item.Checkpoint is null)
				continue;

			try
			{
				await _store.WriteAsync(item.Checkpoint).ConfigureAwait(false);
				lock (_gate)
				{
					_persisted++;
					_pending--;
				}
			}
			catch (Exception exception)
			{
				lock (_gate)
				{
					_failed++;
					_pending--;
					_lastFailure = new Failure("checkpoint.persistence.failed", exception.Message);
				}
			}
		}
	}

	private sealed record CheckpointWorkItem(ProductionCheckpoint? Checkpoint, TaskCompletionSource? Barrier);
}
