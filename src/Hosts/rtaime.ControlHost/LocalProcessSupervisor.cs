// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO.Pipes;

namespace rtaime.ControlHost;

public enum LocalProcessSupervisionState
{
	Created = 1,
	Waiting = 2,
	Starting = 3,
	Healthy = 4,
	RestartBackoff = 5,
	Failed = 6,
	Stopped = 7
}

public sealed record LocalProcessSupervisionSnapshot(
	string Name,
	LocalProcessSupervisionState State,
	int ConsecutiveStartAttempts,
	int? OwnedProcessId,
	string Detail,
	DateTimeOffset UpdatedAt);

public sealed record LocalProcessSupervisionOptions(
	string Name,
	string Endpoint,
	string ExecutablePath,
	TimeSpan ProbeTimeout,
	TimeSpan ProbeInterval,
	TimeSpan RestartBackoff,
	int MaxConsecutiveStartAttempts,
	bool StopOwnedProcessOnDispose = true)
{
	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Supervised process name is required.", nameof(Name));
		if (string.IsNullOrWhiteSpace(Endpoint)) throw new ArgumentException("Supervised process endpoint is required.", nameof(Endpoint));
		if (string.IsNullOrWhiteSpace(ExecutablePath)) throw new ArgumentException("Supervised executable path is required.", nameof(ExecutablePath));
		if (!File.Exists(ExecutablePath)) throw new FileNotFoundException($"Supervised executable '{ExecutablePath}' does not exist.", ExecutablePath);
		if (ProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
		if (ProbeInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ProbeInterval));
		if (RestartBackoff < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RestartBackoff));
		if (MaxConsecutiveStartAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(MaxConsecutiveStartAttempts));
	}
}

/// <summary>
/// Local endpoint-driven process supervision. The endpoint is authoritative for adoption: if a compatible local
/// host is already listening, the supervisor does not launch a duplicate process. Only processes launched by this
/// instance are ever terminated during an orderly dispose. A supervisor crash therefore does not terminate an
/// already-running child process.
/// </summary>
public sealed class LocalProcessSupervisor : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly LocalProcessSupervisionOptions _options;
	private readonly CancellationTokenSource _stop = new();
	private Task? _loop;
	private Process? _ownedProcess;
	private int _consecutiveStartAttempts;
	private LocalProcessSupervisionSnapshot _snapshot;

	public LocalProcessSupervisor(LocalProcessSupervisionOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_options.Validate();
		_snapshot = new LocalProcessSupervisionSnapshot(
			_options.Name,
			LocalProcessSupervisionState.Created,
			0,
			null,
			"Supervisor has not started.",
			DateTimeOffset.UtcNow);
	}

	public LocalProcessSupervisionSnapshot Snapshot
	{
		get
		{
			lock (_gate)
				return _snapshot;
		}
	}

	public Task StartAsync(CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			if (_loop is not null)
				throw new InvalidOperationException($"Supervisor '{_options.Name}' has already been started.");
			var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
			_loop = RunLoopAsync(linked.Token);
		}
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync()
	{
		_stop.Cancel();
		Task? loop;
		lock (_gate) loop = _loop;
		if (loop is not null)
		{
			try { await loop.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
		}

		Process? owned;
		lock (_gate)
		{
			owned = _ownedProcess;
			_ownedProcess = null;
		}
		if (owned is not null)
		{
			try
			{
				if (_options.StopOwnedProcessOnDispose && !owned.HasExited)
				{
					owned.Kill(entireProcessTree: true);
					await owned.WaitForExitAsync().ConfigureAwait(false);
				}
			}
			catch (InvalidOperationException) { }
			finally { owned.Dispose(); }
		}

		Update(LocalProcessSupervisionState.Stopped, "Supervisor stopped.");
		_stop.Dispose();
	}

	private async Task RunLoopAsync(CancellationToken cancellationToken)
	{
		Update(LocalProcessSupervisionState.Waiting, $"Waiting for endpoint '{_options.Endpoint}'.");
		while (!cancellationToken.IsCancellationRequested)
		{
			if (await ProbeEndpointAsync(cancellationToken).ConfigureAwait(false))
			{
				_consecutiveStartAttempts = 0;
				Update(LocalProcessSupervisionState.Healthy, $"Endpoint '{_options.Endpoint}' is reachable.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			DisposeExitedOwnedProcess();
			if (OwnedProcessIsRunning())
			{
				Update(LocalProcessSupervisionState.Starting, "Owned process is running but its endpoint is not ready yet.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (_consecutiveStartAttempts >= _options.MaxConsecutiveStartAttempts)
			{
				Update(
					LocalProcessSupervisionState.Failed,
					$"Restart budget exhausted after {_consecutiveStartAttempts} consecutive start attempt(s); continuing endpoint probes without launching another process.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (_consecutiveStartAttempts > 0 && _options.RestartBackoff > TimeSpan.Zero)
			{
				Update(LocalProcessSupervisionState.RestartBackoff, "Waiting before the next supervised start attempt.");
				await Task.Delay(_options.RestartBackoff, cancellationToken).ConfigureAwait(false);
				if (await ProbeEndpointAsync(cancellationToken).ConfigureAwait(false))
					continue;
			}

			StartOwnedProcess();
			_consecutiveStartAttempts++;
			Update(LocalProcessSupervisionState.Starting, $"Started supervised process attempt {_consecutiveStartAttempts}.");
			await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<bool> ProbeEndpointAsync(CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_options.ProbeTimeout);
		await using var pipe = new NamedPipeClientStream(".", _options.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
			return pipe.IsConnected;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private void StartOwnedProcess()
	{
		var fullPath = Path.GetFullPath(_options.ExecutablePath);
		var isDll = string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase);
		var startInfo = new ProcessStartInfo
		{
			FileName = isDll ? "dotnet" : fullPath,
			Arguments = isDll
				? $"\"{fullPath}\" --listen-endpoint={QuoteArgument(_options.Endpoint)}"
				: $"--listen-endpoint={QuoteArgument(_options.Endpoint)}",
			WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException($"Failed to start supervised process '{_options.Name}'.");
		lock (_gate)
			_ownedProcess = process;
	}

	private bool OwnedProcessIsRunning()
	{
		lock (_gate)
		{
			try { return _ownedProcess is { HasExited: false }; }
			catch (InvalidOperationException) { return false; }
		}
	}

	private void DisposeExitedOwnedProcess()
	{
		Process? process = null;
		lock (_gate)
		{
			if (_ownedProcess is null) return;
			try
			{
				if (!_ownedProcess.HasExited) return;
			}
			catch (InvalidOperationException) { }
			process = _ownedProcess;
			_ownedProcess = null;
		}
		process.Dispose();
	}

	private void Update(LocalProcessSupervisionState state, string detail)
	{
		lock (_gate)
		{
			int? processId = null;
			if (_ownedProcess is not null)
			{
				try
				{
					if (!_ownedProcess.HasExited)
						processId = _ownedProcess.Id;
				}
				catch (InvalidOperationException) { }
			}
			_snapshot = new LocalProcessSupervisionSnapshot(
				_options.Name,
				state,
				_consecutiveStartAttempts,
				processId,
				detail,
				DateTimeOffset.UtcNow);
		}
	}

	private static string QuoteArgument(string value) => value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
