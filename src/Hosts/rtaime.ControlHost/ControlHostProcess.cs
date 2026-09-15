// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Media.Contracts;
using rtaime.Persistence;
using rtaime.Provider.Contracts;
using rtaime.Runtime.Contracts;

namespace rtaime.ControlHost;

public enum ControlHostProcessState
{
	Created = 1,
	Starting = 2,
	Ready = 3,
	Degraded = 4,
	Draining = 5,
	Stopped = 6,
	Failed = 7
}

public enum ControlHostHealthState
{
	Unknown = 1,
	Healthy = 2,
	Degraded = 3,
	Unhealthy = 4,
	Stopped = 5
}

public enum ControlHostExitCode
{
	Success = 0,
	ConfigurationError = 2,
	StartupFailure = 3,
	ShutdownFailure = 4,
	UnexpectedFailure = 10
}

public sealed record ControlHostLifecycleSnapshot(
	ControlHostProcessState State,
	ControlHostHealthState Health,
	string Detail,
	DateTimeOffset UpdatedAt);

/// <summary>
/// Production IPC seam between authoritative ControlHost and execution-owning RuntimeHost.
/// The boundary carries contracts/descriptors only and never introduces a host-to-host project reference.
/// </summary>
public interface IControlRuntimeTransportSeam
{
	bool IsConnected { get; }
	string? HostInstanceId { get; }
	IReadOnlyList<ProviderDescriptor> ProviderDescriptors { get; }

	ValueTask ConnectAsync(CancellationToken cancellationToken = default);
	ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default);
	ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
	ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition,
		CancellationToken cancellationToken = default);
	ValueTask DisconnectAsync();
}

public sealed class UnboundControlRuntimeTransportSeam : IControlRuntimeTransportSeam
{
	private static readonly IReadOnlyList<ProviderDescriptor> EmptyProviders = Array.Empty<ProviderDescriptor>();

	public bool IsConnected => false;
	public string? HostInstanceId => null;
	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => EmptyProviders;

	public ValueTask ConnectAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<IReadOnlyList<ProviderDescriptor>> GetProviderDescriptorsAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<IReadOnlyList<ProviderDescriptor>>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<RuntimeRemoteSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeRemoteSnapshot>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask<RuntimeRemoteApplyResult> ApplyExecutionAsync(
		PreparedExecutionContract preparedExecution,
		MediaSinkId programSinkId,
		RuntimeProgramTransitionIntent? transition,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromException<RuntimeRemoteApplyResult>(new InvalidOperationException("Runtime transport is not configured."));

	public ValueTask DisconnectAsync() => ValueTask.CompletedTask;
}

public sealed record ControlHostProcessOptions(
	ProductionId ProductionId,
	ProductionSourceId SourceAId,
	ProductionSourceId SourceBId,
	string ProductionName,
	int JournalCapacity,
	string ListenEndpoint,
	string RuntimeEndpoint,
	TimeSpan ConnectTimeout,
	TimeSpan RequestTimeout,
	TimeSpan RuntimeRetryInterval,
	TimeSpan ShutdownTimeout)
{
	public static ControlHostProcessOptions Default => new(
		new ProductionId(Identity.Parse("70000000-0000-0000-0000-000000000001")),
		new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000a")),
		new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000b")),
		"rtaime V1 Production",
		1024,
		"rtaime.v1.control.default",
		"rtaime.v1.runtime.default",
		TimeSpan.FromSeconds(1),
		TimeSpan.FromSeconds(5),
		TimeSpan.FromMilliseconds(250),
		TimeSpan.FromSeconds(10));

	public static ControlHostProcessOptions Load(
		IReadOnlyList<string> args,
		Func<string, string?>? environment = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		environment ??= Environment.GetEnvironmentVariable;
		var defaults = Default;

		return new ControlHostProcessOptions(
			new ProductionId(ParseIdentity(Get(args, environment, "production-id", "RTAIME_CONTROL_PRODUCTION_ID", defaults.ProductionId.ToString()), "production-id")),
			new ProductionSourceId(ParseIdentity(Get(args, environment, "source-a-id", "RTAIME_CONTROL_SOURCE_A_ID", defaults.SourceAId.ToString()), "source-a-id")),
			new ProductionSourceId(ParseIdentity(Get(args, environment, "source-b-id", "RTAIME_CONTROL_SOURCE_B_ID", defaults.SourceBId.ToString()), "source-b-id")),
			Get(args, environment, "production-name", "RTAIME_CONTROL_PRODUCTION_NAME", defaults.ProductionName),
			ParsePositiveInt(Get(args, environment, "journal-capacity", "RTAIME_CONTROL_JOURNAL_CAPACITY", defaults.JournalCapacity.ToString()), "journal-capacity"),
			Get(args, environment, "listen-endpoint", "RTAIME_CONTROL_ENDPOINT", defaults.ListenEndpoint),
			Get(args, environment, "runtime-endpoint", "RTAIME_RUNTIME_ENDPOINT", defaults.RuntimeEndpoint),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "connect-timeout-ms", "RTAIME_CONTROL_CONNECT_TIMEOUT_MS", ((int)defaults.ConnectTimeout.TotalMilliseconds).ToString()), "connect-timeout-ms")),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "request-timeout-ms", "RTAIME_CONTROL_REQUEST_TIMEOUT_MS", ((int)defaults.RequestTimeout.TotalMilliseconds).ToString()), "request-timeout-ms")),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "runtime-retry-ms", "RTAIME_CONTROL_RUNTIME_RETRY_MS", ((int)defaults.RuntimeRetryInterval.TotalMilliseconds).ToString()), "runtime-retry-ms")),
			TimeSpan.FromMilliseconds(ParsePositiveInt(Get(args, environment, "shutdown-timeout-ms", "RTAIME_CONTROL_SHUTDOWN_TIMEOUT_MS", ((int)defaults.ShutdownTimeout.TotalMilliseconds).ToString()), "shutdown-timeout-ms")));
	}

	public void Validate()
	{
		if (ProductionId.Value.IsEmpty)
			throw new ArgumentException("Production identity must not be empty.", nameof(ProductionId));
		if (SourceAId.Value.IsEmpty || SourceBId.Value.IsEmpty)
			throw new ArgumentException("Production source identities must not be empty.");
		if (SourceAId == SourceBId)
			throw new ArgumentException("Production source identities must be distinct.");
		if (string.IsNullOrWhiteSpace(ProductionName))
			throw new ArgumentException("Production name is required.", nameof(ProductionName));
		if (JournalCapacity <= 0)
			throw new ArgumentOutOfRangeException(nameof(JournalCapacity));
		if (string.IsNullOrWhiteSpace(ListenEndpoint))
			throw new ArgumentException("ControlHost listen endpoint is required.", nameof(ListenEndpoint));
		if (string.IsNullOrWhiteSpace(RuntimeEndpoint))
			throw new ArgumentException("RuntimeHost endpoint is required.", nameof(RuntimeEndpoint));
		if (ConnectTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
		if (RequestTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
		if (RuntimeRetryInterval <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(RuntimeRetryInterval));
		if (ShutdownTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(ShutdownTimeout));
	}

	private static string Get(
		IReadOnlyList<string> args,
		Func<string, string?> environment,
		string key,
		string environmentName,
		string defaultValue)
	{
		var prefix = $"--{key}=";
		var commandLine = args.LastOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
		if (commandLine is not null)
			return commandLine[prefix.Length..];

		var environmentValue = environment(environmentName);
		return string.IsNullOrWhiteSpace(environmentValue) ? defaultValue : environmentValue.Trim();
	}

	private static Identity ParseIdentity(string value, string key)
	{
		try
		{
			return Identity.Parse(value);
		}
		catch (Exception exception) when (exception is FormatException or ArgumentException)
		{
			throw new ArgumentException($"Configuration '{key}' must be a valid identity.", key, exception);
		}
	}

	private static int ParsePositiveInt(string value, string key)
	{
		if (!int.TryParse(value, out var parsed) || parsed <= 0)
			throw new ArgumentException($"Configuration '{key}' must be a positive integer.", key);
		return parsed;
	}
}

/// <summary>
/// Executable ControlHost composition root. Production authority remains in <see cref="ControlHostService"/>;
/// this class owns process startup, production IPC, runtime binding/recovery and orderly resource shutdown.
/// </summary>
public sealed class ControlHostProcess
{
	private readonly object _gate = new();
	private readonly ControlHostProcessOptions _options;
	private readonly Func<IControlRuntimeTransportSeam> _transportFactory;
	private ControlHostLifecycleSnapshot _lifecycle = new(
		ControlHostProcessState.Created,
		ControlHostHealthState.Unknown,
		"Process has not started.",
		DateTimeOffset.UtcNow);
	private int _runStarted;
	private BoundedProductionJournal? _journal;
	private ControlHostService? _control;
	private IControlRuntimeTransportSeam? _runtimeTransport;
	private ControlHostIpcServer? _ipcServer;
	private Task? _runtimeBindingTask;
	private string? _boundRuntimeHostInstanceId;

	public ControlHostProcess(
		ControlHostProcessOptions options,
		Func<IControlRuntimeTransportSeam>? transportFactory = null)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_transportFactory = transportFactory ?? (() => new NamedPipeRuntimeHostTransport(
			_options.RuntimeEndpoint,
			_options.ConnectTimeout,
			_options.RequestTimeout));
	}

	public ControlHostLifecycleSnapshot Lifecycle
	{
		get
		{
			lock (_gate)
				return _lifecycle;
		}
	}

	public ControlHostService? Control => _control;
	public BoundedProductionJournal? Journal => _journal;
	public IControlRuntimeTransportSeam? RuntimeTransport => _runtimeTransport;
	public ControlHostIpcServer? IpcServer => _ipcServer;

	public async Task<ControlHostExitCode> RunAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _runStarted, 1) != 0)
			throw new InvalidOperationException("A ControlHostProcess instance can be run only once.");

		Update(ControlHostProcessState.Starting, ControlHostHealthState.Unknown, "Composing ControlHost dependencies.");
		try
		{
			_options.Validate();
			Compose();
			await _ipcServer!.StartAsync(cancellationToken).ConfigureAwait(false);
			_runtimeBindingTask = RuntimeBindingLoopAsync(cancellationToken);
		}
		catch (ArgumentException exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, $"Configuration rejected: {exception.Message}");
			return ControlHostExitCode.ConfigurationError;
		}
		catch (Exception exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, $"Startup failed: {exception.Message}");
			return ControlHostExitCode.StartupFailure;
		}

		SetOperationalState(
			ControlHostProcessState.Degraded,
			ControlHostHealthState.Degraded,
			$"ControlHost is listening on '{_options.ListenEndpoint}' while RuntimeHost binding is pending.");

		try
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Expected process stop signal.
		}
		catch (Exception exception)
		{
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, $"Run loop failed: {exception.Message}");
			return ControlHostExitCode.UnexpectedFailure;
		}

		return await StopAsync().ConfigureAwait(false);
	}

	private void Compose()
	{
		_runtimeTransport = _transportFactory()
			?? throw new InvalidOperationException("Runtime transport factory returned null.");
		if (_runtimeTransport.ProviderDescriptors is null)
			throw new InvalidOperationException("Runtime transport provider snapshot must not be null.");

		_journal = new BoundedProductionJournal(_options.JournalCapacity);
		var specification = new ProductionSpecification(
			ControlContractVersion.Current,
			_options.ProductionId,
			_options.ProductionName,
			new[]
			{
				new ProductionSourceSpecification(_options.SourceAId, "Input A"),
				new ProductionSourceSpecification(_options.SourceBId, "Input B")
			},
			new ProductionRoutingState(_options.SourceAId, _options.SourceAId));

		_control = new ControlHostService(specification, _runtimeTransport.ProviderDescriptors, _journal);
		_ipcServer = new ControlHostIpcServer(_options.ListenEndpoint, () => _control, _runtimeTransport);
	}

	private async Task RuntimeBindingLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				var transport = _runtimeTransport ?? throw new InvalidOperationException("Runtime transport was not composed.");
				var control = _control ?? throw new InvalidOperationException("Control service was not composed.");

				await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
				var providers = await transport.GetProviderDescriptorsAsync(cancellationToken).ConfigureAwait(false);
				if (control.HasPendingExecution)
				{
					await Task.Delay(_options.RuntimeRetryInterval, cancellationToken).ConfigureAwait(false);
					continue;
				}

				control.RefreshProviderSnapshot(providers);
				var runtimeHostInstanceId = transport.HostInstanceId
					?? throw new InvalidDataException("Connected RuntimeHost did not expose a host instance identity.");

				if (!control.HasAuthoritativeState)
				{
					var staged = control.Initialize();
					if (staged.Execution is null)
						throw new InvalidOperationException("Control initialization did not produce a prepared execution.");

					var remote = await transport.ApplyExecutionAsync(
						staged.Execution.PreparedExecution,
						staged.Execution.ProgramSinkId,
						staged.Execution.ProgramTransition,
						cancellationToken).ConfigureAwait(false);
					if (remote.Commit is null)
					{
						var failure = remote.Prepare.Failure ?? new Failure("runtime.prepare.rejected", "Runtime rejected initial prepare without a commit result.");
						control.RejectRuntimeCommit(staged.Execution.PreparedExecution.PreparedExecutionId, failure);
						throw new InvalidOperationException(failure.Message);
					}

					var confirmation = control.ConfirmRuntimeCommit(staged.Execution.PreparedExecution.PreparedExecutionId, remote.Commit);
					if (!confirmation.Committed)
						throw new InvalidOperationException(confirmation.Failure?.Message ?? "Initial Runtime commit was not confirmed.");

					_boundRuntimeHostInstanceId = runtimeHostInstanceId;
					SetOperationalState(ControlHostProcessState.Ready, ControlHostHealthState.Healthy, $"ControlHost is bound to RuntimeHost instance '{runtimeHostInstanceId}'.");
					continue;
				}

				if (!string.Equals(_boundRuntimeHostInstanceId, runtimeHostInstanceId, StringComparison.Ordinal))
				{
					SetOperationalState(ControlHostProcessState.Degraded, ControlHostHealthState.Degraded, "RuntimeHost instance changed; authoritative execution is being resynchronized.");
					var revisionBefore = control.State.Revision;
					var execution = control.PrepareCurrentExecution();
					var remote = await transport.ApplyExecutionAsync(
						execution.PreparedExecution,
						execution.ProgramSinkId,
						null,
						cancellationToken).ConfigureAwait(false);
					if (!remote.Committed)
						throw new InvalidOperationException(remote.Commit?.Failure?.Message ?? remote.Prepare.Failure?.Message ?? "Runtime resynchronization was rejected.");
					if (control.State.Revision != revisionBefore)
						throw new InvalidOperationException("Runtime resynchronization must not advance authoritative revision.");

					control.RecordObservation("runtime", "runtime.resync.committed", $"Authoritative revision {revisionBefore} was applied to RuntimeHost instance '{runtimeHostInstanceId}'.");
					_boundRuntimeHostInstanceId = runtimeHostInstanceId;
					SetOperationalState(ControlHostProcessState.Ready, ControlHostHealthState.Healthy, $"ControlHost resynchronized RuntimeHost instance '{runtimeHostInstanceId}'.");
					continue;
				}

				await transport.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
				SetOperationalState(ControlHostProcessState.Ready, ControlHostHealthState.Healthy, $"ControlHost is connected to RuntimeHost instance '{runtimeHostInstanceId}'.");
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				if (_control is { } control)
					control.RecordObservation("runtime", "runtime.connection.degraded", $"RuntimeHost binding failed: {exception.GetType().Name}.", new Failure("runtime.connection.failed", exception.Message));
				if (_runtimeTransport is not null)
				{
					try { await _runtimeTransport.DisconnectAsync().ConfigureAwait(false); }
					catch { }
				}
				SetOperationalState(ControlHostProcessState.Degraded, ControlHostHealthState.Degraded, "RuntimeHost is unavailable; authoritative mutation transport is paused.");
			}

			await Task.Delay(_options.RuntimeRetryInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task<ControlHostExitCode> StopAsync()
	{
		Update(ControlHostProcessState.Draining, ControlHostHealthState.Degraded, "Draining ControlHost IPC, runtime transport and journal resources.");
		_ipcServer?.NotifyObservableStateChanged();
		using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
		try
		{
			if (_ipcServer is not null)
				await _ipcServer.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
			if (_runtimeTransport is not null)
				await _runtimeTransport.DisconnectAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
			if (_runtimeBindingTask is not null)
			{
				try { await _runtimeBindingTask.WaitAsync(timeout.Token).ConfigureAwait(false); }
				catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw; }
				catch (OperationCanceledException) { }
			}
			if (_journal is not null)
			{
				await _journal.FlushAsync(timeout.Token).ConfigureAwait(false);
				await _journal.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
			}

			Update(ControlHostProcessState.Stopped, ControlHostHealthState.Stopped, "ControlHost stopped cleanly.");
			return ControlHostExitCode.Success;
		}
		catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
		{
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, "ControlHost shutdown exceeded the configured timeout.");
			return ControlHostExitCode.ShutdownFailure;
		}
		catch (Exception exception)
		{
			Update(ControlHostProcessState.Failed, ControlHostHealthState.Unhealthy, $"ControlHost shutdown failed: {exception.Message}");
			return ControlHostExitCode.ShutdownFailure;
		}
	}

	private async Task CleanupStartupFailureAsync()
	{
		try
		{
			if (_ipcServer is not null)
				await _ipcServer.DisposeAsync().ConfigureAwait(false);
		}
		catch { }

		try
		{
			if (_runtimeTransport is not null)
				await _runtimeTransport.DisconnectAsync().ConfigureAwait(false);
		}
		catch { }

		if (_journal is null)
			return;

		try
		{
			await _journal.DisposeAsync().ConfigureAwait(false);
		}
		catch
		{
			// Preserve the original startup failure as the process outcome.
		}
	}

	private void SetOperationalState(ControlHostProcessState state, ControlHostHealthState health, string detail)
	{
		var changed = false;
		lock (_gate)
		{
			changed = _lifecycle.State != state || _lifecycle.Health != health || !string.Equals(_lifecycle.Detail, detail, StringComparison.Ordinal);
			if (changed)
				_lifecycle = new ControlHostLifecycleSnapshot(state, health, detail, DateTimeOffset.UtcNow);
		}
		if (changed)
			_ipcServer?.NotifyObservableStateChanged();
	}

	private void Update(ControlHostProcessState state, ControlHostHealthState health, string detail)
	{
		lock (_gate)
			_lifecycle = new ControlHostLifecycleSnapshot(state, health, detail, DateTimeOffset.UtcNow);
	}
}
