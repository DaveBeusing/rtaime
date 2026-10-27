// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Media.Contracts;

namespace rtaime.Operator;

// A single pending Dispatcher callback drains at most one Program frame and sixteen
// source frames. Replaced payloads are released without conversion on the reader thread.
internal readonly record struct MonitoringObservation(
	MonitoringFrame Frame,
	long ConnectionEpoch,
	long PreviewRouteVersion,
	long ProgramRouteVersion,
	DateTimeOffset ReceivedAt);

internal sealed class LatestMonitoringFrameDelivery : IDisposable
{
	internal const int MaximumPendingSources = 16;
	internal const int MaximumPendingFrames = MaximumPendingSources + 1;

	private readonly object _gate = new();
	private readonly Action<Action> _post;
	private readonly Action<MonitoringObservation> _apply;
	private readonly Dictionary<string, PendingSource> _sources = new(StringComparer.Ordinal);
	private MonitoringObservation? _program;
	private bool _queued;
	private bool _disposed;
	private long _order;
	private long _version;
	private long _dropped;

	private readonly record struct PendingSource(MonitoringObservation Observation, long Order);

	internal LatestMonitoringFrameDelivery(Action<Action> post, Action<MonitoringObservation> apply)
	{
		_post = post ?? throw new ArgumentNullException(nameof(post));
		_apply = apply ?? throw new ArgumentNullException(nameof(apply));
	}

	internal int PendingFrameCount
	{
		get { lock (_gate) return _sources.Count + (_program is null ? 0 : 1); }
	}

	internal long DroppedFrames
	{
		get { lock (_gate) return _dropped; }
	}

	internal void Enqueue(MonitoringObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation.Frame);
		bool schedule;
		lock (_gate)
		{
			if (_disposed) return;
			if (observation.Frame.Descriptor.StreamKind == MonitoringStreamKind.Program)
			{
				if (_program is not null) _dropped++;
				_program = observation;
			}
			else
			{
				var key = observation.Frame.Descriptor.SourceId.ToString();
				if (_sources.ContainsKey(key))
					_dropped++;
				else if (_sources.Count == MaximumPendingSources)
				{
					var oldest = _sources.MinBy(entry => entry.Value.Order).Key;
					_sources.Remove(oldest);
					_dropped++;
				}
				_sources[key] = new PendingSource(observation, ++_order);
			}
			schedule = !_queued;
			_queued = true;
		}

		if (schedule)
			_post(Drain);
	}

	// Invalidates pending callbacks without waiting for a potentially blocked Dispatcher.
	internal void Invalidate()
	{
		lock (_gate)
		{
			_version++;
			_program = null;
			_sources.Clear();
		}
	}

	private void Drain()
	{
		MonitoringObservation[] batch;
		long version;
		lock (_gate)
		{
			if (_disposed)
			{
				_queued = false;
				return;
			}
			version = _version;
			batch = (_program is { } program
				? new[] { program }.Concat(_sources.Values.OrderBy(value => value.Order).Select(value => value.Observation))
				: _sources.Values.OrderBy(value => value.Order).Select(value => value.Observation)).ToArray();
			_program = null;
			_sources.Clear();
			_queued = false;
		}

		foreach (var observation in batch)
		{
			lock (_gate)
			{
				if (_disposed || version != _version)
					return;
			}
			_apply(observation);
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_version++;
			_program = null;
			_sources.Clear();
		}
	}
}
