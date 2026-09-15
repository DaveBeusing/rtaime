// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.AI;

namespace rtaime.AIHost;

public enum AIHostProcessState
{
	Created = 1,
	Starting = 2,
	Ready = 3,
	Degraded = 4,
	Draining = 5,
	Stopped = 6,
	Failed = 7
}

public enum AIHostHealthState
{
	Unknown = 1,
	Healthy = 2,
	Degraded = 3,
	Unhealthy = 4,
	Stopped = 5
}

public enum AIHostExitCode
{
	Success = 0,
	ConfigurationError = 2,
	StartupFailure = 3,
	ShutdownFailure = 4,
	UnexpectedFailure = 10
}

public sealed record AIHostLifecycleSnapshot(
	AIHostProcessState State,
	AIHostHealthState Health,
	string Detail,
	DateTimeOffset UpdatedAt);

public sealed record AIHostProcessOptions(
	InferenceRuntimeLimits Limits,
	TimeSpan ShutdownTimeout)
{
	public static AIHostProcessOptions Default => new(
		InferenceRuntimeLimits.ReferenceV1,
		TimeSpan.FromSeconds(10));

	public static AIHostProcessOptions Load(
		IReadOnlyList<string> args,
		Func<string, string?>? environment = null)
	{
		ArgumentNullException.ThrowIfNull(args);
		environment ??= Environment.GetEnvironmentVariable;
		var defaults = Default;

		var computeUnits = ParsePositiveUInt(Get(args, environment, "compute-units", "RTAIME_AI_COMPUTE_UNITS", defaults.Limits.ComputeUnits.ToString()), "compute-units");
		var vramMiB = ParsePositiveULong(Get(args, environment, "vram-mib", "RTAIME_AI_VRAM_MIB", (defaults.Limits.VramBytes / (1024UL * 1024UL)).ToString()), "vram-mib");
		var maxConcurrent = ParsePositiveUInt(Get(args, environment, "max-concurrent", "RTAIME_AI_MAX_CONCURRENT", defaults.Limits.MaxConcurrentRequests.ToString()), "max-concurrent");
		var maxRate = ParsePositiveUInt(Get(args, environment, "max-rate", "RTAIME_AI_MAX_RATE", defaults.Limits.MaxInferenceRatePerSecond.ToString()), "max-rate");
		var shutdownTimeoutMs = ParsePositiveInt(Get(args, environment, "shutdown-timeout-ms", "RTAIME_AI_SHUTDOWN_TIMEOUT_MS", ((int)defaults.ShutdownTimeout.TotalMilliseconds).ToString()), "shutdown-timeout-ms");

		return new AIHostProcessOptions(
			new InferenceRuntimeLimits(computeUnits, checked(vramMiB * 1024UL * 1024UL), maxConcurrent, maxRate),
			TimeSpan.FromMilliseconds(shutdownTimeoutMs));
	}

	public void Validate()
	{
		ArgumentNullException.ThrowIfNull(Limits);
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

	private static uint ParsePositiveUInt(string value, string key)
	{
		if (!uint.TryParse(value, out var parsed) || parsed == 0)
			throw new ArgumentException($"Configuration '{key}' must be a positive unsigned integer.", key);
		return parsed;
	}

	private static ulong ParsePositiveULong(string value, string key)
	{
		if (!ulong.TryParse(value, out var parsed) || parsed == 0)
			throw new ArgumentException($"Configuration '{key}' must be a positive unsigned integer.", key);
		return parsed;
	}

	private static int ParsePositiveInt(string value, string key)
	{
		if (!int.TryParse(value, out var parsed) || parsed <= 0)
			throw new ArgumentException($"Configuration '{key}' must be a positive integer.", key);
		return parsed;
	}
}

/// <summary>
/// Executable AIHost composition root. Governed inference and resource admission remain owned by
/// <see cref="AIHostService"/> and <see cref="GovernedInferenceRuntime"/>; this class owns process lifecycle only.
/// </summary>
public sealed class AIHostProcess
{
	private readonly object _gate = new();
	private readonly AIHostProcessOptions _options;
	private readonly Func<InferenceRuntimeLimits, AIHostService> _serviceFactory;
	private AIHostLifecycleSnapshot _lifecycle = new(
		AIHostProcessState.Created,
		AIHostHealthState.Unknown,
		"Process has not started.",
		DateTimeOffset.UtcNow);
	private int _runStarted;
	private AIHostService? _service;
	private bool _serviceDisposed;
	private AIExecutionSnapshot? _finalExecutionSnapshot;

	public AIHostProcess(
		AIHostProcessOptions options,
		Func<InferenceRuntimeLimits, AIHostService>? serviceFactory = null)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_serviceFactory = serviceFactory ?? AIHostService.CreateManagedReference;
	}

	public AIHostLifecycleSnapshot Lifecycle
	{
		get
		{
			lock (_gate)
				return _lifecycle;
		}
	}

	public AIHostService? Service => _service;
	public bool ServiceDisposed => _serviceDisposed;
	public AIExecutionSnapshot? FinalExecutionSnapshot => _finalExecutionSnapshot;

	public async Task<AIHostExitCode> RunAsync(CancellationToken cancellationToken)
	{
		if (Interlocked.Exchange(ref _runStarted, 1) != 0)
			throw new InvalidOperationException("An AIHostProcess instance can be run only once.");

		Update(AIHostProcessState.Starting, AIHostHealthState.Unknown, "Composing AIHost dependencies.");
		try
		{
			_options.Validate();
			_service = _serviceFactory(_options.Limits)
				?? throw new InvalidOperationException("AI service factory returned null.");
		}
		catch (ArgumentException exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(AIHostProcessState.Failed, AIHostHealthState.Unhealthy, $"Configuration rejected: {exception.Message}");
			return AIHostExitCode.ConfigurationError;
		}
		catch (Exception exception)
		{
			await CleanupStartupFailureAsync().ConfigureAwait(false);
			Update(AIHostProcessState.Failed, AIHostHealthState.Unhealthy, $"Startup failed: {exception.Message}");
			return AIHostExitCode.StartupFailure;
		}

		if (_service.Capabilities.Count == 0)
			Update(AIHostProcessState.Degraded, AIHostHealthState.Degraded, "AIHost is running but no inference capability is currently available.");
		else
			Update(AIHostProcessState.Ready, AIHostHealthState.Healthy, "AIHost is ready with governed inference and resource admission.");

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
			Update(AIHostProcessState.Failed, AIHostHealthState.Unhealthy, $"Run loop failed: {exception.Message}");
			return AIHostExitCode.UnexpectedFailure;
		}

		return await StopAsync().ConfigureAwait(false);
	}

	private async Task<AIHostExitCode> StopAsync()
	{
		Update(AIHostProcessState.Draining, AIHostHealthState.Degraded, "Cancelling inference and draining AIHost resources.");
		using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
		try
		{
			if (_service is not null)
			{
				await _service.DisposeAsync().AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
				_serviceDisposed = true;
				_finalExecutionSnapshot = _service.Snapshot;
				if (_finalExecutionSnapshot.ActiveRequests != 0 ||
					_finalExecutionSnapshot.ReservedComputeUnits != 0 ||
					_finalExecutionSnapshot.ReservedVramBytes != 0)
				{
					throw new InvalidOperationException("AIHost retained active admissions or reserved resources after shutdown.");
				}
			}

			Update(AIHostProcessState.Stopped, AIHostHealthState.Stopped, "AIHost stopped cleanly and released admissions/resources.");
			return AIHostExitCode.Success;
		}
		catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
		{
			Update(AIHostProcessState.Failed, AIHostHealthState.Unhealthy, "AIHost shutdown exceeded the configured timeout.");
			return AIHostExitCode.ShutdownFailure;
		}
		catch (Exception exception)
		{
			Update(AIHostProcessState.Failed, AIHostHealthState.Unhealthy, $"AIHost shutdown failed: {exception.Message}");
			return AIHostExitCode.ShutdownFailure;
		}
	}

	private async Task CleanupStartupFailureAsync()
	{
		if (_service is null)
			return;

		try
		{
			await _service.DisposeAsync().ConfigureAwait(false);
			_serviceDisposed = true;
			_finalExecutionSnapshot = _service.Snapshot;
		}
		catch
		{
			// Preserve the original startup failure as the process outcome.
		}
	}

	private void Update(AIHostProcessState state, AIHostHealthState health, string detail)
	{
		lock (_gate)
			_lifecycle = new AIHostLifecycleSnapshot(state, health, detail, DateTimeOffset.UtcNow);
	}
}
