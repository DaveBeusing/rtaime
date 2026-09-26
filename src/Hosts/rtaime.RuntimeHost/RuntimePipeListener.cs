// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.IO.Pipes;
using rtaime.Core;

namespace rtaime.RuntimeHost;

public enum RuntimePipeListenerState
{
	Created = 1,
	Running = 2,
	Stopping = 3,
	Stopped = 4,
	Faulted = 5
}

public sealed record RuntimePipeListenerSnapshot(
	string Endpoint,
	string Role,
	RuntimePipeListenerState State,
	string? ExceptionType,
	string? FailureDetail,
	DateTimeOffset UpdatedAt);

internal sealed class RuntimePipeListener : IAsyncDisposable
{
	private const int MaxConsecutiveRecoverableAcceptFailures = 3;
	private static readonly TimeSpan RecoverableAcceptRetryDelay = TimeSpan.FromMilliseconds(25);

	private readonly object _gate = new();
	private readonly string _endpoint;
	private readonly string _role;
	private readonly Func<NamedPipeServerStream> _pipeFactory;
	private readonly Func<NamedPipeServerStream, CancellationToken, Task> _connectionHandler;
	private readonly CancellationTokenSource _stop = new();
	private CancellationTokenSource? _runStop;
	private Task? _completion;
	private Exception? _terminalFault;
	private RuntimePipeListenerSnapshot _snapshot;
	private int _disposeStarted;

	public RuntimePipeListener(
		string endpoint,
		string role,
		Func<NamedPipeServerStream> pipeFactory,
		Func<NamedPipeServerStream, CancellationToken, Task> connectionHandler)
	{
		if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Listener endpoint is required.", nameof(endpoint));
		if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("Listener role is required.", nameof(role));
		_endpoint = endpoint.Trim();
		_role = role.Trim();
		_pipeFactory = pipeFactory ?? throw new ArgumentNullException(nameof(pipeFactory));
		_connectionHandler = connectionHandler ?? throw new ArgumentNullException(nameof(connectionHandler));
		_snapshot = CreateSnapshot(RuntimePipeListenerState.Created, null);
	}

	public event Action<RuntimePipeListenerSnapshot, Exception>? TerminalFaulted;

	public RuntimePipeListenerSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return _snapshot;
		}
	}

	public bool Running
	{
		get
		{
			lock (_gate)
				return _snapshot.State == RuntimePipeListenerState.Running &&
					_completion is { IsCompleted: false };
		}
	}

	public Task Completion
	{
		get
		{
			lock (_gate)
				return _completion ?? Task.CompletedTask;
		}
	}

	public Exception? TerminalFault
	{
		get
		{
			lock (_gate)
				return _terminalFault;
		}
	}

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
			if (_completion is not null)
				throw new InvalidOperationException($"Listener '{_role}' has already been started.");

			_runStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
			_snapshot = CreateSnapshot(RuntimePipeListenerState.Running, null);
			_completion = RunAsync(_runStop.Token);
			return Task.CompletedTask;
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
		{
			await Completion.ConfigureAwait(false);
			return;
		}

		lock (_gate)
		{
			if (_snapshot.State == RuntimePipeListenerState.Running)
				_snapshot = CreateSnapshot(RuntimePipeListenerState.Stopping, null);
		}

		_stop.Cancel();
		_runStop?.Cancel();
		await Completion.ConfigureAwait(false);

		lock (_gate)
		{
			if (_snapshot.State != RuntimePipeListenerState.Faulted)
				_snapshot = CreateSnapshot(RuntimePipeListenerState.Stopped, null);
		}

		_runStop?.Dispose();
		_stop.Dispose();
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		try
		{
			await AcceptLoopAsync(cancellationToken).ConfigureAwait(false);
			if (!cancellationToken.IsCancellationRequested)
				throw new InvalidOperationException($"Listener '{_role}' ended without a stop request.");
			SetStoppedUnlessFaulted();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			SetStoppedUnlessFaulted();
		}
		catch (Exception exception)
		{
			var snapshot = SetFaulted(exception);
			NotifyTerminalFault(snapshot, exception);
		}
	}

	private async Task AcceptLoopAsync(CancellationToken cancellationToken)
	{
		var consecutiveRecoverableFailures = 0;
		while (!cancellationToken.IsCancellationRequested)
		{
			NamedPipeServerStream? pipe = null;
			try
			{
				pipe = _pipeFactory();
				await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
				consecutiveRecoverableFailures = 0;

				var accepted = pipe;
				pipe = null;
				_ = _connectionHandler(accepted, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (IOException)
			{
				if (cancellationToken.IsCancellationRequested)
					return;

				consecutiveRecoverableFailures++;
				if (consecutiveRecoverableFailures >= MaxConsecutiveRecoverableAcceptFailures)
					throw;

				await Task.Delay(
					TimeSpan.FromTicks(RecoverableAcceptRetryDelay.Ticks * consecutiveRecoverableFailures),
					cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				pipe?.Dispose();
			}
		}
	}

	private void SetStoppedUnlessFaulted()
	{
		lock (_gate)
		{
			if (_snapshot.State != RuntimePipeListenerState.Faulted)
				_snapshot = CreateSnapshot(RuntimePipeListenerState.Stopped, null);
		}
	}

	private RuntimePipeListenerSnapshot SetFaulted(Exception exception)
	{
		lock (_gate)
		{
			_terminalFault ??= exception;
			_snapshot = CreateSnapshot(RuntimePipeListenerState.Faulted, _terminalFault);
			return _snapshot;
		}
	}

	private void NotifyTerminalFault(RuntimePipeListenerSnapshot snapshot, Exception exception)
	{
		var handlers = TerminalFaulted;
		if (handlers is null)
			return;

		foreach (var subscriber in handlers.GetInvocationList())
		{
			try
			{
				((Action<RuntimePipeListenerSnapshot, Exception>)subscriber)(snapshot, exception);
			}
			catch
			{
				// Listener fault reporting must not replace the listener failure itself.
			}
		}
	}

	private RuntimePipeListenerSnapshot CreateSnapshot(RuntimePipeListenerState state, Exception? exception) =>
		new(
			_endpoint,
			_role,
			state,
			exception?.GetType().FullName ?? exception?.GetType().Name,
			exception is null ? null : DiagnosticRedactor.RedactText(exception.Message),
			DateTimeOffset.UtcNow);
}
