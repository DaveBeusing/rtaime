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
        Failure? failure = null)
    {
        if (productionId.IsEmpty) throw new ArgumentException("Production journal event requires a production identity.", nameof(productionId));
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Journal category is required.", nameof(category));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Journal code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(detail)) throw new ArgumentException("Journal detail is required.", nameof(detail));

        ProductionId = productionId;
        AuthoritativeRevision = authoritativeRevision;
        Timestamp = timestamp;
        Category = category.Trim();
        Code = code.Trim();
        Detail = detail.Trim();
        CausationId = causationId;
        Failure = failure;
    }

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
    int Pending,
    int Retained,
    int Capacity);

/// <summary>
/// Bounded asynchronous in-memory reference journal. Accepted entries are append-only and causally ordered.
/// Queue pressure drops journal work rather than blocking production. Durable SQLite persistence is a separate
/// release qualification seam and is not implied by this reference implementation.
/// </summary>
public sealed class BoundedProductionJournal : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Channel<JournalWorkItem> _channel;
    private readonly List<ProductionJournalEntry> _entries = new();
    private readonly int _capacity;
    private readonly Task _worker;
    private ulong _accepted;
    private ulong _persisted;
    private ulong _dropped;
    private ulong _nextOrdinal;
    private int _pending;
    private bool _disposed;

    public BoundedProductionJournal(int capacity = 1024)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _channel = Channel.CreateBounded<JournalWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
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
                return new ProductionJournalStatistics(_accepted, _persisted, _dropped, _pending, _entries.Count, _capacity);
        }
    }

    public bool TryAppend(ProductionJournalEvent journalEvent)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Count + _pending >= _capacity)
            {
                _dropped++;
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
    }

    private async Task WorkerAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Barrier is not null)
            {
                item.Barrier.TrySetResult();
                continue;
            }

            lock (_gate)
            {
                if (item.Event is not null)
                {
                    _entries.Add(new ProductionJournalEntry(_nextOrdinal++, item.Event));
                    _persisted++;
                    _pending--;
                }
            }
        }
    }

    private sealed record JournalWorkItem(ProductionJournalEvent? Event, TaskCompletionSource? Barrier);
}
