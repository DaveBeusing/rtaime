// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Control.Contracts;
using rtaime.Core;
using rtaime.Persistence;
using rtaime.Provider.Contracts;

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
/// AP-13 transport seam. It intentionally carries only the provider snapshot needed to compose ControlHost.
/// Production network transport is introduced by AP-14 and must implement this boundary without moving
/// Runtime execution into ControlHost.
/// </summary>
public interface IControlRuntimeTransportSeam
{
	bool IsConnected { get; }
	IReadOnlyList<ProviderDescriptor> ProviderDescriptors { get; }
}

public sealed class UnboundControlRuntimeTransportSeam : IControlRuntimeTransportSeam
{
	private static readonly IReadOnlyList<ProviderDescriptor> EmptyProviders = Array.Empty<ProviderDescriptor>();

	public bool IsConnected => false;
	public IReadOnlyList<ProviderDescriptor> ProviderDescriptors => EmptyProviders;
}

public sealed record ControlHostProcessOptions(
	ProductionId ProductionId,
	ProductionSourceId SourceAId,
	ProductionSourceId SourceBId,
	string ProductionName,
	int JournalCapacity,
	TimeSpan ShutdownTimeout)
{
	public static ControlHostProcessOptions Default => new(
		new ProductionId(Identity.Parse("70000000-0000-0000-0000-000000000001")),
		new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000a")),
		new ProductionSourceId(Identity.Parse("70000000-0000-0000-0000-00000000000b")),
		"rtaime V1 Production",
		1024,
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
/// this class only owns process startup, configuration, health and orderly resource shutdown.
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

	public ControlHostProcess(
		ControlHostProcessOptions options,
		Func<IControlRuntimeTransportSeam>? transportFactory = null)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_transportFactory = transportFactory ?? (() => new UnboundControlRuntimeTransportSeam());
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

	public async Task<ControlHostExitCode> RunAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _runStarted, 1) != 0)
			throw new InvalidOperationException("A ControlHostProcess instance can be run only once.");

		Update(ControlHostProcessState.Starting, ControlHostHealthState.Unknown, "Composing ControlHost dependencies.");
		try
		{
			_options.Validate();
			Compose();
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

		if (_runtimeTransport!.IsConnected)
			Update(ControlHostProcessState.Ready, ControlHostHealthState.Healthy, "ControlHost composition is ready.");
		else
			Update(ControlHostProcessState.Degraded, ControlHostHealthState.Degraded, "ControlHost is running with an unbound Runtime transport seam; production commit transport is deferred to AP-14.");

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
	}

	private async Task<ControlHostExitCode> StopAsync()
	{
		Update(ControlHostProcessState.Draining, ControlHostHealthState.Degraded, "Draining ControlHost resources.");
		using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
		try
		{
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

	private void Update(ControlHostProcessState state, ControlHostHealthState health, string detail)
	{
		lock (_gate)
			_lifecycle = new ControlHostLifecycleSnapshot(state, health, detail, DateTimeOffset.UtcNow);
	}
}
