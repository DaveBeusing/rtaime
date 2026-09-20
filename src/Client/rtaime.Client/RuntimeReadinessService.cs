// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public enum RuntimeReadinessState
{
	Initializing,
	Ready,
	Degraded,
	NotReady,
	Recovering,
	Failed
}

public enum ReadinessReasonSeverity
{
	Information,
	Warning,
	Critical
}

public enum RuntimePerformanceVerificationState
{
	Unverified,
	Verified,
	Invalidated
}

public enum RuntimePerformanceInvalidationReason
{
	None,
	HardwareChanged,
	PipelineChanged,
	MeasurementExpired,
	RuntimeFault,
	Explicit
}

public sealed record ReadinessReason(
	string Code,
	string Component,
	ReadinessReasonSeverity Severity,
	string Detail,
	bool IsBlocking);

public sealed record RuntimePerformanceVerificationSnapshot(
	RuntimePerformanceVerificationState State,
	DateTimeOffset? VerifiedAt,
	DateTimeOffset? ValidUntil,
	RuntimePerformanceInvalidationReason InvalidationReason,
	string Detail,
	string HardwareFingerprint,
	string PipelineFingerprint)
{
	public static RuntimePerformanceVerificationSnapshot Unverified { get; } = new(
		RuntimePerformanceVerificationState.Unverified,
		null,
		null,
		RuntimePerformanceInvalidationReason.None,
		"Runtime performance has not been verified yet.",
		string.Empty,
		string.Empty);
}

public sealed record RuntimeReadinessSnapshot(
	RuntimeReadinessState State,
	DateTimeOffset ChangedAt,
	DateTimeOffset ObservedAt,
	IReadOnlyList<ReadinessReason> Reasons,
	bool IsProductionReady,
	RuntimePerformanceVerificationSnapshot Performance)
{
	public static RuntimeReadinessSnapshot Initial(DateTimeOffset now) => new(
		RuntimeReadinessState.Initializing,
		now,
		now,
		[
			new ReadinessReason(
				"control.initializing",
				"Control",
				ReadinessReasonSeverity.Information,
				"Waiting for the first authoritative Control snapshot.",
				true)
		],
		false,
		RuntimePerformanceVerificationSnapshot.Unverified);
}

public sealed record RuntimeReadinessObservation(
	OperatorHealthDescriptor Health,
	string RuntimeStatus,
	OperatorAIShowcaseDescriptor AIShowcase,
	bool Connected,
	bool Stale,
	bool RecoveryExhausted = false,
	string? TerminalFailureDetail = null);

public sealed class RuntimeReadinessChangedEventArgs(
	RuntimeReadinessSnapshot previous,
	RuntimeReadinessSnapshot current) : EventArgs
{
	public RuntimeReadinessSnapshot Previous { get; } = previous;
	public RuntimeReadinessSnapshot Current { get; } = current;
}

public interface IRuntimeReadinessService
{
	RuntimeReadinessSnapshot Current { get; }
	event EventHandler<RuntimeReadinessChangedEventArgs>? Changed;
	void Observe(RuntimeReadinessObservation observation);
	void InvalidatePerformance(string detail);
}

public sealed class RuntimeReadinessService : IRuntimeReadinessService, IDisposable
{
	public static readonly TimeSpan DefaultPerformanceValidity = TimeSpan.FromSeconds(2);

	private static readonly string[] AIDegradedStates = ["UNAVAILABLE", "TIMEOUT", "FAILED"];
	private readonly object _gate = new();
	private readonly Func<DateTimeOffset> _clock;
	private readonly TimeSpan _performanceValidity;
	private RuntimeReadinessSnapshot _current;
	private DateTimeOffset? _explicitPerformanceInvalidatedAt;
	private bool _disposed;

	public RuntimeReadinessService(
		Func<DateTimeOffset>? clock = null,
		TimeSpan? performanceValidity = null)
	{
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
		_performanceValidity = performanceValidity ?? DefaultPerformanceValidity;
		if (_performanceValidity <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(performanceValidity), "Performance validity must be positive.");

		var now = _clock();
		_current = RuntimeReadinessSnapshot.Initial(now);
	}

	public RuntimeReadinessSnapshot Current
	{
		get
		{
			lock (_gate)
				return _current;
		}
	}

	public event EventHandler<RuntimeReadinessChangedEventArgs>? Changed;

	public void Observe(RuntimeReadinessObservation observation)
	{
		ArgumentNullException.ThrowIfNull(observation);
		ArgumentNullException.ThrowIfNull(observation.Health);
		ArgumentNullException.ThrowIfNull(observation.AIShowcase);
		if (string.IsNullOrWhiteSpace(observation.RuntimeStatus))
			throw new ArgumentException("Runtime status is required.", nameof(observation));

		RuntimeReadinessSnapshot previous;
		RuntimeReadinessSnapshot next;
		lock (_gate)
		{
			ThrowIfDisposed();
			var now = _clock();
			previous = _current;
			var performance = EvaluatePerformance(previous.Performance, observation, now);
			var evaluation = EvaluateReadiness(observation, performance);
			var changedAt = previous.State == evaluation.State ? previous.ChangedAt : now;
			next = new RuntimeReadinessSnapshot(
				evaluation.State,
				changedAt,
				now,
				evaluation.Reasons,
				evaluation.IsProductionReady,
				performance);

			if (Equivalent(previous, next))
				return;
			_current = next;
		}

		Changed?.Invoke(this, new RuntimeReadinessChangedEventArgs(previous, next));
	}

	public void InvalidatePerformance(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
			throw new ArgumentException("Invalidation detail is required.", nameof(detail));

		RuntimeReadinessSnapshot previous;
		RuntimeReadinessSnapshot next;
		lock (_gate)
		{
			ThrowIfDisposed();
			var now = _clock();
			_explicitPerformanceInvalidatedAt = now;
			previous = _current;
			var performance = previous.Performance with
			{
				State = RuntimePerformanceVerificationState.Invalidated,
				ValidUntil = null,
				InvalidationReason = RuntimePerformanceInvalidationReason.Explicit,
				Detail = detail.Trim()
			};
			var reasons = previous.Reasons
				.Where(reason => !string.Equals(reason.Code, "performance.unverified", StringComparison.Ordinal))
				.Append(new ReadinessReason(
					"performance.unverified",
					"Performance",
					ReadinessReasonSeverity.Warning,
					detail.Trim(),
					false))
				.ToArray();
			var state = previous.State == RuntimeReadinessState.Ready
				? RuntimeReadinessState.Degraded
				: previous.State;
			next = previous with
			{
				State = state,
				ChangedAt = state == previous.State ? previous.ChangedAt : now,
				ObservedAt = now,
				Reasons = reasons,
				IsProductionReady = state is RuntimeReadinessState.Ready or RuntimeReadinessState.Degraded,
				Performance = performance
			};
			_current = next;
		}

		Changed?.Invoke(this, new RuntimeReadinessChangedEventArgs(previous, next));
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
		}
	}

	private RuntimePerformanceVerificationSnapshot EvaluatePerformance(
		RuntimePerformanceVerificationSnapshot previous,
		RuntimeReadinessObservation observation,
		DateTimeOffset now)
	{
		var health = observation.Health;
		var observedAt = health.ObservedAtUtc;
		var hardwareFingerprint = BuildHardwareFingerprint(health);
		var pipelineFingerprint = NormalizeFingerprint(health.CurrentFormat);
		var measurementPresent = observedAt != DateTimeOffset.MinValue &&
			health.FrameBudget > TimeSpan.Zero &&
			health.FrameTime >= TimeSpan.Zero &&
			!IsUnavailable(health.CurrentFormat);
		var measurementFresh = measurementPresent &&
			observedAt <= now + TimeSpan.FromSeconds(1) &&
			now - observedAt <= _performanceValidity;
		var runtimeFault = health.Runtime.State == OperatorHealthStates.Fail ||
			health.GpuProvider.State == OperatorHealthStates.Fail;
		var hardwareChanged = previous.State == RuntimePerformanceVerificationState.Verified &&
			IsQualifiedHardwareFingerprint(previous.HardwareFingerprint) &&
			IsQualifiedHardwareFingerprint(hardwareFingerprint) &&
			!string.Equals(previous.HardwareFingerprint, hardwareFingerprint, StringComparison.Ordinal);
		var pipelineChanged = previous.State == RuntimePerformanceVerificationState.Verified &&
			!IsUnavailable(previous.PipelineFingerprint) &&
			!IsUnavailable(pipelineFingerprint) &&
			!string.Equals(previous.PipelineFingerprint, pipelineFingerprint, StringComparison.Ordinal);

		if (runtimeFault)
		{
			return previous with
			{
				State = RuntimePerformanceVerificationState.Invalidated,
				ValidUntil = null,
				InvalidationReason = RuntimePerformanceInvalidationReason.RuntimeFault,
				Detail = "Runtime performance verification was invalidated by a relevant Runtime health failure."
			};
		}

		if (hardwareChanged)
		{
			_explicitPerformanceInvalidatedAt = now;
			return previous with
			{
				State = RuntimePerformanceVerificationState.Invalidated,
				ValidUntil = null,
				InvalidationReason = RuntimePerformanceInvalidationReason.HardwareChanged,
				Detail = "Runtime performance verification was invalidated because the observed CPU/GPU identity changed.",
				HardwareFingerprint = hardwareFingerprint,
				PipelineFingerprint = pipelineFingerprint
			};
		}

		if (pipelineChanged)
		{
			_explicitPerformanceInvalidatedAt = now;
			return previous with
			{
				State = RuntimePerformanceVerificationState.Invalidated,
				ValidUntil = null,
				InvalidationReason = RuntimePerformanceInvalidationReason.PipelineChanged,
				Detail = "Runtime performance verification was invalidated because the active output format changed.",
				HardwareFingerprint = hardwareFingerprint,
				PipelineFingerprint = pipelineFingerprint
			};
		}

		if (previous.State == RuntimePerformanceVerificationState.Verified &&
			previous.ValidUntil is { } validUntil &&
			now > validUntil)
		{
			return previous with
			{
				State = RuntimePerformanceVerificationState.Invalidated,
				ValidUntil = null,
				InvalidationReason = RuntimePerformanceInvalidationReason.MeasurementExpired,
				Detail = "Runtime performance verification expired because no qualified measurement arrived within the validity window."
			};
		}

		var newerThanExplicitInvalidation = _explicitPerformanceInvalidatedAt is not { } invalidatedAt ||
			observedAt > invalidatedAt;
		if (measurementFresh && newerThanExplicitInvalidation)
		{
			_explicitPerformanceInvalidatedAt = null;
			return new RuntimePerformanceVerificationSnapshot(
				RuntimePerformanceVerificationState.Verified,
				observedAt,
				observedAt + _performanceValidity,
				RuntimePerformanceInvalidationReason.None,
				$"Runtime frame processing is verified at {health.FrameTime.TotalMilliseconds:0.00} ms against a {health.FrameBudget.TotalMilliseconds:0.00} ms frame budget.",
				hardwareFingerprint,
				pipelineFingerprint);
		}

		if (previous.State == RuntimePerformanceVerificationState.Verified &&
			previous.ValidUntil is { } retainedUntil &&
			now <= retainedUntil)
		{
			return previous;
		}

		if (previous.State == RuntimePerformanceVerificationState.Invalidated)
			return previous;

		return new RuntimePerformanceVerificationSnapshot(
			RuntimePerformanceVerificationState.Unverified,
			null,
			null,
			RuntimePerformanceInvalidationReason.None,
			"Runtime performance is waiting for a fresh qualified frame-time measurement.",
			hardwareFingerprint,
			pipelineFingerprint);
	}

	private static ReadinessEvaluation EvaluateReadiness(
		RuntimeReadinessObservation observation,
		RuntimePerformanceVerificationSnapshot performance)
	{
		if (observation.RecoveryExhausted)
		{
			var detail = string.IsNullOrWhiteSpace(observation.TerminalFailureDetail)
				? "Automatic recovery is exhausted."
				: observation.TerminalFailureDetail.Trim();
			return new ReadinessEvaluation(
				RuntimeReadinessState.Failed,
				[
					new ReadinessReason(
						"runtime.recovery.exhausted",
						"Engine",
						ReadinessReasonSeverity.Critical,
						detail,
						true)
				],
				false);
		}

		if (observation.Stale)
		{
			return new ReadinessEvaluation(
				RuntimeReadinessState.Recovering,
				[
					new ReadinessReason(
						"control.recovering",
						"Control",
						ReadinessReasonSeverity.Warning,
						"Authoritative Control state is stale. Automatic full-snapshot resynchronization is active.",
						true)
				],
				false);
		}

		if (!observation.Connected)
		{
			return new ReadinessEvaluation(
				RuntimeReadinessState.Initializing,
				[
					new ReadinessReason(
						"control.initializing",
						"Control",
						ReadinessReasonSeverity.Information,
						"Waiting for the first authoritative Control snapshot.",
						true)
				],
				false);
		}

		var reasons = new List<ReadinessReason>();
		var runtimeReady = string.Equals(observation.RuntimeStatus.Trim(), "READY", StringComparison.OrdinalIgnoreCase);
		var retainedPerformance = performance.State == RuntimePerformanceVerificationState.Verified;

		if (!runtimeReady)
		{
			var retainedObservation = observation.Health.Runtime.State == OperatorHealthStates.Unverified &&
				retainedPerformance;
			reasons.Add(new ReadinessReason(
				retainedObservation ? "runtime.observation.retained" : "runtime.not_ready",
				"Runtime",
				retainedObservation ? ReadinessReasonSeverity.Warning : ReadinessReasonSeverity.Critical,
				retainedObservation
					? "Runtime refresh missed; the last qualified Runtime performance observation remains within its validity window."
					: $"Runtime readiness is {observation.RuntimeStatus.Trim().ToUpperInvariant()}.",
				!retainedObservation));
		}

		AddHealthReason(reasons, "control", "Control", observation.Health.Control, skipUnverified: false);
		AddHealthReason(reasons, "runtime", "Runtime", observation.Health.Runtime, skipUnverified: !runtimeReady && retainedPerformance);
		AddHealthReason(reasons, "media", "Media", observation.Health.Media, skipUnverified: false);
		AddHealthReason(reasons, "provider", "Provider", observation.Health.Provider, skipUnverified: false);
		AddHealthReason(reasons, "gpu-provider", "GPU / Provider", observation.Health.GpuProvider, skipUnverified: false);

		if (observation.AIShowcase.Enabled &&
			AIDegradedStates.Contains(observation.AIShowcase.Status.Trim().ToUpperInvariant(), StringComparer.Ordinal))
		{
			reasons.Add(new ReadinessReason(
				"ai.degraded",
				"AI",
				ReadinessReasonSeverity.Warning,
				$"AI is {observation.AIShowcase.Status.Trim().ToUpperInvariant()}; governed fallback keeps core production independent.",
				false));
		}

		if (performance.State != RuntimePerformanceVerificationState.Verified)
		{
			reasons.Add(new ReadinessReason(
				"performance.unverified",
				"Performance",
				ReadinessReasonSeverity.Warning,
				performance.Detail,
				false));
		}

		if (reasons.Any(reason => reason.IsBlocking))
			return new ReadinessEvaluation(RuntimeReadinessState.NotReady, reasons.ToArray(), false);

		if (reasons.Count > 0)
			return new ReadinessEvaluation(RuntimeReadinessState.Degraded, reasons.ToArray(), true);

		return new ReadinessEvaluation(RuntimeReadinessState.Ready, Array.Empty<ReadinessReason>(), true);
	}

	private static void AddHealthReason(
		List<ReadinessReason> reasons,
		string codePrefix,
		string component,
		OperatorHealthMetricDescriptor metric,
		bool skipUnverified)
	{
		if (metric.State == OperatorHealthStates.Pass)
			return;

		if (metric.State == OperatorHealthStates.Fail)
		{
			reasons.Add(new ReadinessReason(
				$"{codePrefix}.failed",
				component,
				ReadinessReasonSeverity.Critical,
				metric.Detail,
				true));
			return;
		}

		if (!skipUnverified)
		{
			reasons.Add(new ReadinessReason(
				$"{codePrefix}.unverified",
				component,
				ReadinessReasonSeverity.Warning,
				metric.Detail,
				false));
		}
	}

	private static string BuildHardwareFingerprint(OperatorHealthDescriptor health) =>
		$"{NormalizeFingerprint(health.CpuDeviceName)}|{NormalizeFingerprint(health.GpuDeviceName)}";

	private static string NormalizeFingerprint(string? value) =>
		string.IsNullOrWhiteSpace(value) ? "UNVERIFIED" : value.Trim().ToUpperInvariant();

	private static bool IsQualifiedHardwareFingerprint(string fingerprint) =>
		!string.IsNullOrWhiteSpace(fingerprint) &&
		!fingerprint.Contains("UNVERIFIED", StringComparison.OrdinalIgnoreCase) &&
		!fingerprint.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) &&
		!fingerprint.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase);

	private static bool IsUnavailable(string? value) =>
		string.IsNullOrWhiteSpace(value) ||
		value.Trim().Equals("UNVERIFIED", StringComparison.OrdinalIgnoreCase) ||
		value.Trim().Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
		value.Trim().Equals("UNAVAILABLE", StringComparison.OrdinalIgnoreCase);

	private static bool Equivalent(RuntimeReadinessSnapshot left, RuntimeReadinessSnapshot right)
	{
		if (left.State != right.State ||
			left.IsProductionReady != right.IsProductionReady ||
			left.Performance != right.Performance ||
			left.Reasons.Count != right.Reasons.Count)
		{
			return false;
		}

		for (var index = 0; index < left.Reasons.Count; index++)
		{
			if (left.Reasons[index] != right.Reasons[index])
				return false;
		}
		return true;
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(RuntimeReadinessService));
	}

	private sealed record ReadinessEvaluation(
		RuntimeReadinessState State,
		IReadOnlyList<ReadinessReason> Reasons,
		bool IsProductionReady);
}
