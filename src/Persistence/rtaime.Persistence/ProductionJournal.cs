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
					item.Barrier.TrySetException(new IOException($"Production journal durability is degraded: {value.Code}: {value.Message}"));
				else
					item.Barrier.TrySetResult();
				continue;
			}

			if (item.Event is null)
				continue;

			try
			{
				var entry = _store is null
					? new ProductionJournalEntry(_nextOrdinal++, item.Event)
					: await _store.AppendAsync(item.Event).ConfigureAwait(false);
				lock (_gate)
				{
					_entries.Add(entry);
					while (_entries.Count > _retainedCapacity)
						_entries.RemoveAt(0);
					_persisted++;
					_pending--;
					_healthUpdatedAt = DateTimeOffset.UtcNow;
				}
			}
			catch (Exception exception)
			{
				lock (_gate)
				{
					_failed++;
					_pending--;
					_lastFailure = new Failure("journal.persistence.failed", exception.Message);
					_healthUpdatedAt = DateTimeOffset.UtcNow;
				}
			}
		}
	}

	private sealed record JournalWorkItem(ProductionJournalEvent? Event, TaskCompletionSource? Barrier);
}
