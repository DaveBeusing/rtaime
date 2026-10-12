// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;

namespace rtaime.AppHost;

public enum LifecycleStageStatus
{
	Pending,
	Starting,
	Ready,
	Degraded,
	Failed
}

public enum LifecycleStageRequirement
{
	Critical,
	RequiredForProduction,
	Optional
}

public static class ApplicationLifecycleStages
{
	public const string ApplicationBootstrap = "application-bootstrap";
	public const string Configuration = "configuration";
	public const string OperatorInterface = "operator-interface";
	public const string ControlHost = "control-host";
	public const string RuntimeHost = "runtime-host";
	public const string AIHost = "ai-host";
	public const string ProductionReadiness = "production-readiness";
}

public sealed record LifecycleStageSnapshot(
	string Id,
	string DisplayName,
	LifecycleStageRequirement Requirement,
	LifecycleStageStatus Status,
	DateTimeOffset? StartedAt,
	DateTimeOffset? CompletedAt,
	string? StatusText,
	string? FailureReason,
	bool CanRetry);

public sealed class LifecycleStateChangedEventArgs(
	LifecycleStageSnapshot? activeStage,
	IReadOnlyList<LifecycleStageSnapshot> stages) : EventArgs
{
	public LifecycleStageSnapshot? ActiveStage { get; } = activeStage;
	public IReadOnlyList<LifecycleStageSnapshot> Stages { get; } = stages;
}

public interface IApplicationLifecycleStateProvider
{
	IReadOnlyList<LifecycleStageSnapshot> Stages { get; }
	LifecycleStageSnapshot? ActiveStage { get; }
	event EventHandler<LifecycleStateChangedEventArgs>? StateChanged;
}

public sealed class ApplicationLifecycleStateProvider : IApplicationLifecycleStateProvider
{
	private readonly object _gate = new();
	private readonly IApplicationHostPlatform _platform;
	private readonly string _evidencePath;
	private readonly string? _diagnosticPath;
	private readonly bool _requireAI;
	private List<LifecycleStageSnapshot> _stages = [];
	private ApplicationReadinessFailureSnapshot? _readinessFailure;
	private DateTimeOffset? _shutdownInitiatedAtUtc;
	private string? _shutdownFailureReason;
	private DateTimeOffset? _shutdownFailureAtUtc;

	public ApplicationLifecycleStateProvider(
		string evidencePath,
		bool requireAI,
		IApplicationHostPlatform platform,
		string? diagnosticPath = null)
	{
		if (string.IsNullOrWhiteSpace(evidencePath))
			throw new ArgumentException("Lifecycle evidence path is required.", nameof(evidencePath));

		_platform = platform ?? throw new ArgumentNullException(nameof(platform));
		_evidencePath = Path.GetFullPath(evidencePath);
		_diagnosticPath = string.IsNullOrWhiteSpace(diagnosticPath) ? null : Path.GetFullPath(diagnosticPath);
		_requireAI = requireAI;
		ResetStages();
	}

	public IReadOnlyList<LifecycleStageSnapshot> Stages
	{
		get
		{
			lock (_gate)
				return _stages.ToArray();
		}
	}

	public LifecycleStageSnapshot? ActiveStage
	{
		get
		{
			lock (_gate)
				return ResolveActiveStage(_stages);
		}
	}

	public event EventHandler<LifecycleStateChangedEventArgs>? StateChanged;

	public void Initialize()
	{
		lock (_gate)
		{
			ResetStages();
			_readinessFailure = null;
			_shutdownInitiatedAtUtc = null;
			_shutdownFailureReason = null;
			_shutdownFailureAtUtc = null;
			PublishLocked();
		}
		RaiseStateChanged();
	}

	public void StartStage(string id, string statusText, bool canRetry = false) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Starting,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = null,
				StatusText = statusText,
				FailureReason = null,
				CanRetry = canRetry
			});

	public void CompleteStage(string id, string statusText) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Ready,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = _platform.UtcNow,
				StatusText = statusText,
				FailureReason = null,
				CanRetry = false
			});

	public void DegradeStage(string id, string statusText, string? failureReason = null, bool canRetry = false) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Degraded,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = null,
				StatusText = statusText,
				FailureReason = failureReason,
				CanRetry = canRetry
			});

	public void FailStage(string id, string failureReason, bool canRetry = false) =>
		UpdateStage(
			id,
			stage => stage with
			{
				Status = LifecycleStageStatus.Failed,
				StartedAt = stage.StartedAt ?? _platform.UtcNow,
				CompletedAt = _platform.UtcNow,
				StatusText = "Startup stage failed.",
				FailureReason = failureReason,
				CanRetry = canRetry
			});

	public void FailActiveStages(string failureReason)
	{
		lock (_gate)
		{
			var now = _platform.UtcNow;
			_stages = _stages
				.Select(stage => stage.Status == LifecycleStageStatus.Starting
					? stage with
					{
						Status = LifecycleStageStatus.Failed,
						CompletedAt = now,
						StatusText = "Startup stage failed.",
						FailureReason = failureReason,
						CanRetry = false
					}
					: stage)
				.ToList();
			PublishLocked();
		}
		RaiseStateChanged();
	}

	public void ProjectHostObservations(IReadOnlyList<ApplicationHostReadinessObservation> observations)
	{
		lock (_gate)
		{
			if (_readinessFailure is not null) return;
			foreach (var observation in observations)
			{
				var id = HostStageId(observation.Host);
				var index = _stages.FindIndex(stage => stage.Id == id);
				if (index < 0 || observation.Qualification == "NotRequired") continue;
				var status = observation.Qualification switch
				{
					"Ready" => LifecycleStageStatus.Ready,
					"Failed" => LifecycleStageStatus.Failed,
					"Degraded" => LifecycleStageStatus.Degraded,
					_ => LifecycleStageStatus.Pending
				};
				var stage = _stages[index];
				_stages[index] = stage with
				{
					Status = status,
					StartedAt = stage.StartedAt ?? _platform.UtcNow,
					CompletedAt = status is LifecycleStageStatus.Ready or LifecycleStageStatus.Failed ? _platform.UtcNow : null,
					StatusText = $"{observation.Host}: {observation.Qualification}; lifecycle {observation.LifecycleState ?? "unknown"}, health {observation.HealthState ?? "unknown"}, IPC {observation.IpcReachable?.ToString() ?? "unobserved"}.",
					FailureReason = status == LifecycleStageStatus.Failed ? observation.ReasonCode : null,
					CanRetry = false
				};
			}
			PublishLocked();
		}
		RaiseStateChanged();
	}

	public void CaptureReadinessFailure(ApplicationReadinessFailureSnapshot failure)
	{
		lock (_gate)
		{
			if (_readinessFailure is not null) return;
			_readinessFailure = failure;
			foreach (var observation in failure.Hosts)
			{
				var index = _stages.FindIndex(stage => stage.Id == HostStageId(observation.Host));
				if (index < 0 || observation.Qualification == "NotRequired") continue;
				var stage = _stages[index];
				var status = observation.Qualification switch
				{
					"Failed" => LifecycleStageStatus.Failed,
					"Ready" => LifecycleStageStatus.Ready,
					"Degraded" => LifecycleStageStatus.Degraded,
					_ => LifecycleStageStatus.Pending
				};
				_stages[index] = stage with
				{
					Status = status,
					CompletedAt = status is LifecycleStageStatus.Failed or LifecycleStageStatus.Ready ? failure.CapturedAtUtc : null,
					StatusText = $"Pre-shutdown observation: {observation.Host} {observation.Qualification}; phase {observation.LastProgressPhase ?? "unknown"} / {observation.PhaseOutcome ?? "unknown"}.",
					FailureReason = status == LifecycleStageStatus.Failed ? observation.ReasonCode : null,
					CanRetry = false
				};
			}
			var production = _stages.FindIndex(stage => stage.Id == ApplicationLifecycleStages.ProductionReadiness);
			var unmet = string.Join("; ", failure.UnmetCriteria.Select(item => $"{item.Host}: {item.Code} ({item.Detail})"));
			_stages[production] = _stages[production] with
			{
				Status = LifecycleStageStatus.Failed,
				StartedAt = _stages[production].StartedAt ?? failure.CapturedAtUtc,
				CompletedAt = failure.CapturedAtUtc,
				StatusText = failure.AggregateDeadlineExpired ? "Aggregate startup deadline expired." : "Production startup qualification failed.",
				FailureReason = $"{failure.ReasonCode}: {failure.OriginalFailure} Unmet criteria: {unmet}",
				CanRetry = false
			};
			TryPublishFailureLocked();
		}
		RaiseStateChanged();
	}

	public void MarkShutdownInitiated()
	{
		lock (_gate)
		{
			_shutdownInitiatedAtUtc ??= _platform.UtcNow;
			TryPublishFailureLocked();
		}
		RaiseStateChanged();
	}

	public void RecordShutdownFailure(string reason)
	{
		lock (_gate)
		{
			_shutdownFailureReason ??= reason;
			_shutdownFailureAtUtc ??= _platform.UtcNow;
			TryPublishFailureLocked();
		}
		RaiseStateChanged();
	}

	private void TryPublishFailureLocked()
	{
		try { PublishLocked(); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Retain the original failure in memory when diagnostic storage is unavailable.
		}
	}

	private static string? HostStageId(string host) => host switch
	{
		"ControlHost" => ApplicationLifecycleStages.ControlHost,
		"RuntimeHost" => ApplicationLifecycleStages.RuntimeHost,
		"AIHost" => ApplicationLifecycleStages.AIHost,
		_ => null
	};

	private void UpdateStage(
		string id,
		Func<LifecycleStageSnapshot, LifecycleStageSnapshot> update)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentNullException.ThrowIfNull(update);

		lock (_gate)
		{
			var index = _stages.FindIndex(stage => string.Equals(stage.Id, id, StringComparison.Ordinal));
			if (index < 0)
				throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown application lifecycle stage.");

			_stages[index] = update(_stages[index]);
			PublishLocked();
		}
		RaiseStateChanged();
	}

	private void ResetStages()
	{
		_stages =
		[
			Pending(
				ApplicationLifecycleStages.ApplicationBootstrap,
				"Application Bootstrap",
				LifecycleStageRequirement.Critical),
			Pending(
				ApplicationLifecycleStages.Configuration,
				"Configuration",
				LifecycleStageRequirement.Critical),
			Pending(
				ApplicationLifecycleStages.OperatorInterface,
				"Operator Interface",
				LifecycleStageRequirement.Critical),
			Pending(
				ApplicationLifecycleStages.ControlHost,
				"ControlHost",
				LifecycleStageRequirement.RequiredForProduction),
			Pending(
				ApplicationLifecycleStages.RuntimeHost,
				"RuntimeHost",
				LifecycleStageRequirement.RequiredForProduction),
			Pending(
				ApplicationLifecycleStages.AIHost,
				"AIHost",
				_requireAI ? LifecycleStageRequirement.RequiredForProduction : LifecycleStageRequirement.Optional,
				_requireAI ? "Waiting for startup." : "Not required by the selected startup profile."),
			Pending(
				ApplicationLifecycleStages.ProductionReadiness,
				"Production Readiness",
				LifecycleStageRequirement.RequiredForProduction)
		];
	}

	private static LifecycleStageSnapshot Pending(
		string id,
		string displayName,
		LifecycleStageRequirement requirement,
		string statusText = "Waiting for startup.") =>
		new(
			id,
			displayName,
			requirement,
			LifecycleStageStatus.Pending,
			null,
			null,
			statusText,
			null,
			false);

	private void PublishLocked()
	{
		var activeStage = ResolveActiveStage(_stages);
		var payload = JsonSerializer.Serialize(
			new
			{
				copyright = "Copyright (c) Dave Beusing <david.beusing@gmail.com>.",
				schemaVersion = "1.0",
				observedAtUtc = _platform.UtcNow,
				diagnosticPath = _diagnosticPath,
				readinessFailure = _readinessFailure,
				shutdownInitiatedAtUtc = _shutdownInitiatedAtUtc,
				shutdownFailureReason = _shutdownFailureReason,
				shutdownFailureAtUtc = _shutdownFailureAtUtc,
				activeStageId = activeStage?.Id,
				stages = _stages.Select(stage => new
				{
					id = stage.Id,
					displayName = stage.DisplayName,
					requirement = stage.Requirement.ToString(),
					status = stage.Status.ToString(),
					startedAtUtc = stage.StartedAt,
					completedAtUtc = stage.CompletedAt,
					statusText = stage.StatusText,
					failureReason = stage.FailureReason,
					canRetry = stage.CanRetry
				})
			},
			new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

		_platform.WriteAllTextAtomic(_evidencePath, payload + Environment.NewLine);
	}

	private void RaiseStateChanged()
	{
		var stages = Stages;
		StateChanged?.Invoke(this, new LifecycleStateChangedEventArgs(ResolveActiveStage(stages), stages));
	}

	private static LifecycleStageSnapshot? ResolveActiveStage(IReadOnlyList<LifecycleStageSnapshot> stages) =>
		stages.FirstOrDefault(stage => stage.Status == LifecycleStageStatus.Failed) ??
		stages.FirstOrDefault(stage => stage.Status == LifecycleStageStatus.Degraded) ??
		stages.FirstOrDefault(stage => stage.Status == LifecycleStageStatus.Starting);
}
