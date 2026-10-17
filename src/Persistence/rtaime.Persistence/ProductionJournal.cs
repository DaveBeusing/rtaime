// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Collections.ObjectModel;
using System.Threading.Channels;
using rtaime.Core;

namespace rtaime.Persistence;

public sealed record ProductionJournalEvent
{
	public ProductionJournalEvent(
		Identity productionId,
		Revision authoritativeRevision,
		UtcTimestamp timestamp,
		string category,
		string code,
		string detail,
		Identity? causationId = null,
		Failure? failure = null,
		Identity? eventId = null)
	{
		if (productionId.IsEmpty) throw new ArgumentException("Production journal event requires a production identity.", nameof(productionId));
		if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Journal category is required.", nameof(category));
		if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Journal code is required.", nameof(code));
		if (string.IsNullOrWhiteSpace(detail)) throw new ArgumentException("Journal detail is required.", nameof(detail));

		EventId = eventId ?? Identity.New();
		ProductionId = productionId;
		AuthoritativeRevision = authoritativeRevision;
		Timestamp = timestamp;
		Category = category.Trim();
		Code = code.Trim();
		Detail = detail.Trim();
		CausationId = causationId;
		Failure = failure;
	}

	public Identity EventId { get; }
	public Identity ProductionId { get; }
	public Revision AuthoritativeRevision { get; }
	public UtcTimestamp Timestamp { get; }
	public string Category { get; }
	public string Code { get; }
	public string Detail { get; }
	public Identity? CausationId { get; }
	public Failure? Failure { get; }
}

public sealed record ProductionJournalEntry(ulong Ordinal, ProductionJournalEvent Event);

public readonly record struct ProductionJournalStatistics(
	ulong Accepted,
	ulong Persisted,
	ulong Dropped,
	ulong Failed,
	int Pending,
	int Retained,
	int Capacity);

public enum ProductionJournalHealthState
{
	Healthy = 1,
	Degraded = 2,
	Failed = 3
}

public sealed record ProductionJournalHealthSnapshot(
	ProductionJournalHealthState State,
	Failure? LastFailure,
	DateTimeOffset UpdatedAt);

public interface IProductionJournalStore : IAsyncDisposable
{
	ValueTask<ProductionJournalEntry> AppendAsync(ProductionJournalEvent journalEvent, CancellationToken cancellationToken = default);
	ValueTask<IReadOnlyList<ProductionJournalEntry>> ReadAsync(ulong afterOrdinal, int limit, CancellationToken cancellationToken = default);
	ValueTask<ProductionJournalIntegrityReport> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional atomic batch capability; every accepted event remains represented in order.</summary>
public interface IProductionJournalBatchStore : IProductionJournalStore
{
    const int MaximumBatchSize = 64;
    ValueTask<IReadOnlyList<ProductionJournalEntry>> AppendBatchAsync(
        IReadOnlyList<ProductionJournalEvent> journalEvents, CancellationToken cancellationToken = default);
}

public sealed record ProductionJournalIntegrityReport(bool Healthy, ulong Entries, string Detail);

/// <summary>
/// Bounded asynchronous production journal ingress. TryAppend never waits for storage. A configured durable
/// store is written only by the single background worker, preserving causal order while keeping storage latency
/// outside production command and media execution paths.
/// </summary>
public sealed class BoundedProductionJournal : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly Channel<JournalWorkItem> _channel;
	private readonly List<ProductionJournalEntry> _entries = new();
	private readonly int _capacity;
	private readonly int _retainedCapacity;
	private readonly IProductionJournalStore? _store;
	private readonly Task _worker;
	private ulong _accepted;
	private ulong _persisted;
	private ulong _dropped;
	private ulong _failed;
	private ulong _nextOrdinal = 1;
	private int _pending;
	private bool _disposed;
	private Failure? _lastFailure;
	private DateTimeOffset _healthUpdatedAt = DateTimeOffset.UtcNow;

	public BoundedProductionJournal(
		int capacity = 1024,
		IProductionJournalStore? store = null,
		int retainedCapacity = 256)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		if (retainedCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(retainedCapacity));
		_capacity = capacity;
		_retainedCapacity = retainedCapacity;
		_store = store;
		_channel = Channel.CreateBounded<JournalWorkItem>(new BoundedChannelOptions(capacity)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
		_worker = Task.Run(WorkerAsync);
	}

	public IReadOnlyList<ProductionJournalEntry> Entries
	{
		get
		{
			lock (_gate)
				return new ReadOnlyCollection<ProductionJournalEntry>(_entries.ToArray());
		}
	}

	public ProductionJournalStatistics Statistics
	{
		get
		{
			lock (_gate)
				return new ProductionJournalStatistics(_accepted, _persisted, _dropped, _failed, _pending, _entries.Count, _capacity);
		}
	}

	public ProductionJournalHealthSnapshot Health
	{
		get
		{
			lock (_gate)
			{
				var state = _failed > 0 && _persisted == 0
					? ProductionJournalHealthState.Failed
					: _failed > 0 || _dropped > 0
						? ProductionJournalHealthState.Degraded
						: ProductionJournalHealthState.Healthy;
				return new ProductionJournalHealthSnapshot(state, _lastFailure, _healthUpdatedAt);
			}
		}
	}

	public bool TryAppend(ProductionJournalEvent journalEvent)
	{
		ArgumentNullException.ThrowIfNull(journalEvent);
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_pending >= _capacity)
			{
				_dropped++;
				_healthUpdatedAt = DateTimeOffset.UtcNow;
				return false;
			}

			_pending++;
			_accepted++;
		}

		if (_channel.Writer.TryWrite(new JournalWorkItem(journalEvent, null)))
			return true;

		lock (_gate)
		{
			_pending--;
			_accepted--;
			_dropped++;
			_healthUpdatedAt = DateTimeOffset.UtcNow;
		}
		return false;
	}

	public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
	{
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate)
			ObjectDisposedException.ThrowIf(_disposed, this);

		await _channel.Writer.WriteAsync(new JournalWorkItem(null, completion), cancellationToken).ConfigureAwait(false);
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
		if (_store is not null)
			await _store.DisposeAsync().ConfigureAwait(false);
	}

    private void CompleteBarrier(TaskCompletionSource barrier)
    {
        Failure? failure;
        lock (_gate) failure = _lastFailure;
        if (failure is { } value)
            barrier.TrySetException(new IOException($"Production journal durability is degraded: {value.Code}: {value.Message}"));
        else
            barrier.TrySetResult();
    }

    private void RetainPersisted(ProductionJournalEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > _retainedCapacity) _entries.RemoveAt(0);
        _persisted++;
        _pending--;
        _healthUpdatedAt = DateTimeOffset.UtcNow;
    }

    private void ObserveWriteFailure(Exception exception, int count)
    {
        lock (_gate)
        {
            _failed += (ulong)count;
            _pending -= count;
            _lastFailure = new Failure("journal.persistence.failed", exception.Message);
            _healthUpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private async Task WorkerAsync()
    {
        var batch = new List<ProductionJournalEvent>(Math.Min(_capacity, IProductionJournalBatchStore.MaximumBatchSize));
        while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            if (!_channel.Reader.TryRead(out var item)) continue;
            if (item.Barrier is not null) { CompleteBarrier(item.Barrier); continue; }
            if (item.Event is null) continue;

            if (_store is not IProductionJournalBatchStore batchStore)
            {
                try
                {
                    var entry = _store is null
                        ? new ProductionJournalEntry(_nextOrdinal++, item.Event)
                        : await _store.AppendAsync(item.Event).ConfigureAwait(false);
                    lock (_gate) RetainPersisted(entry);
                }
                catch (Exception exception) { ObserveWriteFailure(exception, 1); }
                continue;
            }

            batch.Clear();
            batch.Add(item.Event);
            TaskCompletionSource? barrier = null;
            while (batch.Count < IProductionJournalBatchStore.MaximumBatchSize && _channel.Reader.TryRead(out var next))
            {
                // A flush fence belongs after all preceding events and before all following events.
                if (next.Barrier is not null) { barrier = next.Barrier; break; }
                if (next.Event is not null) batch.Add(next.Event);
            }
            try
            {
                var entries = await batchStore.AppendBatchAsync(batch).ConfigureAwait(false);
                if (entries.Count != batch.Count ||
                    entries.Where((entry, index) => entry.Event.EventId != batch[index].EventId).Any())
                    throw new InvalidDataException("Journal batch result does not match the accepted event sequence.");
                lock (_gate)
                    foreach (var entry in entries) RetainPersisted(entry);
            }
            catch (Exception exception) { ObserveWriteFailure(exception, batch.Count); }
            if (barrier is not null) CompleteBarrier(barrier);
        }
    }

	private sealed record JournalWorkItem(ProductionJournalEvent? Event, TaskCompletionSource? Barrier);
}
