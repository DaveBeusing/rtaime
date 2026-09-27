// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Core;

public enum HostIpcSessionDrainStatus
{
	Drained = 1,
	TimedOut = 2
}

public sealed record HostIpcSessionSnapshot(
	int ActiveSessions,
	ulong StartedSessions,
	ulong CompletedSessions,
	ulong FaultedSessions,
	bool AcceptingSessions,
	bool StopRequested,
	bool DrainTimedOut,
	string? LastFailureType,
	string? LastFailureDetail);

public sealed record HostIpcSessionDrainResult(
	HostIpcSessionDrainStatus Status,
	HostIpcSessionSnapshot Snapshot);

public sealed class HostIpcSessionTracker
{
	private readonly object _gate = new();
	private readonly Dictionary<long, SessionEntry> _active = new();
	private readonly CancellationTokenSource _stop = new();
	private long _nextSessionId;
	private ulong _startedSessions;
	private ulong _completedSessions;
	private ulong _faultedSessions;
	private bool _acceptingSessions = true;
	private bool _stopRequested;
	private bool _drainTimedOut;
	private string? _lastFailureType;
	private string? _lastFailureDetail;

	public HostIpcSessionSnapshot Snapshot
	{
		get
		{
			lock (_gate)
			{
				return new HostIpcSessionSnapshot(
					_active.Count,
					_startedSessions,
					_completedSessions,
					_faultedSessions,
					_acceptingSessions,
					_stopRequested,
					_drainTimedOut,
					_lastFailureType,
					_lastFailureDetail);
			}
		}
	}

	public bool TryStart(Func<CancellationToken, Task> handler) =>
		TryStart(handler, out _);

	public bool TryStart(Func<CancellationToken, Task> handler, out Task completion)
	{
		ArgumentNullException.ThrowIfNull(handler);

		SessionEntry entry;
		lock (_gate)
		{
			if (!_acceptingSessions)
			{
				completion = Task.CompletedTask;
				return false;
			}
			if (_nextSessionId == long.MaxValue)
				throw new InvalidOperationException("Host IPC session identity space is exhausted.");

			entry = new SessionEntry(++_nextSessionId);
			_active.Add(entry.Id, entry);
			_startedSessions++;
		}

		completion = entry.Completion.Task;
		_ = ExecuteAsync(entry, handler);
		return true;
	}

	public async ValueTask<HostIpcSessionDrainResult> StopAndDrainAsync(
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		if (timeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(timeout));

		Task[] active;
		lock (_gate)
		{
			_acceptingSessions = false;
			_stopRequested = true;
			active = _active.Values.Select(session => session.Completion.Task).ToArray();
		}

		_stop.Cancel();
		if (active.Length == 0)
			return new HostIpcSessionDrainResult(HostIpcSessionDrainStatus.Drained, Snapshot);

		try
		{
			await Task.WhenAll(active).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
			return new HostIpcSessionDrainResult(HostIpcSessionDrainStatus.Drained, Snapshot);
		}
		catch (TimeoutException)
		{
			lock (_gate)
				_drainTimedOut = true;
			return new HostIpcSessionDrainResult(HostIpcSessionDrainStatus.TimedOut, Snapshot);
		}
	}

	private async Task ExecuteAsync(SessionEntry entry, Func<CancellationToken, Task> handler)
	{
		try
		{
			await handler(_stop.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_stop.IsCancellationRequested)
		{
			// Expected cooperative shutdown.
		}
		catch (Exception exception)
		{
			lock (_gate)
			{
				_faultedSessions++;
				_lastFailureType = exception.GetType().FullName ?? exception.GetType().Name;
				_lastFailureDetail = DiagnosticRedactor.RedactText(exception.Message);
			}
		}
		finally
		{
			lock (_gate)
			{
				_active.Remove(entry.Id);
				_completedSessions++;
			}
			entry.Completion.TrySetResult(true);
		}
	}

	private sealed class SessionEntry(long id)
	{
		public long Id { get; } = id;
		public TaskCompletionSource<bool> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
