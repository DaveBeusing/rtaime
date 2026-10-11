// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Operator;

// The management poller may replace pending presentation work; production commands never use this queue.
internal sealed class LatestAudioRefreshDelivery : IDisposable
{
	private readonly object _gate = new();
	private readonly Action<Action> _post;
	private Action? _pending;
	private bool _queued;
	private bool _disposed;
	private long _version;

	internal LatestAudioRefreshDelivery(Action<Action> post) =>
		_post = post ?? throw new ArgumentNullException(nameof(post));

	internal long Version { get { lock (_gate) return _version; } }

	internal void Enqueue(long version, Action update)
	{
		ArgumentNullException.ThrowIfNull(update);
		lock (_gate)
		{
			if (_disposed || version != _version)
				return;
			_pending = update;
			if (_queued)
				return;
			_queued = true;
		}
		_post(Drain);
	}

	internal void Invalidate()
	{
		lock (_gate)
		{
			_version++;
			_pending = null;
		}
	}

	private void Drain()
	{
		Action? update;
		lock (_gate)
		{
			update = _disposed ? null : _pending;
			_pending = null;
			_queued = false;
		}
		update?.Invoke();
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_disposed = true;
			_version++;
			_pending = null;
		}
	}
}
