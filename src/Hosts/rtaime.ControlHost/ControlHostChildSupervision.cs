// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.ControlHost;

/// <summary>
/// Optional executable-host composition for local RuntimeHost and AIHost supervision. ControlHost can adopt
/// already-running endpoints or launch configured executables. Top-level ControlHost supervision remains an
/// operating-system/service-manager responsibility so ControlHost loss cannot take the running children down.
/// </summary>
public sealed class ControlHostChildSupervision : IAsyncDisposable
{
	private readonly LocalProcessSupervisor? _runtime;
	private readonly LocalProcessSupervisor? _ai;

	private ControlHostChildSupervision(LocalProcessSupervisor? runtime, LocalProcessSupervisor? ai)
	{
		_runtime = runtime;
		_ai = ai;
	}

	public LocalProcessSupervisionSnapshot? Runtime => _runtime?.Snapshot;
	public LocalProcessSupervisionSnapshot? AI => _ai?.Snapshot;
	public bool Enabled => _runtime is not null || _ai is not null;

	public static ControlHostChildSupervision Load(
		ControlHostProcessOptions controlOptions,
		Func<string, string?>? environment = null)
	{
		ArgumentNullException.ThrowIfNull(controlOptions);
		environment ??= Environment.GetEnvironmentVariable;

		var probeTimeout = TimeSpan.FromMilliseconds(ParsePositiveInt(environment("RTAIME_SUPERVISION_PROBE_TIMEOUT_MS"), 250, "RTAIME_SUPERVISION_PROBE_TIMEOUT_MS"));
		var probeInterval = TimeSpan.FromMilliseconds(ParsePositiveInt(environment("RTAIME_SUPERVISION_PROBE_INTERVAL_MS"), 500, "RTAIME_SUPERVISION_PROBE_INTERVAL_MS"));
		var restartBackoff = TimeSpan.FromMilliseconds(ParseNonNegativeInt(environment("RTAIME_SUPERVISION_RESTART_BACKOFF_MS"), 500, "RTAIME_SUPERVISION_RESTART_BACKOFF_MS"));
		var maxStarts = ParsePositiveInt(environment("RTAIME_SUPERVISION_MAX_START_ATTEMPTS"), 5, "RTAIME_SUPERVISION_MAX_START_ATTEMPTS");

		var runtime = Create(
			"RuntimeHost",
			controlOptions.RuntimeEndpoint,
			environment("RTAIME_RUNTIME_EXECUTABLE"),
			probeTimeout,
			probeInterval,
			restartBackoff,
			maxStarts);
		var aiEndpoint = environment("RTAIME_AI_ENDPOINT");
		if (string.IsNullOrWhiteSpace(aiEndpoint)) aiEndpoint = "rtaime.v1.ai.default";
		var ai = Create(
			"AIHost",
			aiEndpoint,
			environment("RTAIME_AI_EXECUTABLE"),
			probeTimeout,
			probeInterval,
			restartBackoff,
			maxStarts);

		return new ControlHostChildSupervision(runtime, ai);
	}

	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (_runtime is not null)
			await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
		if (_ai is not null)
			await _ai.StartAsync(cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (_ai is not null)
			await _ai.DisposeAsync().ConfigureAwait(false);
		if (_runtime is not null)
			await _runtime.DisposeAsync().ConfigureAwait(false);
	}

	private static LocalProcessSupervisor? Create(
		string name,
		string endpoint,
		string? executable,
		TimeSpan probeTimeout,
		TimeSpan probeInterval,
		TimeSpan restartBackoff,
		int maxStarts)
	{
		if (string.IsNullOrWhiteSpace(executable)) return null;
		return new LocalProcessSupervisor(new LocalProcessSupervisionOptions(
			name,
			endpoint,
			executable.Trim(),
			probeTimeout,
			probeInterval,
			restartBackoff,
			maxStarts));
	}

	private static int ParsePositiveInt(string? value, int defaultValue, string name)
	{
		if (string.IsNullOrWhiteSpace(value)) return defaultValue;
		if (!int.TryParse(value, out var parsed) || parsed <= 0)
			throw new ArgumentException($"Environment variable '{name}' must be a positive integer.", name);
		return parsed;
	}

	private static int ParseNonNegativeInt(string? value, int defaultValue, string name)
	{
		if (string.IsNullOrWhiteSpace(value)) return defaultValue;
		if (!int.TryParse(value, out var parsed) || parsed < 0)
			throw new ArgumentException($"Environment variable '{name}' must be a non-negative integer.", name);
		return parsed;
	}
}
