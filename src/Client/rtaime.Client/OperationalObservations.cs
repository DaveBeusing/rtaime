// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Diagnostics;

namespace rtaime.Client;

// A versioned, non-authoritative projection of existing subsystem evidence.
// Timestamps describe Health projection, not the physical device or wire event.
public enum OperationalObservationCondition
{
	Clear,
	Active,
	Stale
}

public enum OperationalObservationSeverity
{
	Information,
	Warning,
	Degraded,
	Error
}

public sealed record OperationalObservation(
	int FormatVersion,
	string Key,
	string ComponentId,
	string DisplayName,
	OperationalObservationCondition Condition,
	OperationalObservationSeverity Severity,
	DateTimeOffset ProjectedAtUtc,
	long MonotonicTimestamp,
	DateTimeOffset SinceUtc,
	TimeSpan Duration,
	DateTimeOffset? LastGoodProjectedUtc,
	string EvidenceAvailability,
	string EvidenceSource,
	SubsystemProgramImpact ProgramImpact,
	string Detail,
	string RecommendedAction,
	string? CorrelationId = null,
	DateTimeOffset? ResolvedAtUtc = null,
	string? OwningProviderId = null)
{
	public const int CurrentFormatVersion = 1;
}

// Bounded and event-driven. A change of severity is an event, but a repeated
// metric or description update cannot generate another alarm notification.
public sealed class OperationalObservationProjector
{
	public const int Capacity = 64;
	private readonly Func<long> _monotonicClock;
	private readonly long _frequency;
	private Dictionary<string, Entry> _current = new(StringComparer.Ordinal);

	private sealed record Entry(OperationalObservation Observation, long SinceTick, bool HadActive);

	public OperationalObservationProjector(Func<long>? monotonicClock = null, long? frequency = null)
	{
		_monotonicClock = monotonicClock ?? Stopwatch.GetTimestamp;
		_frequency = frequency ?? Stopwatch.Frequency;
		if (_frequency <= 0)
			throw new ArgumentOutOfRangeException(nameof(frequency));
	}

	public IReadOnlyList<OperationalObservation> Current => _current.Values
		.Select(entry => entry.Observation)
		.OrderBy(entry => entry.ComponentId, StringComparer.Ordinal)
		.ToArray();

	// This method owns no polling, I/O, Runtime command, or media buffer.
	public IReadOnlyList<OperationalObservation> Update(
		IReadOnlyList<SubsystemHealthSnapshot> snapshots,
		DateTimeOffset projectedAt)
	{
		ArgumentNullException.ThrowIfNull(snapshots);
		var utc = projectedAt.ToUniversalTime();
		var ticks = _monotonicClock();
		var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
		var transitions = new List<OperationalObservation>();

		foreach (var source in snapshots)
		{
			if (next.Count == Capacity)
				break;
			if (source is null || string.IsNullOrWhiteSpace(source.Id) ||
				next.ContainsKey(source.Id))
				continue;

			_current.TryGetValue(source.Id, out var previous);
			var availability = EvidenceAvailability(source);
			var condition = Condition(source, availability);
			var severity = Severity(source.State);
			var changedCondition = previous is null || previous.Observation.Condition != condition;
			var sinceTick = changedCondition ? ticks : previous!.SinceTick;
			var sinceUtc = changedCondition ? utc : previous!.Observation.SinceUtc;
			var hadActive = condition == OperationalObservationCondition.Active || previous?.HadActive == true;
			var resolvedAt = condition == OperationalObservationCondition.Clear
				? (previous?.Observation.Condition != OperationalObservationCondition.Clear && hadActive
					? utc : previous?.Observation.ResolvedAtUtc)
				: null;
			var lastGood = condition == OperationalObservationCondition.Clear
				? source.LastSuccessfulCheck?.ToUniversalTime() ?? utc
				: previous?.Observation.LastGoodProjectedUtc ?? source.LastSuccessfulCheck?.ToUniversalTime();

			var current = new OperationalObservation(
				OperationalObservation.CurrentFormatVersion,
				$"health:{source.Id}",
				source.Id,
				source.DisplayName,
				condition,
				severity,
				utc,
				ticks,
				sinceUtc,
				Elapsed(sinceTick, ticks),
				lastGood,
				availability,
				string.IsNullOrWhiteSpace(source.EvidenceSource) ? "UNSPECIFIED" : source.EvidenceSource,
				source.ProgramImpact,
				source.Detail,
				source.RecoveryStatus,
				ResolvedAtUtc: resolvedAt,
				OwningProviderId: KnownProviderId(source));
			next.Add(source.Id, new Entry(current, sinceTick, hadActive));

			if (previous is null
				? condition == OperationalObservationCondition.Active
				: changedCondition || previous.Observation.Severity != severity)
				transitions.Add(current);
		}

		// Loss of an entire source does not silently resolve an active incident.
		// Retain its last evidence as stale, without inventing a new observation.
		foreach (var pair in _current)
		{
			if (next.Count == Capacity)
				break;
			if (next.ContainsKey(pair.Key))
				continue;
			var previous = pair.Value;
			var changed = previous.Observation.Condition != OperationalObservationCondition.Stale;
			var sinceTick = changed ? ticks : previous.SinceTick;
			var retained = previous.Observation with
			{
				Condition = OperationalObservationCondition.Stale,
				Severity = OperationalObservationSeverity.Information,
				EvidenceAvailability = "UNAVAILABLE",
				ProjectedAtUtc = utc,
				MonotonicTimestamp = ticks,
				SinceUtc = changed ? utc : previous.Observation.SinceUtc,
				Duration = Elapsed(sinceTick, ticks)
			};
			next.Add(pair.Key, new Entry(retained, sinceTick, previous.HadActive));
			if (changed)
				transitions.Add(retained);
		}

		_current = next;
		return transitions;
	}

	// Only use a provider identifier actually published by the source. The
	// descriptive evidence source is retained independently as provenance.
	private static string? KnownProviderId(SubsystemHealthSnapshot snapshot)
	{
		var value = snapshot.Metrics.FirstOrDefault(metric =>
			string.Equals(metric.Label, "Provider", StringComparison.Ordinal))?.Value;
		return string.IsNullOrWhiteSpace(value) ||
			value is "UNKNOWN" or "UNAVAILABLE" or "UNVERIFIED" or "—"
			? null : value;
	}

	public static string EvidenceAvailability(SubsystemHealthSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		var evidence = snapshot.Metrics
			.FirstOrDefault(metric => string.Equals(metric.Label, "Evidence status", StringComparison.OrdinalIgnoreCase))
			?.Value?.Trim().ToUpperInvariant();
		return evidence switch
		{
			"STALE" or "UNAVAILABLE" or "UNVERIFIED" or "UNKNOWN" => evidence,
			"OBSERVED" => "OBSERVED",
			_ => snapshot.State == SubsystemHealthState.Unknown ? "UNKNOWN" : "OBSERVED"
		};
	}

	public static OperationalObservationCondition Condition(SubsystemHealthSnapshot snapshot, string availability)
	{
		if (snapshot.State == SubsystemHealthState.Unknown ||
			!string.Equals(availability, "OBSERVED", StringComparison.Ordinal))
			return OperationalObservationCondition.Stale;
		return HealthSnapshotAnalysis.RequiresAttention(snapshot.State)
			? OperationalObservationCondition.Active
			: OperationalObservationCondition.Clear;
	}

	private static OperationalObservationSeverity Severity(SubsystemHealthState state) => state switch
	{
		SubsystemHealthState.Failed => OperationalObservationSeverity.Error,
		SubsystemHealthState.Degraded => OperationalObservationSeverity.Degraded,
		SubsystemHealthState.Warning or SubsystemHealthState.Recovering => OperationalObservationSeverity.Warning,
		_ => OperationalObservationSeverity.Information
	};

	private TimeSpan Elapsed(long sinceTick, long nowTick)
	{
		var delta = Math.Max(0d, (double)nowTick - sinceTick);
		return TimeSpan.FromSeconds(Math.Min(delta / _frequency, TimeSpan.MaxValue.TotalSeconds));
	}
}
