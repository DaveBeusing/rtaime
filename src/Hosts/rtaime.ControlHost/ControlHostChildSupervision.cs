// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;

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
		var runtimeExecutable = environment("RTAIME_RUNTIME_EXECUTABLE");
		var aiExecutable = environment("RTAIME_AI_EXECUTABLE");
		var aiEndpoint = environment("RTAIME_AI_ENDPOINT");
		if (string.IsNullOrWhiteSpace(aiEndpoint)) aiEndpoint = "rtaime.v1.ai.default";
		var ownership = CreateOwnershipJournal(environment, controlOptions.RuntimeEndpoint, aiEndpoint, runtimeExecutable, aiExecutable, maxStarts);

		var runtime = Create(
			"RuntimeHost",
			controlOptions.RuntimeEndpoint,
			runtimeExecutable,
			probeTimeout,
			probeInterval,
			restartBackoff,
			maxStarts,
			$"--source-a-id={controlOptions.SourceAId} --source-b-id={controlOptions.SourceBId}",
			ownership);
		var ai = Create(
			"AIHost",
			aiEndpoint,
			aiExecutable,
			probeTimeout,
			probeInterval,
			restartBackoff,
			maxStarts,
			string.Empty,
			ownership);

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
		int maxStarts,
		string additionalArguments,
		LocalProcessOwnershipJournal? ownership)
	{
		if (string.IsNullOrWhiteSpace(executable)) return null;
		return new LocalProcessSupervisor(new LocalProcessSupervisionOptions(
			name,
			endpoint,
			executable.Trim(),
			probeTimeout,
			probeInterval,
			restartBackoff,
			maxStarts)
		{
			AdditionalArguments = additionalArguments,
			OwnershipJournal = ownership
		});
	}

	private static LocalProcessOwnershipJournal? CreateOwnershipJournal(
		Func<string, string?> environment,
		string runtimeEndpoint,
		string aiEndpoint,
		string? runtimeExecutable,
		string? aiExecutable,
		int maxStarts)
	{
		var ownershipFile = environment("RTAIME_CHILD_OWNERSHIP_FILE");
		if (string.IsNullOrWhiteSpace(ownershipFile)) return null;
		var runId = environment("RTAIME_LIFECYCLE_RUN_ID");
		var readinessFile = environment("RTAIME_HOST_READINESS_FILE");
		if (!Guid.TryParseExact(runId, "N", out _) || string.IsNullOrWhiteSpace(readinessFile) ||
			string.IsNullOrWhiteSpace(runtimeExecutable) || string.IsNullOrWhiteSpace(aiExecutable))
			throw new ArgumentException("Durable child ownership requires a lifecycle run, readiness path and both managed host executables.");
		var expected = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(readinessFile))!, "child-ownership", "ownership.json");
		if (!string.Equals(Path.GetFullPath(ownershipFile), expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
			throw new ArgumentException("Durable child ownership must remain inside the managed lifecycle run directory.");
		using var control = Process.GetCurrentProcess();
		var identity = LocalProcessIdentity.Capture(control, typeof(ControlHostChildSupervision).Assembly.Location, runId!, runId);
		return new LocalProcessOwnershipJournal(expected, runId!, identity,
		[
			new("runtime", "RuntimeHost", runtimeEndpoint, Path.GetFullPath(runtimeExecutable.Trim()), maxStarts),
			new("ai", "AIHost", aiEndpoint, Path.GetFullPath(aiExecutable.Trim()), maxStarts)
		]);
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
