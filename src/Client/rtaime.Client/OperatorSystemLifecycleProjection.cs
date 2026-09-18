// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

namespace rtaime.Client;

public static class OperatorLifecycleStates
{
	public const string Stopped = "STOPPED";
	public const string Starting = "STARTING";
	public const string Healthy = "HEALTHY";
	public const string Degraded = "DEGRADED";
	public const string Recovering = "RECOVERING";
	public const string Failed = "FAILED";
	public const string Stopping = "STOPPING";
}

public static class OperatorProgramSafetyStates
{
	public const string Unknown = "UNKNOWN";
	public const string Safe = "SAFE";
	public const string Caution = "CAUTION";
	public const string Blocked = "BLOCKED";
}

public sealed record OperatorSystemLifecycleSnapshot(
	string State,
	string Detail,
	string ProgramSafety,
	string RequiredAction,
	string? AffectedComponent,
	bool MainUiReady);

public static class OperatorSystemLifecycleProjection
{
	private static readonly string[] AIDegradedStates = ["UNAVAILABLE", "TIMEOUT", "FAILED"];

	public static OperatorSystemLifecycleSnapshot Evaluate(
		OperatorHealthDescriptor health,
		string runtimeStatus,
		OperatorAIShowcaseDescriptor aiShowcase,
		bool connected,
		bool stale,
		bool recoveryExhausted = false,
		string? terminalFailureDetail = null)
	{
		ArgumentNullException.ThrowIfNull(health);
		if (string.IsNullOrWhiteSpace(runtimeStatus))
			throw new ArgumentException("Runtime status is required.", nameof(runtimeStatus));
		ArgumentNullException.ThrowIfNull(aiShowcase);

		if (recoveryExhausted)
		{
			return new(
				OperatorLifecycleStates.Failed,
				string.IsNullOrWhiteSpace(terminalFailureDetail) ? "Automatic recovery is exhausted." : terminalFailureDetail.Trim(),
				OperatorProgramSafetyStates.Blocked,
				"Keep production mutations blocked and use the bounded diagnostics/support snapshot.",
				ResolveFailedComponent(health) ?? "Engine",
				false);
		}

		if (stale)
		{
			return new(
				OperatorLifecycleStates.Recovering,
				"Authoritative Control state is stale. Automatic full-snapshot resynchronization is running.",
				OperatorProgramSafetyStates.Blocked,
				"No mutation is required while automatic recovery is active.",
				"Control",
				false);
		}

		if (!connected)
		{
			return new(
				OperatorLifecycleStates.Starting,
				"Waiting for the first authoritative Control snapshot.",
				OperatorProgramSafetyStates.Unknown,
				"Startup is automatic; no operator action is required.",
				"Control",
				false);
		}

		var runtimeReady = string.Equals(runtimeStatus, "READY", StringComparison.OrdinalIgnoreCase);
		var failedComponent = ResolveFailedComponent(health);
		var mainUiReady = runtimeReady &&
			health.Control.State != OperatorHealthStates.Fail &&
			health.Runtime.State != OperatorHealthStates.Fail;

		if (!runtimeReady || failedComponent is not null)
		{
			var component = !runtimeReady ? "Runtime" : failedComponent;
			var detail = !runtimeReady
				? $"Runtime readiness is {runtimeStatus.Trim().ToUpperInvariant()}."
				: ResolveFailureDetail(health, failedComponent!);
			return new(
				OperatorLifecycleStates.Degraded,
				detail,
				OperatorProgramSafetyStates.Blocked,
				"Keep production mutations blocked while the affected required subsystem recovers.",
				component,
				mainUiReady);
		}

		if (aiShowcase.Enabled && AIDegradedStates.Contains(aiShowcase.Status.Trim().ToUpperInvariant(), StringComparer.Ordinal))
		{
			var safety = health.Engine.State == OperatorHealthStates.Pass
				? OperatorProgramSafetyStates.Safe
				: OperatorProgramSafetyStates.Caution;
			return new(
				OperatorLifecycleStates.Degraded,
				$"AI is {aiShowcase.Status.Trim().ToUpperInvariant()}; governed fallback keeps core Control/Runtime execution independent.",
				safety,
				"Review AI status only if the governed AI effect is required.",
				"AI",
				mainUiReady);
		}

		if (health.Engine.State == OperatorHealthStates.Pass)
		{
			return new(
				OperatorLifecycleStates.Healthy,
				"Control, Runtime, Media and required provider evidence is healthy.",
				OperatorProgramSafetyStates.Safe,
				"No operator action is required.",
				null,
				mainUiReady);
		}

		return new(
			OperatorLifecycleStates.Degraded,
			$"Engine evidence is {health.Engine.State}; missing evidence is not treated as healthy.",
			OperatorProgramSafetyStates.Caution,
			"Review System Status before relying on an unverified capability.",
			ResolveUnverifiedComponent(health),
			mainUiReady);
	}

	private static string? ResolveFailedComponent(OperatorHealthDescriptor health)
	{
		if (health.Control.State == OperatorHealthStates.Fail) return "Control";
		if (health.Runtime.State == OperatorHealthStates.Fail) return "Runtime";
		if (health.Media.State == OperatorHealthStates.Fail) return "Media";
		if (health.Provider.State == OperatorHealthStates.Fail) return "Provider";
		if (health.GpuProvider.State == OperatorHealthStates.Fail) return "GPU / Provider";
		return null;
	}

	private static string ResolveFailureDetail(OperatorHealthDescriptor health, string component) =>
		component switch
		{
			"Control" => health.Control.Detail,
			"Runtime" => health.Runtime.Detail,
			"Media" => health.Media.Detail,
			"Provider" => health.Provider.Detail,
			"GPU / Provider" => health.GpuProvider.Detail,
			_ => health.Engine.Detail
		};

	private static string? ResolveUnverifiedComponent(OperatorHealthDescriptor health)
	{
		if (health.Control.State == OperatorHealthStates.Unverified) return "Control";
		if (health.Runtime.State == OperatorHealthStates.Unverified) return "Runtime";
		if (health.Media.State == OperatorHealthStates.Unverified) return "Media";
		if (health.Provider.State == OperatorHealthStates.Unverified) return "Provider";
		if (health.GpuProvider.State == OperatorHealthStates.Unverified) return "GPU / Provider";
		return null;
	}
}
