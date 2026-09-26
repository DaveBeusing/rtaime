// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;
using System.IO.Pipes;
using rtaime.Core;

namespace rtaime.ControlHost;

public enum LocalProcessSupervisionState
{
	Created = 1,
	Waiting = 2,
	Starting = 3,
	Healthy = 4,
	RestartBackoff = 5,
	Failed = 6,
	Stopped = 7,
	Recovering = 8
}

public sealed record LocalProcessSupervisionSnapshot(
	string Name,
	LocalProcessSupervisionState State,
	int StartAttempts,
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
	int MaxStartAttempts,
	bool StopOwnedProcessOnDispose = true)
{
	public string AdditionalArguments { get; init; } = string.Empty;
	public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(10);
	public TimeSpan InitialAdoptionWindow { get; init; } = TimeSpan.FromSeconds(1);

	public void Validate()
	{
		if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Supervised process name is required.", nameof(Name));
		if (string.IsNullOrWhiteSpace(Endpoint)) throw new ArgumentException("Supervised process endpoint is required.", nameof(Endpoint));
		if (string.IsNullOrWhiteSpace(ExecutablePath)) throw new ArgumentException("Supervised executable path is required.", nameof(ExecutablePath));
		if (!File.Exists(ExecutablePath)) throw new FileNotFoundException($"Supervised executable '{ExecutablePath}' does not exist.", ExecutablePath);
		if (ProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
		if (ProbeInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ProbeInterval));
		if (RestartBackoff < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RestartBackoff));
		if (MaxStartAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(MaxStartAttempts));
		if (GracefulStopTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(GracefulStopTimeout));
		if (InitialAdoptionWindow < ProbeInterval) throw new ArgumentOutOfRangeException(nameof(InitialAdoptionWindow), "Initial adoption window must cover at least one probe interval.");
	}
}

/// <summary>
/// Local process supervision uses process-shared endpoint lifetime and readiness leases to adopt current rtaime hosts without
/// destructively probing their Named Pipe listener. A lifetime lease suppresses competing child starts while a distinct readiness
/// lease proves that the external host lifecycle is healthy and its IPC server is running. Processes launched by this supervisor
/// still require their unique managed readiness file before becoming Healthy. A previously healthy owned child that remains alive
/// without that readiness receives a bounded grace period, then the existing graceful-stop and bounded restart path. Bare endpoint
/// probing remains only as a compatibility fallback for legacy endpoints that publish neither lease. Only processes launched by this
/// instance are terminated. Start attempts are bounded for the lifetime of this supervisor instance.
/// </summary>
public sealed class LocalProcessSupervisor : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly LocalProcessSupervisionOptions _options;
	private readonly CancellationTokenSource _stop = new();
	private readonly Stopwatch _lifetime = Stopwatch.StartNew();
	private CancellationTokenSource? _linkedStop;
	private Task? _loop;
	private Process? _ownedProcess;
	private string? _ownedStopFilePath;
	private string? _ownedReadinessFilePath;
	private bool _ownedProcessReady;
	private TimeSpan? _ownedReadinessLostSince;
	private TimeSpan? _initialEndpointAbsentSince;
	private int _startAttempts;
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
			_linkedStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
			_loop = RunLoopAsync(_linkedStop.Token);
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
		string? stopFile;
		string? readinessFile;
		lock (_gate)
		{
			owned = _ownedProcess;
			stopFile = _ownedStopFilePath;
			readinessFile = _ownedReadinessFilePath;
			_ownedProcess = null;
			_ownedStopFilePath = null;
			_ownedReadinessFilePath = null;
			_ownedProcessReady = false;
			_ownedReadinessLostSince = null;
		}
		if (owned is not null)
		{
			try
			{
				if (_options.StopOwnedProcessOnDispose && !owned.HasExited)
					_ = await StopOwnedProcessAsync(owned, stopFile).ConfigureAwait(false);
			}
			catch (InvalidOperationException) { }
			finally
			{
				owned.Dispose();
				CleanupManagedFile(stopFile);
				CleanupManagedFile(readinessFile);
			}
		}
		else
		{
			CleanupManagedFile(stopFile);
			CleanupManagedFile(readinessFile);
		}

		Update(LocalProcessSupervisionState.Stopped, "Supervisor stopped.");
		_linkedStop?.Dispose();
		_stop.Dispose();
	}

	private async Task RunLoopAsync(CancellationToken cancellationToken)
	{
		Update(LocalProcessSupervisionState.Waiting, $"Waiting for endpoint '{_options.Endpoint}'.");
		while (!cancellationToken.IsCancellationRequested)
		{
			DisposeExitedOwnedProcess();
			if (OwnedProcessIsReadyAndRunning())
			{
				ClearOwnedReadinessLoss();
				Update(LocalProcessSupervisionState.Healthy, "Owned process is running with explicit managed readiness.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (OwnedProcessIsRunning())
			{
				if (OwnedProcessReadinessObserved())
				{
					var recoveredWithoutRestart = OwnedProcessHadReadinessLoss();
					MarkOwnedProcessReadyIfRunning();
					ClearOwnedReadinessLoss();
					Update(
						LocalProcessSupervisionState.Healthy,
						recoveredWithoutRestart
							? "Owned process restored explicit managed readiness within the recovery grace period; restart was not required."
							: "Owned process published explicit managed readiness.");
				}
				else if (OwnedProcessWasPreviouslyReady())
				{
					MarkOwnedReadinessLost();
					if (OwnedReadinessLossDuration() < RecoveryGracePeriod)
					{
						Update(
							LocalProcessSupervisionState.Recovering,
							"Owned process lost explicit managed readiness after previously becoming healthy; waiting for the bounded recovery grace period.");
					}
					else
					{
						await RecoverUnreadyOwnedProcessAsync().ConfigureAwait(false);
					}
				}
				else
				{
					Update(LocalProcessSupervisionState.Starting, "Owned process is running but has not published managed readiness yet.");
				}
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			var endpointLeased = LocalEndpointLease.IsHeld(_options.Endpoint);
			if (endpointLeased && LocalEndpointReadinessLease.IsHeld(_options.Endpoint))
			{
				_initialEndpointAbsentSince = null;
				Update(LocalProcessSupervisionState.Healthy, $"Adopted explicitly ready external endpoint '{_options.Endpoint}'.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (endpointLeased)
			{
				_initialEndpointAbsentSince = null;
				Update(
					LocalProcessSupervisionState.Waiting,
					$"Endpoint '{_options.Endpoint}' is leased by an existing host but is not explicitly ready; suppressing managed launch.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (await ProbeEndpointAsync(cancellationToken).ConfigureAwait(false))
			{
				_initialEndpointAbsentSince = null;
				Update(LocalProcessSupervisionState.Healthy, $"Adopted reachable legacy external endpoint '{_options.Endpoint}'.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (_startAttempts == 0)
			{
				var now = _lifetime.Elapsed;
				_initialEndpointAbsentSince ??= now;
				var continuousAbsence = now - _initialEndpointAbsentSince.Value;
				if (continuousAbsence < _options.InitialAdoptionWindow)
				{
					Update(
						LocalProcessSupervisionState.Waiting,
						$"Endpoint '{_options.Endpoint}' remains absent for {continuousAbsence.TotalMilliseconds:F0} ms; waiting for the bounded initial adoption window before managed launch.");
					await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
					continue;
				}
			}

			if (_startAttempts >= _options.MaxStartAttempts)
			{
				Update(
					LocalProcessSupervisionState.Failed,
					$"Start budget exhausted after {_startAttempts} attempt(s); continuing endpoint probes without launching another process.");
				await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (_startAttempts > 0 && _options.RestartBackoff > TimeSpan.Zero)
			{
				Update(LocalProcessSupervisionState.RestartBackoff, "Waiting before the next supervised start attempt.");
				await Task.Delay(_options.RestartBackoff, cancellationToken).ConfigureAwait(false);

				endpointLeased = LocalEndpointLease.IsHeld(_options.Endpoint);
				if (endpointLeased && LocalEndpointReadinessLease.IsHeld(_options.Endpoint))
				{
					Update(LocalProcessSupervisionState.Healthy, $"Adopted explicitly ready external endpoint '{_options.Endpoint}' during restart backoff.");
					continue;
				}
				if (endpointLeased)
				{
					Update(LocalProcessSupervisionState.Waiting, $"Endpoint '{_options.Endpoint}' became leased during restart backoff; suppressing managed restart until explicit readiness is published.");
					continue;
				}
				if (await ProbeEndpointAsync(cancellationToken).ConfigureAwait(false))
				{
					Update(LocalProcessSupervisionState.Healthy, $"Adopted reachable legacy external endpoint '{_options.Endpoint}' during restart backoff.");
					continue;
				}
			}

			_startAttempts++;
			try
			{
				StartOwnedProcess();
				Update(LocalProcessSupervisionState.Starting, $"Started supervised process attempt {_startAttempts} of {_options.MaxStartAttempts}; awaiting managed readiness.");
			}
			catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				Update(
					_startAttempts >= _options.MaxStartAttempts ? LocalProcessSupervisionState.Failed : LocalProcessSupervisionState.RestartBackoff,
					$"Supervised process start attempt {_startAttempts} failed: {exception.Message}");
			}
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
		var hostArguments = $"--listen-endpoint={QuoteArgument(_options.Endpoint)}";
		if (!string.IsNullOrWhiteSpace(_options.AdditionalArguments))
			hostArguments += " " + _options.AdditionalArguments.Trim();
		var supervisionDirectory = Path.Combine(Path.GetTempPath(), "rtaime", "supervision");
		Directory.CreateDirectory(supervisionDirectory);
		var instanceId = Guid.NewGuid().ToString("N");
		var stopFile = Path.Combine(supervisionDirectory, $"{_options.Name}-{instanceId}.stop");
		var readinessFile = Path.Combine(supervisionDirectory, $"{_options.Name}-{instanceId}.ready.json");
		CleanupManagedFile(stopFile);
		CleanupManagedFile(readinessFile);
		var startInfo = new ProcessStartInfo
		{
			FileName = isDll ? "dotnet" : fullPath,
			Arguments = isDll ? $"\"{fullPath}\" {hostArguments}" : hostArguments,
			WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.Environment["RTAIME_HOST_STOP_FILE"] = stopFile;
		startInfo.Environment["RTAIME_HOST_READINESS_FILE"] = readinessFile;
		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException($"Failed to start supervised process '{_options.Name}'.");
		lock (_gate)
		{
			_ownedProcess = process;
			_ownedStopFilePath = stopFile;
			_ownedReadinessFilePath = readinessFile;
			_ownedProcessReady = false;
			_ownedReadinessLostSince = null;
		}
	}

	private async Task<bool> StopOwnedProcessAsync(Process process, string? stopFile)
	{
		var gracefulStopRequested = false;
		if (!string.IsNullOrWhiteSpace(stopFile))
		{
			try
			{
				var directory = Path.GetDirectoryName(stopFile);
				if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
				File.WriteAllText(stopFile, $"stopRequestedAtUtc={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
				gracefulStopRequested = true;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Fall through to the existing emergency process-tree termination path.
			}
		}

		if (gracefulStopRequested)
		{
			using var timeout = new CancellationTokenSource(_options.GracefulStopTimeout);
			try
			{
				await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
				return false;
			}
			catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
		}

		if (!process.HasExited)
		{
			process.Kill(entireProcessTree: true);
			await process.WaitForExitAsync().ConfigureAwait(false);
			return true;
		}

		return false;
	}

	private bool OwnedProcessIsReadyAndRunning()
	{
		lock (_gate)
		{
			if (!_ownedProcessReady || string.IsNullOrWhiteSpace(_ownedReadinessFilePath) || !File.Exists(_ownedReadinessFilePath)) return false;
			try { return _ownedProcess is { HasExited: false }; }
			catch (InvalidOperationException) { return false; }
		}
	}

	private bool OwnedProcessIsRunning()
	{
		lock (_gate)
		{
			try { return _ownedProcess is { HasExited: false }; }
			catch (InvalidOperationException) { return false; }
		}
	}

	private bool OwnedProcessReadinessObserved()
	{
		lock (_gate)
		{
			if (string.IsNullOrWhiteSpace(_ownedReadinessFilePath) || !File.Exists(_ownedReadinessFilePath)) return false;
			try { return _ownedProcess is { HasExited: false }; }
			catch (InvalidOperationException) { return false; }
		}
	}

	private TimeSpan RecoveryGracePeriod =>
		_options.RestartBackoff >= _options.ProbeInterval
			? _options.RestartBackoff
			: _options.ProbeInterval;

	private TimeSpan OwnedReadinessLossDuration()
	{
		lock (_gate)
			return _ownedReadinessLostSince is { } since ? _lifetime.Elapsed - since : TimeSpan.Zero;
	}

	private bool OwnedProcessWasPreviouslyReady()
	{
		lock (_gate)
			return _ownedProcessReady;
	}

	private bool OwnedProcessHadReadinessLoss()
	{
		lock (_gate)
			return _ownedReadinessLostSince is not null;
	}

	private void MarkOwnedReadinessLost()
	{
		lock (_gate)
			_ownedReadinessLostSince ??= _lifetime.Elapsed;
	}

	private void ClearOwnedReadinessLoss()
	{
		lock (_gate)
			_ownedReadinessLostSince = null;
	}

	private async Task RecoverUnreadyOwnedProcessAsync()
	{
		Process? process;
		string? stopFile;
		string? readinessFile;
		lock (_gate)
		{
			if (_ownedProcess is null || !_ownedProcessReady || _ownedReadinessLostSince is null)
				return;
			try
			{
				if (_ownedProcess.HasExited)
					return;
			}
			catch (InvalidOperationException)
			{
				return;
			}
			if (!string.IsNullOrWhiteSpace(_ownedReadinessFilePath) && File.Exists(_ownedReadinessFilePath))
				return;

			process = _ownedProcess;
			stopFile = _ownedStopFilePath;
			readinessFile = _ownedReadinessFilePath;
		}

		Update(
			LocalProcessSupervisionState.Recovering,
			"Owned process remained alive without explicit managed readiness beyond the recovery grace period; requesting graceful recovery stop.");

		var forcedTermination = false;
		try
		{
			forcedTermination = await StopOwnedProcessAsync(process, stopFile).ConfigureAwait(false);
		}
		finally
		{
			lock (_gate)
			{
				if (ReferenceEquals(_ownedProcess, process))
				{
					_ownedProcess = null;
					_ownedStopFilePath = null;
					_ownedReadinessFilePath = null;
					_ownedProcessReady = false;
					_ownedReadinessLostSince = null;
				}
			}
			process.Dispose();
			CleanupManagedFile(stopFile);
			CleanupManagedFile(readinessFile);
		}

		if (_startAttempts >= _options.MaxStartAttempts)
		{
			Update(
				LocalProcessSupervisionState.Failed,
				forcedTermination
					? $"Owned process readiness recovery required forced process-tree termination; start budget exhausted after {_startAttempts} attempt(s)."
					: $"Owned process stopped gracefully after persistent readiness loss; start budget exhausted after {_startAttempts} attempt(s).");
			return;
		}

		Update(
			LocalProcessSupervisionState.RestartBackoff,
			forcedTermination
				? "Owned process readiness recovery required forced process-tree termination; waiting before the next bounded supervised start attempt."
				: "Owned process stopped gracefully after persistent readiness loss; waiting before the next bounded supervised start attempt.");
	}

	private void MarkOwnedProcessReadyIfRunning()
	{
		lock (_gate)
		{
			try
			{
				if (_ownedProcess is { HasExited: false } &&
					!string.IsNullOrWhiteSpace(_ownedReadinessFilePath) &&
					File.Exists(_ownedReadinessFilePath))
				{
					_ownedProcessReady = true;
				}
			}
			catch (InvalidOperationException) { }
		}
	}

	private void DisposeExitedOwnedProcess()
	{
		Process? process = null;
		string? stopFile = null;
		string? readinessFile = null;
		lock (_gate)
		{
			if (_ownedProcess is null) return;
			try
			{
				if (!_ownedProcess.HasExited) return;
			}
			catch (InvalidOperationException) { }
			process = _ownedProcess;
			stopFile = _ownedStopFilePath;
			readinessFile = _ownedReadinessFilePath;
			_ownedProcess = null;
			_ownedStopFilePath = null;
			_ownedReadinessFilePath = null;
			_ownedProcessReady = false;
			_ownedReadinessLostSince = null;
		}
		process.Dispose();
		CleanupManagedFile(stopFile);
		CleanupManagedFile(readinessFile);
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
				_startAttempts,
				processId,
				detail,
				DateTimeOffset.UtcNow);
		}
	}

	private static void CleanupManagedFile(string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		try { if (File.Exists(path)) File.Delete(path); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
	}

	private static string QuoteArgument(string value) => value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
