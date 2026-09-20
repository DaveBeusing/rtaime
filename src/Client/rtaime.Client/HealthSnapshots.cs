// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public enum SubsystemHealthState
{
	Healthy,
	Warning,
	Degraded,
	Recovering,
	Failed,
	Unknown
}

public static class HealthStateMapping
{
	public static SubsystemHealthState FromEvidence(string? evidence) =>
		evidence?.Trim().ToUpperInvariant() switch
		{
			"PASS" => SubsystemHealthState.Healthy,
			"FAIL" => SubsystemHealthState.Failed,
			"WARNING" => SubsystemHealthState.Warning,
			"DEGRADED" => SubsystemHealthState.Degraded,
			"RECOVERING" => SubsystemHealthState.Recovering,
			_ => SubsystemHealthState.Unknown
		};
}

public sealed record HealthMetricSnapshot(
	string Label,
	string Value,
	string? Unit = null);

public sealed record SubsystemHealthSnapshot(
	string Id,
	string DisplayName,
	string Category,
	SubsystemHealthState State,
	string Detail,
	DateTimeOffset StatusSince,
	DateTimeOffset? LastSuccessfulCheck,
	IReadOnlyList<HealthMetricSnapshot> Metrics,
	string RecoveryStatus,
	string TechnicalDetail,
	bool CanRecover = false,
	string? RecoveryActionLabel = null);

public static class HealthSnapshotHistory
{
	public static SubsystemHealthSnapshot Retain(
		SubsystemHealthSnapshot candidate,
		SubsystemHealthSnapshot? previous,
		DateTimeOffset observedAt)
	{
		ArgumentNullException.ThrowIfNull(candidate);

		var statusSince = previous is not null && previous.State == candidate.State
			? previous.StatusSince
			: observedAt;
		var lastSuccessfulCheck = candidate.State == SubsystemHealthState.Healthy
			? observedAt
			: previous?.LastSuccessfulCheck;

		return candidate with
		{
			StatusSince = statusSince,
			LastSuccessfulCheck = lastSuccessfulCheck
		};
	}
}

public sealed class HealthSnapshotChangedEventArgs(
	IReadOnlyList<SubsystemHealthSnapshot> current) : EventArgs
{
	public IReadOnlyList<SubsystemHealthSnapshot> Current { get; } = current;
}

public interface IHealthSnapshotProvider
{
	IReadOnlyList<SubsystemHealthSnapshot> GetCurrent();
	event EventHandler<HealthSnapshotChangedEventArgs>? Changed;
}
