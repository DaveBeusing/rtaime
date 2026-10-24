// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.ControlHost;

internal enum ControlRequestReservation { Owner, Replay, Conflict, CapacityExceeded }

internal readonly record struct ControlRequestCacheSnapshot(
	int Entries, int InFlight, long Replays, long Conflicts, long CapacityRejections);

internal sealed class ControlRequestCache
{
	private readonly int _capacity;
	private readonly object _gate = new();
	private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
	private readonly Queue<Entry> _completed = new();
	private long _replays;
	private long _conflicts;
	private long _capacityRejections;

	public ControlRequestCache(int capacity)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		_capacity = capacity;
	}

	public ControlRequestCacheSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return new(_entries.Count, _entries.Count - _completed.Count, _replays, _conflicts, _capacityRejections);
		}
	}

	public ControlRequestReservation Reserve(string requestId, string canonical, out Entry? reservation)
	{
		lock (_gate)
		{
			if (_entries.TryGetValue(requestId, out reservation))
			{
				if (!string.Equals(reservation.Canonical, canonical, StringComparison.Ordinal))
				{
					_conflicts++;
					return ControlRequestReservation.Conflict;
				}
				_replays++;
				return ControlRequestReservation.Replay;
			}

			if (_entries.Count == _capacity)
			{
				if (!_completed.TryDequeue(out var expired))
				{
					_capacityRejections++;
					return ControlRequestReservation.CapacityExceeded;
				}
				_entries.Remove(expired.RequestId);
			}

			reservation = new Entry(requestId, canonical);
			_entries.Add(requestId, reservation);
			return ControlRequestReservation.Owner;
		}
	}

	public void Complete(Entry reservation, byte[] response)
	{
		lock (_gate)
		{
			if (reservation.Completion.TrySetResult(response))
				_completed.Enqueue(reservation);
		}
	}

	internal sealed class Entry(string requestId, string canonical)
	{
		public string RequestId { get; } = requestId;
		public string Canonical { get; } = canonical;
		public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<byte[]> Response => Completion.Task;
	}
}
